import test from 'node:test';
import assert from 'node:assert/strict';

import {
  buildRequestBody, isAnthropicModel, normaliseUsage, addUsage, elideOldToolResults,
  streamCompletion, runTurn, ELIDE_THRESHOLD, toolResultMeta
} from '../../js/llm.js';

import { textStream } from './fixtures.mjs';

const tools = [
  { type: 'function', function: { name: 'get_emails', description: 'd', parameters: { type: 'object', properties: {} } } },
  { type: 'function', function: { name: 'list_folder_info_by_type', description: 'd', parameters: { type: 'object', properties: {} } } }
];

const conversation = () => [
  { role: 'system', content: 'You are the SmarterMail Agent.' },
  { role: 'user', content: 'what is unread?' }
];

/** A fetch that records the request body and answers with a recorded stream. */
function capturingFetch(parts, seen) {
  return async (url, init) => {
    seen.push(JSON.parse(init.body));
    const encoder = new TextEncoder();
    const queue = parts.map((p) => encoder.encode(p));
    let i = 0;
    return {
      ok: true,
      status: 200,
      body: { getReader: () => ({ read: async () => (i < queue.length ? { done: false, value: queue[i++] } : { done: true }), releaseLock() {} }) }
    };
  };
}

/* ------------------------------------------------------------ request body */

test('isAnthropicModel: anthropic/* and ~anthropic/* only', () => {
  assert.equal(isAnthropicModel('anthropic/claude-haiku-5.5'), true);
  assert.equal(isAnthropicModel('~anthropic/claude-sonnet-latest'), true);
  assert.equal(isAnthropicModel('openai/gpt-5.6'), false);
  assert.equal(isAnthropicModel('google/gemini-2.5-flash'), false);
  assert.equal(isAnthropicModel('openrouter/auto'), false);
  assert.equal(isAnthropicModel(''), false);
  assert.equal(isAnthropicModel(undefined), false);
});

test('buildRequestBody: anthropic gets a system breakpoint plus top-level automatic caching', () => {
  const messages = conversation();
  const body = buildRequestBody({ model: 'anthropic/claude-haiku-5.5', messages, tools });

  assert.deepEqual(body.cache_control, { type: 'ephemeral' });
  assert.deepEqual(body.messages[0], {
    role: 'system',
    content: [{ type: 'text', text: 'You are the SmarterMail Agent.', cache_control: { type: 'ephemeral' } }]
  });
  // Nothing else carries a breakpoint: two of Anthropic's four slots.
  assert.equal(JSON.stringify(body).split('cache_control').length - 1, 2);
  assert.equal(body.messages[1], messages[1]);
  // The caller's history is not touched.
  assert.equal(messages[0].content, 'You are the SmarterMail Agent.');
  assert.equal(body.tools, tools);
  assert.equal(body.tool_choice, 'auto');
});

test('buildRequestBody: other models get a plain body', () => {
  for (const model of ['openai/gpt-5.6', 'deepseek/deepseek-chat', 'google/gemini-2.5-pro', 'x-ai/grok-4', 'm']) {
    const messages = conversation();
    const body = buildRequestBody({ model, messages, tools });
    assert.equal(body.cache_control, undefined, model);
    assert.equal(body.messages, messages, model);
    assert.equal(typeof body.messages[0].content, 'string', model);
    assert.ok(!JSON.stringify(body).includes('cache_control'), model);
  }
});

test('buildRequestBody: session id is passed through, capped at 256 characters', () => {
  assert.equal(buildRequestBody({ model: 'm', messages: [], sessionId: 'sma-1' }).session_id, 'sma-1');
  assert.equal(buildRequestBody({ model: 'm', messages: [], sessionId: 'x'.repeat(300) }).session_id.length, 256);
  assert.equal('session_id' in buildRequestBody({ model: 'm', messages: [] }), false);
});

test('buildRequestBody: the same history serialises byte-identically every round', () => {
  const messages = conversation();
  const a = JSON.stringify(buildRequestBody({ model: 'anthropic/claude-haiku-5.5', messages, tools }));
  const b = JSON.stringify(buildRequestBody({ model: 'anthropic/claude-haiku-5.5', messages, tools }));
  assert.equal(a, b);
  messages.push({ role: 'assistant', content: 'ok' });
  const c = JSON.stringify(buildRequestBody({ model: 'anthropic/claude-haiku-5.5', messages, tools }));
  // The earlier body (minus its closing of the messages array) is a prefix of the new one up to the history.
  const prefix = a.slice(0, a.indexOf('what is unread?'));
  assert.ok(c.startsWith(prefix));
});

