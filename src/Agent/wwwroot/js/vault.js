/**
 * vault.js — the browser's half of a server-mode profile's cryptography.
 *
 * The profile key (PK) is 32 random bytes made here and never sent anywhere
 * in the clear. Everything else derives from it, HKDF-SHA-256 with a 32-byte
 * zero salt and a label:
 *
 *   sma-settings-v1   AES-GCM key for the settings blob (OpenRouter key, model,
 *                     switched-off tool groups). Never leaves the browser.
 *   sma-accounts-v1   32 raw bytes, the "accounts key". Sent to the server at
 *                     unlock: it seals the stored SmarterMail accounts, which
 *                     the server needs anyway to relay every call.
 *   sma-inbox-v1      AES-GCM key for the profile's ECDH private key. Task-run
 *                     transcripts are sealed to its public key by the server.
 *
 * PK itself is stored on the server wrapped (AES-GCM) under a key derived from
 * each passkey's PRF output (sma-profile-wrap-v1), and optionally under a
 * recovery code (sma-recovery-wrap-v1; sma-recovery-auth-v1 proves the code to
 * the server without revealing it). Pure WebCrypto: runs in node for tests.
 */

const enc = new TextEncoder();
const dec = new TextDecoder();

/** The PRF input for profile passkeys. Changing it orphans every profile. */
export const PROFILE_PRF_SALT = enc.encode('smartermail-agent profile v1');

const ZERO_SALT = new Uint8Array(32);
const subtle = () => globalThis.crypto.subtle;

/* ---------------------------------------------------------------- bytes */

export function random(n) { return globalThis.crypto.getRandomValues(new Uint8Array(n)); }

export function toBytes(v) {
  if (v instanceof Uint8Array) return v;
  if (ArrayBuffer.isView(v)) return new Uint8Array(v.buffer, v.byteOffset, v.byteLength);
  return new Uint8Array(v);
}

