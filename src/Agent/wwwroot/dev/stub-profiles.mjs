/**
 * stub-profiles.mjs — server mode for the dev stub: profiles, passkeys,
 * settings, delegation and scheduled tasks, so the Profile menu and the Tasks
 * dialog can be exercised without the .NET server or a SmarterMail.
 *
 * What it does not do: verify WebAuthn (any credential the browser makes is
 * accepted, keyed by its id) or run a model. The cryptography the browser does
 * is real, and so is the transcript seal: a fake run is sealed to the
 * profile's public key with the server's format, so the browser opens it with
 * vault.js exactly as it would in production.
 *
 * SEED_PROFILE=1 creates a profile at startup and prints its recovery code:
 * paste it under "Use a recovery code" to get a profile session without a
 * passkey (two accounts, one delegated, a task key, one task with a result).
 */

import crypto from 'node:crypto';
import * as vault from '../js/vault.js';

const profiles = new Map();   // id -> profile
const ceremonies = new Map(); // ceremonyId -> { kind, profileId?, sessionId? }

const b64 = (buf) => Buffer.from(buf).toString('base64url');
const sha256 = (buf) => crypto.createHash('sha256').update(buf).digest();
const accountsCheck = (key) => crypto.createHmac('sha256', key).update('sma-accounts-check-v1').digest('base64url');

export const MODE = process.env.MODE === 'browser' ? 'browser' : 'server';
export const TASKS = process.env.TASKS !== 'false';

/** /api/config, as the server answers it. */
export function config(resumeEnabled, resumeDays) {
  return {
    mode: MODE,
    resume: { enabled: resumeEnabled, days: resumeEnabled ? resumeDays : 0 },
    profiles: { enabled: MODE === 'server' },
    tasks: { enabled: MODE === 'server' && TASKS, minIntervalMinutes: 15, maxPerProfile: 10, maxToolRounds: 15 }
  };
}

export const profileState = (s) => (s && s.profileId ? { id: s.profileId, unlocked: !!s.unlocked } : null);

function view(p) {
  return {
    id: p.id,
    unlocked: true,
    passkeys: [...p.passkeys.values()].map((k) => ({ id: k.id, label: k.label, createdAt: k.createdAt, lastUsedAt: k.lastUsedAt })),
    accounts: p.accounts.map((a) => ({
      id: a.id, live: true, handle: a.handle, login: a.emailAddress || a.username, baseUrl: a.baseUrl, role: a.role,
      readOnly: a.readOnly, delegated: !!a.delegated, state: 'ok'
    })),
    recovery: !!p.recovery,
    publicKey: p.publicKey,
    encryptedPrivateKey: p.encryptedPrivateKey,
    settingsVersion: p.settingsVersion,
    canDelegate: TASKS,
    tasksEnabled: TASKS,
    hasTaskKey: !!p.taskKey,
    tasksPaused: !!p.paused
  };
}

/**
 * Handles /profile/* and /tasks/* (and /config). Returns true when it answered.
 * ctx: { req, res, p, method, readBody, json, sessionOf, sessions, newSessionId, sessionCookie, publicSession, host }
 */
