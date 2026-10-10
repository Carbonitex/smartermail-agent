/**
 * api.js — thin fetch wrapper over the smartermail-agent HTTP contract.
 *
 * All URLs are derived from the current document path so the same files work at the
 * site root (http://localhost:8080/) and under a PATH_BASE behind a reverse proxy
 * (https://example.com/mail-agent/). No absolute origins anywhere.
 */

/** Directory the page lives in, always with a trailing slash. */
export function appRoot() {
  const p = location.pathname;
  return p.endsWith('/') ? p : p.replace(/[^/]*$/, '');
}

const api = (path) => appRoot() + 'api' + path;

export class ApiError extends Error {
  constructor(message, status, body) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
    /** Machine-readable reason from the server, e.g. "CHANGE_PASSWORD_NEEDED". */
    this.code = (body && typeof body.code === 'string') ? body.code : null;
    /** Only set by POST /auth/two-factor on a wrong code. */
    this.attemptsLeft = (body && typeof body.attemptsLeft === 'number') ? body.attemptsLeft : null;
  }
}

let unauthorizedHandler = null;
/**
 * Called with the ApiError whenever any call comes back 401, so the UI can drop
 * back to login. A 401 that carries a `code` is a rejected credential or 2FA
 * code (login, add account, two-factor), not a dead session; the handler gets
 * the error so it can tell the two apart.
 */
export function onUnauthorized(fn) { unauthorizedHandler = fn; }

/* "Remember me on this device": every request says which bundle version the
   browser holds (X-Resume-Version, 0 for none); a response to the browser's
   cookie answers with a newer number when the saved bundle has gone stale
   (SmarterMail rotated a refresh token, or an account changed). */
const RESUME_HEADER = 'X-Resume-Version';
let resumeVersionSource = null;
let resumeVersionHandler = null;

/**
 * source() → the saved bundle's version; onNewer(version) is called when a
 * response announces a newer one (then fetch it with getResume()). Not called
 * for /auth/resume itself, whose bodies carry the bundle.
 */
export function trackResumeVersion(source, onNewer) {
  resumeVersionSource = source;
  resumeVersionHandler = onNewer;
}

let activityHandler = null;

/** fn() after every successful request: the server counted it as session activity. */
export function onActivity(fn) { activityHandler = fn; }

async function request(path, { method = 'GET', body, signal } = {}) {
  const headers = body === undefined ? { Accept: 'application/json' } : { Accept: 'application/json', 'Content-Type': 'application/json' };
  if (resumeVersionSource) headers[RESUME_HEADER] = String(resumeVersionSource() || 0);

  let res;
  try {
    res = await fetch(api(path), {
      method,
      credentials: 'same-origin',
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal
    });
  } catch (e) {
    if (e && e.name === 'AbortError') throw e;
    throw new ApiError('Could not reach the agent server. Is it running?', 0, null);
  }

  const announced = res.headers && typeof res.headers.get === 'function' ? Number(res.headers.get(RESUME_HEADER)) : NaN;
  if (resumeVersionHandler && Number.isSafeInteger(announced) && announced > 0 && !path.startsWith('/auth/resume')) {
    resumeVersionHandler(announced);
  }

  if (res.status === 204) return null;

  const text = await res.text();
  let data = null;
  if (text) { try { data = JSON.parse(text); } catch { data = null; } }

  if (!res.ok) {
    const msg = (data && (data.error || data.message)) || text || `HTTP ${res.status}`;
    const err = new ApiError(msg, res.status, data);
    if (res.status === 401 && unauthorizedHandler) unauthorizedHandler(err);
    throw err;
  }
  if (activityHandler) activityHandler();
  return data;
}

/* ---- contract ---- */

/**
 * The SessionResponse every successful sign-in step returns (login, two-factor,
 * add account) and GET /api/auth/session reads back:
 *
 *   { expiresAt, maxAccounts, remembered,
 *     accounts: [{ id, handle, role, username, emailAddress, domain, baseUrl, readOnly }],
 *     mcpToken: { active, expiresAt } }
 *
 * `role` is "User" | "DomainAdmin" | "SysAdmin". `handle` is what the model
 * passes as a tool's `account` argument: the email address for mailbox and
 * domain-admin accounts, `sysadmin:<username>@<host>` for a system admin.
 * `remembered` is true while "Remember me on this device" is on.
 */