export function b64url(v) {
  let s = '';
  for (const b of toBytes(v)) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function fromB64url(s) {
  const b = atob(String(s).replace(/-/g, '+').replace(/_/g, '/') + '==='.slice((String(s).length + 3) % 4));
  const out = new Uint8Array(b.length);
  for (let i = 0; i < b.length; i++) out[i] = b.charCodeAt(i);
  return out;
}

/* ----------------------------------------------------------------- HKDF */

async function hkdfBase(ikm) {
  return subtle().importKey('raw', toBytes(ikm), 'HKDF', false, ['deriveKey', 'deriveBits']);
}

async function hkdfAesKey(ikm, info, extractable = false) {
  return subtle().deriveKey(
    { name: 'HKDF', hash: 'SHA-256', salt: ZERO_SALT, info: enc.encode(info) },
    await hkdfBase(ikm), { name: 'AES-GCM', length: 256 }, extractable, ['encrypt', 'decrypt']);
}

async function hkdfBytes(ikm, info) {
  const bits = await subtle().deriveBits(
    { name: 'HKDF', hash: 'SHA-256', salt: ZERO_SALT, info: enc.encode(info) }, await hkdfBase(ikm), 256);
  return new Uint8Array(bits);
}

/* --------------------------------------------------------------- AES-GCM */

/** AES-GCM with a random 12-byte nonce: base64url(nonce || ciphertext || tag). */
export async function seal(key, bytes) {
  const iv = random(12);
  const ct = new Uint8Array(await subtle().encrypt({ name: 'AES-GCM', iv }, key, toBytes(bytes)));
  const out = new Uint8Array(12 + ct.length);
  out.set(iv, 0);
  out.set(ct, 12);
  return b64url(out);
}

/** The opposite of seal(). Throws (OperationError) on the wrong key or a tampered value. */
export async function open(key, sealed) {
  const data = fromB64url(sealed);
  return new Uint8Array(await subtle().decrypt({ name: 'AES-GCM', iv: data.subarray(0, 12) }, key, data.subarray(12)));
}

export async function sealJson(key, value) { return seal(key, enc.encode(JSON.stringify(value))); }

export async function openJson(key, sealed) { return JSON.parse(dec.decode(await open(key, sealed))); }

/* --------------------------------------------------------- profile keys */

export function newProfileKey() { return random(32); }

/** The key a passkey's PRF output wraps PK under. */
export function passkeyWrapKey(prfOutput) { return hkdfAesKey(prfOutput, 'sma-profile-wrap-v1'); }

export async function wrapProfileKey(wrapKey, pk) { return seal(wrapKey, pk); }

export async function unwrapProfileKey(wrapKey, wrapped) {
  const pk = await open(wrapKey, wrapped);
  if (pk.length !== 32) throw new Error('That is not a profile key.');
  return pk;
}

/** What the browser keeps of an unlocked profile: the derived keys. PK itself can be dropped. */
export async function deriveProfileKeys(pk) {
  return {
    accountsKey: await hkdfBytes(pk, 'sma-accounts-v1'),
    settingsKey: await hkdfAesKey(pk, 'sma-settings-v1'),
    inboxKey: await hkdfAesKey(pk, 'sma-inbox-v1')
  };
}

/* -------------------------------------------------------------- recovery */

/** A recovery code: `<profileId>.<16 random bytes>` in base64url. Shown once. */
export function newRecoveryCode(profileId) { return `${profileId}.${b64url(random(16))}`; }

/** Splits a code as typed (spaces, line breaks and a trailing dot are forgiven); null when malformed. */
export function parseRecoveryCode(code) {
  const m = /^([A-Za-z0-9_-]{16,64})\.([A-Za-z0-9_-]{20,24})$/.exec(String(code || '').replace(/\s+/g, '').replace(/\.$/, ''));
  if (!m) return null;
  const secret = fromB64url(m[2]);
  return secret.length === 16 ? { profileId: m[1], secret } : null;
}

export function recoveryWrapKey(secret) { return hkdfAesKey(secret, 'sma-recovery-wrap-v1'); }

/** Proves the code to the server (which stores only its SHA-256). */
export async function recoveryAuthKey(secret) { return hkdfBytes(secret, 'sma-recovery-auth-v1'); }

/** `{ wrappedKey, authKey }` for POST /api/profile and PUT /api/profile/recovery. */
export async function recoveryMaterial(code, pk) {
  const parsed = parseRecoveryCode(code);
  if (!parsed) throw new Error('Malformed recovery code.');
  return {
    wrappedKey: await wrapProfileKey(await recoveryWrapKey(parsed.secret), pk),
    authKey: b64url(await recoveryAuthKey(parsed.secret))
  };
}

/* ------------------------------------------------------- task-run inbox */

/** A new ECDH P-256 key pair: the public key for the server, the private key encrypted under the inbox key. */
export async function newInboxKeyPair(inboxKey) {
  const pair = await subtle().generateKey({ name: 'ECDH', namedCurve: 'P-256' }, true, ['deriveBits']);
  const spki = new Uint8Array(await subtle().exportKey('spki', pair.publicKey));
  const pkcs8 = new Uint8Array(await subtle().exportKey('pkcs8', pair.privateKey));
  const encryptedPrivateKey = await seal(inboxKey, pkcs8);
  pkcs8.fill(0);
  return { publicKey: b64url(spki), encryptedPrivateKey };
}

export async function openInboxPrivateKey(inboxKey, encryptedPrivateKey) {
  const pkcs8 = await open(inboxKey, encryptedPrivateKey);
  try {
    return await subtle().importKey('pkcs8', pkcs8, { name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
  } finally {
    pkcs8.fill(0);
  }
}

/**
 * Opens a value the server sealed to the profile's public key
 * (ProfileCrypto.SealToPublicKey): [0x01][ephemeral key 65][nonce 12][ciphertext][tag 16],
 * ECDH → HKDF-SHA-256 (salt = ephemeral key, info sma-run-transcript-v1) → AES-256-GCM
 * with `context` as associated data.
 */
export async function openSealedToMe(privateKey, sealed, context) {
  const data = fromB64url(sealed);
  if (data.length < 1 + 65 + 12 + 16 || data[0] !== 0x01) throw new Error('Not a sealed transcript.');
  const epk = data.slice(1, 66);
  const ephemeral = await subtle().importKey('raw', epk, { name: 'ECDH', namedCurve: 'P-256' }, false, []);
  const shared = await subtle().deriveBits({ name: 'ECDH', public: ephemeral }, privateKey, 256);
  const base = await subtle().importKey('raw', shared, 'HKDF', false, ['deriveKey']);
  const key = await subtle().deriveKey(
    { name: 'HKDF', hash: 'SHA-256', salt: epk, info: enc.encode('sma-run-transcript-v1') },
    base, { name: 'AES-GCM', length: 256 }, false, ['decrypt']);
  const plain = await subtle().decrypt(
    { name: 'AES-GCM', iv: data.slice(66, 78), additionalData: enc.encode(context) }, key, data.slice(78));
  return new Uint8Array(plain);
}
