import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { Worker } from 'node:worker_threads';

import {
  prepare, runOp, splitLines, compile, groupNames, lineTime, detectFormat, OUTPUT_CAP, OPERATOR_TOOLS, OPERATOR_NAMES
} from '../../js/artifact-ops.js';
import { handleMessage } from '../../js/artifact-worker.js';
import { createWorkerRunner } from '../../js/subagent.js';
import { ArtifactStore } from '../../js/artifacts.js';

// Shared with the C# port (tests/Agent.Tests/ArtifactOperatorTests.cs): same inputs, same expected output.
const FIXTURE = fileURLToPath(new URL('../../../../../tests/Agent.Tests/Fixtures/artifact-ops.json', import.meta.url));
const fixture = JSON.parse(readFileSync(FIXTURE, 'utf8'));
const datasets = {
  text: prepare('text', fixture.text, fixture.meta),
  records: prepare('records', fixture.records, fixture.meta)
};

function run(c) {
  try {
    return runOp(datasets[c.data], c.op, c.args);
  } catch (e) {
    return `Error: ${e.message}`;
  }
}

/** search_log_files' shape around the fixture log: the stub both stores build for it must match. */
const stubInput = () => JSON.stringify({
  success: true, logType: 'smtpLog', searchTerm: '', totalChars: fixture.text.length, hasMore: false, nextOffset: null,
  tail: true, content: fixture.text, note: null
});
const stubOf = () => new ArtifactStore({ threshold: 1000 }).capture('search_log_files', { type: 'smtpLog' }, { isError: false, content: stubInput() }).content;

if (process.env.UPDATE_ARTIFACT_FIXTURE === '1') {
  for (const c of fixture.cases) c.expected = run(c);
  fixture.stub = { content: stubInput(), expected: stubOf().replace('in this browser tab', 'for this run') };
  writeFileSync(FIXTURE, JSON.stringify(fixture, null, 2) + '\n');
}

test('fixture: the stub (the C# port builds the same one, "for this run")', () => {
  assert.equal(fixture.stub.content, stubInput());
  assert.equal(stubOf().replace('in this browser tab', 'for this run'), fixture.stub.expected);
});

for (const c of fixture.cases) {
  test(`fixture: ${c.name}`, () => {
    assert.equal(run(c), c.expected);
  });
}

test('fixture: every output fits the cap, and a capped one says what it left out', () => {
  for (const c of fixture.cases) assert.ok(c.expected.length <= OUTPUT_CAP + 120, c.name);
  const capped = fixture.cases.find((c) => c.name === 'grep-cap').expected;
  assert.match(capped, /^\d+ matching lines of \d+/);
  assert.match(capped, /\n… \d+ more matching lines not shown \(output cap 8000 chars\)$/);
});

test('splitLines: CRLF, no empty last line', () => {
  assert.deepEqual(splitLines('a\r\nb\r\n'), ['a', 'b']);
  assert.deepEqual(splitLines('a\n\nb'), ['a', '', 'b']);
  assert.deepEqual(splitLines(''), []);
});

