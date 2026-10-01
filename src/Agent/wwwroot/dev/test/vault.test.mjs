/**
 * vault.test.mjs — the browser half of a server-mode profile (js/vault.js),
 * with the real WebCrypto:
 *
 *  1. the profile key wraps under a passkey's PRF output and a recovery code,
 *     and opens only with the same one;
 *  2. the derived keys are stable for one PK and separate from each other;
 *  3. a transcript the C# server sealed to the profile's public key opens here
 *     (vectors/transcript.json, written by ProfileCryptoTests).
 */

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import * as vault from '../../js/vault.js';

const dec = new TextDecoder();

test('PK wraps under a PRF output and opens only with it', async () => {
  const pk = vault.newProfileKey();
  const prf = vault.random(32);
  const wrapped = await vault.wrapProfileKey(await vault.passkeyWrapKey(prf), pk);

  assert.deepEqual(await vault.unwrapProfileKey(await vault.passkeyWrapKey(prf), wrapped), pk);
  await assert.rejects(vault.unwrapProfileKey(await vault.passkeyWrapKey(vault.random(32)), wrapped));
});

test('derived keys are stable per PK and differ from each other', async () => {
  const pk = vault.newProfileKey();
  const a = await vault.deriveProfileKeys(pk);
  const b = await vault.deriveProfileKeys(pk);
  assert.deepEqual(a.accountsKey, b.accountsKey);
  assert.equal(a.accountsKey.length, 32);
  assert.notDeepEqual(a.accountsKey, (await vault.deriveProfileKeys(vault.newProfileKey())).accountsKey);

  // The settings key opens what it sealed, the inbox key does not.
  const sealed = await vault.sealJson(a.settingsKey, { openRouterKey: 'sk-or-1', model: 'm' });
  assert.deepEqual(await vault.openJson(b.settingsKey, sealed), { openRouterKey: 'sk-or-1', model: 'm' });
  await assert.rejects(vault.openJson(a.inboxKey, sealed));
});

test('recovery codes parse, wrap PK and prove themselves', async () => {
  const code = vault.newRecoveryCode('AAAAAAAAAAAAAAAAAAAAAA');
  const parsed = vault.parseRecoveryCode(` ${code.slice(0, 10)}\n${code.slice(10)} `);
  assert.equal(parsed.profileId, 'AAAAAAAAAAAAAAAAAAAAAA');
  assert.equal(parsed.secret.length, 16);
  assert.equal(vault.parseRecoveryCode('nope'), null);
  assert.equal(vault.parseRecoveryCode('AAAAAAAAAAAAAAAAAAAAAA.short'), null);

  const pk = vault.newProfileKey();
  const { wrappedKey, authKey } = await vault.recoveryMaterial(code, pk);
  assert.equal(vault.fromB64url(authKey).length, 32);
  assert.deepEqual(await vault.unwrapProfileKey(await vault.recoveryWrapKey(parsed.secret), wrappedKey), pk);
  // What the server sees (the auth key) does not open the wrapped PK.
  const authAsKey = await crypto.subtle.importKey('raw', vault.fromB64url(authKey), 'AES-GCM', false, ['decrypt']);
  await assert.rejects(vault.unwrapProfileKey(authAsKey, wrappedKey));
});

test('the inbox private key round-trips under the inbox key', async () => {
  const { inboxKey } = await vault.deriveProfileKeys(vault.newProfileKey());
  const pair = await vault.newInboxKeyPair(inboxKey);
  assert.ok(vault.fromB64url(pair.publicKey).length > 64);
  const privateKey = await vault.openInboxPrivateKey(inboxKey, pair.encryptedPrivateKey);
  assert.equal(privateKey.type, 'private');
  assert.equal(privateKey.extractable, false);
});

test('a transcript sealed by the C# server opens here', async () => {
  const path = fileURLToPath(new URL('./vectors/transcript.json', import.meta.url));
  const vector = JSON.parse(readFileSync(path, 'utf8'));
  const privateKey = await crypto.subtle.importKey('pkcs8', vault.fromB64url(vector.privateKeyPkcs8),
    { name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);

  const plain = await vault.openSealedToMe(privateKey, vector.sealedValue, vector.context);
  assert.equal(dec.decode(plain), vector.plaintext);

  // The context is bound: another run's id does not open it.
  await assert.rejects(vault.openSealedToMe(privateKey, vector.sealedValue, vector.context + 'x'));
});
