/**
 * resume.test.mjs — "Remember me on this device", browser side:
 *
 *  1. js/resume.js: the saved record (versions only move forward, a stale
 *     clear never deletes a newer copy) and the passkey lock against a fake
 *     WebAuthn authenticator whose PRF is an HMAC, with the real WebCrypto;
 *  2. js/api.js: X-Resume-Version out on every request, newer versions back;
 *  3. dev/stub-server.mjs: enable → restart → resume → rotation, against a
 *     real stub on a random port.
 */

import test from 'node:test';
import assert from 'node:assert/strict';

import { startStub, realFetch } from './stub.mjs';
import * as resume from '../../js/resume.js';
import { trackResumeVersion, session as getSession, resume as postResume } from '../../js/api.js';

/* --------------------------------------------------------- fake browser */

globalThis.location = { pathname: '/mail-agent/', hostname: 'localhost', search: '' };

const memory = new Map();
globalThis.localStorage = {
  getItem: (k) => (memory.has(k) ? memory.get(k) : null),
  setItem: (k, v) => memory.set(k, String(v)),
  removeItem: (k) => memory.delete(k)
};

const b64 = (bytes) => Buffer.from(bytes).toString('base64url');

/**
 * A platform authenticator with the PRF extension: PRF(salt) = HMAC(secret, salt)
 * per credential. `prfAtCreate: false` makes create() only report `enabled`, as
 * some authenticators do, so the code has to follow up with get().
 */
