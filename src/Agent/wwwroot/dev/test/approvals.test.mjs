/**
 * approvals.test.mjs — the browser half of the approval queue:
 *
 *  1. the argument hash the browser sends equals the server's (a vector the
 *     C# tests write: vectors/proposal-hash.json);
 *  2. how arguments are laid out for review (verbatim, every value whole);
 *  3. the queue end to end against the dev stub: a task run proposes, the
 *     browser opens the sealed display copy, hashes what it shows, approves
 *     once; a mismatch, a second approval and a missing passkey are refused.
 */

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import * as vault from '../../js/vault.js';
import {
  proposalHash, argumentRows, formatRemaining, defaultApprovalMode, groupByRun, isAddress
} from '../../js/approvals-core.js';
import { startStub, call, post, cookieOf, creds } from './stub.mjs';

test('the browser hash matches the C# server on a stored argsJson', async () => {
  const path = fileURLToPath(new URL('./vectors/proposal-hash.json', import.meta.url));
  const vector = JSON.parse(readFileSync(path, 'utf8'));
  assert.equal(await proposalHash(vector.tool, vector.accountId, vector.argsJson), vector.argsHash);

  // Every byte counts: a different account, tool or one character of the arguments changes it.
  assert.notEqual(await proposalHash(vector.tool, 'acc-124', vector.argsJson), vector.argsHash);
  assert.notEqual(await proposalHash('send_email', vector.accountId, vector.argsJson), vector.argsHash);
  assert.notEqual(await proposalHash(vector.tool, vector.accountId, vector.argsJson.replace('archive', 'archivE')), vector.argsHash);
});

test('arguments are shown whole, in order, with addresses and empties marked', () => {
  const long = 'x'.repeat(5000);
  const rows = argumentRows(JSON.stringify({ bcc: '', body: `${long}\nend`, n: 1.5, opts: { a: [1, 2] }, subject: 'Hi', to: 'a@example.net' }));
  assert.deepEqual(rows.map((r) => r.key), ['bcc', 'body', 'n', 'opts', 'subject', 'to']);
  assert.deepEqual(rows.map((r) => r.kind), ['empty', 'long', 'plain', 'json', 'plain', 'address']);
  assert.equal(rows[0].text, '(empty)');
  assert.equal(rows[1].text, `${long}\nend`);                 // never shortened
  assert.equal(rows[3].text, JSON.stringify({ a: [1, 2] }, null, 2));
  assert.equal(rows[5].text, 'a@example.net');

  assert.equal(argumentRows('not json'), null);
  assert.equal(argumentRows('[1,2]'), null);
  assert.deepEqual(argumentRows('{}'), []);
  assert.ok(isAddress('forwardTo', 'x'));
  assert.ok(isAddress('note', 'see bob@example.org'));
  assert.ok(!isAddress('domain', 'example.com'));
});

test('editor defaults, countdowns and grouping', () => {
  assert.equal(defaultApprovalMode({ name: 'send_email', scope: 'Mailbox', destructive: false }), 'auto');
  assert.equal(defaultApprovalMode({ name: 'delete_contacts', scope: 'Mailbox', destructive: true }), 'approve');
  assert.equal(defaultApprovalMode({ name: 'domain_create_alias', scope: 'DomainAdmin' }), 'approve');
  assert.equal(defaultApprovalMode({ name: 'enable_dkim', scope: 'SysAdmin' }), 'approve');

  assert.equal(formatRemaining(0), 'expired');
  assert.equal(formatRemaining(9 * 60000 + 1), '9 min');
  assert.equal(formatRemaining((4 * 60 + 10) * 60000), '4 h 10 min');
  assert.equal(formatRemaining((2 * 1440 + 3 * 60) * 60000), '2 d 3 h');

  const groups = groupByRun([{ id: 1, runId: 'b' }, { id: 2, runId: 'a' }, { id: 3, runId: 'b' }]);
  assert.deepEqual(groups.map((g) => [g.runId, g.items.map((i) => i.id)]), [['b', [1, 3]], ['a', [2]]]);
});

