/**
 * invites.test.mjs — invite-only scheduled tasks (TASKS_ACCESS=invite): the dev stub mirrors the
 * server's gates, and the UI decides from the profile view whether to ask for a code.
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { startStub, call, post, cookieOf, creds } from './stub.mjs';

async function profileSession(stub) {
  const login = await post(stub.base, '/api/auth/login', creds('me@example.com', { readOnly: false }));
  const cookie = cookieOf(login.setCookie);
  const begin = await post(stub.base, '/api/profile/register/options', {}, cookie);
  const { publicKey } = await crypto.webcrypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, true, ['deriveBits']);
  const spki = Buffer.from(await crypto.webcrypto.subtle.exportKey('spki', publicKey)).toString('base64url');
  const created = await post(stub.base, '/api/profile', {
    ceremonyId: begin.body.ceremonyId, credential: { rawId: 'cred-1' }, wrappedKey: 'AA',
    accountsKey: crypto.randomBytes(32).toString('base64url'), publicKey: spki, encryptedPrivateKey: 'AA'
  }, cookie);
  assert.equal(created.status, 200);
  return { cookie: cookieOf(created.setCookie), account: created.body.accounts[0] };
}

test('stub: invite-only tasks wait for a code', async () => {
  const stub = await startStub({ TASKS_ACCESS: 'invite' });
  try {
    const config = await call(stub.base, 'GET', '/api/config');
    assert.equal(config.body.tasks.inviteOnly, true);

    const { cookie, account } = await profileSession(stub);
    let view = (await call(stub.base, 'GET', '/api/profile', undefined, cookie)).body;
    assert.deepEqual(view.taskAccess, { inviteOnly: true, granted: false });
    assert.equal(view.canDelegate, false);

    const delegate = () => call(stub.base, 'PUT', `/api/profile/accounts/${account.id}/delegation`, { enabled: true }, cookie);
    assert.equal((await delegate()).body.code, 'TASKS_NOT_INVITED');
    assert.equal((await call(stub.base, 'PUT', '/api/profile/task-key', { key: 'sk-or-x' }, cookie)).status, 403);
    assert.equal((await post(stub.base, '/api/tasks', { name: 'T' }, cookie)).body.code, 'TASKS_NOT_INVITED');
    assert.equal((await call(stub.base, 'GET', '/api/tasks', undefined, cookie)).status, 200);

    const wrong = await post(stub.base, '/api/profile/task-access', { code: 'AAAA-AAAA-AAAA-AAAA' }, cookie);
    assert.equal(wrong.status, 400);
    assert.equal(wrong.body.code, 'INVITE_INVALID');

    // Case, spaces, and O/I read as 0/1 are forgiven, as on the server.
    const right = await post(stub.base, '/api/profile/task-access', { code: 'stub invt code oooo' }, cookie);
    assert.equal(right.status, 200);
    assert.deepEqual(right.body.taskAccess, { inviteOnly: true, granted: true });
    assert.equal((await delegate()).status, 200);
  } finally {
    await stub.stop();
  }
});

test('stub: an open server never asks for a code', async () => {
  const stub = await startStub();
  try {
    assert.equal((await call(stub.base, 'GET', '/api/config')).body.tasks.inviteOnly, false);
    const { cookie } = await profileSession(stub);
    const view = (await call(stub.base, 'GET', '/api/profile', undefined, cookie)).body;
    assert.deepEqual(view.taskAccess, { inviteOnly: false, granted: true });
  } finally {
    await stub.stop();
  }
});
