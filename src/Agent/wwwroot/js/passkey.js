/**
 * passkey.js — WebAuthn glue for server-mode profiles.
 *
 * The server sends standard JSON options (base64url fields) and verifies what
 * comes back; this module turns them into what navigator.credentials wants,
 * asks for the PRF extension with the profile salt, and serialises the answer
 * by hand. By hand on purpose: PublicKeyCredential.toJSON() would include
 * clientExtensionResults, and the PRF output in there is the secret that
 * unwraps the profile key. It must never leave the browser.
 */

import { PROFILE_PRF_SALT, b64url, fromB64url, random } from './vault.js';
import { createCredential, getCredential } from './webauthn.js';

/** The authenticator gave no PRF output: it cannot unlock a profile. */
export class NoPrfError extends Error {
  constructor() {
    super('This passkey cannot unlock a profile (it does not support the PRF extension). ' +
      'Use a passkey from iCloud Keychain, Google Password Manager, 1Password, Windows Hello or a recent security key.');
    this.name = 'NoPrfError';
  }
}

/** True when the browser says it can evaluate PRF. Without getClientCapabilities() we cannot know before prompting. */
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

const prfInput = () => ({ prf: { eval: { first: PROFILE_PRF_SALT } } });

/** Server creation options (JSON) → PublicKeyCredentialCreationOptions. */
export function creationOptions(json) {
  return {
    rp: json.rp,
    user: { ...json.user, id: fromB64url(json.user.id) },
    challenge: fromB64url(json.challenge),
    pubKeyCredParams: json.pubKeyCredParams,
    timeout: Math.max(json.timeout || 0, 120000),
    excludeCredentials: (json.excludeCredentials || []).map((c) => ({ ...c, id: fromB64url(c.id) })),
    authenticatorSelection: json.authenticatorSelection,
    attestation: json.attestation || 'none',
    extensions: prfInput()
  };
}

/** Server request options (JSON) → PublicKeyCredentialRequestOptions. */
export function requestOptions(json) {
  return {
    challenge: fromB64url(json.challenge),
    rpId: json.rpId,
    timeout: Math.max(json.timeout || 0, 120000),
    allowCredentials: (json.allowCredentials || []).map((c) => ({ ...c, id: fromB64url(c.id) })),
    userVerification: json.userVerification || 'required',
    extensions: prfInput()
  };
}

/** A registration for the server: no client extension results, ever. */
export function attestationJson(credential) {
  const r = credential.response;
  return {
    id: credential.id,
    rawId: b64url(credential.rawId),
    type: credential.type,
    response: {
      clientDataJSON: b64url(r.clientDataJSON),
      attestationObject: b64url(r.attestationObject),
      transports: typeof r.getTransports === 'function' ? r.getTransports() : []
    },
    clientExtensionResults: {}
  };
}

/** A sign-in for the server: no client extension results, ever. */
export function assertionJson(credential) {
  const r = credential.response;
  return {
    id: credential.id,
    rawId: b64url(credential.rawId),
    type: credential.type,
    response: {
      clientDataJSON: b64url(r.clientDataJSON),
      authenticatorData: b64url(r.authenticatorData),
      signature: b64url(r.signature),
      userHandle: r.userHandle ? b64url(r.userHandle) : null
    },
    clientExtensionResults: {}
  };
}

function prfResult(credential) {
  const first = credential?.getClientExtensionResults?.().prf?.results?.first;
  return first ? new Uint8Array(first instanceof ArrayBuffer ? first : first.buffer.slice(first.byteOffset, first.byteOffset + first.byteLength)) : null;
}

/**
 * Creates a profile passkey. Returns { credential (JSON for the server), credentialId, prf }.
 * Some authenticators only evaluate PRF on an assertion, so a missing result is followed by
 * one get() for this credential (its challenge is never verified: it only derives a key).
 * Throws NoPrfError when there is no PRF at all; the new passkey is then reported as unknown
 * to the browser (where supported) so it does not linger in the user's password manager.
 * A password manager's dismissed prompt falls through to the browser's own (webauthn.js).
 */
export async function createPasskey(serverOptions) {
  const { credential, native } = await createCredential(creationOptions(serverOptions));
  if (!credential) throw new Error('No passkey was created.');

  const ext = credential.getClientExtensionResults?.().prf;
  let prf = prfResult(credential);
  if (!prf && ext && ext.enabled !== false) prf = await prfFor(b64url(credential.rawId), serverOptions.rp.id, native);
  if (!prf) {
    try {
      await globalThis.PublicKeyCredential?.signalUnknownCredential?.({ rpId: serverOptions.rp.id, credentialId: b64url(credential.rawId) });
    } catch { /* best effort */ }
    throw new NoPrfError();
  }

  return { credential: attestationJson(credential), credentialId: b64url(credential.rawId), prf };
}

/** Signs in with any profile passkey (no allow-list). Returns { credential, prf }; NoPrfError without PRF. */
export async function getPasskey(serverOptions) {
  const { credential } = await getCredential(requestOptions(serverOptions));
  if (!credential) throw new Error('No passkey was chosen.');
  return { credential: assertionJson(credential), prf: prfResult(credential) };
}

/**
 * A step-up: confirms one action with a passkey of this profile (approving a
 * proposed change). No PRF extension: nothing secret is derived, the server
 * only verifies the signature, user verification and that the ceremony is
 * bound to this session, proposal and argument hash. Returns the assertion
 * JSON for the server. A password manager's dismissed prompt falls through to
 * the browser's own (webauthn.js).
 */
export async function assertPasskey(serverOptions) {
  const { extensions, ...publicKey } = requestOptions(serverOptions);
  void extensions;
  const { credential } = await getCredential(publicKey);
  if (!credential) throw new Error('No passkey was chosen.');
  return assertionJson(credential);
}

/**
 * PRF output of one known credential, with a local challenge (nothing is sent to the server).
 * `native`: the credential was made past a password manager, so ask the browser directly again.
 */
async function prfFor(credentialId, rpId, native) {
  const { credential: assertion } = await getCredential({
    challenge: random(32),
    rpId,
    allowCredentials: [{ type: 'public-key', id: fromB64url(credentialId) }],
    userVerification: 'required',
    timeout: 120000,
    extensions: prfInput()
  }, { native });
  return prfResult(assertion);
}

/** WebAuthn failures in words. NotAllowedError is a cancel, a timeout, or a missing user gesture. */
export function passkeyErrorMessage(err, what) {
  if (err instanceof NoPrfError) return err.message;
  if (err && err.name === 'NotAllowedError') return `${what} with a passkey was cancelled or timed out. Try again.`;
  if (err && err.name === 'InvalidStateError') return 'This device already has a passkey for this profile.';
  if (err && err.name === 'OperationError') return 'That passkey does not open this profile.';
  if (err && err.body && err.body.error) return err.body.error;
  return (err && err.message) || `${what} failed.`;
}