test('streamCompletion: sends the cached body and reports cached tokens', async () => {
  const seen = [];
  const usageFrame = 'data: {"id":"g","choices":[],"usage":{"prompt_tokens":5000,"completion_tokens":20,"total_tokens":5020,"cost":0.0012,"prompt_tokens_details":{"cached_tokens":4800,"cache_write_tokens":0}}}\n\n';
  const r = await streamCompletion({
    apiKey: 'k', model: 'anthropic/claude-haiku-5.5', messages: conversation(), tools, sessionId: 'sma-abc',
    fetchImpl: capturingFetch([...textStream.slice(0, -1), usageFrame, 'data: [DONE]\n\n'], seen)
  });
  assert.equal(seen.length, 1);
  assert.deepEqual(seen[0].cache_control, { type: 'ephemeral' });
  assert.equal(seen[0].session_id, 'sma-abc');
  assert.equal(seen[0].messages[0].content[0].cache_control.type, 'ephemeral');
  assert.deepEqual(r.usageSummary, { promptTokens: 5000, completionTokens: 20, cachedTokens: 4800, cacheWriteTokens: 0, cost: 0.0012 });
});

test('normaliseUsage / addUsage: missing fields count as zero', () => {
  assert.equal(normaliseUsage(null), null);
  assert.deepEqual(normaliseUsage({ prompt_tokens: 10 }), { promptTokens: 10, completionTokens: 0, cachedTokens: 0, cacheWriteTokens: 0, cost: 0 });
  const t = addUsage(addUsage(null, normaliseUsage({ prompt_tokens: 10, prompt_tokens_details: { cache_write_tokens: 8 } })),
    normaliseUsage({ prompt_tokens: 12, prompt_tokens_details: { cached_tokens: 8 }, cost: 0.5 }));
  assert.deepEqual(t, { promptTokens: 22, completionTokens: 0, cachedTokens: 8, cacheWriteTokens: 8, cost: 0.5, requests: 2 });
});

/* ------------------------------------------------------------ elision */

const big = (ch, n = 5000) => ch.repeat(n);
const call = (id, name) => ({ id, type: 'function', function: { name, arguments: '{}' } });

function threeTurns() {
  return [
    { role: 'system', content: 'sys' },
    { role: 'user', content: 'turn 1' },
    { role: 'assistant', content: '', tool_calls: [call('a1', 'get_emails'), call('a2', 'get_note')] },
    { role: 'tool', tool_call_id: 'a1', content: big('a') },
    { role: 'tool', tool_call_id: 'a2', content: 'small' },
    { role: 'assistant', content: 'summary 1' },
    { role: 'user', content: 'turn 2' },
    { role: 'assistant', content: '', tool_calls: [call('b1', 'read_email_part')] },
    { role: 'tool', tool_call_id: 'b1', content: big('b') },
    { role: 'assistant', content: 'summary 2' },
    { role: 'user', content: 'turn 3' }
  ];
}

test('elideOldToolResults: only large results older than the previous turn become stubs', () => {
  const m = threeTurns();
  const n = elideOldToolResults(m);
  assert.equal(n, 1);
  assert.equal(m[3].content, '[earlier result of get_emails (5,000 chars) omitted to save context; call the tool again if you need it]');
  assert.equal(m[4].content, 'small');           // under the threshold
  assert.equal(m[8].content, big('b'));          // the previous turn is kept whole
  // Pairing intact: every tool_call id still has its tool message, in place.
  const ids = m.filter((x) => x.tool_calls).flatMap((x) => x.tool_calls.map((c) => c.id));
  assert.deepEqual(m.filter((x) => x.role === 'tool').map((x) => x.tool_call_id), ids);
  assert.equal(m.length, 11);
});

