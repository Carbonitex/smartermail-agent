/**
 * accounts.test.mjs — several accounts in one session, from both ends:
 *
 *  1. js/api.js: addAccount / removeAccount, callTool's `account` echo and the
 *     read-only refusal that names the account, the coded/uncoded 401 split;
 *  2. dev/stub-server.mjs: roles and handles, per-account tool lists with the
 *     injected `account` enum, account resolution on /tools/call, the cap,
 *     removal, and the add-account two-factor flow bound to its session.
 */

import test from 'node:test';
import assert from 'node:assert/strict';

import { startStub, call, post, cookieOf, creds, realFetch } from './stub.mjs';
import { ApiError, addAccount, removeAccount, callTool, onUnauthorized, session } from '../../js/api.js';

globalThis.location = { pathname: '/mail-agent/', hostname: 'localhost', search: '' };

function fakeFetch(status, body) {
  const calls = [];
  globalThis.fetch = async (url, init) => {
    calls.push({ url, init, body: init && init.body ? JSON.parse(init.body) : null });
    return {
      ok: status >= 200 && status < 300,
      status,
      text: async () => (body === undefined ? '' : JSON.stringify(body))
    };
  };
  return calls;
}

/* ------------------------------------------------------------ api.js */

test('addAccount() posts the login body to /api/accounts', async () => {
  const calls = fakeFetch(200, { expiresAt: 'x', maxAccounts: 5, accounts: [] });
  await addAccount({ hostname: 'mail.example.com', email: 'admin', password: 'pw', readOnly: false });
  assert.equal(calls[0].url, '/mail-agent/api/accounts');
  assert.equal(calls[0].init.method, 'POST');
  assert.deepEqual(calls[0].body, { hostname: 'mail.example.com', email: 'admin', password: 'pw', readOnly: false });
});

test('addAccount() 409 is an ACCOUNT_LIMIT ApiError', async () => {
  fakeFetch(409, { error: 'This chat already has 5 accounts.', code: 'ACCOUNT_LIMIT' });
  const err = await addAccount(creds('x@example.com')).then(() => null, (e) => e);
  assert.ok(err instanceof ApiError);
  assert.equal(err.status, 409);
  assert.equal(err.code, 'ACCOUNT_LIMIT');
});

test('removeAccount() DELETEs /api/accounts/{id} and returns null on 204', async () => {
  const calls = fakeFetch(204);
  assert.equal(await removeAccount('a/b c'), null);
  assert.equal(calls[0].url, '/mail-agent/api/accounts/a%2Fb%20c');
  assert.equal(calls[0].init.method, 'DELETE');
});

test('callTool() passes back the account the server ran as', async () => {
  fakeFetch(200, { isError: false, content: '{}', account: 'sysadmin:admin@mail.example.com' });
  const r = await callTool('get_domains', {});
  assert.deepEqual(r, { isError: false, content: '{}', account: 'sysadmin:admin@mail.example.com' });

  fakeFetch(200, { isError: false, content: '{}' });
  assert.equal((await callTool('get_emails', { account: 'me@example.com' })).account, 'me@example.com');
  assert.equal((await callTool('get_emails', {})).account, null);
});

test('callTool() 403 names the read-only account', async () => {
  fakeFetch(403, { error: '"delete_domain" changes data and the account "sysadmin:admin@h" is read-only.' });
  const r = await callTool('delete_domain', { domain: 'x', account: 'sysadmin:admin@h' });
  assert.equal(r.isError, true);
  assert.match(r.content, /sysadmin:admin@h/);
  assert.match(r.content, /add it again with "Allow changes" ticked/);

  // A server message that does not name the account still gets it from the arguments.
  fakeFetch(403, { error: 'read-only session' });
  const r2 = await callTool('send_email', { account: 'me@example.com' });
  assert.match(r2.content, /"send_email" changes data and the account "me@example\.com" is read-only/);
});