export async function handle(ctx) {
  const { p, method, json, res } = ctx;
  if (!p.startsWith('/profile') && !p.startsWith('/tasks')) return false;
  if (MODE !== 'server') {
    json(res, 404, { error: 'This server runs in browser-only mode: nothing is stored here.', code: 'SERVER_MODE_DISABLED' });
    return true;
  }

  const body = method === 'GET' || method === 'DELETE' ? {} : await ctx.readBody(ctx.req);
  const s = ctx.sessionOf(ctx.req);
  const profile = s && s.profileId ? profiles.get(s.profileId) : null;

  /* ---- anonymous: sign in ---- */

  if (p === '/profile/login/options' && method === 'POST') {
    const ceremonyId = b64(crypto.randomBytes(16));
    ceremonies.set(ceremonyId, { kind: 'login' });
    json(res, 200, { ceremonyId, options: { challenge: b64(crypto.randomBytes(16)), timeout: 60000, rpId: ctx.host, allowCredentials: [], userVerification: 'required', hints: [] } });
    return true;
  }

  if (p === '/profile/login' && method === 'POST') {
    const c = ceremonies.get(String(body.ceremonyId || ''));
    ceremonies.delete(String(body.ceremonyId || ''));
    const credId = body.credential && body.credential.rawId;
    const owner = [...profiles.values()].find((x) => x.passkeys.has(credId));
    if (!c || c.kind !== 'login' || !owner) {
      json(res, 401, { error: 'That passkey was not accepted here.', code: 'PASSKEY_INVALID' });
      return true;
    }
    owner.passkeys.get(credId).lastUsedAt = new Date().toISOString();
    openProfileSession(ctx, owner, owner.passkeys.get(credId).wrappedKey, credId);
    return true;
  }

  if (p === '/profile/recover' && method === 'POST') {
    const owner = profiles.get(String(body.profileId || ''));
    const auth = body.authKey ? Buffer.from(String(body.authKey), 'base64url') : null;
    if (!owner || !owner.recovery || !auth || !crypto.timingSafeEqual(sha256(auth), owner.recovery.authHash)) {
      json(res, 401, { error: 'That recovery code is not valid here.', code: 'RECOVERY_INVALID' });
      return true;
    }
    openProfileSession(ctx, owner, owner.recovery.wrappedKey, null);
    return true;
  }

  if (!s) {
    json(res, 401, { error: 'Not signed in.' });
    return true;
  }

  /* ---- creating a profile ---- */

  if (p === '/profile/register/options' && method === 'POST') {
    if (s.profileId) return json(res, 409, { error: 'This chat is already saved to a profile.', code: 'ALREADY_PROFILE' }), true;
    const profileId = b64(crypto.randomBytes(16));
    const ceremonyId = b64(crypto.randomBytes(16));
    ceremonies.set(ceremonyId, { kind: 'register', profileId, sessionId: s.id });
    const name = `${s.accounts[0]?.emailAddress || 'you'} (SmarterMail Agent profile)`;
    json(res, 200, { ceremonyId, profileId, options: registrationOptions(ctx.host, profileId, name, []) });
    return true;
  }

  if (p === '/profile' && method === 'POST') {
    const c = ceremonies.get(String(body.ceremonyId || ''));
    ceremonies.delete(String(body.ceremonyId || ''));
    if (!c || c.kind !== 'register' || c.sessionId !== s.id || !body.credential?.rawId) {
      json(res, 400, { error: 'The passkey could not be verified. Try again.', code: 'PASSKEY_INVALID' });
      return true;
    }
    const created = {
      id: c.profileId,
      passkeys: new Map([[body.credential.rawId, { id: body.credential.rawId, label: body.label || 'Passkey', wrappedKey: body.wrappedKey, createdAt: new Date().toISOString(), lastUsedAt: null }]]),
      recovery: body.recovery ? { wrappedKey: body.recovery.wrappedKey, authHash: sha256(Buffer.from(body.recovery.authKey, 'base64url')) } : null,
      check: accountsCheck(Buffer.from(body.accountsKey, 'base64url')),
      publicKey: body.publicKey,
      encryptedPrivateKey: body.encryptedPrivateKey,
      settings: body.settings || null,
      settingsVersion: body.settings ? 1 : 0,
      accounts: s.accounts.map((a) => ({ ...a })),
      taskKey: null,
      paused: false,
      tasks: new Map(),
      runs: []
    };
    profiles.set(created.id, created);
    const next = { ...s, id: ctx.newSessionId(), profileId: created.id, unlocked: true, remembered: false };
    ctx.sessions.delete(s.id);
    ctx.sessions.set(next.id, next);
    json(res, 200, ctx.publicSession(next), { 'Set-Cookie': ctx.sessionCookie(next) });
    return true;
  }

  if (!profile) {
    json(res, 404, { error: 'This chat is not saved to a profile.', code: 'NO_PROFILE' });
    return true;
  }

  /* ---- the profile ---- */

  if (p === '/profile/unlock' && method === 'POST') {
    const key = body.accountsKey ? Buffer.from(String(body.accountsKey), 'base64url') : null;
    if (!key || accountsCheck(key) !== profile.check) {
      json(res, 401, { error: 'That key does not open this profile.', code: 'PROFILE_KEY_INVALID' });
      return true;
    }
    s.unlocked = true;
    s.accounts = profile.accounts.map((a) => ({ ...a }));
    json(res, 200, { session: ctx.publicSession(s), skipped: [] });
    return true;
  }

  if (p === '/profile' && method === 'GET') return json(res, 200, view(profile)), true;
  if (p === '/profile/settings' && method === 'GET') return json(res, 200, { settings: profile.settings, version: profile.settingsVersion }), true;
  if (p === '/profile/settings' && method === 'PUT') {
    if (Number(body.version) !== profile.settingsVersion) {
      json(res, 409, { error: 'Settings changed in another browser.', code: 'SETTINGS_STALE', settings: profile.settings, version: profile.settingsVersion });
      return true;
    }
    profile.settings = body.settings;
    profile.settingsVersion += 1;
    json(res, 200, { version: profile.settingsVersion });
    return true;
  }

  if (p === '/profile/passkeys/options' && method === 'POST') {
    const ceremonyId = b64(crypto.randomBytes(16));
    ceremonies.set(ceremonyId, { kind: 'register', profileId: profile.id, sessionId: s.id });
    json(res, 200, { ceremonyId, options: registrationOptions(ctx.host, profile.id, 'SmarterMail Agent profile', [...profile.passkeys.keys()]) });
    return true;
  }
  if (p === '/profile/passkeys' && method === 'POST') {
    const c = ceremonies.get(String(body.ceremonyId || ''));
    ceremonies.delete(String(body.ceremonyId || ''));
    if (!c || c.sessionId !== s.id || !body.credential?.rawId) return json(res, 400, { error: 'The passkey could not be verified.', code: 'PASSKEY_INVALID' }), true;
    profile.passkeys.set(body.credential.rawId, { id: body.credential.rawId, label: body.label || 'Passkey', wrappedKey: body.wrappedKey, createdAt: new Date().toISOString(), lastUsedAt: null });
    json(res, 200, view(profile));
    return true;
  }
  const pk = /^\/profile\/passkeys\/([^/]+)$/.exec(p);
  if (pk && method === 'DELETE') {
    if (profile.passkeys.size <= 1) return json(res, 409, { error: 'That is the profile\'s last passkey.', code: 'LAST_PASSKEY' }), true;
    profile.passkeys.delete(decodeURIComponent(pk[1]));
    res.writeHead(204);
    res.end();
    return true;
  }
  if (p === '/profile/recovery' && method === 'PUT') {
    profile.recovery = body.wrappedKey ? { wrappedKey: body.wrappedKey, authHash: sha256(Buffer.from(body.authKey, 'base64url')) } : null;
    res.writeHead(204);
    res.end();
    return true;
  }
  const dg = /^\/profile\/accounts\/([^/]+)\/delegation$/.exec(p);
  if (dg && method === 'PUT') {
    const a = profile.accounts.find((x) => x.id === decodeURIComponent(dg[1]));
    if (!a) return json(res, 404, { error: 'That account is not signed in to this profile.', code: 'ACCOUNT_NOT_LIVE' }), true;
    a.delegated = !!body.enabled;
    json(res, 200, view(profile));
    return true;
  }
  if (p === '/profile/task-key' && method === 'PUT') { profile.taskKey = body.key || null; json(res, 200, view(profile)); return true; }
  if (p === '/profile/tasks-paused' && method === 'PUT') { profile.paused = !!body.paused; json(res, 200, view(profile)); return true; }
  if (p === '/profile' && method === 'DELETE') {
    profiles.delete(profile.id);
    for (const [id, x] of ctx.sessions) if (x.profileId === profile.id) ctx.sessions.delete(id);
    res.writeHead(204, { 'Set-Cookie': ctx.clearCookie });
    res.end();
    return true;
  }

  /* ---- tasks ---- */

  if (!TASKS) return json(res, 404, { error: 'Scheduled tasks are not enabled on this server.', code: 'TASKS_DISABLED' }), true;

  if (p === '/tasks' && method === 'GET') {
    json(res, 200, { tasks: [...profile.tasks.values()].map(taskView), unread: profile.runs.filter((r) => !r.read && r.status !== 'running').length });
    return true;
  }
  if (p === '/tasks' && method === 'POST') {
    const errors = validate(profile, body);
    if (errors.length) return json(res, 400, { error: errors.join(' '), errors, code: 'TASK_INVALID' }), true;
    const t = { id: b64(crypto.randomBytes(9)), enabled: body.enabled !== false, status: 'ok', definition: definitionOf(body), lastRunAt: null };
    profile.tasks.set(t.id, t);
    json(res, 200, taskView(t));
    return true;
  }
  const tk = /^\/tasks\/([^/]+)$/.exec(p);
  if (tk && !p.startsWith('/tasks/runs')) {
    const t = profile.tasks.get(decodeURIComponent(tk[1]));
    if (!t) return json(res, 404, { error: 'No such task.', code: 'TASK_NOT_FOUND' }), true;
    if (method === 'PUT') {
      const errors = validate(profile, body);
      if (errors.length) return json(res, 400, { error: errors.join(' '), errors, code: 'TASK_INVALID' }), true;
      t.definition = definitionOf(body);
      t.enabled = body.enabled !== false;
      json(res, 200, taskView(t));
      return true;
    }
    if (method === 'DELETE') {
      profile.tasks.delete(t.id);
      profile.runs = profile.runs.filter((r) => r.taskId !== t.id);
      res.writeHead(204);
      res.end();
      return true;
    }
  }
  const rn = /^\/tasks\/([^/]+)\/run$/.exec(p);
  if (rn && method === 'POST') {
    const t = profile.tasks.get(decodeURIComponent(rn[1]));
    if (!t) return json(res, 404, { error: 'No such task.', code: 'TASK_NOT_FOUND' }), true;
    const run = startRun(profile, t, 'manual', !!body.dryRun);
    json(res, 202, { runId: run.id });
    return true;
  }
  if (p === '/tasks/runs' && method === 'GET') {
    const taskId = new URL(ctx.req.url, 'http://x').searchParams.get('taskId');
    json(res, 200, profile.runs.filter((r) => !taskId || r.taskId === taskId).slice().reverse().map(({ transcript, ...r }) => ({ ...r, transcript: null })));
    return true;
  }
  const one = /^\/tasks\/runs\/([^/]+)$/.exec(p);
  if (one && method === 'GET') {
    const r = profile.runs.find((x) => x.id === decodeURIComponent(one[1]));
    if (!r) return json(res, 404, { error: 'No such run.', code: 'RUN_NOT_FOUND' }), true;
    json(res, 200, r);
    return true;
  }
  const rd = /^\/tasks\/runs\/([^/]+)\/read$/.exec(p);
  if (rd && method === 'POST') {
    const r = profile.runs.find((x) => x.id === decodeURIComponent(rd[1]));
    if (r) r.read = true;
    res.writeHead(204);
    res.end();
    return true;
  }

  json(res, 404, { error: 'no such endpoint' });
  return true;
}

