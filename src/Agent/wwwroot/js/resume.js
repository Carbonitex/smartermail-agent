/**
 * resume.js — "Remember me on this device": the browser's half.
 *
 * The server stores nothing per user. When remembering is on, it hands this
 * browser a bundle sealed under its own key (the accounts' SmarterMail refresh
 * tokens; opaque to us). It lives in localStorage under `sma.resume` and is
 * presented to POST /api/auth/resume after a restart or an expired session.
 * SmarterMail rotates refresh tokens, so the server announces every newer copy
 * (X-Resume-Version) and chat.js fetches and saves it here.
 *
 * Optional passkey lock, entirely client-side: a WebAuthn credential with the
 * PRF extension yields a secret only the authenticator can reproduce; HKDF
 * turns it into a non-extractable AES-GCM key that encrypts the bundle again
 * before it is stored. The server never sees the passkey, the PRF output or
 * the key. The key is kept in memory for the page's lifetime once created or
 * unlocked, so newer bundles can be re-encrypted without another prompt.
 *
 * Stored record (JSON):
 *   { v: 1, locked: false, bundle, version }
 *   { v: 1, locked: true, credentialId, iv, ciphertext, version }   base64url fields
 */

const KEY = 'sma.resume';
const enc = new TextEncoder();

/** Fixed PRF input: exactly 32 bytes. Changing it orphans every existing lock. */
export const PRF_SALT = enc.encode('smartermail-agent resume lock v1');
const HKDF_INFO = enc.encode('sma-resume-bundle-v1');

/** Thrown by open() when the saved bundle is passkey-locked and not unlocked on this page. */
export class LockedError extends Error {
  constructor() { super('The saved sign-in is locked with a passkey.'); this.name = 'LockedError'; }
}

let memoryKey = null;          // CryptoKey, non-extractable
let memoryCredentialId = null; // base64url

/* ---------------------------------------------------------------- storage */

function readRecord() {
  try {
    const r = JSON.parse(globalThis.localStorage.getItem(KEY) || 'null');
    if (!r || r.v !== 1 || !Number.isSafeInteger(r.version)) return null;
    if (r.locked) return (typeof r.credentialId === 'string' && typeof r.iv === 'string' && typeof r.ciphertext === 'string') ? r : null;
    return typeof r.bundle === 'string' && r.bundle ? r : null;
  } catch {
    return null;
  }
}

function writeRecord(r) {
  try {
    if (r) globalThis.localStorage.setItem(KEY, JSON.stringify(r));
    else globalThis.localStorage.removeItem(KEY);
  } catch {
    /* storage disabled — remembering just does not stick */
  }
}

export const store = {
  /** The saved record, or null. */
  get record() { return readRecord(); },

  /** The version of the saved bundle; 0 when there is none. Sent as X-Resume-Version. */
  get version() { const r = readRecord(); return r ? r.version : 0; },

  has() { return readRecord() !== null; },

  get locked() { const r = readRecord(); return !!(r && r.locked); },

  /**
   * Save `{ bundle, version }` from the server.
   *   force — replace whatever is there (a new sign-in or a resume); otherwise
   *           an older version never overwrites a newer one.
   *   lock  — undefined: keep the current mode; null: store unlocked;
   *           { key, credentialId } from createLock(): lock with it.
   * Returns false when nothing was saved: an older version, or a locked record
   * whose key is not in memory (it is kept as is rather than downgraded to an
   * unlocked copy).
   */
  async save({ bundle, version }, { force = false, lock } = {}) {
    if (typeof bundle !== 'string' || !bundle || !Number.isSafeInteger(version)) return false;
    const current = readRecord();
    if (!force && current && current.version >= version) return false;

    if (lock) { memoryKey = lock.key; memoryCredentialId = lock.credentialId; }
    else if (lock === null) { memoryKey = null; memoryCredentialId = null; }

    const wantLocked = lock ? true : lock === null ? false : !!(current && current.locked) || !!memoryKey;
    if (!wantLocked) {
      writeRecord({ v: 1, locked: false, bundle, version });
      return true;
    }
    if (!memoryKey) return false;

    const iv = globalThis.crypto.getRandomValues(new Uint8Array(12));
    const ciphertext = await globalThis.crypto.subtle.encrypt({ name: 'AES-GCM', iv }, memoryKey, enc.encode(bundle));
    writeRecord({ v: 1, locked: true, credentialId: memoryCredentialId, iv: b64url(iv), ciphertext: b64url(ciphertext), version });
    return true;
  },

  /** The sealed bundle to present. Throws LockedError when a passkey unlock is needed first. */
  async open() {
    const r = readRecord();
    if (!r) return null;
    if (!r.locked) return r.bundle;
    if (!memoryKey || memoryCredentialId !== r.credentialId) throw new LockedError();
    return decrypt(memoryKey, r);
  },

  /**
   * Forget the saved bundle. With `expected` (a record read earlier) only if it
   * is still that one: another tab may have saved a newer copy meanwhile.
   */
  clear(expected) {
    if (expected) {
      const r = readRecord();
      if (!r || r.version !== expected.version) return;
    }
    writeRecord(null);
  }
};

