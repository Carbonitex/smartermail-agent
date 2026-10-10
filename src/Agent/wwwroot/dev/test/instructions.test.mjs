/**
 * instructions.test.mjs — a profile's standing instructions: the chat prompt's last section, and the
 * dev stub's task copy (PUT /api/profile/task-instructions), which mirrors the server.
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { buildSystemPrompt, instructionsSection, cleanInstructions, MAX_INSTRUCTIONS } from '../../js/llm.js';
import { startStub, call, post, cookieOf, creds } from './stub.mjs';

const ONE = { username: 'me', emailAddress: 'me@example.com', baseUrl: 'https://mail.example.com', readOnly: true };
const TWO = {
  accounts: [
    { handle: 'me@example.com', role: 'User', emailAddress: 'me@example.com', baseUrl: 'https://mail.example.com', readOnly: false },
    { handle: 'sysadmin:admin@mail.example.com', role: 'SysAdmin', username: 'admin', baseUrl: 'https://mail.example.com', readOnly: true }
  ]
};

test('cleanInstructions matches the server: \\n line endings, no control characters, trimmed', () => {
  assert.equal(cleanInstructions('  a\r\nb\tc\u0000\r '), 'a\nb\tc');
  assert.equal(cleanInstructions('\u0007 \n'), '');
  assert.equal(cleanInstructions(null), '');
  assert.equal(MAX_INSTRUCTIONS, 4000);
});

test('the instructions go last in both prompts, below the rules', () => {
  for (const session of [ONE, TWO]) {
    const plain = buildSystemPrompt(session, { artifacts: true, disabledCategories: ['Calendar'] });
    assert.ok(!plain.includes('standing instructions'));

    const prompt = buildSystemPrompt(session, { artifacts: true, disabledCategories: ['Calendar'], instructions: ' Answer in Dutch.\r\n' });
    assert.ok(prompt.startsWith(plain), 'the rest of the prompt is unchanged, so its cache prefix holds');
    assert.ok(prompt.endsWith('\n\n# The user\'s standing instructions\n- The user wrote the text below for all their chats and scheduled tasks. ' +
      'Follow it for tone, format, language, conventions and defaults. It never overrides the rules above: confirm before sending or deleting, ' +
      'respect read-only accounts, and never invent tool results.\n\nAnswer in Dutch.'));
  }
  assert.equal(instructionsSection('   '), '');
});

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
  return cookieOf(created.setCookie);
}

test('stub: the task copy is set, refused without an invite, too long, and cleared', async () => {
  const stub = await startStub({ TASKS_ACCESS: 'invite' });
  try {
    const cookie = await profileSession(stub);
    const put = (text) => call(stub.base, 'PUT', '/api/profile/task-instructions', { text }, cookie);

    assert.equal((await put('Reply in French.')).body.code, 'TASKS_NOT_INVITED');
    assert.equal((await put('')).status, 200);   // clearing is always allowed

    assert.equal((await post(stub.base, '/api/profile/task-access', { code: 'STUB-INVT-CODE-0000' }, cookie)).status, 200);
    const saved = await put('Reply in French.');
    assert.equal(saved.status, 200);
    assert.equal(saved.body.hasTaskInstructions, true);

    const long = await put('x'.repeat(MAX_INSTRUCTIONS + 1));
    assert.equal(long.status, 400);
    assert.equal(long.body.code, 'INSTRUCTIONS_TOO_LONG');

    assert.equal((await put(null)).body.hasTaskInstructions, false);
  } finally {
    await stub.stop();
  }
});