function openProfileSession(ctx, owner, wrappedKey, credentialId) {
  const old = ctx.sessionOf(ctx.req);
  if (old) ctx.sessions.delete(old.id);
  const s = { id: ctx.newSessionId(), expiresAt: new Date(Date.now() + 12 * 3600 * 1000).toISOString(), accounts: [], profileId: owner.id, unlocked: false };
  ctx.sessions.set(s.id, s);
  ctx.json(ctx.res, 200, { profileId: owner.id, wrappedKey, credentialId, session: ctx.publicSession(s) }, { 'Set-Cookie': ctx.sessionCookie(s) });
}

function registrationOptions(host, profileId, name, exclude) {
  return {
    rp: { id: host, name: 'SmarterMail Agent' },
    user: { name, id: profileId, displayName: name },
    challenge: b64(crypto.randomBytes(16)),
    pubKeyCredParams: [{ type: 'public-key', alg: -7 }, { type: 'public-key', alg: -257 }],
    timeout: 60000,
    attestation: 'none',
    authenticatorSelection: { residentKey: 'required', requireResidentKey: true, userVerification: 'required' },
    excludeCredentials: exclude.map((id) => ({ type: 'public-key', id }))
  };
}

const definitionOf = (b) => ({
  version: 1, name: String(b.name || ''), prompt: String(b.prompt || ''), cron: String(b.cron || ''), timeZone: String(b.timeZone || 'UTC'),
  accountIds: b.accountIds || [], allowedWrites: b.allowedWrites || [], maxWrites: Number(b.maxWrites) || 5, model: String(b.model || ''),
  emailAccountId: b.emailAccountId || null
});