/**
 * POST /api/auth/login — always starts a NEW session (any session on the
 * cookie is destroyed). Two possible 200 bodies, both returned as-is:
 *
 *   SessionResponse                                                  — signed in, cookie set
 *   { twoFactorRequired: true, challengeId, method, emailAddress,
 *     expiresAt }                                                    — no cookie yet
 *
 * `method` is "rfc6238" (authenticator app) or "email".
 *
 * Failures throw ApiError with `.code` set:
 *   401 USERNAME_OR_PASSWORD_INCORRECT
 *   403 CHANGE_PASSWORD_NEEDED | PASSWORD_EXPIRED | TWO_FACTOR_SETUP_REQUIRED |
 *       APP_PASSWORD_REQUIRED   — `.message` explains what to finish in webmail
 *   429 rate limited (per visitor IP; no body), or
 *   429 code HOST_THROTTLED — too many failed sign-ins to that mail server from
 *       this service; `.message` says how long, `body.retryAfterSeconds` exact
 */
export function login({ hostname, email, password, readOnly = true }) {
  return request('/auth/login', { method: 'POST', body: { hostname, email, password, readOnly } });
}

/**
 * POST /api/accounts — adds another account to the current session. Same body
 * and the same two 200 shapes as login(); a challenge from here is bound to
 * this session and can only be completed from it. The conversation is kept.
 *
 * Throws ApiError with, in addition to login()'s failures:
 *   409 code ACCOUNT_LIMIT  — the session already holds maxAccounts accounts
 *   401 without a code      — the session itself is gone
 */
export function addAccount({ hostname, email, password, readOnly = true }) {
  return request('/accounts', { method: 'POST', body: { hostname, email, password, readOnly } });
}

/**
 * DELETE /api/accounts/{id} → 204. Removing the last account ends the session
 * and clears the cookie; the next call answers 401.
 */
export function removeAccount(id) {
  return request('/accounts/' + encodeURIComponent(id), { method: 'DELETE' });
}

/**
 * POST /api/auth/two-factor → the same SessionResponse a normal login returns.
 * Completes either kind of challenge: a new session (sets the cookie) or an
 * account being added to the current one.
 *
 * Throws ApiError with:
 *   401 code INVALID_TWO_FACTOR_CODE, `.attemptsLeft` remaining
 *   410 code CHALLENGE_EXPIRED       — start over from the login view
 *   429 rate limited (shared with the login limiter), or code HOST_THROTTLED
 *       as for login()
 */
export function twoFactor(challengeId, code) {
  return request('/auth/two-factor', { method: 'POST', body: { challengeId, code } });
}

/** POST /api/auth/logout → 204 */
export function logout() {
  return request('/auth/logout', { method: 'POST' });
}

/** GET /api/auth/session → SessionResponse, or throws ApiError(401). */
export function session() {
  return request('/auth/session');
}

/* ---- remember me on this device ---- */

/** GET /api/auth/resume/config → { enabled, days }. `enabled: false` hides the option. */
export function resumeConfig() {
  return request('/auth/resume/config');
}

/**
 * PUT /api/auth/resume → { bundle, version, rememberedUntil }: remember this
 * session (and every account added to it later) on this device.
 * 410 code RESUME_EXPIRED when this sign-in's remember period is over.
 */
export function enableResume() {
  return request('/auth/resume', { method: 'PUT' });
}

/** GET /api/auth/resume → { bundle, version, rememberedUntil }, or 404 NOT_REMEMBERED. */
export function getResume() {
  return request('/auth/resume');
}

/** DELETE /api/auth/resume → 204: stop remembering (the session itself carries on). */
export function disableResume() {
  return request('/auth/resume', { method: 'DELETE' });
}

/**
 * POST /api/auth/resume { bundle } → the SessionResponse fields plus
 * { bundle, version, rememberedUntil, skipped: [{ baseUrl, login, role, reason }] },
 * and the session cookie. The bundle sent is dead afterwards: save the new one.
 *
 * Throws ApiError with:
 *   401 code RESUME_EXPIRED     — discard the saved bundle, sign in again
 *   400 code RESUME_INVALID     — likewise (tampered, or sealed by another key)
 *   404 code RESUME_DISABLED    — the server no longer offers it; likewise
 *   503 code RESUME_UNAVAILABLE — a mail server did not answer; keep it, retry
 *   429 rate limited, or code HOST_THROTTLED; keep it
 */
export function resume(bundle) {
  return request('/auth/resume', { method: 'POST', body: { bundle } });
}