test('grep: counts every match, shows line numbers, honours limit', () => {
  const out = runOp(datasets.text, 'artifact_grep', { pattern: 'rsp: 550', limit: 2 });
  const total = fixture.text.split('\n').filter((l) => l.includes('rsp: 550')).length;
  assert.match(out, new RegExp(`^${total} matching lines of `));
  assert.equal((out.match(/^#\d+: /gm) || []).length, 2);
  assert.match(out, new RegExp(`… ${total - 2} more matching lines not shown \\(limit 2\\)$`));
});

test('grep: context lines are marked with "-", groups separated by "--"', () => {
  const out = runOp(datasets.text, 'artifact_grep', { pattern: 'rsp: 451', context: 1, limit: 2 });
  assert.match(out, /\n#\d+- /);
  assert.match(out, /\n--\n/);
});

test('count by capture group: sorted by count then value', () => {
  const out = runOp(datasets.text, 'artifact_count', { pattern: 'rsp: (\\d{3})', by: '1' });
  const rows = out.split('\n').slice(1).map((l) => l.split('\t'));
  for (let i = 1; i < rows.length; i++) assert.ok(Number(rows[i - 1][0]) >= Number(rows[i][0]));
  assert.deepEqual(rows.map((r) => r[1]).sort(), ['220', '250', '451', '550']);
});

test('between: wraps across midnight and gives untimed lines the time above', () => {
  const out = runOp(datasets.text, 'artifact_between', { start: '23:59:58', end: '00:00:01' });
  assert.match(out, /^\d+ lines between 23:59:58 and 00:00:01 of /);
  assert.equal(lineTime('23:59:58.010 [1] x'), 86398);
  assert.equal(lineTime('2026-10-09T07:05:00Z x'), 7 * 3600 + 300);
  assert.equal(lineTime('    continuation'), null);
});

test('invalid patterns and unknown operators are model-readable errors', () => {
  assert.throws(() => runOp(datasets.text, 'artifact_grep', { pattern: '(' }), /^OpError: Invalid pattern/);
  assert.throws(() => compile('x'.repeat(2000)), /longer than 1000/);
  assert.throws(() => runOp(datasets.text, 'artifact_exec', {}), /Unknown operator artifact_exec/);
  assert.equal(String(compile('a', 'gyimz').flags), 'im');
});

test('group names come from the pattern text, in order', () => {
  assert.deepEqual(groupNames('(?<a>x)(y)(?<b>z)(?<a>w)'), ['a', 'b']);
  assert.deepEqual(groupNames('(?:x)(?=y)'), []);
});

test('detectFormat and the tool list', () => {
  assert.match(detectFormat(datasets.text), /^SmarterMail log/);
  assert.equal(detectFormat(datasets.records), 'JSON records, one per line');
  assert.equal(detectFormat(prepare('text', 'hello\nworld')), 'plain text');
  assert.deepEqual(OPERATOR_NAMES, [...OPERATOR_NAMES].sort(), 'tools are sorted by name (a stable prefix)');
  for (const t of OPERATOR_TOOLS) assert.equal(t.parameters.type, 'object');
});

test('worker message handler: load, then operators', () => {
  assert.equal(handleMessage({ type: 'op', id: 1, op: 'artifact_info' }).ok, false);
  assert.equal(handleMessage({ type: 'load', id: 'l', kind: 'text', body: 'a\nb\n', meta: { handle: 'r9', tool: 't' } }).ok, true);
  const r = handleMessage({ type: 'op', id: 2, op: 'artifact_slice', args: { from: 2 } });
  assert.deepEqual(r, { id: 2, ok: true, text: 'lines 2-2 of 2\n#2: b' });
  assert.match(handleMessage({ type: 'op', id: 3, op: 'artifact_grep', args: { pattern: '[' } }).error, /Invalid pattern/);
});

/* ------------------------------------------------- worker timeout (threads) */

const SHIM = `
  const { parentPort } = require('node:worker_threads');
  import(${JSON.stringify(new URL('../../js/artifact-worker.js', import.meta.url).href)}).then(({ handleMessage }) => {
    parentPort.on('message', (m) => parentPort.postMessage(handleMessage(m)));
  });
`;

const threadWorker = () => new Worker(SHIM, { eval: true });

test('worker runner: a catastrophic pattern is stopped and the next call still works', async () => {
  const artifact = { handle: 'r1', tool: 't', kind: 'text', body: 'a'.repeat(40) + '!\nok line\n', truncated: false, originalChars: 0 };
  let created = 0;
  const runner = createWorkerRunner(artifact, { timeoutMs: 600, createWorker: () => { created++; return threadWorker(); } });
  try {
    const started = Date.now();
    const slow = await runner.run('artifact_grep', { pattern: '^(a+)+$' });
    assert.equal(slow.ok, false);
    assert.match(slow.error, /took longer than 0.6 s/);
    assert.ok(Date.now() - started < 5000);

    const fine = await runner.run('artifact_grep', { pattern: 'ok' });
    assert.equal(fine.ok, true, fine.error);
    assert.match(fine.text, /^1 matching lines of 2\n#2: ok line$/);
    assert.equal(created, 2, 'a fresh worker after the timeout');
  } finally {
    runner.close();
  }
});
