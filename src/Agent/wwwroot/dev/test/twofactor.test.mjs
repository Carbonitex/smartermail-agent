/**
 * twofactor.test.mjs — the two-factor login step, from both ends:
 *
 *  1. js/api.js parsing of the three login outcomes (session, challenge,
 *     coded failure) and of the three /auth/two-factor outcomes, against a
 *     fake fetch;
 *  2. dev/stub-server.mjs's challenge state machine, against a real stub
 *     spawned on a random port.
 */

import test from 'node:test';
import assert from 'node:assert/strict';

import { startStub, post, realFetch } from './stub.mjs';
import { ApiError, login, twoFactor } from '../../js/api.js';

/* --------------------------------------------------------- fake browser */

// api.js derives every URL from location.pathname and uses the global fetch.
globalThis.location = { pathname: '/mail-agent/', hostname: 'localhost', search: '' };

/** Install a fetch that answers once with `status` / `body`, and record the call. */
function fakeFetch(status, body, headers = {}) {
  const calls = [];
  globalThis.fetch = async (url, init) => {
    calls.push({ url, init, body: init && init.body ? JSON.parse(init.body) : null });
    return {
      ok: status >= 200 && status < 300,
      status,
      headers: new Map(Object.entries(headers)),
      text: async () => (body === undefined ? '' : JSON.stringify(body))
    };
  };
  return calls;
}

const CREDS = { hostname: 'mail.example.com', email: 'me@example.com', password: 'pw' };

const account = (over = {}) => ({
  id: 'a1', handle: 'me@example.com', role: 'User', username: 'me', emailAddress: 'me@example.com',
  domain: 'example.com', baseUrl: 'https://mail.example.com', readOnly: true, ...over
});
const sessionBody = (...accounts) => ({ expiresAt: '2026-09-12T12:00:00Z', maxAccounts: 5, accounts });

/* ------------------------------------------------- api.js: login outcomes */

test('login() returns a normal session body unchanged', async () => {
  const calls = fakeFetch(200, sessionBody(account()));
  const s = await login(CREDS);
  assert.equal(s.accounts[0].username, 'me');
  assert.equal(s.accounts[0].readOnly, true);
  assert.equal(s.maxAccounts, 5);
  assert.equal(s.twoFactorRequired, undefined);
  assert.equal(calls[0].url, '/mail-agent/api/auth/login');
  assert.deepEqual(calls[0].body, { ...CREDS, readOnly: true });
});

test('login() returns the two-factor challenge body just as happily', async () => {
  fakeFetch(200, {
    twoFactorRequired: true,
    challengeId: 'chg_abc',
    method: 'rfc6238',
    emailAddress: '2fa@example.com',
    expiresAt: '2026-09-12T12:05:00Z'
  });
  const r = await login(CREDS);
  assert.equal(r.twoFactorRequired, true);
  assert.equal(r.challengeId, 'chg_abc');
  assert.equal(r.method, 'rfc6238');
  assert.equal(r.emailAddress, '2fa@example.com');
  // No session fields: the caller must not mistake this for a signed-in session.
  assert.equal(r.accounts, undefined);
});

test('login() 401 surfaces error text and code', async () => {
  fakeFetch(401, { error: 'The username or password is incorrect.', code: 'USERNAME_OR_PASSWORD_INCORRECT' });
  const err = await login(CREDS).then(() => null, (e) => e);
  assert.ok(err instanceof ApiError);
  assert.equal(err.status, 401);
  assert.equal(err.message, 'The username or password is incorrect.');
  assert.equal(err.code, 'USERNAME_OR_PASSWORD_INCORRECT');
  assert.equal(err.attemptsLeft, null);
});

test('login() 403 keeps the server wording verbatim for the webmail hint', async () => {
  const text = 'Your SmarterMail password must be changed before you can sign in.';
  fakeFetch(403, { error: text, code: 'CHANGE_PASSWORD_NEEDED' });
  const err = await login(CREDS).then(() => null, (e) => e);
  assert.equal(err.status, 403);
  assert.equal(err.message, text);
  assert.equal(err.code, 'CHANGE_PASSWORD_NEEDED');
  assert.equal(err.body.error, text);
});

for (const code of ['PASSWORD_EXPIRED', 'TWO_FACTOR_SETUP_REQUIRED', 'APP_PASSWORD_REQUIRED']) {
  test(`login() 403 ${code} carries its code through`, async () => {
    fakeFetch(403, { error: 'Finish this in webmail.', code });
    const err = await login(CREDS).then(() => null, (e) => e);
    assert.equal(err.status, 403);
    assert.equal(err.code, code);
  });
}

test('a coded-less error body still throws a usable ApiError', async () => {
  fakeFetch(401, { error: 'nope' });
  const err = await login(CREDS).then(() => null, (e) => e);
  assert.equal(err.code, null);
  assert.equal(err.message, 'nope');
});

/* ------------------------------------------- api.js: two-factor outcomes */