function fakeAuthenticator({ prf = true, prfAtCreate = true } = {}) {
  const secrets = new Map();
  const hmac = async (secret, salt) => {
    const key = await crypto.subtle.importKey('raw', secret, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
    return crypto.subtle.sign('HMAC', key, salt);
  };
  const auth = {
    calls: [],
    secrets,
    async create({ publicKey }) {
      auth.calls.push(['create', publicKey]);
      const id = crypto.getRandomValues(new Uint8Array(16));
      const secret = crypto.getRandomValues(new Uint8Array(32));
      secrets.set(b64(id), secret);
      const results = prf && prfAtCreate ? { first: await hmac(secret, publicKey.extensions.prf.eval.first) } : undefined;
      return {
        rawId: id.buffer,
        getClientExtensionResults: () => (prf ? { prf: { enabled: true, ...(results ? { results } : {}) } } : {})
      };
    },
    async get({ publicKey }) {
      auth.calls.push(['get', publicKey]);
      const secret = secrets.get(b64(publicKey.allowCredentials[0].id));
      if (!secret) throw Object.assign(new Error('The operation either timed out or was not allowed.'), { name: 'NotAllowedError' });
      const first = await hmac(secret, publicKey.extensions.prf.eval.first);
      return { getClientExtensionResults: () => ({ prf: { results: { first } } }) };
    }
  };
  return auth;
}

function useAuthenticator(auth) {
  Object.defineProperty(globalThis, 'navigator', { value: { credentials: auth }, configurable: true, writable: true });
}

function reset() {
  memory.clear();
  resume.forgetKey();
}

const SAVED = (bundle, version) => ({ bundle, version, rememberedUntil: '2026-10-25T00:00:00Z' });

/* ------------------------------------------------------ the saved record */

test('store: save, version, and versions only move forward unless forced', async () => {
  reset();
  assert.equal(resume.store.version, 0);
  assert.equal(resume.store.has(), false);

  assert.equal(await resume.store.save(SAVED('bundle-5', 5)), true);
  assert.equal(resume.store.version, 5);
  assert.equal(await resume.store.open(), 'bundle-5');

  assert.equal(await resume.store.save(SAVED('bundle-4', 4)), false, 'an older copy never overwrites a newer one');
  assert.equal(await resume.store.save(SAVED('bundle-5b', 5)), false);
  assert.equal(await resume.store.open(), 'bundle-5');

  assert.equal(await resume.store.save(SAVED('bundle-1', 1), { force: true }), true, 'a new sign-in replaces it');
  assert.equal(await resume.store.open(), 'bundle-1');

  assert.equal(await resume.store.save({ bundle: '', version: 9 }), false);
  assert.equal(await resume.store.save({ bundle: 'x', version: 'nine' }), false);
});

test('store: clear(expected) leaves a copy another tab saved meanwhile', async () => {
  reset();
  await resume.store.save(SAVED('old', 1));
  const seen = resume.store.record;
  await resume.store.save(SAVED('newer', 2));   // "another tab"

  resume.store.clear(seen);
  assert.equal(await resume.store.open(), 'newer');

  resume.store.clear(resume.store.record);
  assert.equal(resume.store.has(), false);
});

test('store: garbage in localStorage reads as nothing saved', () => {
  reset();
  memory.set('sma.resume', '{not json');
  assert.equal(resume.store.has(), false);
  memory.set('sma.resume', JSON.stringify({ v: 1, locked: true, version: 3 }));
  assert.equal(resume.store.has(), false);
  assert.equal(resume.store.version, 0);
});

/* -------------------------------------------------------- passkey lock */

test('prfSupported() follows getClientCapabilities and is false without it', async () => {
  useAuthenticator(fakeAuthenticator());
  delete globalThis.PublicKeyCredential;
  assert.equal(await resume.prfSupported(), false);

  globalThis.PublicKeyCredential = { getClientCapabilities: async () => ({ 'extension:prf': true }) };
  assert.equal(await resume.prfSupported(), true);

  globalThis.PublicKeyCredential = { getClientCapabilities: async () => ({ 'extension:prf': false }) };
  assert.equal(await resume.prfSupported(), false);

  globalThis.PublicKeyCredential = { getClientCapabilities: async () => { throw new Error('nope'); } };
  assert.equal(await resume.prfSupported(), false);
  delete globalThis.PublicKeyCredential;
});

test('lock: the stored copy is encrypted, a reload needs the passkey, and updates re-encrypt silently', async () => {
  reset();
  const auth = fakeAuthenticator();
  useAuthenticator(auth);

  const lock = await resume.createLock();
  const [, options] = auth.calls[0];
  assert.equal(options.rp.id, 'localhost');
  assert.equal(options.authenticatorSelection.userVerification, 'required');
  assert.equal(options.authenticatorSelection.residentKey, 'preferred');
  assert.deepEqual([...options.extensions.prf.eval.first], [...resume.PRF_SALT]);
  assert.equal(resume.PRF_SALT.length, 32);

  assert.equal(await resume.store.save(SAVED('sealed-bundle-1', 10), { force: true, lock }), true);
  const record = resume.store.record;
  assert.equal(record.locked, true);
  assert.equal(record.credentialId, lock.credentialId);
  assert.equal(record.version, 10);
  assert.ok(!JSON.stringify(record).includes('sealed-bundle-1'), 'no plaintext bundle in storage');
  assert.equal(await resume.store.open(), 'sealed-bundle-1');   // key still in memory

  // A newer bundle while unlocked: re-encrypted with no prompt.
  const prompts = auth.calls.length;
  assert.equal(await resume.store.save(SAVED('sealed-bundle-2', 11)), true);
  assert.equal(auth.calls.length, prompts);
  assert.equal(resume.store.locked, true);

  // "Reload": the key is gone from memory.
  resume.forgetKey();
  assert.equal(resume.hasKey(), false);
  await assert.rejects(resume.store.open(), resume.LockedError);
  assert.equal(await resume.store.save(SAVED('sealed-bundle-3', 12)), false, 'locked and not unlocked: kept, never stored in the clear');
  assert.equal(resume.store.version, 11);

  assert.equal(await resume.unlock(), 'sealed-bundle-2');
  const [, getOptions] = auth.calls.at(-1);
  assert.equal(getOptions.allowCredentials[0].type, 'public-key');
  assert.equal(b64(getOptions.allowCredentials[0].id), lock.credentialId);
  assert.equal(getOptions.userVerification, 'required');
  assert.equal(resume.hasKey(), true);
  assert.equal(await resume.store.save(SAVED('sealed-bundle-3', 12)), true);
  assert.equal(await resume.store.open(), 'sealed-bundle-3');

  // Removing the lock: stored in the clear again, key dropped.
  await resume.store.save(SAVED('sealed-bundle-4', 13), { force: true, lock: null });
  assert.equal(resume.store.locked, false);
  assert.equal(resume.hasKey(), false);
  assert.equal(resume.store.record.bundle, 'sealed-bundle-4');
});

test('lock: an authenticator that only reports PRF on assertion gets a follow-up get()', async () => {
  reset();
  const auth = fakeAuthenticator({ prfAtCreate: false });
  useAuthenticator(auth);

  const lock = await resume.createLock();
  assert.deepEqual(auth.calls.map(([kind]) => kind), ['create', 'get']);
  await resume.store.save(SAVED('b', 1), { force: true, lock });
  resume.forgetKey();
  assert.equal(await resume.unlock(), 'b');
});

test('lock: no PRF means no lock, and the wrong passkey opens nothing', async () => {
  reset();
  useAuthenticator(fakeAuthenticator({ prf: false }));
  await assert.rejects(resume.createLock(), /PRF/);

  const auth = fakeAuthenticator();
  useAuthenticator(auth);
  const lock = await resume.createLock();
  await resume.store.save(SAVED('b', 1), { force: true, lock });
  resume.forgetKey();

  // Same credential id, different secret: the derived key cannot decrypt.
  auth.secrets.set(lock.credentialId, crypto.getRandomValues(new Uint8Array(32)));
  await assert.rejects(resume.unlock(), (e) => e.name === 'OperationError');
  assert.equal(resume.hasKey(), false, 'a key that failed to decrypt is not kept');

  // A cancelled prompt surfaces the browser's NotAllowedError; the copy stays.
  auth.secrets.clear();
  await assert.rejects(resume.unlock(), (e) => e.name === 'NotAllowedError');
  assert.equal(resume.store.has(), true);
});

/* ------------------------------------------------- api.js: the header */

test('api: every request says which version it holds; a newer one is reported', async () => {
  const sent = [];
  const announced = [];
  let reply = {};
  globalThis.fetch = async (url, init) => {
    sent.push({ url, headers: init.headers });
    return {
      ok: true,
      status: 200,
      headers: new Headers(reply),
      text: async () => JSON.stringify({ expiresAt: 'x', maxAccounts: 5, accounts: [] })
    };
  };
  let held = 0;
  trackResumeVersion(() => held, (v) => announced.push(v));

  await getSession();
  assert.equal(sent[0].headers['X-Resume-Version'], '0');
  assert.deepEqual(announced, []);

  held = 41;
  reply = { 'x-resume-version': '42' };
  await getSession();
  assert.equal(sent[1].headers['X-Resume-Version'], '41');
  assert.deepEqual(announced, [42]);

  // /auth/resume bodies carry the bundle themselves: no second fetch for them.
  await postResume('b');
  assert.deepEqual(announced, [42]);

  reply = { 'x-resume-version': 'soon' };
  await getSession();
  assert.deepEqual(announced, [42]);
  trackResumeVersion(null, null);
});

/* ------------------------------------------------------- against the stub */

async function req(base, method, p, { body, cookie, version, bearer } = {}) {
  const res = await realFetch(base + p, {
    method,
    headers: {
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...(cookie ? { Cookie: cookie } : {}),
      ...(bearer ? { Authorization: `Bearer ${bearer}` } : {}),
      ...(version === undefined ? {} : { 'X-Resume-Version': String(version) })
    },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = await res.text();
  const m = /sma_session=([^;]*)/.exec(res.headers.get('set-cookie') || '');
  return {
    status: res.status,
    body: text ? JSON.parse(text) : null,
    cookie: m && m[1] ? `sma_session=${m[1]}` : null,
    announced: res.headers.get('x-resume-version')
  };
}

test('stub: remember, restart, resume, rotate — and an old copy is refused', async () => {
  const stub = await startStub();
  try {
    const { base } = stub;
    const config = await req(base, 'GET', '/api/auth/resume/config');
    assert.deepEqual(config.body, { enabled: true, days: 30 });

    const login = await req(base, 'POST', '/api/auth/login',
      { body: { hostname: 'mail.example.com', email: 'me@example.com', password: 'pw', readOnly: true } });
    assert.equal(login.body.remembered, false);
    const cookie = login.cookie;

    assert.equal((await req(base, 'GET', '/api/auth/resume', { cookie })).status, 404);
    const on = await req(base, 'PUT', '/api/auth/resume', { cookie });
    assert.equal(on.status, 200);
    assert.ok(on.body.bundle && on.body.version > 0);
    assert.equal((await req(base, 'GET', '/api/auth/session', { cookie })).body.remembered, true);

    // A tool call rotates; its own response announces the newer version to the cookie only.
    const call = await req(base, 'POST', '/api/tools/call',
      { cookie, version: on.body.version, body: { name: 'get_emails', arguments: {} } });
    assert.ok(Number(call.announced) > on.body.version);
    const bearer = await req(base, 'GET', '/api/auth/session', { bearer: cookie.split('=')[1], version: 0 });
    assert.equal(bearer.announced, null);
    const fresh = await req(base, 'GET', '/api/auth/resume', { cookie });
    assert.equal(fresh.body.version, Number(call.announced));
    assert.notEqual(fresh.body.bundle, on.body.bundle);

    // Restart: the session is gone, the newest bundle brings it back.
    await req(base, 'POST', '/api/dev/restart');
    assert.equal((await req(base, 'GET', '/api/auth/session', { cookie })).status, 401);
    assert.equal((await req(base, 'POST', '/api/auth/resume', { body: { bundle: on.body.bundle } })).body.code, 'RESUME_EXPIRED',
      'the copy from before the rotation is dead');

    const back = await req(base, 'POST', '/api/auth/resume', { body: { bundle: fresh.body.bundle } });
    assert.equal(back.status, 200);
    assert.equal(back.body.accounts[0].handle, 'me@example.com');
    assert.equal(back.body.remembered, true);
    assert.deepEqual(back.body.skipped, []);
    assert.ok(back.cookie);
    assert.notEqual(back.body.bundle, fresh.body.bundle);
    assert.equal((await req(base, 'POST', '/api/auth/resume', { body: { bundle: fresh.body.bundle } })).status, 401,
      'each resume rotates: the copy just presented is dead now');

    // Logout revokes the chain.
    await req(base, 'POST', '/api/auth/logout', { cookie: back.cookie });
    assert.equal((await req(base, 'POST', '/api/auth/resume', { body: { bundle: back.body.bundle } })).body.code, 'RESUME_EXPIRED');
    assert.equal((await req(base, 'POST', '/api/auth/resume', { body: { bundle: 'nonsense' } })).body.code, 'RESUME_INVALID');
  } finally {
    await stub.stop();
  }
});

test('stub: RESUME=false switches the feature off', async () => {
  const stub = await startStub({ RESUME: 'false' });
  try {
    assert.deepEqual((await req(stub.base, 'GET', '/api/auth/resume/config')).body, { enabled: false, days: 0 });
    assert.equal((await req(stub.base, 'POST', '/api/auth/resume', { body: { bundle: 'x' } })).body.code, 'RESUME_DISABLED');
  } finally {
    await stub.stop();
  }
});