test('the queue against the stub: propose, open, approve once', async (t) => {
  const stub = await startStub();
  t.after(() => stub.stop());
  const { base } = stub;

  // Sign in (read-write), save a profile with real keys, delegate the account.
  const login = await post(base, '/api/auth/login', creds('alice@example.com', { readOnly: false }));
  assert.equal(login.status, 200);
  const accountId = login.body.accounts[0].id;
  let cookie = cookieOf(login.setCookie);
  const begin = await post(base, '/api/profile/register/options', {}, cookie);
  const keys = await vault.deriveProfileKeys(vault.newProfileKey());
  const inbox = await vault.newInboxKeyPair(keys.inboxKey);
  const created = await post(base, '/api/profile', {
    ceremonyId: begin.body.ceremonyId, credential: { id: 'cred-1', rawId: 'cred-1' }, wrappedKey: 'AA',
    accountsKey: vault.b64url(keys.accountsKey), publicKey: inbox.publicKey, encryptedPrivateKey: inbox.encryptedPrivateKey
  }, cookie);
  assert.equal(created.status, 200);
  cookie = cookieOf(created.setCookie);
  const profileId = created.body.profile.id;
  assert.equal((await call(base, 'PUT', `/api/profile/accounts/${accountId}/delegation`, { enabled: true }, cookie)).status, 200);
  const privateKey = await vault.openInboxPrivateKey(keys.inboxKey, inbox.encryptedPrivateKey);
  const open = async (sealed, context) => JSON.parse(new TextDecoder().decode(await vault.openSealedToMe(privateKey, sealed, context)));

  // Approval writes must be allowed writes.
  const bad = await post(base, '/api/tasks', {
    name: 'Bad', prompt: 'x', cron: '0 7 * * *', timeZone: 'UTC', accountIds: [accountId], allowedWrites: [], model: 'm',
    approvals: { writes: ['send_email'] }
  }, cookie);
  assert.equal(bad.status, 400);

  const task = await post(base, '/api/tasks', {
    name: 'Replies', prompt: 'Reply to invoices.', cron: '0 7 * * *', timeZone: 'UTC', accountIds: [accountId],
    allowedWrites: ['send_email'], model: 'm', approvals: { writes: ['send_email'], maxProposals: 5, ttlHours: 24 }
  }, cookie);
  assert.equal(task.status, 200);
  assert.deepEqual(task.body.definition.approvals.writes, ['send_email']);

  assert.equal((await post(base, `/api/tasks/${task.body.id}/run`, { dryRun: false }, cookie)).status, 202);
  let pending = [];
  for (let i = 0; i < 40 && !pending.length; i++) {
    await new Promise((r) => setTimeout(r, 100));
    pending = (await call(base, 'GET', '/api/tasks/proposals', undefined, cookie)).body;
  }
  assert.equal(pending.length, 1);
  const p = pending[0];
  assert.equal(p.status, 'pending');
  assert.equal(p.needsPasskey, false);
  assert.equal((await call(base, 'GET', '/api/tasks', undefined, cookie)).body.pending, 1);

  // The display copy opens with the profile's private key; the hash is over the string shown.
  const display = await open(p.display, `task-proposal|${profileId}|${p.id}`);
  assert.equal(display.tool, 'send_email');
  const hash = await proposalHash(display.tool, display.accountId, display.argsJson);
  assert.equal(hash, display.argsHash);
  await assert.rejects(open(p.display, `task-proposal|${profileId}|other`));

  const mismatch = await post(base, `/api/tasks/proposals/${p.id}/approve`,
    { argsHash: await proposalHash(display.tool, display.accountId, display.argsJson + ' ') }, cookie);
  assert.equal(mismatch.status, 409);
  assert.equal(mismatch.body.code, 'PROPOSAL_MISMATCH');

  const options = await post(base, `/api/tasks/proposals/${p.id}/approve/options`, { argsHash: hash }, cookie);
  assert.deepEqual(options.body, { passkey: false });
  const approved = await post(base, `/api/tasks/proposals/${p.id}/approve`, { argsHash: hash }, cookie);
  assert.equal(approved.status, 200);
  assert.equal(approved.body.status, 'executed');
  const result = await open(approved.body.result, `task-proposal-result|${profileId}|${p.id}`);
  assert.equal(result.isError, false);

  const again = await post(base, `/api/tasks/proposals/${p.id}/approve`, { argsHash: hash }, cookie);
  assert.equal(again.status, 409);
  assert.equal(again.body.code, 'PROPOSAL_NOT_PENDING');
  assert.equal((await call(base, 'GET', '/api/tasks', undefined, cookie)).body.pending, 0);

  // A task that asks for a passkey on every approval: refused without one, bound to the hash.
  const strict = await post(base, '/api/tasks', {
    name: 'Strict', prompt: 'Reply.', cron: '0 8 * * *', timeZone: 'UTC', accountIds: [accountId],
    allowedWrites: ['send_email'], model: 'm', approvals: { writes: ['send_email'], requirePasskey: true }
  }, cookie);
  await post(base, `/api/tasks/${strict.body.id}/run`, { dryRun: false }, cookie);
  let q = [];
  for (let i = 0; i < 40 && !q.length; i++) {
    await new Promise((r) => setTimeout(r, 100));
    q = (await call(base, 'GET', `/api/tasks/proposals?taskId=${strict.body.id}`, undefined, cookie)).body;
  }
  const sp = q[0];
  assert.equal(sp.needsPasskey, true);
  const sd = await open(sp.display, `task-proposal|${profileId}|${sp.id}`);
  const sh = await proposalHash(sd.tool, sd.accountId, sd.argsJson);
  assert.equal((await post(base, `/api/tasks/proposals/${sp.id}/approve`, { argsHash: sh }, cookie)).body.code, 'PASSKEY_REQUIRED');
  const so = await post(base, `/api/tasks/proposals/${sp.id}/approve/options`, { argsHash: sh }, cookie);
  assert.equal(so.body.passkey, true);
  const ok = await post(base, `/api/tasks/proposals/${sp.id}/approve`,
    { argsHash: sh, ceremonyId: so.body.ceremonyId, credential: { id: 'cred-1', rawId: 'cred-1' } }, cookie);
  assert.equal(ok.body.status, 'executed');

  // Deny-all and the decided list.
  const decided = (await call(base, 'GET', '/api/tasks/proposals?status=decided', undefined, cookie)).body;
  assert.equal(decided.length, 2);
  assert.equal((await post(base, '/api/tasks/proposals/deny', { runId: 'nope' }, cookie)).body.denied, 0);
});
