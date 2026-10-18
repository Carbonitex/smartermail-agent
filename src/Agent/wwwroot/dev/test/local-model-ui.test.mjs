import test from 'node:test';
import assert from 'node:assert/strict';

import {
  LOCAL_MODEL_ID, LOCAL_MODEL_NAME, LOCAL_HUB_MODEL, isLocalModel, taskModelFor, withLocalModel,
  createProgressTracker, formatBytes, progressText, formatRoundStats,
  writeGate, gateRefusal, gateCallTool, GATE_DECLINED, GATE_CANCELLED
} from '../../js/local-model-ui.js';
import { DEFAULT_LOCAL_MODEL, createLocalStream } from '../../js/local-llm.js';
import { readFileSync } from 'node:fs';

test('the picker id names local-llm.js\'s default model and is never an OpenRouter id', () => {
  assert.equal(LOCAL_HUB_MODEL, DEFAULT_LOCAL_MODEL);
  assert.ok(isLocalModel(LOCAL_MODEL_ID));
  for (const id of ['anthropic/claude-haiku-5.5', 'openai/gpt-6-luna', 'meta-llama/llama-4:free', '', null, undefined, 42]) {
    assert.equal(isLocalModel(id), false, String(id));
  }
});

test('a task never starts from the local model', () => {
  assert.equal(taskModelFor(LOCAL_MODEL_ID, 'x/default'), 'x/default');
  assert.equal(taskModelFor('', 'x/default'), 'x/default');
  assert.equal(taskModelFor('openai/gpt-6-luna', 'x/default'), 'openai/gpt-6-luna');
});

test('withLocalModel puts the local option first, exactly once', () => {
  const list = withLocalModel([{ id: 'a/b', name: 'B' }, { id: LOCAL_MODEL_ID, name: 'dup' }, { id: 'c/d', name: 'D' }]);
  assert.deepEqual(list.map((m) => m.id), [LOCAL_MODEL_ID, 'a/b', 'c/d']);
  assert.equal(list[0].name, LOCAL_MODEL_NAME);
  assert.match(LOCAL_MODEL_NAME, /experimental/);
  assert.deepEqual(withLocalModel(null).map((m) => m.id), [LOCAL_MODEL_ID]);
});

test('progress is summed over files, unknown sizes count as zero, done completes a file', () => {
  const t = createProgressTracker();
  assert.equal(t.snapshot().fraction, null);
  t.update({ status: 'initiate', file: 'tokenizer.json' });
  t.update({ status: 'progress', file: 'tokenizer.json', loaded: 50, total: 100 });
  let s = t.update({ status: 'progress', file: 'onnx/model_q4f16.onnx_data', loaded: 100, total: 900 });
  assert.equal(s.loaded, 150);
  assert.equal(s.total, 1000);
  assert.equal(s.fraction, 0.15);
  assert.equal(s.active, 'onnx/model_q4f16.onnx_data');
  s = t.update({ status: 'done', file: 'tokenizer.json' });
  assert.equal(s.loaded, 200);
  // Aggregates and events without a file change nothing.
  assert.deepEqual(t.update({ status: 'progress_total', progress: 99, loaded: 1, total: 1 }), s);
  assert.deepEqual(t.update({ status: 'ready', model: 'x' }), s);
  assert.deepEqual(t.update(null), s);
  // A bogus loaded never exceeds the total.
  s = t.update({ status: 'progress', file: 'onnx/model_q4f16.onnx_data', loaded: 5000, total: 900 });
  assert.equal(s.fraction, 1);
});

test('progress text', () => {
  assert.equal(progressText(null), 'Starting…');
  assert.equal(progressText({ loaded: 0, total: 0, fraction: null }), 'Starting…');
  assert.equal(progressText({ loaded: 2048, total: 0, fraction: null }), 'Downloading 2.0 KB…');
  assert.equal(progressText({ loaded: 640 * 1024 ** 2, total: 1.5 * 1024 ** 3, fraction: 640 / 1536 }), 'Downloading 640 MB of 1.50 GB (41%)');
  assert.match(progressText({ loaded: 10, total: 10, fraction: 1 }), /GPU/);
  assert.equal(formatBytes(512), '512 B');
  assert.equal(formatBytes(50 * 1024 ** 2), '50.0 MB');
  assert.equal(formatBytes(-1), '?');
});

test('round stats from stream.last', () => {
  assert.equal(formatRoundStats(null), '');
  const last = {
    promptTokens: 1234, completionTokens: 210, tokensPerSecond: 18.26, ttftMs: 1400,
    selectedTools: ['a', 'b', 'c'], validations: [{ ok: true }, { ok: false, error: 'x' }], parseErrors: []
  };
  assert.equal(formatRoundStats(last, 59), '1.2k in · 210 out · 18.3 tok/s · first token 1.4 s · tools offered: 3 of 59 · 1 invalid call answered locally');
  assert.equal(formatRoundStats({ promptTokens: 10, completionTokens: 1, tokensPerSecond: null, ttftMs: null, selectedTools: [] }),
    '10 in · 1 out · tools offered: 0');
});

/* ------------------------------------------------------------ the write gate */

const fixture = (name) => JSON.parse(readFileSync(new URL(`../local-model/fixtures/${name}`, import.meta.url), 'utf8'));

