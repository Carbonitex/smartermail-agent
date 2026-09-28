import test from 'node:test';
import assert from 'node:assert/strict';

import {
  createSseParser, ToolCallAccumulator, parseToolArguments, toolsToOpenAI,
  buildSystemPrompt, streamCompletion, runTurn, clampToolResult, describeHttpError, LlmError,
  toolGroups, filterToolsByCategory, sessionAccounts
} from '../../js/llm.js';

import {
  textStream, toolCallStream, parallelToolCallStream, noIndexToolCallStream,
  truncatedStream, midStreamErrorStream, malformedStream, fakeFetch, shred
} from './fixtures.mjs';

/* ------------------------------------------------------------ SSE parser */

test('sse parser: extracts data frames, skips comments, reports [DONE]', () => {
  const p = createSseParser();
  const events = p.push(textStream.join(''));
  const done = events.filter((e) => e.done);
  const data = events.filter((e) => e.data);
  assert.equal(done.length, 1);
  assert.equal(data.length, 6);
  assert.equal(data[1].data.choices[0].delta.content, 'You have ');
});

test('sse parser: survives being fed one byte at a time', () => {
  const p = createSseParser();
  const whole = textStream.join('');
  let content = '';
  for (const ch of whole) {
    for (const ev of p.push(ch)) {
      const d = ev.data && ev.data.choices && ev.data.choices[0];
      if (d && d.delta && d.delta.content) content += d.delta.content;
    }
  }
  assert.equal(content, 'You have 3 unread messages.');
});

test('sse parser: drops malformed frames without throwing', () => {
  const p = createSseParser();
  const events = p.push(malformedStream.join(''));
  const contents = events.filter((e) => e.data).map((e) => e.data.choices[0].delta.content);
  assert.deepEqual(contents, [' fine']);
});

test('sse parser: flush emits a trailing frame with no final newline', () => {
  const p = createSseParser();
  assert.deepEqual(p.push('data: {"a":1}'), []);
  const out = p.flush();
  assert.equal(out.length, 1);
  assert.equal(out[0].data.a, 1);
});

/* -------------------------------------------------- tool-call accumulator */

test('accumulator: concatenates argument fragments for one call', () => {
  const acc = new ToolCallAccumulator();
  acc.add([{ index: 0, id: 'c1', type: 'function', function: { name: 'get_emails', arguments: '{"a"' } }]);
  acc.add([{ index: 0, function: { arguments: ':1}' } }]);
  const [tc] = acc.toMessageToolCalls();
  assert.equal(tc.id, 'c1');
  assert.equal(tc.function.name, 'get_emails');
  assert.equal(tc.function.arguments, '{"a":1}');
});

test('accumulator: keeps parallel calls separate and in index order', () => {
  const acc = new ToolCallAccumulator();
  acc.add([{ index: 1, id: 'b', function: { name: 'two', arguments: '{' } }]);
  acc.add([{ index: 0, id: 'a', function: { name: 'one', arguments: '{' } }]);
  acc.add([{ index: 1, function: { arguments: '}' } }]);
  acc.add([{ index: 0, function: { arguments: '}' } }]);
  const calls = acc.toMessageToolCalls();
  assert.deepEqual(calls.map((c) => c.function.name), ['one', 'two']);
  assert.deepEqual(calls.map((c) => c.id), ['a', 'b']);
});

test('accumulator: repeated identical names are not duplicated', () => {
  const acc = new ToolCallAccumulator();
  acc.add([{ index: 0, id: 'c', function: { name: 'send_email', arguments: '' } }]);
  acc.add([{ index: 0, id: 'c', function: { name: 'send_email', arguments: '{}' } }]);
  assert.equal(acc.toMessageToolCalls()[0].function.name, 'send_email');
});

test('accumulator: genuinely fragmented names are joined', () => {
  const acc = new ToolCallAccumulator();
  acc.add([{ index: 0, id: 'c', function: { name: 'get_' } }]);
  acc.add([{ index: 0, function: { name: 'emails' } }]);
  assert.equal(acc.toMessageToolCalls()[0].function.name, 'get_emails');
});