function validate(profile, b) {
  const errors = [];
  if (!String(b.name || '').trim()) errors.push('Give the task a name (at most 80 characters).');
  if (!String(b.prompt || '').trim()) errors.push('Describe what the task should do (at most 4000 characters).');
  if (!/^\S+ \S+ \S+ \S+ \S+$/.test(String(b.cron || '').trim())) errors.push('The schedule is not a valid five-field cron expression in a known time zone.');
  else if (/^(\*|\*\/([1-9]|1[0-4]))\s/.test(String(b.cron).trim())) errors.push('Runs must be at least 15 minutes apart.');
  if (!(b.accountIds || []).length) errors.push('Pick at least one account for the task.');
  for (const id of b.accountIds || []) if (!profile.accounts.find((a) => a.id === id && a.delegated)) errors.push('Every account a task uses must allow scheduled tasks (Profile menu).');
  return [...new Set(errors)];
}

function nextRun() { const d = new Date(); d.setUTCDate(d.getUTCDate() + 1); d.setUTCHours(14, 0, 0, 0); return d.toISOString(); }

const taskView = (t) => ({ id: t.id, enabled: t.enabled, status: t.status, consecutiveFailures: 0, nextRunAt: t.enabled ? nextRun() : null, lastRunAt: t.lastRunAt, definition: t.definition });