test('the gate asks for every write, never for a read, and for names the tool list does not know', () => {
  assert.equal(writeGate({ name: 'get_emails', write: false, destructive: false }), null);
  assert.deepEqual(writeGate({ name: 'send_email', write: true, destructive: false }), { destructive: false });
  assert.deepEqual(writeGate({ name: 'delete_note', write: true, destructive: true }), { destructive: true });
  // Fail closed: no entry, or an entry without the flag, is gated.
  assert.deepEqual(writeGate(null), { destructive: false });
  assert.deepEqual(writeGate({ name: 'x' }), { destructive: false });
  // Against the real contract: exactly the write tools, destructive ones marked.
  for (const file of ['tools-user.json', 'tools-domainadmin.json', 'tools-sysadmin.json', 'tools-multi.json']) {
    for (const t of fixture(file)) {
      const g = writeGate(t);
      assert.equal(g !== null, t.write === true, `${file} ${t.name}`);
      if (g) assert.equal(g.destructive, t.destructive === true, `${file} ${t.name}`);
    }
  }
});

test('a refused call is an error the model is told not to retry', () => {
  assert.deepEqual(gateRefusal('deny'), { isError: true, content: GATE_DECLINED });
  assert.deepEqual(gateRefusal('cancel'), { isError: true, content: GATE_CANCELLED });
  assert.deepEqual(gateRefusal(undefined), { isError: true, content: GATE_DECLINED });
  assert.match(GATE_DECLINED, /declined.*Do not retry/);
  assert.match(GATE_CANCELLED, /not performed.*Do not retry/);
});

const LIST = new Map([
  ['get_emails', { name: 'get_emails', write: false, destructive: false }],
  ['send_email', { name: 'send_email', write: true, destructive: false }],
  ['delete_note', { name: 'delete_note', write: true, destructive: true }]
]);

function harness(answer) {
  const calls = [];
  const asked = [];
  const callTool = async (name, args) => { calls.push({ name, args }); return { isError: false, content: 'ok' }; };
  const confirm = (req) => { asked.push(req); return typeof answer === 'function' ? answer(req) : answer; };
  return { calls, asked, callTool, confirm, toolInfo: (n) => LIST.get(n) || null };
}

test('reads run without asking; an approved write runs with its arguments unchanged', async () => {
  const h = harness(Promise.resolve('approve'));
  const run = gateCallTool(h.callTool, h);
  assert.deepEqual(await run('get_emails', { take: 3 }), { isError: false, content: 'ok' });
  assert.equal(h.asked.length, 0);
  const args = { account: 'a@example.com', to: 'b@example.com', body: 'x'.repeat(5000) };
  assert.deepEqual(await run('send_email', args), { isError: false, content: 'ok' });
  assert.equal(h.asked.length, 1);
  assert.equal(h.asked[0].name, 'send_email');
  assert.equal(h.asked[0].destructive, false);
  assert.equal(h.asked[0].args, args);
  assert.deepEqual(h.calls.map((c) => c.name), ['get_emails', 'send_email']);
  assert.equal(h.calls[1].args, args);
});

test('deny, a throwing confirm or an unexpected answer makes no call', async () => {
  for (const answer of ['deny', 'maybe', undefined, () => { throw new Error('card broke'); }, () => Promise.reject(new Error('no'))]) {
    const h = harness(answer);
    const r = await gateCallTool(h.callTool, h)('delete_note', { id: 1 });
    assert.deepEqual(r, { isError: true, content: GATE_DECLINED }, String(answer));
    assert.equal(h.calls.length, 0);
  }
  const h = harness('deny');
  await gateCallTool(h.callTool, h)('delete_note', { id: 1 });
  assert.equal(h.asked[0].destructive, true);
  // An unknown name is asked about too (the guard normally answers it first).
  await gateCallTool(h.callTool, h)('mystery_tool', {});
  assert.equal(h.asked.length, 2);
  assert.equal(h.calls.length, 0);
});

test('Stop while a card is pending cancels: no call, even if confirm never settles', async () => {
  const ac = new AbortController();
  const h = harness(() => new Promise(() => {}));   // a card nobody answers
  const pending = gateCallTool(h.callTool, { ...h, signal: ac.signal })('send_email', { to: 'b@example.com' });
  await new Promise((r) => setTimeout(r, 5));
  assert.equal(h.asked.length, 1);
  assert.equal(h.asked[0].signal, ac.signal);
  ac.abort();
  assert.deepEqual(await pending, { isError: true, content: GATE_CANCELLED });
  assert.equal(h.calls.length, 0);
  // Already stopped: not even asked. Reads are untouched by the gate.
  const later = await gateCallTool(h.callTool, { ...h, signal: ac.signal })('send_email', {});
  assert.deepEqual(later, { isError: true, content: GATE_CANCELLED });
  assert.equal(h.asked.length, 1);
  await gateCallTool(h.callTool, { ...h, signal: ac.signal })('get_emails', {});
  assert.deepEqual(h.calls.map((c) => c.name), ['get_emails']);
});

test('an approval that lands after Stop does not run', async () => {
  const ac = new AbortController();
  let approve;
  const h = harness(() => new Promise((r) => { approve = r; }));
  const pending = gateCallTool(h.callTool, { ...h, signal: ac.signal })('send_email', {});
  await new Promise((r) => setTimeout(r, 5));
  ac.abort();
  approve('approve');
  assert.deepEqual(await pending, { isError: true, content: GATE_CANCELLED });
  assert.equal(h.calls.length, 0);
});

test('inside stream.guard (as turnOptions wires it): a declined call repeated unchanged is not asked again', async () => {
  const stream = createLocalStream({ getModel: async () => { throw new Error('not loaded'); } });
  const h = harness('deny');
  const { callTool } = stream.guard({ callTool: gateCallTool(h.callTool, h) });
  const first = await callTool('send_email', { to: 'b@example.com' });
  assert.equal(first.content, GATE_DECLINED);
  const again = await callTool('send_email', { to: 'b@example.com' });
  assert.equal(again.isError, true);
  assert.match(again.content, /already failed in this turn: The user declined/);
  assert.equal(h.asked.length, 1);
  assert.equal(h.calls.length, 0);
});