/**
 * POST /api/auth/token → { token, expiresAt }: a new MCP token (`sma_mcp_…`)
 * for `Authorization: Bearer` against /mcp (Cursor, Claude Code). Replaces any
 * previous token. The server keeps only a hash, so this is the one time the
 * value is visible; SessionResponse.mcpToken says whether one is active. The
 * token opens /mcp only, never /api/*.
 */
export function createMcpToken() {
  return request('/auth/token', { method: 'POST' });
}

/** DELETE /api/auth/token → 204: the session's MCP token stops working. */
export function revokeMcpToken() {
  return request('/auth/token', { method: 'DELETE' });
}

/* ---- server mode: what this server offers ---- */

/**
 * GET /api/config → { mode: "server" | "browser", resume: { enabled, days },
 * profiles: { enabled }, tasks: { enabled, minIntervalMinutes, maxPerProfile, maxToolRounds } }.
 */
export function config() {
  return request('/config');
}

/* ---- server mode: profiles (passkey-encrypted, see js/vault.js) ---- */

/** POST /api/profile/register/options → { ceremonyId, profileId, options } for navigator.credentials.create. */
export function profileRegisterOptions(label) {
  return request('/profile/register/options', { method: 'POST', body: { label: label || null } });
}

/**
 * POST /api/profile → SessionResponse of the new profile session (new cookie).
 * body: { ceremonyId, credential, wrappedKey, accountsKey, publicKey, encryptedPrivateKey,
 *         settings, recovery: { wrappedKey, authKey } | null, label }
 */
export function createProfile(body) {
  return request('/profile', { method: 'POST', body });
}

/** POST /api/profile/login/options → { ceremonyId, options } for navigator.credentials.get. */
export function profileLoginOptions() {
  return request('/profile/login/options', { method: 'POST', body: {} });
}

/** POST /api/profile/login → { profileId, wrappedKey, credentialId, session } and a profile-session cookie (still locked). */
export function profileLogin(ceremonyId, credential) {
  return request('/profile/login', { method: 'POST', body: { ceremonyId, credential } });
}

/** POST /api/profile/recover → the same as profileLogin, with the recovery-wrapped key. */
export function profileRecover(profileId, authKey) {
  return request('/profile/recover', { method: 'POST', body: { profileId, authKey } });
}

/** POST /api/profile/unlock → { session, skipped: [{ id, baseUrl, login, role, reason }] }. 401 PROFILE_KEY_INVALID. */
export function profileUnlock(accountsKey) {
  return request('/profile/unlock', { method: 'POST', body: { accountsKey } });
}

/** GET /api/profile → passkeys, stored accounts (live / needs sign-in, delegated), task settings. */
export function profile() {
  return request('/profile');
}

/** GET /api/profile/settings → { settings, version }. */
export function profileSettings() {
  return request('/profile/settings');
}

/** PUT /api/profile/settings → { version }; 409 SETTINGS_STALE with the current { settings, version }. */
export function saveProfileSettings(settings, version) {
  return request('/profile/settings', { method: 'PUT', body: { settings, version } });
}

export function addPasskeyOptions() {
  return request('/profile/passkeys/options', { method: 'POST', body: {} });
}

export function addPasskey(body) {
  return request('/profile/passkeys', { method: 'POST', body });
}

export function deletePasskey(credentialId) {
  return request('/profile/passkeys/' + encodeURIComponent(credentialId), { method: 'DELETE' });
}

/** PUT /api/profile/recovery { wrappedKey, authKey } replaces the recovery code; null removes it. */
export function setRecovery(material) {
  return request('/profile/recovery', { method: 'PUT', body: material || {} });
}

/** PUT /api/profile/accounts/{id}/delegation → the profile view. Scheduled tasks may use the account while nobody is signed in. */
export function setDelegation(accountId, enabled) {
  return request('/profile/accounts/' + encodeURIComponent(accountId) + '/delegation', { method: 'PUT', body: { enabled } });
}

/** PUT /api/profile/task-key → the profile view. The OpenRouter key scheduled tasks use; null clears it. */
export function setTaskKey(key) {
  return request('/profile/task-key', { method: 'PUT', body: { key: key || null } });
}

/** PUT /api/profile/idle → the profile view; minutes null = the server default. 400 IDLE_OUT_OF_RANGE. */
export function setProfileIdle(minutes) {
  return request('/profile/idle', { method: 'PUT', body: { minutes } });
}