test('onUnauthorized gets the error, so a coded 401 is distinguishable from a dead session', async () => {
  const seen = [];
  onUnauthorized((err) => seen.push(err.code));
  fakeFetch(401, { error: 'bad', code: 'USERNAME_OR_PASSWORD_INCORRECT' });
  await addAccount(creds('x@example.com')).catch(() => {});
  fakeFetch(401);
  await session().catch(() => {});
  onUnauthorized(null);
  assert.deepEqual(seen, ['USERNAME_OR_PASSWORD_INCORRECT', null]);
});

/* ------------------------------------------------------------ the stub */

async function signedIn(stub, email = 'me@example.com', extra = {}) {
  const r = await post(stub.base, '/api/auth/login', creds(email, extra));
  assert.equal(r.status, 200);
  return { cookie: cookieOf(r.setCookie), body: r.body };
}

const tools = async (stub, cookie) => (await call(stub.base, 'GET', '/api/tools', undefined, cookie)).body;
const byName = (list) => Object.fromEntries(list.map((t) => [t.name, t]));

test('stub: roles and handles follow the login', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());
  const { cookie } = await signedIn(stub);

  const sys = await post(stub.base, '/api/accounts', creds('admin'), cookie);
  assert.equal(sys.status, 200);
  const dom = await post(stub.base, '/api/accounts', creds('domainadmin@contoso.com', { hostname: 'mx.contoso.com' }), cookie);
  const accounts = dom.body.accounts;
  assert.deepEqual(accounts.map((a) => [a.role, a.handle]), [
    ['User', 'me@example.com'],
    ['SysAdmin', 'sysadmin:admin@mail.example.com'],
    ['DomainAdmin', 'domainadmin@contoso.com']
  ]);
  assert.equal(dom.body.maxAccounts, 5);
  assert.equal(accounts[2].domain, 'contoso.com');
  for (const a of accounts) assert.ok(a.id && a.baseUrl && typeof a.readOnly === 'boolean');

  // Same email on another server: the handle gets "#host" appended.
  const clash = await post(stub.base, '/api/accounts', creds('me@example.com', { hostname: 'other.example.net' }), cookie);
  assert.equal(clash.body.accounts.at(-1).handle, 'me@example.com#other.example.net');

  // Same (server, login) again replaces rather than duplicates.
  const again = await post(stub.base, '/api/accounts', creds('admin', { readOnly: false }), cookie);
  assert.equal(again.body.accounts.length, 4);
  assert.equal(again.body.accounts.find((a) => a.role === 'SysAdmin').readOnly, false);
});

test('stub: a single-account session has no account property at all', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());
  const { cookie } = await signedIn(stub);
  const list = await tools(stub, cookie);
  assert.deepEqual(list.map((x) => x.name), ['list_folder_info_by_type', 'get_emails']);   // read-only: no send_email
  for (const tool of list) {
    assert.equal(tool.inputSchema.properties.account, undefined);
    assert.ok(tool.category && tool.scope && tool.write === false);
  }
});

test('stub: tools carry the account enum of exactly the eligible handles', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());
  const { cookie } = await signedIn(stub, 'me@example.com', { readOnly: false });
  await post(stub.base, '/api/accounts', creds('domainadmin@example.com'), cookie);           // read-only
  await post(stub.base, '/api/accounts', creds('admin'), cookie);                              // read-only

  const list = byName(await tools(stub, cookie));
  // Mailbox reads: both mailbox accounts, so `account` is required.
  assert.deepEqual(list.get_emails.inputSchema.properties.account.enum, ['me@example.com', 'domainadmin@example.com']);
  assert.ok(list.get_emails.inputSchema.required.includes('account'));
  // A mailbox write: only the read-write user qualifies, so it may be omitted.
  assert.deepEqual(list.send_email.inputSchema.properties.account.enum, ['me@example.com']);
  assert.ok(!list.send_email.inputSchema.required.includes('account'));
  // Domain reads for the domain admin; its write is hidden (read-only).
  assert.deepEqual(list.domain_list_users.inputSchema.properties.account.enum, ['domainadmin@example.com']);
  assert.equal(list.domain_create_alias, undefined);
  // Sysadmin reads only.
  assert.deepEqual(list.get_domains.inputSchema.properties.account.enum, ['sysadmin:admin@mail.example.com']);
  assert.equal(list.delete_domain, undefined);
  assert.equal(list.get_domains.category, 'Domains');
  assert.equal(list.get_domains.scope, 'SysAdmin');
});