test('accumulator: missing index falls back to the id, then to the last call', () => {
  const acc = new ToolCallAccumulator();
  acc.add([{ id: 'x', function: { name: 'f', arguments: '{"a"' } }]);
  acc.add([{ id: 'x', function: { arguments: ':1}' } }]);
  acc.add([{ function: { arguments: '' } }]);
  const calls = acc.toMessageToolCalls();
  assert.equal(calls.length, 1);
  assert.equal(calls[0].function.arguments, '{"a":1}');
});

test('accumulator: synthesises an id when the provider omits one', () => {
  const acc = new ToolCallAccumulator();
  acc.add([{ index: 0, function: { name: 'get_emails', arguments: '{}' } }]);
  assert.match(acc.toMessageToolCalls()[0].id, /get_emails/);
});

/* --------------------------------------------------------- argument parse */

test('parseToolArguments handles empty, valid, and broken JSON', () => {
  assert.deepEqual(parseToolArguments('').value, {});
  assert.deepEqual(parseToolArguments('  ').value, {});
  assert.deepEqual(parseToolArguments('{"take":3}').value, { take: 3 });
  assert.equal(parseToolArguments('{"take":').ok, false);
  assert.equal(parseToolArguments('[1,2]').ok, false);
});

/* ------------------------------------------------------- tool definitions */

test('toolsToOpenAI maps the contract shape and fills a missing schema', () => {
  const out = toolsToOpenAI([
    { name: 'a', description: 'does a', inputSchema: { type: 'object', properties: { x: { type: 'string' } } } },
    { name: 'b' },
    { description: 'nameless, dropped' }
  ]);
  assert.equal(out.length, 2);
  assert.equal(out[0].type, 'function');
  assert.equal(out[0].function.parameters.properties.x.type, 'string');
  assert.deepEqual(out[1].function.parameters, { type: 'object', properties: {} });
  assert.equal(out[1].function.description, 'b');
});

/* --------------------------------------------------------- system prompt */

test('buildSystemPrompt states the mode and the folder id format', () => {
  const ro = buildSystemPrompt({ username: 'me', emailAddress: 'me@example.com', baseUrl: 'https://mail.example.com', readOnly: true });
  assert.match(ro, /READ-ONLY/);
  assert.match(ro, /me@example\.com\/Inbox/);
  assert.match(ro, /mail\.example\.com/);
  const rw = buildSystemPrompt({ username: 'me', emailAddress: 'me@example.com', baseUrl: 'https://mail.example.com', readOnly: false });
  assert.match(rw, /READ-WRITE/);
  assert.ok(!rw.includes('not available to you'));
});

const acct = (over) => ({
  id: 'x', role: 'User', username: 'me', emailAddress: 'me@example.com', domain: 'example.com',
  baseUrl: 'https://mail.example.com', readOnly: true, handle: 'me@example.com', ...over
});
const NOW = new Date('2026-09-24T10:00:00Z');

test('buildSystemPrompt: a single-mailbox SessionResponse gets exactly the legacy prompt', () => {
  const legacy = buildSystemPrompt({ username: 'me', emailAddress: 'me@example.com', baseUrl: 'https://mail.example.com', readOnly: true }, { now: NOW });
  const current = buildSystemPrompt({ expiresAt: 'x', maxAccounts: 5, accounts: [acct()] }, { now: NOW });
  assert.equal(current, legacy);
  assert.ok(!/`account`/.test(current), 'no account argument talk for one mailbox');
  assert.ok(!/System admin/.test(current));
});