/** Whether this page holds the key for the saved (locked) bundle. */
export function hasKey() {
  const r = readRecord();
  return !!(memoryKey && r && r.locked && r.credentialId === memoryCredentialId);
}

/** Drop the in-memory key (logout, "stop remembering"). */
export function forgetKey() { memoryKey = null; memoryCredentialId = null; }

/* ---------------------------------------------------------------- passkey */

/**
 * True when the browser says it can evaluate the WebAuthn PRF extension.
 * Without getClientCapabilities() there is no reliable way to know before
 * prompting, so the lock is simply not offered.
 */
export async function prfSupported() {
  try {
    const PKC = globalThis.PublicKeyCredential;
    if (!PKC || typeof PKC.getClientCapabilities !== 'function' || !globalThis.navigator?.credentials) return false;
    const caps = await PKC.getClientCapabilities();
    return !!caps && caps['extension:prf'] === true;
  } catch {
    return false;
  }
}

/**
 * Create a passkey for this site and derive the lock key from its PRF output.
 * The challenge is random and never checked: nothing is verified by a server,
 * the credential only derives a local key. Returns { key, credentialId } for
 * store.save(…, { lock }). Must run inside a user gesture.
 */
export async function createLock() {
  const creds = globalThis.navigator.credentials;
  const credential = await creds.create({
    publicKey: {
      rp: { id: globalThis.location.hostname, name: 'SmarterMail Agent' },
      user: {
        id: random(16),
        name: 'SmarterMail Agent on this device',
        displayName: 'SmarterMail Agent (this device)'
      },
      challenge: random(32),
      pubKeyCredParams: [{ type: 'public-key', alg: -7 }, { type: 'public-key', alg: -257 }],
      authenticatorSelection: { residentKey: 'preferred', userVerification: 'required' },
      timeout: 120000,
      extensions: { prf: { eval: { first: PRF_SALT } } }
    }
  });
  if (!credential) throw new Error('No passkey was created.');

  const ext = credential.getClientExtensionResults?.().prf;
  if (!ext || (ext.enabled === false && !ext.results)) {
    throw new Error('This passkey cannot derive a key (no PRF support), so it cannot lock the saved sign-in.');
  }
  const credentialId = b64url(credential.rawId);
  // Some authenticators answer PRF at creation, others only on an assertion.
  const output = ext.results?.first || await prfOutput(credentialId);
  return { key: await deriveKey(output), credentialId };
}

/**
 * Unlock the saved bundle with its passkey (one prompt) and keep the key in
 * memory. Returns the sealed bundle. Must run inside a user gesture. Throws the
 * browser's NotAllowedError if the user cancels.
 */
export async function unlock() {
  const r = readRecord();
  if (!r || !r.locked) return r ? r.bundle : null;
  const key = await deriveKey(await prfOutput(r.credentialId));
  const bundle = await decrypt(key, r);   // a wrong passkey fails here, before anything is kept
  memoryKey = key;
  memoryCredentialId = r.credentialId;
  return bundle;
}

async function prfOutput(credentialId) {
  const assertion = await globalThis.navigator.credentials.get({
    publicKey: {
      challenge: random(32),
      rpId: globalThis.location.hostname,
      allowCredentials: [{ type: 'public-key', id: fromB64url(credentialId) }],
      userVerification: 'required',
      timeout: 120000,
      extensions: { prf: { eval: { first: PRF_SALT } } }
    }
  });
  const first = assertion?.getClientExtensionResults?.().prf?.results?.first;
  if (!first) throw new Error('The passkey did not return its key (no PRF support).');
  return first;
}

/** HKDF-SHA-256 over the PRF output → a non-extractable AES-256-GCM key. */
export async function deriveKey(prfOutput) {
  const subtle = globalThis.crypto.subtle;
  const base = await subtle.importKey('raw', toBytes(prfOutput), 'HKDF', false, ['deriveKey']);
  return subtle.deriveKey(
    { name: 'HKDF', hash: 'SHA-256', salt: new Uint8Array(32), info: HKDF_INFO },
    base, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
}

async function decrypt(key, record) {
  const plain = await globalThis.crypto.subtle.decrypt(
    { name: 'AES-GCM', iv: fromB64url(record.iv) }, key, fromB64url(record.ciphertext));
  return new TextDecoder().decode(plain);
}

/* ---------------------------------------------------------------- helpers */

function random(n) { return globalThis.crypto.getRandomValues(new Uint8Array(n)); }

function toBytes(v) {
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
  const b = atob(s.replace(/-/g, '+').replace(/_/g, '/') + '==='.slice((s.length + 3) % 4));
  const out = new Uint8Array(b.length);
  for (let i = 0; i < b.length; i++) out[i] = b.charCodeAt(i);
  return out;
}