/** A fake run: a moment "running", then a report sealed to the profile's public key the way the server seals it. */
function startRun(profile, t, trigger, dryRun) {
  const run = {
    id: b64(crypto.randomBytes(12)), taskId: t.id, startedAt: new Date().toISOString(), finishedAt: null, status: 'running',
    dryRun, trigger, errorCode: null, errorMessage: null, toolCalls: 2, writes: t.definition.allowedWrites.length ? 1 : 0,
    promptTokens: 1200, completionTokens: 180, read: false, transcript: null
  };
  profile.runs.push(run);
  t.lastRunAt = run.startedAt;
  setTimeout(async () => {
    const steps = [
      { kind: 'tool', tool: 'get_emails', arguments: '{"folderId":"Inbox","take":10}', content: '{"items":[{"subject":"Invoice 1042","from":"billing@example.com"}]}', account: null, isError: false, simulated: false }
    ];
    if (t.definition.allowedWrites.length) {
      steps.push({ kind: 'tool', tool: t.definition.allowedWrites[0], arguments: '{}', content: dryRun ? '{"dryRun":true}' : '{"success":true}', account: null, isError: false, simulated: dryRun });
    }
    const transcript = {
      version: 1, taskName: t.definition.name, prompt: t.definition.prompt, model: t.definition.model, dryRun, startedAt: run.startedAt,
      stop: 'completed', final: `**${t.definition.name}**\n\n- One unread message: *Invoice 1042* from billing@example.com.\n- Nothing needs an answer today.`,
      steps, error: null, emailed: !!t.definition.emailAccountId && !dryRun
    };
    run.transcript = await sealToPublicKey(Buffer.from(JSON.stringify(transcript)), profile.publicKey, `task-run|${profile.id}|${run.id}`);
    run.status = 'ok';
    run.finishedAt = new Date().toISOString();
  }, 1500);
  return run;
}