test('twoFactor() posts the challenge id and code, returns the session', async () => {
  const calls = fakeFetch(200, sessionBody(account({ handle: '2fa@example.com', username: '2fa', emailAddress: '2fa@example.com', readOnly: false })));
  const s = await twoFactor('chg_abc', '123456');
  assert.equal(calls[0].url, '/mail-agent/api/auth/two-factor');
  assert.equal(calls[0].init.method, 'POST');
  assert.deepEqual(calls[0].body, { challengeId: 'chg_abc', code: '123456' });
  assert.equal(s.accounts[0].emailAddress, '2fa@example.com');
  assert.equal(s.accounts[0].readOnly, false);
});

test('twoFactor() 401 exposes attemptsLeft', async () => {
  fakeFetch(401, { error: 'That code is not correct.', code: 'INVALID_TWO_FACTOR_CODE', attemptsLeft: 3 });
  const err = await twoFactor('chg_abc', '000000').then(() => null, (e) => e);
  assert.equal(err.status, 401);
  assert.equal(err.code, 'INVALID_TWO_FACTOR_CODE');
  assert.equal(err.attemptsLeft, 3);
});

test('twoFactor() 401 with attemptsLeft 0 still reads as a number, not null', async () => {
  fakeFetch(401, { error: 'no', code: 'INVALID_TWO_FACTOR_CODE', attemptsLeft: 0 });
  const err = await twoFactor('chg_abc', '000000').then(() => null, (e) => e);
  assert.equal(err.attemptsLeft, 0);
});

test('twoFactor() 410 is a CHALLENGE_EXPIRED with no attempt count', async () => {
  fakeFetch(410, { error: 'That verification request has expired.', code: 'CHALLENGE_EXPIRED' });
  const err = await twoFactor('chg_abc', '123456').then(() => null, (e) => e);
  assert.equal(err.status, 410);
  assert.equal(err.code, 'CHALLENGE_EXPIRED');
  assert.equal(err.attemptsLeft, null);
});

test('twoFactor() 429 comes back as a rate-limit ApiError', async () => {
  fakeFetch(429, { error: 'Too many attempts.', code: 'RATE_LIMITED' });
  const err = await twoFactor('chg_abc', '123456').then(() => null, (e) => e);
  assert.equal(err.status, 429);
  assert.equal(err.code, 'RATE_LIMITED');
});

test('login() 429 HOST_THROTTLED keeps the server wording and the wait', async () => {
  const error = 'Too many failed sign-ins to this mail server from this service. Try again in 12 minutes.';
  fakeFetch(429, { error, code: 'HOST_THROTTLED', retryAfterSeconds: 700 }, { 'Retry-After': '700' });
  const err = await login(CREDS).then(() => null, (e) => e);
  assert.ok(err instanceof ApiError);
  assert.equal(err.status, 429);
  assert.equal(err.code, 'HOST_THROTTLED');
  assert.equal(err.message, error);
  assert.equal(err.body.retryAfterSeconds, 700);
});

/* ------------------------------------------- the stub's state machine ---- */

const doLogin = (base, email, extra = {}) =>
  post(base, '/api/auth/login', { hostname: 'mail.example.com', email, password: 'pw', readOnly: true, ...extra });

test('stub: a plain email logs straight in with a cookie', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());

  const r = await doLogin(stub.base, 'me@example.com');
  assert.equal(r.status, 200);
  assert.equal(r.body.twoFactorRequired, undefined);
  assert.equal(r.body.accounts.length, 1);
  assert.equal(r.body.accounts[0].emailAddress, 'me@example.com');
  assert.equal(r.body.accounts[0].handle, 'me@example.com');
  assert.equal(r.body.accounts[0].role, 'User');
  assert.match(r.setCookie || '', /sma_session=/);
});

test('stub: 2fa@ needs a challenge and sets no cookie', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());

  const r = await doLogin(stub.base, '2fa@example.com');
  assert.equal(r.status, 200);
  assert.equal(r.body.twoFactorRequired, true);
  assert.equal(r.body.method, 'rfc6238');
  assert.equal(r.body.emailAddress, '2fa@example.com');
  assert.ok(r.body.challengeId);
  assert.ok(Date.parse(r.body.expiresAt) > Date.now());
  assert.equal(r.setCookie, null);
  // No session was created: the cookie-less state really is unauthenticated.
  const me = await realFetch(stub.base + '/api/auth/session');
  assert.equal(me.status, 401);
});

test('stub: a local part containing "mail" uses the email method', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());

  assert.equal((await doLogin(stub.base, '2famail@example.com')).body.method, 'email');
  assert.equal((await doLogin(stub.base, '2fa.mail@example.com')).body.method, 'email');
  assert.equal((await doLogin(stub.base, '2fatotp@example.com')).body.method, 'rfc6238');
  // "mail" in the domain must not count.
  assert.equal((await doLogin(stub.base, '2fa@mail.example.com')).body.method, 'rfc6238');
});

