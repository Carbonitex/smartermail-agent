/**
 * profile.js — server-mode profiles, the browser's side (no DOM here).
 *
 * A profile is a passkey-encrypted copy of a chat's accounts and settings kept
 * on the server, so the same person can come back from any browser:
 *
 *   create   after a password sign-in: a passkey (with PRF) wraps a fresh
 *            profile key; the server gets the wrapped key, the accounts key,
 *            the inbox public key and the encrypted settings.
 *   sign in  passkey → the server verifies it and returns the wrapped key →
 *            the PRF output unwraps it here → unlock sends the accounts key →
 *            the stored accounts come back, and the settings are opened here.
 *
 * The profile key and what derives from it stay in this module's memory for
 * the page's lifetime (they are equally sensitive). A reload keeps the server
 * session (cookie) but not the keys: chatting works, and saving settings or
 * reading task results asks for the passkey again (a fresh sign-in).
 * `sma.profileHint` in localStorage only remembers that this browser has a
 * profile here (to lead with "Sign in with passkey"); it holds no secret.
 */

import * as api from './api.js';
import * as vault from './vault.js';
import { createPasskey, getPasskey, NoPrfError } from './passkey.js';

const HINT = 'sma.profileHint';
const OPTIONS_MAX_AGE_MS = 90_000;   // server ceremonies live two minutes

let config = null;
let keys = null;              // { accountsKey, settingsKey, inboxKey } while unlocked on this page
let profileKey = null;        // PK itself, for wrapping it again (another passkey, a new recovery code)
let inboxPrivateKey = null;   // CryptoKey, opened on first use
let settingsVersion = 0;
let loginOptions = null;      // { at, promise } prefetched for the passkey button
let registerOptions = null;

/* ---------------------------------------------------------------- config */

/** GET /api/config, once. Anything unreachable or old counts as browser-only. */
export async function loadConfig() {
  if (config) return config;
  try {
    config = await api.config();
  } catch {
    config = { mode: 'browser', resume: null, profiles: { enabled: false }, tasks: { enabled: false } };
  }
  return config;
}

export const serverMode = () => !!(config && config.mode === 'server' && config.profiles?.enabled);
export const tasksEnabled = () => serverMode() && !!config.tasks?.enabled;
export const taskLimits = () => config?.tasks || {};

/** This page holds the profile's keys (settings can be saved, task results opened). */
export const hasKeys = () => !!keys;

/** This page can wrap the profile key again (add a passkey, make a recovery code). */
export const canWrapKey = () => !!profileKey;

export function hint() {
  try {
    const h = JSON.parse(globalThis.localStorage.getItem(HINT) || 'null');
    return h && h.v === 1 && typeof h.profileId === 'string' ? h : null;
  } catch {
    return null;
  }
}

function setHint(profileId, label) {
  try {
    if (profileId) globalThis.localStorage.setItem(HINT, JSON.stringify({ v: 1, profileId, label: label || '' }));
    else globalThis.localStorage.removeItem(HINT);
  } catch { /* storage disabled */ }
}

export function forgetHint() { setHint(null); }

/** Drop the keys (logout / lock). The hint stays: this browser still has a profile here. */
export function lock() {
  keys = null;
  profileKey?.fill(0);
  profileKey = null;
  inboxPrivateKey = null;
  settingsVersion = 0;
}

/* --------------------------------------------------------------- options */

/**
 * Fetches passkey options ahead of the click: WebAuthn wants to be called in
 * the click's user gesture, and Safari loses it across a network round trip.
 */
export function prefetchLogin() {
  if (!serverMode()) return;
  if (loginOptions && Date.now() - loginOptions.at < OPTIONS_MAX_AGE_MS) return;
  loginOptions = { at: Date.now(), promise: api.profileLoginOptions().catch((err) => { loginOptions = null; throw err; }) };
  loginOptions.promise.catch(() => {});
}

export function prefetchRegister() {
  if (!serverMode()) return;
  if (registerOptions && Date.now() - registerOptions.at < OPTIONS_MAX_AGE_MS) return;
  registerOptions = { at: Date.now(), promise: api.profileRegisterOptions().catch((err) => { registerOptions = null; throw err; }) };
  registerOptions.promise.catch(() => {});
}

async function take(slot, fetchFresh) {
  const cached = slot === 'login' ? loginOptions : registerOptions;
  if (slot === 'login') loginOptions = null; else registerOptions = null;
  if (cached && Date.now() - cached.at < OPTIONS_MAX_AGE_MS) return cached.promise;
  return fetchFresh();
}

/* ------------------------------------------------------------- settings */

/** What the profile keeps for the chat: { openRouterKey, model, toolsOff: [...] }. */
async function openSettings(sealed) {
  if (!sealed || !keys) return null;
  try {
    const s = await vault.openJson(keys.settingsKey, sealed);
    return s && s.v === 1 ? s : null;
  } catch {
    return null;
  }
}

/**
 * Saves the chat's settings to the profile (encrypted here). Last write wins
 * across browsers: on SETTINGS_STALE the server's version is taken and the
 * write retried once. False when this page does not hold the keys.
 */
export async function saveSettings({ openRouterKey, model, toolsOff }) {
  if (!keys) return false;
  const sealed = await vault.sealJson(keys.settingsKey, { v: 1, openRouterKey: openRouterKey || '', model: model || '', toolsOff: [...(toolsOff || [])] });
  try {
    settingsVersion = (await api.saveProfileSettings(sealed, settingsVersion)).version;
  } catch (err) {
    if (!(err instanceof api.ApiError && err.code === 'SETTINGS_STALE')) throw err;
    settingsVersion = (await api.saveProfileSettings(sealed, err.body?.version ?? 0)).version;
  }
  return true;
}