test('elideOldToolResults: idempotent, and nothing to do in the first two turns', () => {
  const m = threeTurns();
  elideOldToolResults(m);
  const once = JSON.stringify(m);
  assert.equal(elideOldToolResults(m), 0);
  assert.equal(JSON.stringify(m), once);

  const early = threeTurns().slice(0, 7);   // up to "turn 2"
  assert.equal(elideOldToolResults(early), 0);
  assert.equal(elideOldToolResults([{ role: 'system', content: 's' }, { role: 'user', content: 'u' }]), 0);
});

test('elideOldToolResults: threshold and keepTurns are honoured', () => {
  const m = threeTurns();
  assert.equal(elideOldToolResults(m, { keepTurns: 0 }), 2);
  assert.match(m[8].content, /^\[earlier result of read_email_part \(5,000 chars\)/);
  const n = threeTurns();
  assert.equal(elideOldToolResults(n, { threshold: 10000 }), 0);
  assert.ok(ELIDE_THRESHOLD >= 1000);
});

test('runTurn: elides at the start of a turn, then every round sends the same prefix', async () => {
  const messages = threeTurns();
  const sent = [];
  const usages = [];
  let i = 0;
  const script = [
    { toolCalls: [call('c1', 'get_emails')], finishReason: 'tool_calls' },
    { toolCalls: [call('c2', 'get_emails')], finishReason: 'tool_calls' },
    { content: 'done', finishReason: 'stop' }
  ];
  const r = await runTurn({
    messages, apiKey: 'k', model: 'anthropic/claude-haiku-5.5', tools,
    callTool: async () => ({ isError: false, content: big('z', 70000) }),
    streamImpl: async ({ messages: m, model, tools: t }) => {
      sent.push(JSON.stringify(buildRequestBody({ model, messages: m, tools: t })));
      const s = script[i++];
      return {
        content: s.content || '', reasoning: '', toolCalls: s.toolCalls || [], finishReason: s.finishReason, aborted: false,
        usage: { prompt_tokens: 1000 * i, completion_tokens: 5, prompt_tokens_details: { cached_tokens: i > 1 ? 900 : 0 } }
      };
    },
    ui: { onUsage: (round, total) => usages.push([round.cachedTokens, total.requests]) }
  });

  assert.equal(sent.length, 3);
  assert.match(sent[0], /earlier result of get_emails \(5,000 chars\)/);
  // Each round's request is the previous one with messages appended: a stable prefix.
  for (let k = 1; k < sent.length; k++) {
    const prev = JSON.parse(sent[k - 1]);
    const cur = JSON.parse(sent[k]);
    assert.equal(JSON.stringify(cur.tools), JSON.stringify(prev.tools));
    assert.ok(cur.messages.length > prev.messages.length);
    assert.equal(JSON.stringify(cur.messages.slice(0, prev.messages.length)), JSON.stringify(prev.messages), `round ${k} rewrote the prefix`);
  }
  // This turn's own (large) results are not elided while the turn runs.
  assert.equal(messages.filter((m) => m.role === 'tool' && m.content.length > 60000).length, 2);
  assert.deepEqual(usages, [[0, 1], [900, 2], [900, 3]]);
  assert.equal(r.usage.cachedTokens, 1800);
  assert.equal(r.usage.promptTokens, 6000);
  assert.equal(r.usage.requests, 3);

  // The next turn elides the old turn-2 result but keeps this turn's whole.
  messages.push({ role: 'user', content: 'turn 4' });
  elideOldToolResults(messages);
  assert.match(messages[8].content, /^\[earlier result of read_email_part/);
  assert.equal(messages.filter((m) => m.role === 'tool' && m.content.length > 60000).length, 2);
});

test('runTurn: elide:false leaves history alone', async () => {
  const messages = threeTurns();
  await runTurn({
    messages, apiKey: 'k', model: 'm', tools: [], elide: false, callTool: async () => ({}),
    streamImpl: async () => ({ content: 'ok', reasoning: '', toolCalls: [], finishReason: 'stop', usage: null, aborted: false })
  });
  assert.equal(messages[3].content, big('a'));
});

/* ------------------------------------------- elision never hides a write or a failure */

/** Two turns: a write and a read with large results, run through runTurn, then two more user turns. */
async function writeTurn({ isWrite, results }) {
  const messages = [{ role: 'system', content: 'sys' }, { role: 'user', content: 'move them and list the folder' }];
  let i = 0;
  const script = [
    { toolCalls: [call('w1', 'move_emails'), call('r1', 'get_emails'), call('e1', 'get_emails')], finishReason: 'tool_calls' },
    { content: 'done', finishReason: 'stop' }
  ];
  await runTurn({
    messages, apiKey: 'k', model: 'm', tools, isWrite,
    callTool: async () => results.shift(),
    streamImpl: async () => {
      const s = script[i++];
      return { content: s.content || '', reasoning: '', toolCalls: s.toolCalls || [], finishReason: s.finishReason, usage: null, aborted: false };
    }
  });
  messages.push({ role: 'user', content: 'thanks' }, { role: 'assistant', content: 'ok' }, { role: 'user', content: 'did that work?' });
  return messages;
}

test('elideOldToolResults: a write tool\'s result and an error result are never elided', async () => {
  const writeResult = big('w');
  const errorResult = big('e');
  const messages = await writeTurn({
    isWrite: (name) => name === 'move_emails',
    results: [
      { isError: false, content: writeResult },
      { isError: false, content: big('r') },
      { isError: true, content: errorResult }
    ]
  });
  const tool = (id) => messages.find((m) => m.role === 'tool' && m.tool_call_id === id);
  assert.deepEqual(toolResultMeta(tool('w1')), { name: 'move_emails', write: true, error: false });
  assert.deepEqual(toolResultMeta(tool('e1')), { name: 'get_emails', write: false, error: true });

  // isWrite is not even needed later: what runTurn recorded decides.
  assert.equal(elideOldToolResults(messages), 1);
  assert.equal(tool('w1').content, writeResult);
  assert.equal(tool('e1').content, errorResult);
  assert.match(tool('r1').content, /^\[earlier result of get_emails \(5,000 chars\) omitted to save context; call the tool again if you need it\]$/);
  // Idempotent: the next turn changes nothing.
  const once = JSON.stringify(messages);
  messages.push({ role: 'assistant', content: 'yes' }, { role: 'user', content: 'and now?' });
  assert.equal(elideOldToolResults(messages), 0);
  assert.equal(JSON.stringify(messages.slice(0, JSON.parse(once).length)), once);
});

test('elideOldToolResults: the tool list changing later does not unprotect a write', async () => {
  const messages = await writeTurn({
    isWrite: (name) => name === 'move_emails',
    results: [{ isError: false, content: big('w') }, { isError: false, content: big('r') }, { isError: false, content: big('x') }]
  });
  // The account that could write is gone: isWrite now says nothing about move_emails.
  elideOldToolResults(messages, { isWrite: () => undefined });
  assert.equal(messages.find((m) => m.tool_call_id === 'w1').content, big('w'));
});

test('elideOldToolResults: without a record, isWrite and then the name decide; unknown names get a "do not repeat" stub', () => {
  const history = (name, content = big('a')) => [
    { role: 'system', content: 'sys' },
    { role: 'user', content: 'u1' },
    { role: 'assistant', content: '', tool_calls: [call('x1', name)] },
    { role: 'tool', tool_call_id: 'x1', content },
    { role: 'user', content: 'u2' },
    { role: 'assistant', content: 'a2' },
    { role: 'user', content: 'u3' }
  ];
  // isWrite says write: kept.
  const a = history('domain_create_user');
  assert.equal(elideOldToolResults(a, { isWrite: (n) => n === 'domain_create_user' }), 0);
  assert.equal(a[3].content, big('a'));
  // Nothing known about a non-read name: elided, but told the change was already made.
  const b = history('domain_create_user');
  assert.equal(elideOldToolResults(b), 1);
  assert.equal(b[3].content, '[earlier result of domain_create_user (5,000 chars) omitted to save context; if this call changed something, that change was already made: do not repeat it]');
  assert.doesNotMatch(b[3].content, /call the tool again/);
  // A read-looking name keeps the plain stub.
  const c = history('search_log_files');
  elideOldToolResults(c);
  assert.match(c[3].content, /call the tool again if you need it\]$/);
  // An in-band failure payload is kept even without a record.
  const d = history('get_emails', '{"success":false,"error":"API call failed: InternalServerError",' + '"x":"' + 'y'.repeat(3000) + '"}');
  assert.equal(elideOldToolResults(d), 0);
  // Tool-call pairing is intact in every case.
  for (const m of [a, b, c, d]) assert.equal(m[3].tool_call_id, 'x1');
});
