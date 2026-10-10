import test from 'node:test';
import assert from 'node:assert/strict';

import {
  ArtifactStore, unwrap, buildStub, isArtifactStub, ARTIFACT_THRESHOLD, ANALYZE_RESULT_TOOL, formatSize
} from '../../js/artifacts.js';
import { clampToolResult } from '../../js/llm.js';

const logLine = (i) => `${String(Math.floor(i / 3600) % 24).padStart(2, '0')}:${String(Math.floor(i / 60) % 60).padStart(2, '0')}:${String(i % 60).padStart(2, '0')}.000 [${i}] cmd: RCPT TO:<user${i}@example.com>`;
const log = (n) => Array.from({ length: n }, (_, i) => logLine(i)).join('\r\n') + '\r\n';

/** search_log_files' result shape (src/Tools.SysAdmin/LogSearchTools.cs ShapeResult). */
const logResult = (content) => JSON.stringify({
  success: true, logType: 'smtpLog', searchTerm: '', contains: null, matchedLines: 10, totalLines: 10,
  totalChars: content.length, offset: 0, returnedChars: content.length, hasMore: false, nextOffset: null,
  tail: true, isTruncated: false, content, note: null
});

test('threshold: at or under it the result is clamped text, over it a stub', () => {
  const store = new ArtifactStore();
  const under = store.capture('get_emails', {}, { isError: false, content: 'x'.repeat(ARTIFACT_THRESHOLD) }, clampToolResult);
  assert.equal(under.artifact, null);
  assert.equal(under.content.length, ARTIFACT_THRESHOLD);

  const over = store.capture('get_emails', {}, { isError: false, content: 'x'.repeat(ARTIFACT_THRESHOLD + 1) }, clampToolResult);
  assert.ok(over.artifact);
  assert.ok(isArtifactStub(over.content));
  assert.equal(JSON.parse(over.content).artifact, 'r1');
  assert.equal(store.size, 1);
});

test('errors and analyze_result itself are never kept', () => {
  const store = new ArtifactStore({ threshold: 10 });
  assert.equal(store.capture('x', {}, { isError: true, content: 'e'.repeat(100) }).artifact, null);
  assert.equal(store.capture(ANALYZE_RESULT_TOOL.name, {}, { isError: false, content: 'a'.repeat(100) }).artifact, null);
});

test('unwrap: search_log_files content becomes the text, the small fields become meta', () => {
  const content = log(800);
  const u = unwrap(logResult(content));
  assert.equal(u.kind, 'text');
  assert.equal(u.field, 'content');
  assert.equal(u.body, content);
  assert.equal(u.meta.logType, 'smtpLog');
  assert.equal(u.meta.totalChars, content.length);
  assert.equal(u.meta.hasMore, false);
  assert.equal('content' in u.meta, false);
});

test('unwrap: an array (bare or as the bulk field) becomes records; plain text stays text', () => {
  const users = Array.from({ length: 300 }, (_, i) => ({ userName: `u${i}`, status: 'active' }));
  const bare = unwrap(JSON.stringify(users));
  assert.equal(bare.kind, 'records');
  const wrapped = unwrap(JSON.stringify({ success: true, count: 300, users }));
  assert.equal(wrapped.kind, 'records');
  assert.equal(wrapped.field, 'users');
  assert.deepEqual(wrapped.meta, { success: true, count: 300 });
  assert.deepEqual(JSON.parse(wrapped.body), users);

  assert.deepEqual(unwrap('plain\ntext'), { kind: 'text', body: 'plain\ntext', field: null, meta: null });
  assert.equal(unwrap('{not json').kind, 'text');
  // An object with no bulk field is pretty-printed, one field per line.
  const flat = unwrap(JSON.stringify(Object.fromEntries(Array.from({ length: 50 }, (_, i) => [`k${i}`, `v${i}`]))));
  assert.equal(flat.kind, 'text');
  assert.ok(flat.body.split('\n').length > 50);
});

test('stub: bounded, deterministic, head and tail lines, a pointer to analyze_result', () => {
  const store = new ArtifactStore();
  const content = logResult(log(30000));   // ~1.9 MB
  const a = store.capture('search_log_files', { type: 'smtpLog', account: 'sysadmin:admin@mail.example.com' }, { isError: false, content });
  const stub = JSON.parse(a.content);
  assert.ok(a.content.length < 5000, `stub is ${a.content.length} chars`);
  assert.deepEqual(Object.keys(stub), ['artifact', 'tool', 'kind', 'chars', 'lines', 'field', 'meta', 'head', 'tail', 'note']);
  assert.equal(stub.lines, 30000);
  assert.equal(stub.head[0], logLine(0));
  assert.equal(stub.tail[stub.tail.length - 1], logLine(29999));
  assert.match(stub.note, /analyze_result/);
  assert.deepEqual(a.artifact.args, { type: 'smtpLog' }, 'the account handle is not kept');
  // Same artifact, same stub.
  assert.equal(buildStub(a.artifact), a.content);
});

test('LRU: the least recently used goes first when the total cap is reached', () => {
  const store = new ArtifactStore({ threshold: 10, maxTotal: 250, maxEach: 1000 });
  const put = (c) => store.capture('t', {}, { isError: false, content: c.repeat(100) }).artifact.handle;
  const r1 = put('a');
  const r2 = put('b');
  assert.ok(store.get(r1));   // r1 is now the most recent
  const r3 = put('c');        // evicts r2
  assert.equal(store.get(r2), null);
  assert.ok(store.get(r1));
  assert.ok(store.get(r3));
  assert.equal(store.issued(r2), true, 'an evicted handle reads as expired, not unknown');
  assert.equal(store.issued('r99'), false);
  assert.ok(store.total <= 250);
});

test('oversize: kept truncated at a line end, with the flag and the original size', () => {
  const store = new ArtifactStore({ threshold: 10, maxEach: 1000 });
  const body = log(100);
  const { artifact, content } = store.capture('search_log_files', {}, { isError: false, content: body });
  assert.equal(artifact.truncated, true);
  assert.ok(artifact.chars <= 1000);
  assert.ok(artifact.body.endsWith('\n'));
  assert.equal(artifact.originalChars, body.length);
  assert.match(JSON.parse(content).truncated, /only the first \d+ of \d+ chars/);

  const recs = new ArtifactStore({ threshold: 10, maxEach: 500 });
  const r = recs.capture('domain_list_users', {}, { isError: false, content: JSON.stringify(Array.from({ length: 100 }, (_, i) => ({ n: i }))) }).artifact;
  assert.equal(r.kind, 'records');
  assert.equal(r.truncated, true);
  assert.ok(JSON.parse(r.body).length < 100);
});

test('formatSize', () => {
  assert.equal(formatSize(3 * 1024 * 1024), '3.0 MB');
  assert.equal(formatSize(20480), '20 KB');
  assert.equal(formatSize(1500), '1,500 chars');
});