test('stub: 123456 completes the challenge and issues the session', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());

  const c = await doLogin(stub.base, '2fa@example.com', { readOnly: false });
  const r = await post(stub.base, '/api/auth/two-factor', { challengeId: c.body.challengeId, code: '123456' });
  assert.equal(r.status, 200);
  const [a] = r.body.accounts;
  assert.equal(a.emailAddress, '2fa@example.com');
  assert.equal(a.username, '2fa');
  assert.equal(a.baseUrl, 'https://mail.example.com');
  assert.equal(a.readOnly, false);                 // the login's readOnly carries over
  assert.match(r.setCookie || '', /sma_session=/);

  // The cookie really works.
  const sid = /sma_session=([^;]+)/.exec(r.setCookie)[1];
  const me = await realFetch(stub.base + '/api/auth/session', { headers: { Cookie: `sma_session=${sid}` } });
  assert.equal(me.status, 200);
  assert.equal((await me.json()).accounts[0].emailAddress, '2fa@example.com');
});

test('stub: spaces in the code are ignored', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());

  const c = await doLogin(stub.base, '2fa@example.com');
  const r = await post(stub.base, '/api/auth/two-factor', { challengeId: c.body.challengeId, code: ' 123 456 ' });
  assert.equal(r.status, 200);
});

test('stub: wrong codes count down from 5 and then burn the challenge', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());

  const c = await doLogin(stub.base, '2fa@example.com');
  const id = c.body.challengeId;

  for (const expected of [4, 3, 2, 1]) {
    const r = await post(stub.base, '/api/auth/two-factor', { challengeId: id, code: '000000' });
    assert.equal(r.status, 401);
    assert.equal(r.body.code, 'INVALID_TWO_FACTOR_CODE');
    assert.equal(r.body.attemptsLeft, expected);
  }

  const fifth = await post(stub.base, '/api/auth/two-factor', { challengeId: id, code: '000000' });
  assert.equal(fifth.status, 410);
  assert.equal(fifth.body.code, 'CHALLENGE_EXPIRED');

  // Even the right code cannot revive it.
  const after = await post(stub.base, '/api/auth/two-factor', { challengeId: id, code: '123456' });
  assert.equal(after.status, 410);
  assert.equal(after.body.code, 'CHALLENGE_EXPIRED');
});

test('stub: a challenge is single use — replaying a success is 410', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());

  const c = await doLogin(stub.base, '2fa@example.com');
  assert.equal((await post(stub.base, '/api/auth/two-factor', { challengeId: c.body.challengeId, code: '123456' })).status, 200);

  const again = await post(stub.base, '/api/auth/two-factor', { challengeId: c.body.challengeId, code: '123456' });
  assert.equal(again.status, 410);
  assert.equal(again.body.code, 'CHALLENGE_EXPIRED');
});

test('stub: an unknown challenge id is 410, not 404', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());

  const r = await post(stub.base, '/api/auth/two-factor', { challengeId: 'nope', code: '123456' });
  assert.equal(r.status, 410);
  assert.equal(r.body.code, 'CHALLENGE_EXPIRED');
});

test('stub: a missing challengeId or code is a 400', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());

  assert.equal((await post(stub.base, '/api/auth/two-factor', { code: '123456' })).status, 400);
  assert.equal((await post(stub.base, '/api/auth/two-factor', { challengeId: 'x' })).status, 400);
});

test('stub: challenges expire on the clock (TWO_FACTOR_TTL_MS)', async (t) => {
  const stub = await startStub({ TWO_FACTOR_TTL_MS: '150' });
  t.after(() => stub.stop());

  const c = await doLogin(stub.base, '2fa@example.com');
  assert.ok(Date.parse(c.body.expiresAt) - Date.now() < 5000);

  await new Promise((r) => setTimeout(r, 250));
  const r = await post(stub.base, '/api/auth/two-factor', { challengeId: c.body.challengeId, code: '123456' });
  assert.equal(r.status, 410);
  assert.equal(r.body.code, 'CHALLENGE_EXPIRED');
});

test('stub: coded failure shapes for the login view', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());

  const bad = await post(stub.base, '/api/auth/login', { hostname: 'mail.example.com', email: 'me@example.com', password: 'bad' });
  assert.equal(bad.status, 401);
  assert.equal(bad.body.code, 'USERNAME_OR_PASSWORD_INCORRECT');
  assert.ok(bad.body.error);

  const expired = await doLogin(stub.base, 'expired@example.com');
  assert.equal(expired.status, 403);
  assert.equal(expired.body.code, 'CHANGE_PASSWORD_NEEDED');
  assert.match(expired.body.error, /webmail/i);
  assert.equal(expired.setCookie, null);

  for (const [email, code] of [
    ['stale@example.com', 'PASSWORD_EXPIRED'],
    ['setup2fa@example.com', 'TWO_FACTOR_SETUP_REQUIRED'],
    ['apppass@example.com', 'APP_PASSWORD_REQUIRED']
  ]) {
    const r = await doLogin(stub.base, email);
    assert.equal(r.status, 403, email);
    assert.equal(r.body.code, code);
  }

  const missing = await post(stub.base, '/api/auth/login', { hostname: 'mail.example.com' });
  assert.equal(missing.status, 400);
});
