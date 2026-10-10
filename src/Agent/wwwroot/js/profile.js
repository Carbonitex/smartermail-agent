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
 * the page's lifetime (they are equally sensitive). Until the profile's idle
 * timeout passes without activity (a successful request from a page holding
 * the keys: the same clock as the server's) after the passkey or recovery code
 * was used,
 * the settings and inbox keys are also
 * kept in IndexedDB as non-extractable CryptoKeys, so a reload, a new tab or a
 * restarted browser on a still-unlocked profile session reopens the settings
 * (the OpenRouter key) without asking again. PK and the accounts key are never
 * kept: adding a passkey or a recovery code still needs a fresh sign-in.
 * `sma.profileHint` in localStorage only remembers that this browser has a
 * profile here (to lead with "Sign in with passkey"); it holds no secret.
 */

import * as api from './api.js';
import * as vault from './vault.js';
import { createPasskey, getPasskey, NoPrfError } from './passkey.js';

const HINT = 'sma.profileHint';
const OPTIONS_MAX_AGE_MS = 90_000;   // server ceremonies live two minutes
const WARM_DEFAULT_MS = 30 * 60_000; // until the server says otherwise (SessionResponse.profile.idleMinutes)
const WARM_TOUCH_MS = 60_000;        // activity moves the deadline at most this often
const WARM_DB = 'sma-profile';
const WARM_STORE = 'keys';

let config = null;
let keys = null;              // { accountsKey, settingsKey, inboxKey } while unlocked on this page
let profileKey = null;        // PK itself, for wrapping it again (another passkey, a new recovery code)
let inboxPrivateKey = null;   // CryptoKey, opened on first use
let settingsVersion = 0;
let loginOptions = null;      // { at, promise } prefetched for the passkey button
let registerOptions = null;
let warmProfileId = null;     // the profile whose keys this page keeps warm
let warmTouchedAt = 0;
let warmMs = WARM_DEFAULT_MS;  // the profile's idle timeout: a new page reopens the settings within it

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
  warmProfileId = null;
}

/* ------------------------------------------------------------ warm keys */

function warmDb() {
  return new Promise((resolve, reject) => {
    const req = globalThis.indexedDB.open(WARM_DB, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(WARM_STORE);
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function warmOp(mode, fn) {
  const db = await warmDb();
  try {
    return await new Promise((resolve, reject) => {
      const tx = db.transaction(WARM_STORE, mode);
      const req = fn(tx.objectStore(WARM_STORE));
      tx.oncomplete = () => resolve(req.result);
      tx.onerror = tx.onabort = () => reject(tx.error);
    });
  } finally {
    db.close();
  }
}

/** Keeps the settings and inbox keys (non-extractable) for WARM_MS from now. Best effort. */
async function keepWarm(profileId) {
  if (!keys || !profileId) return;
  warmProfileId = profileId;
  warmTouchedAt = Date.now();
  const entry = { v: 1, profileId, until: warmTouchedAt + warmMs, settingsKey: keys.settingsKey, inboxKey: keys.inboxKey };
  try { await warmOp('readwrite', (s) => s.put(entry, 'current')); } catch { /* IndexedDB unavailable */ }
}

/**
 * Follows the session's profile idle timeout (Settings). A change applies to
 * the kept keys at once, so a shorter timeout also shortens the current window.
 */
export function followSession(session) {
  const minutes = session && session.profile && session.profile.idleMinutes;
  const ms = Number.isFinite(minutes) && minutes > 0 ? minutes * 60_000 : WARM_DEFAULT_MS;
  if (ms === warmMs) return;
  warmMs = ms;
  if (keys && warmProfileId) keepWarm(warmProfileId);
}

/** Activity on a page holding the keys: the kept keys last the idle timeout from now (at most once a minute). */
export function touchWarm() {
  if (!keys || !warmProfileId || Date.now() - warmTouchedAt < WARM_TOUCH_MS) return;
  keepWarm(warmProfileId);
}

/** Forgets the kept keys (logout, profile deleted). The in-memory keys are lock()'s job. */
export async function forgetWarm() {
  try { await warmOp('readwrite', (s) => s.delete('current')); } catch { /* IndexedDB unavailable */ }
}

/**
 * On a new page with an unlocked profile session: the kept keys, if this
 * browser was active on the profile within its idle timeout, take this page's keys and open the
 * profile's settings. Resolves the settings, or null (nothing kept, expired,
 * another profile, unreadable).
 */
export async function reopenWarm(profileId) {
  let entry = null;
  try { entry = await warmOp('readonly', (s) => s.get('current')); } catch { return null; }
  if (!entry || entry.v !== 1) return null;
  if (entry.profileId !== profileId || !(entry.until > Date.now())) {
    await forgetWarm();
    return null;
  }
  lock();
  keys = { settingsKey: entry.settingsKey, inboxKey: entry.inboxKey };
  try {
    const stored = await api.profileSettings();
    settingsVersion = stored.version || 0;
    const settings = await openSettings(stored.settings);
    if (!settings) { lock(); await forgetWarm(); return null; }
    await keepWarm(profileId);
    return settings;
  } catch {
    lock();
    return null;
  }
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

/** What the profile keeps for the chat: { openRouterKey, model, toolsOff: [...], allowChanges }. */
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
export async function saveSettings({ openRouterKey, model, toolsOff, allowChanges }) {
  if (!keys) return false;
  const sealed = await vault.sealJson(keys.settingsKey,
    { v: 1, openRouterKey: openRouterKey || '', model: model || '', toolsOff: [...(toolsOff || [])], allowChanges: !!allowChanges });
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
  await keepWarm(signIn.profileId);
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
  await keepWarm(profileId);
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

/**
 * Opens any value the server sealed to this profile's public key with the given
 * context (approval proposals: `task-proposal|<profile>|<id>`, results:
 * `task-proposal-result|<profile>|<id>`), as parsed JSON.
 */
export async function openSealedJson(profileView, sealed, context) {
  if (!keys) throw new Error('Unlock the profile with your passkey to read this.');
  if (!sealed) return null;
  if (!inboxPrivateKey) inboxPrivateKey = await vault.openInboxPrivateKey(keys.inboxKey, profileView.encryptedPrivateKey);
  return JSON.parse(new TextDecoder().decode(await vault.openSealedToMe(inboxPrivateKey, sealed, context)));
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