/** ProfileCrypto.SealToPublicKey, in node: [0x01][epk 65][nonce 12][ciphertext][tag 16]. */
async function sealToPublicKey(plaintext, spkiB64, context) {
  const subtle = crypto.webcrypto.subtle;
  const recipient = await subtle.importKey('spki', Buffer.from(spkiB64, 'base64url'), { name: 'ECDH', namedCurve: 'P-256' }, false, []);
  const eph = await subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, true, ['deriveBits']);
  const epk = new Uint8Array(await subtle.exportKey('raw', eph.publicKey));
  const shared = await subtle.deriveBits({ name: 'ECDH', public: recipient }, eph.privateKey, 256);
  const base = await subtle.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
  const key = await subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt: epk, info: new TextEncoder().encode('sma-run-transcript-v1') },
    base, { name: 'AES-GCM', length: 256 }, false, ['encrypt']);
  const iv = crypto.randomBytes(12);
  const ct = new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv, additionalData: new TextEncoder().encode(context) }, key, plaintext));
  return b64(Buffer.concat([Buffer.from([1]), epk, iv, ct]));
}

/**
 * SEED_PROFILE=1: a profile with real key material, opened by its recovery code.
 * The accounts are plain stub accounts; `fields` builds them the way the stub does.
 */
export async function seed(newAccount) {
  if (MODE !== 'server' || process.env.SEED_PROFILE !== '1') return null;
  const id = b64(crypto.randomBytes(16));
  const pk = vault.newProfileKey();
  const keys = await vault.deriveProfileKeys(pk);
  const inbox = await vault.newInboxKeyPair(keys.inboxKey);
  const code = vault.newRecoveryCode(id);
  const recovery = await vault.recoveryMaterial(code, pk);
  const accounts = [
    newAccount({ hostname: 'mail.example.com', email: 'alice@example.com', readOnly: false }),
    newAccount({ hostname: 'mail.example.com', email: 'domainadmin@example.com', readOnly: true })
  ];
  accounts[0].handle = accounts[0].emailAddress;
  accounts[1].handle = accounts[1].emailAddress;
  accounts[0].delegated = true;
  const profile = {
    id,
    passkeys: new Map([['seed', { id: 'seed', label: 'Seeded (no real passkey)', wrappedKey: 'AA', createdAt: new Date().toISOString(), lastUsedAt: null }]]),
    recovery: { wrappedKey: recovery.wrappedKey, authHash: sha256(Buffer.from(recovery.authKey, 'base64url')) },
    check: accountsCheck(Buffer.from(keys.accountsKey)),
    publicKey: inbox.publicKey,
    encryptedPrivateKey: inbox.encryptedPrivateKey,
    settings: await vault.sealJson(keys.settingsKey, { v: 1, openRouterKey: 'sk-or-v1-stub', model: 'anthropic/claude-haiku-5.5', toolsOff: [] }),
    settingsVersion: 1,
    accounts,
    taskKey: 'sk-or-stub',
    paused: false,
    tasks: new Map(),
    runs: []
  };
  const task = {
    id: b64(crypto.randomBytes(9)), enabled: true, status: 'ok', lastRunAt: null,
    definition: definitionOf({ name: 'Morning summary', prompt: 'Summarise unread mail from the last day.', cron: '0 7 * * 1-5', timeZone: 'America/Phoenix', accountIds: [accounts[0].id], model: 'anthropic/claude-haiku-5.5' })
  };
  profile.tasks.set(task.id, task);
  profiles.set(id, profile);
  startRun(profile, task, 'schedule', false);
  return code;
}
