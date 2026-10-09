/**
 * webauthn.js: a password manager's refused prompt falls through to the
 * browser's own navigator.credentials (from a fresh iframe), once.
 */
import test from 'node:test';
import assert from 'node:assert/strict';
import { createCredential, getCredential } from '../../js/webauthn.js';

const setNavigator = (credentials) =>
  Object.defineProperty(globalThis, 'navigator', { value: { credentials }, configurable: true, writable: true });

const NATIVE = (name) => `function ${name}() { [native code] }`;

/** A credentials object whose methods "look native" to the frame's toString. */
function credentials(log, label, { fail } = {}) {
  const make = (kind) => {
    const fn = async function (opts) {
      log.push(`${label}.${kind}`);
      if (fail) throw Object.assign(new Error(fail), { name: fail });
      return { kind, label, opts };
    };
    fn.native = NATIVE(kind);
    return fn;
  };
  return { create: make('create'), get: make('get') };
}

function setup({ page, frame }) {
  const log = [];
  let attached = 0;
  const frameCreds = frame === null ? null : credentials(log, 'frame', frame);
  const pageCreds = credentials(log, 'page', page);
  if (page.patched) for (const k of ['create', 'get']) delete pageCreds[k].native;
  if (frame?.patched) for (const k of ['create', 'get']) delete frameCreds[k].native;
  const toString = function () { return this.native || 'async function (opts) { /* extension */ }'; };
  setNavigator(pageCreds);
  globalThis.document = {
    body: { appendChild() { attached++; } },
    createElement: () => ({
      setAttribute() {},
      remove() { attached--; },
      contentWindow: frameCreds ? { navigator: { credentials: frameCreds }, Function: { prototype: { toString } } } : null
    })
  };
  return { log, attached: () => attached };
}

test('no extension: one call, a cancel is final', async () => {
  const env = setup({ page: { fail: 'NotAllowedError' }, frame: {} });
  await assert.rejects(createCredential({}), { name: 'NotAllowedError' });
  assert.deepEqual(env.log, ['page.create']);
  assert.equal(env.attached(), 0);
});

test('extension refuses: the browser\'s own create runs, and says so', async () => {
  const env = setup({ page: { patched: true, fail: 'NotAllowedError' }, frame: {} });
  const r = await createCredential({ x: 1 });
  assert.deepEqual(env.log, ['page.create', 'frame.create']);
  assert.equal(r.native, true);
  assert.deepEqual(r.credential.opts, { publicKey: { x: 1 } });
  assert.equal(env.attached(), 0);
});

test('extension succeeds: its credential is used, not native', async () => {
  const env = setup({ page: { patched: true }, frame: {} });
  const r = await createCredential({});
  assert.deepEqual(env.log, ['page.create']);
  assert.equal(r.native, false);
});

test('InvalidStateError is an answer, not a reason to retry', async () => {
  const env = setup({ page: { patched: true, fail: 'InvalidStateError' }, frame: {} });
  await assert.rejects(createCredential({}), { name: 'InvalidStateError' });
  assert.deepEqual(env.log, ['page.create']);
});

test('extension in the frame too: the original error stands', async () => {
  const env = setup({ page: { patched: true, fail: 'NotAllowedError' }, frame: { patched: true } });
  await assert.rejects(getCredential({}), { name: 'NotAllowedError' });
  assert.deepEqual(env.log, ['page.get']);
});

test('native: true skips the extension for a follow-up get', async () => {
  const env = setup({ page: { patched: true }, frame: {} });
  const r = await getCredential({}, { native: true });
  assert.deepEqual(env.log, ['frame.get']);
  assert.equal(r.native, true);
});

test('no DOM: plain navigator.credentials', async () => {
  setNavigator(credentials([], 'page'));
  delete globalThis.document;
  const r = await getCredential({});
  assert.equal(r.credential.label, 'page');
});