export function setTasksPaused(paused) {
  return request('/profile/tasks-paused', { method: 'PUT', body: { paused } });
}

/** DELETE /api/profile → 204: every account revoked, every session of the profile ended. */
export function deleteProfile() {
  return request('/profile', { method: 'DELETE' });
}

/* ---- server mode: scheduled tasks ---- */

/** GET /api/tasks → { tasks: [{ id, enabled, status, nextRunAt, lastRunAt, definition }], unread }. */
export function tasks() {
  return request('/tasks');
}

/** body: { name, prompt, cron, timeZone, accountIds, allowedWrites, maxWrites, model, emailAccountId, enabled } */
export function createTask(body) {
  return request('/tasks', { method: 'POST', body });
}

export function updateTask(id, body) {
  return request('/tasks/' + encodeURIComponent(id), { method: 'PUT', body });
}

export function deleteTask(id) {
  return request('/tasks/' + encodeURIComponent(id), { method: 'DELETE' });
}

/** POST /api/tasks/{id}/run → 202 { runId }. dryRun: changes are simulated, nothing is mailed. */
export function runTask(id, dryRun) {
  return request('/tasks/' + encodeURIComponent(id) + '/run', { method: 'POST', body: { dryRun: !!dryRun } });
}

/** GET /api/tasks/runs → newest first, without transcripts. */
export function taskRuns(taskId) {
  return request('/tasks/runs' + (taskId ? '?taskId=' + encodeURIComponent(taskId) : ''));
}

/** GET /api/tasks/runs/{id} → the run with its transcript, sealed to the profile's public key. */
export function taskRun(runId) {
  return request('/tasks/runs/' + encodeURIComponent(runId));
}

export function markRunRead(runId) {
  return request('/tasks/runs/' + encodeURIComponent(runId) + '/read', { method: 'POST' });
}

/** Absolute URL of the MCP endpoint, for an MCP client's config. */
export const mcpUrl = () => location.origin + appRoot() + 'mcp';

/**
 * GET /api/tools → [{ name, description, inputSchema, category, scope, write }].
 * Only tools at least one account may run are listed (write tools are omitted
 * for read-only accounts). With several accounts, `inputSchema` carries an
 * injected `account` property: an enum of the handles allowed to run it.
 */
export function tools() {
  return request('/tools');
}

/**
 * POST /api/tools/call → { isError, content, account }, `account` being the
 * handle the call ran as (null when the server did not say).
 * Tool-level failures come back 200 with isError:true — including a missing,
 * unknown or wrong-role `account`, whose message lists the valid handles so the
 * model can correct itself. Transport/authorisation failures (404 unknown tool,
 * 403 write on a read-only account) throw and are normalised here into the same
 * shape so the loop can always feed something to the model.
 */
export async function callTool(name, args, signal) {
  const asAccount = args && typeof args.account === 'string' && args.account ? args.account : null;
  try {
    const r = await request('/tools/call', { method: 'POST', body: { name, arguments: args || {} }, signal });
    return {
      isError: !!(r && r.isError),
      content: (r && typeof r.content === 'string') ? r.content : JSON.stringify(r ?? null),
      account: (r && typeof r.account === 'string' && r.account) ? r.account : asAccount
    };
  } catch (e) {
    if (e && e.name === 'AbortError') throw e;
    const fail = (content) => ({ isError: true, content, account: asAccount });
    if (e instanceof ApiError) {
      if (e.status === 404) return fail(`Tool "${name}" does not exist on this session.`);
      if (e.status === 403) return fail(readOnlyRefusal(name, asAccount, e));
      if (e.status === 429) return fail('Rate limited by the agent server. Wait a moment and try again.');
      if (e.status === 401) throw e;
      return fail(`Tool call failed: ${e.message}`);
    }
    return fail(`Tool call failed: ${e.message}`);
  }
}

/** The 403 a write on a read-only account gets, always naming the account. */
function readOnlyRefusal(name, account, err) {
  const server = err.body && typeof err.body.error === 'string' ? err.body.error.trim() : '';
  const who = account ? `the account "${account}"` : 'that account';
  const what = server && (!account || server.includes(account))
    ? server
    : `"${name}" changes data and ${who} is read-only.`;
  return `Refused: ${what} To allow it, the user must remove ${who} and add it again with "Allow changes" ticked.`;
}

export const healthUrl = () => appRoot() + 'health';