test('stub: /tools/call resolves, refuses and explains the account', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());
  const { cookie } = await signedIn(stub);
  await post(stub.base, '/api/accounts', creds('domainadmin@example.com'), cookie);
  await post(stub.base, '/api/accounts', creds('admin'), cookie);
  const run = (name, args) => post(stub.base, '/api/tools/call?delay=0', { name, arguments: args }, cookie);

  // A sole eligible account may be omitted, and the response names it.
  const ok = await run('get_domains', {});
  assert.equal(ok.status, 200);
  assert.equal(ok.body.isError, false);
  assert.equal(ok.body.account, 'sysadmin:admin@mail.example.com');

  // Several eligible and none named → isError listing the handles.
  const missing = await run('get_emails', { folderId: 'me@example.com/Inbox' });
  assert.equal(missing.body.isError, true);
  assert.match(missing.body.content, /needs an "account" argument/);
  assert.match(missing.body.content, /me@example\.com, domainadmin@example\.com/);

  // Wrong role → isError with the valid handles.
  const wrong = await run('get_domains', { account: 'me@example.com' });
  assert.equal(wrong.body.isError, true);
  assert.match(wrong.body.content, /cannot run as me@example\.com/);
  assert.match(wrong.body.content, /sysadmin:admin@mail\.example\.com/);

  // Unknown handle → isError.
  const unknown = await run('get_domains', { account: 'nobody' });
  assert.match(unknown.body.content, /Unknown account "nobody"/);

  // Named correctly: runs as that account, and `account` never reaches the tool.
  const mine = await run('get_emails', { folderId: 'domainadmin@example.com/Inbox', account: 'domainadmin@example.com' });
  assert.equal(mine.body.isError, false);
  assert.equal(mine.body.account, 'domainadmin@example.com');

  // A write on a read-only account stays a 403 that names it.
  const ro = await run('delete_domain', { domain: 'example.com' });
  assert.equal(ro.status, 403);
  assert.match(ro.body.error, /sysadmin:admin@mail\.example\.com.*read-only/);

  // Unknown tool is still 404.
  assert.equal((await run('nope', {})).status, 404);
});

test('stub: POST /api/accounts needs a session, validates like login, and caps at maxAccounts', async (t) => {
  const stub = await startStub({ SESSION_MAX_ACCOUNTS: '2' });
  t.after(() => stub.stop());

  assert.equal((await post(stub.base, '/api/accounts', creds('x@example.com'))).status, 401);

  const { cookie } = await signedIn(stub);
  const bad = await post(stub.base, '/api/accounts', { ...creds('x@example.com'), password: 'bad' }, cookie);
  assert.equal(bad.status, 401);
  assert.equal(bad.body.code, 'USERNAME_OR_PASSWORD_INCORRECT');
  assert.equal((await post(stub.base, '/api/accounts', creds('expired@example.com'), cookie)).status, 403);
  assert.equal((await post(stub.base, '/api/accounts', { hostname: 'h' }, cookie)).status, 400);

  assert.equal((await post(stub.base, '/api/accounts', creds('admin'), cookie)).status, 200);
  const full = await post(stub.base, '/api/accounts', creds('two@example.com'), cookie);
  assert.equal(full.status, 409);
  assert.equal(full.body.code, 'ACCOUNT_LIMIT');
  // Replacing an existing account is not "adding" and still works at the cap.
  assert.equal((await post(stub.base, '/api/accounts', creds('admin', { readOnly: false }), cookie)).status, 200);
  // And a 2FA add at the cap is refused before any challenge is issued.
  assert.equal((await post(stub.base, '/api/accounts', creds('2fa@example.com'), cookie)).status, 409);
});