test('buildSystemPrompt: several accounts are all listed with handle, role, host and mode', () => {
  const p = buildSystemPrompt({
    accounts: [
      acct(),
      acct({ id: 'd', role: 'DomainAdmin', handle: 'boss@contoso.com', emailAddress: 'boss@contoso.com', domain: 'contoso.com', baseUrl: 'https://mx.contoso.com', readOnly: false }),
      acct({ id: 's', role: 'SysAdmin', handle: 'sysadmin:admin@mail.example.com', username: 'admin', emailAddress: '', domain: '' })
    ]
  }, { now: NOW });

  assert.match(p, /3 accounts/);
  assert.match(p, /`me@example\.com` — User on mail\.example\.com, READ-ONLY/);
  assert.match(p, /`boss@contoso\.com` — Domain admin of contoso\.com on mx\.contoso\.com, READ-WRITE/);
  assert.match(p, /`sysadmin:admin@mail\.example\.com` — System admin on mail\.example\.com, READ-ONLY/);
  // the account argument and what each role reaches
  assert.match(p, /`account` argument/);
  assert.match(p, /may leave it out/);
  assert.match(p, /System admin — server-wide/);
  assert.match(p, /Domain admin — its own mailbox/);
  assert.match(p, /has no mailbox/);
  // admin safety rules
  assert.match(p, /affects every domain and every user/);
  assert.match(p, /affects every user in that domain/);
  assert.match(p, /explicit confirmation in a separate turn/);
  assert.match(p, /Before ANY admin write/);
  // mailbox guidance still there, with a real mailbox as the example owner
  assert.match(p, /me@example\.com\/Inbox/);
  assert.match(p, /remove that account and add it again/);
});