/* -------------------------------------------------------------- unlock */

/** With PK in hand: derive, unlock on the server, open the settings. */
async function unlockWith(pk, signIn) {
  const derived = await vault.deriveProfileKeys(pk);
  const unlocked = await api.profileUnlock(vault.b64url(derived.accountsKey));
  lock();
  keys = derived;
  profileKey = pk;

  const stored = await api.profileSettings();
  settingsVersion = stored.version || 0;
  const settings = await openSettings(stored.settings);
  setHint(signIn.profileId, accountLabel(unlocked.session));
  return { session: unlocked.session, skipped: unlocked.skipped || [], settings };
}

/**
 * "Sign in with passkey": one prompt. Resolves { session, skipped, settings }.
 * Must run inside a click. Throws NoPrfError for a passkey without PRF (the
 * recovery code is the way in then), or the browser's NotAllowedError on cancel.
 */
export async function signInWithPasskey() {
  const { ceremonyId, options } = await take('login', api.profileLoginOptions);
  const { credential, prf } = await getPasskey(options);
  if (!prf) throw new NoPrfError();
  const signIn = await api.profileLogin(ceremonyId, credential);
  const pk = await vault.unwrapProfileKey(await vault.passkeyWrapKey(prf), signIn.wrappedKey);
  return unlockWith(pk, signIn);
}

/** Opens the profile with its recovery code instead of a passkey. Add a new passkey afterwards. */
export async function signInWithRecoveryCode(code) {
  const parsed = vault.parseRecoveryCode(code);
  if (!parsed) throw new Error('That does not look like a recovery code. It is two parts separated by a dot.');
  const signIn = await api.profileRecover(parsed.profileId, vault.b64url(await vault.recoveryAuthKey(parsed.secret)));
  const pk = await vault.unwrapProfileKey(await vault.recoveryWrapKey(parsed.secret), signIn.wrappedKey);
  return unlockWith(pk, signIn);
}

/* -------------------------------------------------------------- create */

/**
 * Saves the current chat (its accounts, and these settings) to a new profile.
 * Must run inside a click. Resolves { session, recoveryCode } — the code is
 * shown once and never again.
 */
export async function createProfile({ settings, label, withRecovery = true }) {
  const { ceremonyId, profileId, options } = await take('register', () => api.profileRegisterOptions(label));
  const created = await createPasskey(options);

  const pk = vault.newProfileKey();
  const derived = await vault.deriveProfileKeys(pk);
  const inbox = await vault.newInboxKeyPair(derived.inboxKey);
  const recoveryCode = withRecovery ? vault.newRecoveryCode(profileId) : null;

  const session = await api.createProfile({
    ceremonyId,
    credential: created.credential,
    wrappedKey: await vault.wrapProfileKey(await vault.passkeyWrapKey(created.prf), pk),
    accountsKey: vault.b64url(derived.accountsKey),
    publicKey: inbox.publicKey,
    encryptedPrivateKey: inbox.encryptedPrivateKey,
    settings: await vault.sealJson(derived.settingsKey, { v: 1, ...settings, toolsOff: [...(settings.toolsOff || [])] }),
    recovery: recoveryCode ? await vault.recoveryMaterial(recoveryCode, pk) : null,
    label: deviceLabel()
  });

  lock();
  keys = derived;
  profileKey = pk;
  settingsVersion = 1;
  setHint(profileId, label || accountLabel(session));
  return { session, recoveryCode };
}

/* --------------------------------------------------- passkeys, recovery */

/** Adds a passkey to the unlocked profile (another device's, or one after a recovery-code sign-in). */
export async function addPasskey() {
  if (!profileKey) throw new Error('Sign in with a passkey or the recovery code on this page first.');
  const { ceremonyId, options } = await api.addPasskeyOptions();
  const created = await createPasskey(options);
  return api.addPasskey({
    ceremonyId,
    credential: created.credential,
    wrappedKey: await vault.wrapProfileKey(await vault.passkeyWrapKey(created.prf), profileKey),
    label: deviceLabel()
  });
}

/** Replaces the recovery code. Resolves the new code (shown once). */
export async function newRecoveryCode(profileId) {
  if (!profileKey) throw new Error('Sign in with a passkey or the recovery code on this page first.');
  const code = vault.newRecoveryCode(profileId);
  await api.setRecovery(await vault.recoveryMaterial(code, profileKey));
  return code;
}

/* --------------------------------------------------------- task results */

/** Opens a run's transcript (sealed by the server to this profile's public key). */
export async function openTranscript(profileView, run) {
  if (!keys) throw new Error('Unlock the profile with your passkey to read task results.');
  if (!run.transcript) return null;
  if (!inboxPrivateKey) inboxPrivateKey = await vault.openInboxPrivateKey(keys.inboxKey, profileView.encryptedPrivateKey);
  const plain = await vault.openSealedToMe(inboxPrivateKey, run.transcript, `task-run|${profileView.id}|${run.id}`);
  return JSON.parse(new TextDecoder().decode(plain));
}

/* --------------------------------------------------------------- helpers */

function accountLabel(session) {
  const first = session && Array.isArray(session.accounts) ? session.accounts[0] : null;
  return first ? (first.emailAddress || first.username || '') : '';
}

/** A short name for this browser, shown next to its passkey in the Profile menu. */
function deviceLabel() {
  const ua = globalThis.navigator?.userAgent || '';
  const browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
  const os = /iPhone|iPad/.test(ua) ? 'iOS' : /Android/.test(ua) ? 'Android' : /Mac OS X/.test(ua) ? 'macOS' : /Windows/.test(ua) ? 'Windows' : /Linux/.test(ua) ? 'Linux' : '';
  return os ? `${browser} on ${os}` : browser;
}

export { NoPrfError };