test('stub: DELETE removes one account; the last one ends the session', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());
  const { cookie } = await signedIn(stub);
  const added = await post(stub.base, '/api/accounts', creds('admin'), cookie);
  const [user, sys] = added.body.accounts;

  assert.equal((await call(stub.base, 'DELETE', '/api/accounts/nope', undefined, cookie)).status, 404);

  assert.equal((await call(stub.base, 'DELETE', `/api/accounts/${sys.id}`, undefined, cookie)).status, 204);
  const after = await call(stub.base, 'GET', '/api/auth/session', undefined, cookie);
  assert.deepEqual(after.body.accounts.map((a) => a.id), [user.id]);
  // The admin tools went with it.
  assert.equal(byName(await tools(stub, cookie)).get_domains, undefined);

  const last = await call(stub.base, 'DELETE', `/api/accounts/${user.id}`, undefined, cookie);
  assert.equal(last.status, 204);
  assert.match(last.setCookie || '', /Max-Age=0/);
  assert.equal((await call(stub.base, 'GET', '/api/auth/session', undefined, cookie)).status, 401);
});

test('stub: login always starts a new session and drops the old one', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());
  const first = await signedIn(stub);
  await post(stub.base, '/api/accounts', creds('admin'), first.cookie);

  const r = await post(stub.base, '/api/auth/login', creds('other@example.com'), first.cookie);
  assert.equal(r.status, 200);
  assert.deepEqual(r.body.accounts.map((a) => a.handle), ['other@example.com']);
  assert.notEqual(cookieOf(r.setCookie), first.cookie);
  assert.equal((await call(stub.base, 'GET', '/api/auth/session', undefined, first.cookie)).status, 401);
});

test('stub: add-account two-factor completes only from its own session', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());
  const mine = await signedIn(stub);
  const theirs = await signedIn(stub, 'someone@example.net');

  const challenge = await post(stub.base, '/api/accounts', creds('2faadmin'), mine.cookie);
  assert.equal(challenge.status, 200);
  assert.equal(challenge.body.twoFactorRequired, true);
  assert.equal(challenge.body.accounts, undefined);
  const id = challenge.body.challengeId;

  // Not yet added.
  assert.equal((await call(stub.base, 'GET', '/api/auth/session', undefined, mine.cookie)).body.accounts.length, 1);

  // From another session, or from no session at all: 410, and nobody gains an account.
  const other = await post(stub.base, '/api/auth/two-factor', { challengeId: id, code: '123456' }, theirs.cookie);
  assert.equal(other.status, 410);
  assert.equal(other.body.code, 'CHALLENGE_EXPIRED');
  assert.equal((await post(stub.base, '/api/auth/two-factor', { challengeId: id, code: '123456' })).status, 410);
  assert.equal((await call(stub.base, 'GET', '/api/auth/session', undefined, theirs.cookie)).body.accounts.length, 1);

  // A wrong code from the right session still counts down.
  const wrong = await post(stub.base, '/api/auth/two-factor', { challengeId: id, code: '000000' }, mine.cookie);
  assert.equal(wrong.status, 401);
  assert.equal(wrong.body.attemptsLeft, 4);

  // The right session and code: the account joins, no new cookie, same session.
  const done = await post(stub.base, '/api/auth/two-factor', { challengeId: id, code: '123456' }, mine.cookie);
  assert.equal(done.status, 200);
  assert.equal(done.setCookie, null);
  assert.deepEqual(done.body.accounts.map((a) => [a.role, a.handle]), [
    ['User', 'me@example.com'],
    ['SysAdmin', 'sysadmin:2faadmin@mail.example.com']
  ]);

  // Single use.
  assert.equal((await post(stub.base, '/api/auth/two-factor', { challengeId: id, code: '123456' }, mine.cookie)).status, 410);
});

test('stub: the fake model names the account when there are several', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());
  const { cookie } = await signedIn(stub);
  await post(stub.base, '/api/accounts', creds('admin'), cookie);

  const res = await realFetch(stub.base + '/api/dev/completions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Cookie: cookie },
    body: JSON.stringify({ messages: [{ role: 'user', content: "What's stuck in the spool?" }] })
  });
  const text = await res.text();
  assert.match(text, /get_spool_messages/);
  assert.match(text, /sysadmin:admin@mail\.example\.com/);
});