test('buildSystemPrompt: a lone system admin gets role and safety rules but no account-choosing section', () => {
  const p = buildSystemPrompt({ accounts: [acct({ role: 'SysAdmin', handle: 'sysadmin:admin@mail.example.com', username: 'admin', emailAddress: '', readOnly: false })] }, { now: NOW });
  assert.match(p, /one signed-in account/);
  assert.match(p, /System admin on mail\.example\.com, READ-WRITE/);
  assert.match(p, /affects every domain/);
  assert.ok(!/# Choosing the account/.test(p));
  assert.ok(!/Inbox/.test(p), 'no mailbox folder guidance without a mailbox');
  assert.ok(!/every user in that domain/.test(p), 'no domain-admin rule without a domain admin');
});

test('buildSystemPrompt: switched-off tool groups are named for the model', () => {
  const p = buildSystemPrompt({ accounts: [acct()] }, { now: NOW, disabledCategories: ['Calendar', 'Notes'] });
  assert.match(p, /switched these tool groups off in the Tools menu.*Calendar, Notes/);
  const none = buildSystemPrompt({ accounts: [acct()] }, { now: NOW });
  assert.ok(!/Tools menu/.test(none));
});

test('sessionAccounts accepts both the SessionResponse and a legacy single account', () => {
  assert.equal(sessionAccounts({ accounts: [acct(), acct({ id: 'y' })] }).length, 2);
  const [legacy] = sessionAccounts({ username: 'me', emailAddress: 'me@example.com', baseUrl: 'b', readOnly: true });
  assert.equal(legacy.role, 'User');
  assert.equal(legacy.handle, 'me@example.com');
  assert.deepEqual(sessionAccounts(null), []);
});

/* --------------------------------------------------------- tool groups */

const TOOL_LIST = [
  { name: 'get_emails', category: 'Mail', scope: 'Mailbox', write: false },
  { name: 'send_email', category: 'Mail', scope: 'Mailbox', write: true },
  { name: 'get_calendar_events', category: 'Calendar', scope: 'Mailbox', write: false },
  { name: 'domain_list_users', category: 'Domain', scope: 'DomainAdmin', write: false },
  { name: 'get_spool_messages', category: 'Spool', scope: 'SysAdmin', write: false },
  { name: 'get_domains', category: 'Domains', scope: 'SysAdmin', write: false },
  { name: 'mystery', scope: 'Mailbox' }
];

test('toolGroups: only present categories, grouped by scope in display order, with counts', () => {
  const g = toolGroups(TOOL_LIST);
  assert.deepEqual(g.map((x) => x.label), ['Mailbox', 'Domain admin', 'System admin', 'Other']);
  assert.deepEqual(g[0].categories, [{ name: 'Mail', count: 2 }, { name: 'Calendar', count: 1 }]);
  // Domains comes before Spool in the fixed order, whatever the list order was
  assert.deepEqual(g[2].categories.map((c) => c.name), ['Domains', 'Spool']);
  assert.deepEqual(g[3].categories, [{ name: 'Other', count: 1 }]);
  assert.deepEqual(toolGroups([]), []);
});

test('filterToolsByCategory: drops switched-off categories, keeps everything else', () => {
  assert.equal(filterToolsByCategory(TOOL_LIST, new Set()).length, TOOL_LIST.length);
  const kept = filterToolsByCategory(TOOL_LIST, new Set(['Mail', 'Spool']));
  assert.deepEqual(kept.map((t) => t.name), ['get_calendar_events', 'domain_list_users', 'get_domains', 'mystery']);
  assert.deepEqual(filterToolsByCategory(TOOL_LIST, ['Other']).map((t) => t.name).includes('mystery'), false);
  // and what survives converts cleanly: the extra contract fields are ignored
  const openai = toolsToOpenAI(kept);
  assert.equal(openai.length, 4);
  assert.deepEqual(Object.keys(openai[0].function), ['name', 'description', 'parameters']);
});

test('toolsToOpenAI passes an injected account enum straight through', () => {
  const [t] = toolsToOpenAI([{
    name: 'get_domains', category: 'Domains', scope: 'SysAdmin', write: false,
    inputSchema: { type: 'object', properties: { account: { type: 'string', enum: ['sysadmin:a@h', 'sysadmin:b@h'] } }, required: ['account'] }
  }]);
  assert.deepEqual(t.function.parameters.properties.account.enum, ['sysadmin:a@h', 'sysadmin:b@h']);
  assert.deepEqual(t.function.parameters.required, ['account']);
});

/* ------------------------------------------------------ streamCompletion */

test('streamCompletion: streams text and reports the finish reason and usage', async () => {
  const seen = [];
  const r = await streamCompletion({
    apiKey: 'k', model: 'm', messages: [], tools: [],
    fetchImpl: fakeFetch(textStream),
    onContent: (c) => seen.push(c)
  });
  assert.equal(r.content, 'You have 3 unread messages.');
  assert.equal(r.finishReason, 'stop');
  assert.equal(r.usage.total_tokens, 108);
  assert.deepEqual(seen, ['You have ', '3 unread', ' messages.']);
  assert.equal(r.toolCalls.length, 0);
});

test('streamCompletion: reassembles a tool call whose arguments are fragmented', async () => {
  const r = await streamCompletion({
    apiKey: 'k', model: 'm', messages: [], tools: [{ type: 'function', function: { name: 'get_emails' } }],
    fetchImpl: fakeFetch(toolCallStream)
  });
  assert.equal(r.finishReason, 'tool_calls');
  assert.equal(r.content, '');
  assert.equal(r.toolCalls.length, 1);
  assert.deepEqual(JSON.parse(r.toolCalls[0].function.arguments), { folderId: 'me@example.com/Inbox', take: 3 });
});

test('streamCompletion: handles content and tool calls together, in parallel', async () => {
  const r = await streamCompletion({ apiKey: 'k', model: 'm', messages: [], fetchImpl: fakeFetch(parallelToolCallStream) });
  assert.equal(r.content, 'Let me check.');
  assert.deepEqual(r.toolCalls.map((t) => t.function.name), ['list_folder_info_by_type', 'get_emails']);
  assert.deepEqual(JSON.parse(r.toolCalls[1].function.arguments), { folderId: 'me@example.com/Inbox' });
});

test('streamCompletion: infers tool_calls when the provider omits finish_reason and index', async () => {
  const r = await streamCompletion({ apiKey: 'k', model: 'm', messages: [], fetchImpl: fakeFetch(noIndexToolCallStream) });
  assert.equal(r.finishReason, 'tool_calls');
  assert.equal(r.toolCalls.length, 1);
  assert.deepEqual(JSON.parse(r.toolCalls[0].function.arguments), { folderId: 'me@example.com/Inbox' });
});

test('streamCompletion: works when frames are split across arbitrary byte boundaries', async () => {
  const r = await streamCompletion({ apiKey: 'k', model: 'm', messages: [], fetchImpl: fakeFetch(null, { byteChunks: shred(parallelToolCallStream, 5) }) });
  assert.equal(r.content, 'Let me check.');
  assert.equal(r.toolCalls.length, 2);
});

test('streamCompletion: reports truncation', async () => {
  const r = await streamCompletion({ apiKey: 'k', model: 'm', messages: [], fetchImpl: fakeFetch(truncatedStream) });
  assert.equal(r.finishReason, 'length');
});

test('streamCompletion: a mid-stream error frame becomes an LlmError', async () => {
  await assert.rejects(
    streamCompletion({ apiKey: 'k', model: 'm', messages: [], fetchImpl: fakeFetch(midStreamErrorStream) }),
    (e) => e instanceof LlmError && e.status === 429
  );
});

test('streamCompletion: HTTP failures get actionable messages', async () => {
  for (const [status, re] of [[401, /key/i], [402, /credits/i], [429, /rate limit/i]]) {
    await assert.rejects(
      streamCompletion({ apiKey: 'k', model: 'm', messages: [], fetchImpl: fakeFetch([], { status }) }),
      (e) => e instanceof LlmError && e.status === status && re.test(e.message)
    );
  }
  assert.equal(describeHttpError(401).code, 'bad_key');
});

test('streamCompletion: refuses to run without a key', async () => {
  await assert.rejects(streamCompletion({ apiKey: '', model: 'm', messages: [] }), /key/i);
});

/* ------------------------------------------------------------- the loop */

function scriptedStream(turns) {
  let i = 0;
  return async ({ messages, onContent }) => {
    const t = turns[Math.min(i++, turns.length - 1)];
    if (t.content && onContent) onContent(t.content, t.content);
    return { content: t.content || '', reasoning: '', toolCalls: t.toolCalls || [], finishReason: t.finishReason || 'stop', usage: null, aborted: !!t.aborted, seenMessages: messages.length };
  };
}

const tc = (id, name, args) => ({ id, type: 'function', function: { name, arguments: JSON.stringify(args) } });

test('runTurn: executes tools, appends tool messages, and loops back', async () => {
  const messages = [{ role: 'system', content: 'sys' }, { role: 'user', content: 'what is unread?' }];
  const calls = [];
  const events = [];
  const r = await runTurn({
    messages,
    apiKey: 'k', model: 'm', tools: [],
    callTool: async (name, args) => { calls.push([name, args]); return { isError: false, content: '{"ok":true}' }; },
    streamImpl: scriptedStream([
      { toolCalls: [tc('c1', 'list_folder_info_by_type', { folderType: 'mail' })], finishReason: 'tool_calls' },
      { toolCalls: [tc('c2', 'get_emails', { folderId: 'me/Inbox', take: 3 })], finishReason: 'tool_calls' },
      { content: 'You have 3 unread.', finishReason: 'stop' }
    ]),
    ui: {
      onToolStart: (c) => events.push('start:' + c.name),
      onToolEnd: (c, res) => events.push('end:' + c.name + ':' + res.isError)
    }
  });

  assert.equal(r.rounds, 2);
  assert.equal(r.stopped, false);
  assert.deepEqual(calls.map((c) => c[0]), ['list_folder_info_by_type', 'get_emails']);
  assert.deepEqual(calls[1][1], { folderId: 'me/Inbox', take: 3 });
  assert.deepEqual(events, [
    'start:list_folder_info_by_type', 'end:list_folder_info_by_type:false',
    'start:get_emails', 'end:get_emails:false'
  ]);
  assert.deepEqual(messages.map((m) => m.role), ['system', 'user', 'assistant', 'tool', 'assistant', 'tool', 'assistant']);
  assert.equal(messages[3].tool_call_id, 'c1');
  assert.equal(messages.at(-1).content, 'You have 3 unread.');
});

test('runTurn: every tool_call id gets a matching tool message even on failure', async () => {
  const messages = [{ role: 'user', content: 'hi' }];
  await runTurn({
    messages, apiKey: 'k', model: 'm',
    callTool: async () => { throw new Error('network down'); },
    streamImpl: scriptedStream([
      { toolCalls: [tc('a', 'x', {}), tc('b', 'y', {})], finishReason: 'tool_calls' },
      { content: 'sorry', finishReason: 'stop' }
    ])
  });
  const ids = messages.filter((m) => m.role === 'tool').map((m) => m.tool_call_id);
  assert.deepEqual(ids, ['a', 'b']);
  assert.match(messages[2].content, /network down/);
});

test('runTurn: invalid JSON arguments are reported back to the model, not executed', async () => {
  const messages = [{ role: 'user', content: 'hi' }];
  let called = 0;
  await runTurn({
    messages, apiKey: 'k', model: 'm',
    callTool: async () => { called++; return { isError: false, content: 'x' }; },
    streamImpl: scriptedStream([
      { toolCalls: [{ id: 'a', type: 'function', function: { name: 'x', arguments: '{"broken"' } }], finishReason: 'tool_calls' },
      { content: 'ok', finishReason: 'stop' }
    ])
  });
  assert.equal(called, 0);
  assert.match(messages.find((m) => m.role === 'tool').content, /not valid JSON/);
});

test('runTurn: caps tool rounds and still closes the protocol', async () => {
  const messages = [{ role: 'user', content: 'loop forever' }];
  const notices = [];
  const r = await runTurn({
    messages, apiKey: 'k', model: 'm', maxToolRounds: 3,
    callTool: async () => ({ isError: false, content: '{}' }),
    streamImpl: scriptedStream([{ toolCalls: [tc('c', 'x', {})], finishReason: 'tool_calls' }]),
    ui: { onNotice: (t) => notices.push(t) }
  });
  assert.equal(r.rounds, 3);
  assert.match(notices.join(' '), /3 tool rounds/);
  assert.equal(messages.at(-1).role, 'tool');
  assert.match(messages.at(-1).content, /limit/i);
});

test('runTurn: a stopped stream keeps the partial text and drops half-built tool calls', async () => {
  const messages = [{ role: 'user', content: 'hi' }];
  const r = await runTurn({
    messages, apiKey: 'k', model: 'm',
    callTool: async () => ({ isError: false, content: '{}' }),
    streamImpl: scriptedStream([{ content: 'partial ans', toolCalls: [tc('c', 'x', {})], aborted: true }])
  });
  assert.equal(r.stopped, true);
  assert.equal(messages.length, 2);
  assert.equal(messages[1].content, 'partial ans');
  assert.ok(!messages[1].tool_calls);
});

test('runTurn: truncation and content filtering end the turn with a notice', async () => {
  for (const [finishReason, re] of [['length', /output limit/i], ['content_filter', /filtered/i]]) {
    const notices = [];
    await runTurn({
      messages: [{ role: 'user', content: 'hi' }], apiKey: 'k', model: 'm',
      callTool: async () => ({ isError: false, content: '' }),
      streamImpl: scriptedStream([{ content: 'partial', finishReason }]),
      ui: { onNotice: (t) => notices.push(t) }
    });
    assert.match(notices.join(' '), re);
  }
});

test('runTurn: an empty, tool-less assistant turn is not appended', async () => {
  const messages = [{ role: 'user', content: 'hi' }];
  await runTurn({
    messages, apiKey: 'k', model: 'm',
    callTool: async () => ({ isError: false, content: '' }),
    streamImpl: scriptedStream([{ content: '', finishReason: 'stop' }])
  });
  assert.equal(messages.length, 1);
});

/* ------------------------------------------------------------- clamping */

test('clampToolResult truncates very large results with an explanation', () => {
  const big = 'x'.repeat(100);
  const out = clampToolResult(big, 50);
  assert.ok(out.length < big.length + 200);
  assert.match(out, /truncated 50 more characters/);
  assert.equal(clampToolResult('small', 50), 'small');
});
