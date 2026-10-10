#!/usr/bin/env node
/**
 * stub-server.mjs — development stand-in for the .NET agent server.
 *
 * Serves wwwroot/ at /mail-agent/ and implements the HTTP contract (see
 * CLAUDE.md, and plan-multiple-accounts-andmore.md for the multi-account
 * parts) with a handful of fake mailbox, domain-admin and sysadmin tools and
 * canned data, so the frontend can be exercised without the .NET backend.
 *
 *   node wwwroot/dev/stub-server.mjs      →  http://localhost:8787/mail-agent/
 *
 * This file is dev-only. Keep it out of the image via .dockerignore.
 */

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { handle as handleProfiles, config as serverConfig, profileState, seed as seedProfile, MODE } from './stub-profiles.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..');               // wwwroot/
const PORT = Number(process.env.PORT || 8787);
const BASE = process.env.PATH_BASE || '/mail-agent';

/* ------------------------------------------------------------- fake tools */

/* Each tool carries the scope and category the real catalog reports. Scope
   decides which account roles may run it; `write` tools are hidden from, and
   refused for, read-only accounts. */
const TOOLS = [
  {
    name: 'list_folder_info_by_type',
    description: 'List mail folders of a given type with message and unread counts.',
    scope: 'Mailbox', category: 'Folders', write: false,
    inputSchema: {
      type: 'object',
      properties: {
        folderType: { type: 'string', description: 'mail | calendar | contacts | tasks | notes', default: 'mail' }
      },
      required: []
    }
  },
  {
    name: 'get_emails',
    description: 'Get a page of emails from a folder. Folder ids look like "owner/Inbox".',
    scope: 'Mailbox', category: 'Mail', write: false,
    inputSchema: {
      type: 'object',
      properties: {
        folderId: { type: 'string', description: 'e.g. "user@example.com/Inbox"' },
        take: { type: 'integer', description: 'How many to return (default 10)', default: 10 },
        unreadOnly: { type: 'boolean', description: 'Only unread messages', default: false }
      },
      required: ['folderId']
    }
  },
  {
    name: 'send_email',
    description: 'Send an email from the signed-in account. WRITE TOOL.',
    scope: 'Mailbox', category: 'Mail', write: true,
    inputSchema: {
      type: 'object',
      properties: {
        to: { type: 'string', description: 'Comma-separated recipients' },
        subject: { type: 'string' },
        body: { type: 'string' }
      },
      required: ['to', 'subject', 'body']
    }
  },
  {
    name: 'domain_list_users',
    description: "List the users of the domain admin's own domain.",
    scope: 'DomainAdmin', category: 'Domain', write: false,
    inputSchema: { type: 'object', properties: {}, required: [] }
  },
  {
    name: 'domain_list_aliases',
    description: "List the aliases of the domain admin's own domain.",
    scope: 'DomainAdmin', category: 'Domain', write: false,
    inputSchema: { type: 'object', properties: {}, required: [] }
  },
  {
    name: 'domain_create_alias',
    description: "Create an alias on the domain admin's own domain. WRITE TOOL.",
    scope: 'DomainAdmin', category: 'Domain', write: true,
    inputSchema: {
      type: 'object',
      properties: {
        name: { type: 'string', description: 'Alias local part, e.g. "sales"' },
        targets: { type: 'array', items: { type: 'string' }, description: 'Addresses the alias delivers to' }
      },
      required: ['name', 'targets']
    }
  },
  {
    name: 'get_domains',
    description: 'List all domains on the server.',
    scope: 'SysAdmin', category: 'Domains', write: false,
    inputSchema: { type: 'object', properties: {}, required: [] }
  },
  {
    name: 'get_spool_messages',
    description: 'List messages waiting in the outbound spool.',
    scope: 'SysAdmin', category: 'Spool', write: false,
    inputSchema: {
      type: 'object',
      properties: { take: { type: 'integer', description: 'How many to return (default 20)', default: 20 } },
      required: []
    }
  },
  {
    name: 'delete_domain',
    description: 'Delete a domain and all of its data from the server. WRITE TOOL.',
    scope: 'SysAdmin', category: 'Domains', write: true, destructive: true,
    inputSchema: {
      type: 'object',
      properties: { domain: { type: 'string', description: 'Domain name, e.g. example.com' } },
      required: ['domain']
    }
  },
  {
    name: 'enable_dkim',
    description: 'Enable DKIM signing for a domain. WRITE TOOL.',
    scope: 'SysAdmin', category: 'DKIM', write: true,
    inputSchema: {
      type: 'object',
      properties: { domain: { type: 'string', description: 'Domain name, e.g. example.com' } },
      required: ['domain']
    }
  }
];

/** Which account roles may run a tool of each scope. */
const SCOPE_ROLES = {
  Mailbox: ['User', 'DomainAdmin'],
  DomainAdmin: ['DomainAdmin'],
  SysAdmin: ['SysAdmin']
};

const roleAllows = (role, scope) => (SCOPE_ROLES[scope] || []).includes(role);

const CANNED_EMAILS = [
  { id: '4821', from: 'billing@contoso.com', fromName: 'Contoso Billing', subject: 'Invoice 10423 is overdue', date: '2026-09-11T08:14:00Z', isRead: false, size: 4821, preview: 'Our records show invoice 10423 for $420.00 remains unpaid…' },
  { id: '4820', from: 'ci@build.example.net', fromName: 'Build Server', subject: '[smartermail-agent] build #17 succeeded', date: '2026-09-11T07:02:00Z', isRead: false, size: 2210, preview: 'Pipeline finished in 1m 48s. Image pushed.' },
  { id: '4819', from: 'jen@example.org', fromName: 'Jen Alvarez', subject: 'Re: lunch Thursday?', date: '2026-09-10T21:40:00Z', isRead: true, size: 1180, preview: 'Thursday works — 12:30 at the usual place?' },
  { id: '4818', from: 'news@fabrikam.example', fromName: 'Fabrikam Updates', subject: 'Release notes: version 9.2', date: '2026-09-10T16:00:00Z', isRead: true, size: 9902, preview: 'This release fixes 14 issues including folder sync…' },
  { id: '4817', from: 'security@contoso.com', fromName: 'Contoso Security', subject: 'Unusual sign-in blocked', date: '2026-09-10T11:26:00Z', isRead: false, size: 3300, preview: 'We blocked a sign-in attempt from an unrecognised device.' }
];

function runTool(name, args, session) {
  const domain = session.domain || String(session.emailAddress || '').split('@')[1] || 'example.com';
  switch (name) {
    case 'list_folder_info_by_type': {
      const type = String(args.folderType || 'mail');
      if (type !== 'mail') return { isError: false, content: JSON.stringify({ folderType: type, folders: [] }) };
      const owner = session.emailAddress;
      return {
        isError: false,
        content: JSON.stringify({
          folders: [
            { id: `${owner}/Inbox`, name: 'Inbox', count: 5, unread: 3 },
            { id: `${owner}/Sent Items`, name: 'Sent Items', count: 42, unread: 0 },
            { id: `${owner}/Junk E-Mail`, name: 'Junk E-Mail', count: 7, unread: 7 },
            { id: `${owner}/1042`, name: 'Projects/forge', count: 19, unread: 1 }
          ]
        }, null, 2)
      };
    }
    case 'get_emails': {
      const folderId = String(args.folderId || '');
      if (!folderId) return { isError: true, content: 'folderId is required. Call list_folder_info_by_type first.' };
      if (!folderId.includes('/')) return { isError: true, content: `Unknown folder "${folderId}". Folder ids look like "${session.emailAddress}/Inbox".` };
      const take = Math.max(1, Math.min(50, Number(args.take) || 10));
      let list = CANNED_EMAILS;
      if (args.unreadOnly) list = list.filter((m) => !m.isRead);
      if (!/inbox/i.test(folderId)) list = list.slice(0, 1);
      return { isError: false, content: JSON.stringify({ folderId, total: list.length, messages: list.slice(0, take) }, null, 2) };
    }
    case 'send_email': {
      if (!args.to || !args.subject) return { isError: true, content: 'to and subject are required.' };
      return { isError: false, content: JSON.stringify({ sent: true, to: args.to, subject: args.subject, messageId: '<stub-' + Date.now() + '@dev>' }) };
    }
    case 'domain_list_users':
      return { isError: false, content: JSON.stringify({ domain, users: ['jen', 'matt', 'support', 'billing'].map((u) => ({ userName: u, emailAddress: `${u}@${domain}`, isDomainAdmin: u === 'matt' })) }, null, 2) };
    case 'domain_list_aliases':
      return { isError: false, content: JSON.stringify({ domain, aliases: [{ name: 'sales', targets: [`jen@${domain}`] }, { name: 'info', targets: [`support@${domain}`, `matt@${domain}`] }] }, null, 2) };
    case 'domain_create_alias':
      if (!args.name) return { isError: true, content: JSON.stringify({ success: false, error: 'name is required' }) };
      return { isError: false, content: JSON.stringify({ success: true, alias: `${args.name}@${domain}`, targets: args.targets || [] }) };
    case 'get_domains':
      return { isError: false, content: JSON.stringify({ domains: [{ name: 'example.com', users: 14 }, { name: 'contoso.com', users: 212 }, { name: 'fabrikam.example', users: 3 }] }, null, 2) };
    case 'get_spool_messages':
      return {
        isError: false,
        content: JSON.stringify({
          total: 2,
          messages: [
            { id: 'spool-771', from: 'billing@contoso.com', to: 'ap@fabrikam.example', attempts: 6, lastError: '451 4.7.1 Greylisted, try again later', queuedAt: '2026-09-11T02:10:00Z' },
            { id: 'spool-772', from: 'jen@example.com', to: 'old-partner@defunct.example', attempts: 11, lastError: 'DNS lookup failed: NXDOMAIN', queuedAt: '2026-09-10T19:44:00Z' }
          ]
        }, null, 2)
      };
    case 'delete_domain':
      return { isError: false, content: JSON.stringify({ success: true, deleted: args.domain }) };
    case 'enable_dkim':
      return { isError: false, content: JSON.stringify({ success: true, domain: args.domain, dkim: 'enabled' }) };
    default:
      return null;
  }
}

/* ---------------------------------------------------------------- sessions
   A browser session holds up to MAX_ACCOUNTS accounts. Each account has the
   role its login implies (see roleOf below), its own readOnly flag and a
   handle: the email for mailbox and domain-admin accounts,
   sysadmin:<username>@<host> for a system admin, with "#<host>" appended on
   a clash. */

const MAX_ACCOUNTS = Number(process.env.SESSION_MAX_ACCOUNTS || 5);
const sessions = new Map(); // id -> { id, expiresAt, accounts: [] }

/* Role from what was typed, ignoring a leading "2fa" (so 2faadmin@… is a
   system admin that also needs a code): no "@" or a local part starting with
   "admin"/"sysadmin" → SysAdmin; "domainadmin…" → DomainAdmin; else User. */
function roleOf(email) {
  const e = String(email);
  if (!e.includes('@')) return 'SysAdmin';
  const lp = e.split('@')[0].toLowerCase().replace(/^2fa[._-]?/, '');
  if (lp.startsWith('domainadmin')) return 'DomainAdmin';
  if (lp.startsWith('admin') || lp.startsWith('sysadmin')) return 'SysAdmin';
  return 'User';
}

const normaliseBaseUrl = (hostname) => (/^https?:\/\//i.test(hostname) ? hostname : 'https://' + hostname).replace(/\/+$/, '');
const hostOf = (baseUrl) => { try { return new URL(baseUrl).host; } catch { return baseUrl; } };

function newAccount({ hostname, email, readOnly }) {
  const role = roleOf(email);
  const baseUrl = normaliseBaseUrl(hostname);
  const [user, dom] = String(email).split('@');
  return {
    id: crypto.randomBytes(6).toString('base64url'),
    handle: '',
    role,
    username: user,
    emailAddress: String(email).includes('@') ? String(email) : '',
    domain: dom || '',
    baseUrl,
    readOnly: !!readOnly
  };
}

function handleFor(account, others) {
  const host = hostOf(account.baseUrl);
  const base = account.role === 'SysAdmin' ? `sysadmin:${account.username}@${host}` : account.emailAddress;
  return others.some((o) => o.handle === base) ? `${base}#${host}` : base;
}

/** Add, replacing an existing account with the same (baseUrl, login). Returns false at the cap. */
function addAccount(session, fields) {
  const a = newAccount(fields);
  const login = (x) => `${x.baseUrl.toLowerCase()}|${(x.emailAddress || x.username).toLowerCase()}`;
  const rest = session.accounts.filter((x) => login(x) !== login(a));
  if (rest.length >= MAX_ACCOUNTS) return false;
  a.handle = handleFor(a, rest);
  session.accounts = [...rest, a];
  return true;
}

function newSession(fields) {
  const id = crypto.randomBytes(32).toString('base64url');
  const s = { id, expiresAt: new Date(Date.now() + 12 * 3600 * 1000).toISOString(), accounts: [] };
  addAccount(s, fields);
  sessions.set(id, s);
  return s;
}

const sessionCookie = (s) => `sma_session=${s.id}; Path=${BASE}/; HttpOnly; SameSite=Strict; Max-Age=43200`;
const clearCookie = `sma_session=; Path=${BASE}/; HttpOnly; SameSite=Strict; Max-Age=0`;

const publicSession = (s) => ({
  expiresAt: s.expiresAt,
  maxAccounts: MAX_ACCOUNTS,
  remembered: !!s.remembered,
  accounts: s.accounts.map(({ id, handle, role, username, emailAddress, domain, baseUrl, readOnly }) =>
    ({ id, handle, role, username, emailAddress, domain, baseUrl, readOnly })),
  mcpToken: s.mcpToken ? { active: true, expiresAt: s.mcpToken.expiresAt } : { active: false, expiresAt: null },
  profile: profileState(s)
});

/* ------------------------------------------- remember me on this device */

/* The real server seals the accounts' refresh tokens under RESUME_KEY. Here a
   bundle is an opaque random id pointing at a snapshot, and only the newest one
   per chain works, which is what SmarterMail's rotating refresh tokens amount
   to: presenting an older copy fails. RESUME=false switches the feature off. */
const RESUME_ENABLED = process.env.RESUME !== 'false';
const RESUME_DAYS = Number(process.env.RESUME_DAYS || 30);
const bundles = new Map();   // bundle id -> { chain, since, accounts: [fields] }
const chains = new Map();    // chain id -> the one live bundle id

function bumpVersion(s) { s.version = Math.max(Date.now(), (s.version || 0) + 1); }

function remember(s, since = Date.now(), chain = crypto.randomBytes(8).toString('hex')) {
  Object.assign(s, { remembered: true, since, chain });
  bumpVersion(s);
}

/** The bundle for the session's current state: a new id whenever the version moved. */
function sealFor(s) {
  if (s.sealedVersion !== s.version) {
    const id = crypto.randomBytes(24).toString('base64url');
    bundles.set(id, {
      chain: s.chain,
      since: s.since,
      accounts: s.accounts.map((a) => ({ hostname: a.baseUrl, email: a.emailAddress || a.username, readOnly: a.readOnly }))
    });
    chains.set(s.chain, id);
    s.sealedId = id;
    s.sealedVersion = s.version;
  }
  return { bundle: s.sealedId, version: s.version, rememberedUntil: new Date(s.since + RESUME_DAYS * 86400000).toISOString() };
}

/** X-Resume-Version for a cookie request whose remembered session moved past the browser's copy. */
function resumeHeaders(req, s) {
  if (!s || !s.remembered || !/sma_session=/.test(req.headers.cookie || '') || (req.headers.authorization || '').startsWith('Bearer ')) return {};
  const known = Number(req.headers['x-resume-version']);
  return Number.isSafeInteger(known) && known < s.version ? { 'X-Resume-Version': String(s.version) } : {};
}

/* ------------------------------------------------------- tool policy */

/** Accounts that may run `tool`: the role allows its scope, and it is a read or the account is read-write. */
const eligibleAccounts = (s, tool) =>
  s.accounts.filter((a) => roleAllows(a.role, tool.scope) && (!tool.write || !a.readOnly));

/** The tool list for this session, with `account` injected when there is more than one account. */
function toolList(s) {
  const out = [];
  for (const t of TOOLS) {
    const eligible = eligibleAccounts(s, t);
    if (!eligible.length) continue;
    const inputSchema = structuredClone(t.inputSchema);
    if (s.accounts.length > 1) {
      inputSchema.properties = inputSchema.properties || {};
      inputSchema.properties.account = {
        type: 'string',
        enum: eligible.map((a) => a.handle),
        description: eligible.length > 1
          ? 'Which signed-in account runs this tool. Required: pass one of the listed handles.'
          : `Which signed-in account runs this tool. Only ${eligible[0].handle} can; it may be omitted.`
      };
      if (eligible.length > 1) inputSchema.required = [...new Set([...(inputSchema.required || []), 'account'])];
    }
    out.push({ name: t.name, description: t.description, inputSchema, category: t.category, scope: t.scope, write: t.write, destructive: !!t.destructive });
  }
  return out;
}

/**
 * Resolve `arguments.account` the way the server's ToolDispatcher does.
 * Returns { account } or { error, status } where status 200 means an isError
 * result the model can fix, and 403 a write on a read-only account.
 */
function resolveAccount(s, tool, handle) {
  const byRole = s.accounts.filter((a) => roleAllows(a.role, tool.scope));
  const valid = eligibleAccounts(s, tool).map((a) => a.handle);
  const list = valid.length ? ` Valid accounts for ${tool.name}: ${valid.join(', ')}.` : '';

  let account;
  if (handle) {
    account = s.accounts.find((a) => a.handle === handle);
    if (!account) return { status: 200, error: `Unknown account "${handle}".${list}` };
    if (!roleAllows(account.role, tool.scope)) {
      return { status: 200, error: `${tool.name} cannot run as ${handle} (a ${account.role} account).${list}` };
    }
  } else if (byRole.length === 1) {
    account = byRole[0];
  } else if (byRole.length === 0) {
    return { status: 200, error: `No account in this session can run ${tool.name}.` };
  } else if (valid.length === 1) {
    account = s.accounts.find((a) => a.handle === valid[0]);
  } else {
    return { status: 200, error: `${tool.name} needs an "account" argument.${list}` };
  }

  if (tool.write && account.readOnly) {
    return { status: 403, error: `"${tool.name}" changes data and the account "${account.handle}" is read-only.` };
  }
  return { account };
}

/* ------------------------------------------------------- login outcomes */

/* Logins the server refuses outright: the user has to finish something in
   SmarterMail webmail first. Matched on the local part's prefix. */
const BLOCKED_LOGINS = [
  { prefix: 'expired', code: 'CHANGE_PASSWORD_NEEDED', error: 'Your SmarterMail password must be changed before you can sign in. Change it in SmarterMail webmail, then come back.' },
  { prefix: 'stale', code: 'PASSWORD_EXPIRED', error: 'Your SmarterMail password has expired. Set a new one in SmarterMail webmail, then come back.' },
  { prefix: 'setup2fa', code: 'TWO_FACTOR_SETUP_REQUIRED', error: 'This account must finish setting up two-step authentication. Complete the setup in SmarterMail webmail, then come back.' },
  { prefix: 'apppass', code: 'APP_PASSWORD_REQUIRED', error: 'This account requires an application-specific password. Create one in SmarterMail webmail, then use it here.' }
];


/* ------------------------------------------------------ 2FA challenges ---
   Any email whose local part starts with "2fa" needs a second factor. The
   method is "email" when the local part also contains "mail" (2famail@…),
   otherwise "rfc6238". The accepted code is always 123456. Five wrong codes,
   a five-minute lifetime, and one successful use all kill the challenge — it
   then answers 410 CHALLENGE_EXPIRED, same as a stale one. */

// Five minutes, overridable so tests can watch a challenge go stale.
const CHALLENGE_TTL_MS = Number(process.env.TWO_FACTOR_TTL_MS || 5 * 60 * 1000);
const MAX_2FA_ATTEMPTS = 5;
const TOTP_CODE = '123456';
const challenges = new Map(); // id -> challenge

const localPart = (email) => String(email).split('@')[0].toLowerCase();
const needsTwoFactor = (email) => localPart(email).startsWith('2fa');
const twoFactorMethod = (email) => (localPart(email).includes('mail') ? 'email' : 'rfc6238');

/* A challenge from POST /api/accounts carries the session it was issued to,
   and can only be completed by a request carrying that same session. */
function newChallenge({ hostname, email, readOnly }, sessionId = null) {
  const c = {
    id: crypto.randomBytes(24).toString('base64url'),
    hostname,
    emailAddress: email,
    readOnly: !!readOnly,
    sessionId,
    method: twoFactorMethod(email),
    attemptsLeft: MAX_2FA_ATTEMPTS,
    expiresAt: Date.now() + CHALLENGE_TTL_MS
  };
  challenges.set(c.id, c);
  return c;
}

/** Live challenge, or undefined if it never existed / was used / expired. */
function liveChallenge(id) {
  const c = challenges.get(id);
  if (!c) return undefined;
  if (c.expiresAt <= Date.now()) { challenges.delete(id); return undefined; }
  return c;
}

/** The shared validation of POST /auth/login and POST /accounts: an error response, or null. */
function refuseLogin(body) {
  const { hostname, email, password } = body || {};
  if (!hostname || !email || !password) return { status: 400, body: { error: 'hostname, email and password are required', code: 'BAD_REQUEST' } };
  // Stub SSRF guard, so the 400 path can be exercised: opt out with ALLOW_PRIVATE_HOSTS=true
  if (process.env.ALLOW_PRIVATE_HOSTS !== 'true' && /^(localhost|127\.|10\.|192\.168\.|169\.254\.|172\.(1[6-9]|2\d|3[01])\.)/i.test(String(hostname).replace(/^https?:\/\//, ''))) {
    return { status: 400, body: { error: 'That hostname resolves to a private address and is refused.', code: 'HOSTNAME_REFUSED' } };
  }
  // Any credentials work, except a password of "bad" so the 401 path can be tested.
  if (password === 'bad') return { status: 401, body: { error: 'The username or password is incorrect.', code: 'USERNAME_OR_PASSWORD_INCORRECT' } };
  // Blocked accounts: 403 + a code the user can only clear in webmail.
  const blocked = BLOCKED_LOGINS.find((b) => localPart(email).startsWith(b.prefix));
  if (blocked) return { status: 403, body: { error: blocked.error, code: blocked.code } };
  return null;
}

const challengeBody = (c) => ({
  twoFactorRequired: true,
  challengeId: c.id,
  method: c.method,
  emailAddress: c.emailAddress,
  expiresAt: new Date(c.expiresAt).toISOString()
});

/** Cookie only, as on the server: the session id is never a bearer, and an MCP token opens /mcp only. */
function sessionOf(req) {
  const cookie = req.headers.cookie || '';
  const m = /(?:^|;\s*)sma_session=([^;]+)/.exec(cookie);
  return m ? sessions.get(decodeURIComponent(m[1])) : undefined;
}

/* ------------------------------------------------------------------ http */

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.md': 'text/markdown; charset=utf-8',
  '.woff2': 'font/woff2'
};

function json(res, status, obj, headers = {}) {
  const body = JSON.stringify(obj);
  // Evaluated as the response goes out, so a rotation made by this request is announced by it.
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', ...resumeHeaders(res.req, res.session), ...headers });
  res.end(body);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    let data = '';
    req.on('data', (c) => {
      data += c;
      if (data.length > 1e6) { reject(new Error('body too large')); req.destroy(); }
    });
    req.on('end', () => {
      if (!data) return resolve({});
      try { resolve(JSON.parse(data)); } catch { reject(new Error('invalid JSON body')); }
    });
    req.on('error', reject);
  });
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  let p = decodeURIComponent(url.pathname);

  if (p === BASE) { res.writeHead(301, { Location: BASE + '/' }); return res.end(); }
  if (!p.startsWith(BASE + '/') && p !== '/') { res.writeHead(404); return res.end('not found'); }
  if (p === '/') { res.writeHead(302, { Location: BASE + '/' }); return res.end(); }

  p = p.slice(BASE.length) || '/';
  console.log(`${req.method} ${BASE}${p}`);

  if (p === '/health') { res.writeHead(200, { 'Content-Type': 'text/plain' }); return res.end('smartermail-agent ok (stub)'); }

  if (p.startsWith('/api/')) {
    try { return await handleApi(req, res, p.slice(4), url); } catch (e) {
      return json(res, 400, { error: e.message });
    }
  }

  return serveStatic(res, p);
});

async function handleApi(req, res, p, url) {
  const method = req.method;

  /* ---- auth ---- */

  if (p === '/auth/login' && method === 'POST') {
    const body = await readBody(req);
    const refused = refuseLogin(body);
    if (refused) return json(res, refused.status, refused.body);

    // Always a new session: whatever the incoming cookie pointed at is gone.
    const old = sessionOf(req);
    if (old) sessions.delete(old.id);

    const { hostname, email, readOnly = true } = body;
    if (needsTwoFactor(email)) return json(res, 200, challengeBody(newChallenge({ hostname, email, readOnly })));

    const s = newSession({ hostname, email, readOnly });
    return json(res, 200, publicSession(s), { 'Set-Cookie': sessionCookie(s) });
  }

  if (p === '/auth/two-factor' && method === 'POST') {
    const body = await readBody(req);
    const challengeId = String(body.challengeId || '');
    const code = String(body.code || '').replace(/\s+/g, '');
    if (!challengeId || !code) return json(res, 400, { error: 'challengeId and code are required', code: 'BAD_REQUEST' });

    const c = liveChallenge(challengeId);
    const expired = () => json(res, 410, { error: 'That verification request has expired. Log in again to get a new code.', code: 'CHALLENGE_EXPIRED' });
    if (!c) return expired();

    // An add-account challenge only completes from the session it belongs to.
    const current = sessionOf(req);
    if (c.sessionId && (!current || current.id !== c.sessionId)) return expired();

    if (code !== TOTP_CODE) {
      c.attemptsLeft -= 1;
      if (c.attemptsLeft <= 0) {
        challenges.delete(c.id);
        return json(res, 410, { error: 'Too many incorrect codes. Log in again to get a new one.', code: 'CHALLENGE_EXPIRED' });
      }
      return json(res, 401, {
        error: 'That code is not correct.',
        code: 'INVALID_TWO_FACTOR_CODE',
        attemptsLeft: c.attemptsLeft
      });
    }

    challenges.delete(c.id);                       // single use: replay → 410
    const fields = { hostname: c.hostname, email: c.emailAddress, readOnly: c.readOnly };
    if (c.sessionId) {
      if (!addAccount(current, fields)) return json(res, 409, { error: `This chat already has ${MAX_ACCOUNTS} accounts. Remove one first.`, code: 'ACCOUNT_LIMIT' });
      if (current.remembered) bumpVersion(current);
      return json(res, 200, publicSession(current));
    }
    const s = newSession(fields);
    return json(res, 200, publicSession(s), { 'Set-Cookie': sessionCookie(s) });
  }

  /* ---- fake OpenRouter, dev only ----
     The UI points at this instead of openrouter.ai when loaded with
     ?llmUrl=./api/dev/completions (honoured on localhost only), so the whole
     tool-calling loop can be exercised without a real key. Deliberately does
     NOT require a session, so that killing the session mid-conversation
     reproduces production: the model keeps answering, the tool calls 401. */
  if (p === '/dev/completions' && method === 'POST') {
    return fakeCompletions(res, await readBody(req), sessionOf(req) || { accounts: [] });
  }

  /* ---- server mode (stub-profiles.mjs) ---- */

  if (p === '/config' && method === 'GET') return json(res, 200, serverConfig(RESUME_ENABLED, RESUME_DAYS));

  if (await handleProfiles({
    req, res, p, method, readBody, json, sessionOf, sessions, publicSession, sessionCookie, clearCookie,
    newSessionId: () => crypto.randomBytes(32).toString('base64url'),
    host: String(req.headers.host || 'localhost').replace(/:\d+$/, '')
  })) return;

  /* ---- remember me on this device ---- */

  if (p === '/auth/resume/config' && method === 'GET') {
    return json(res, 200, { enabled: RESUME_ENABLED, days: RESUME_ENABLED ? RESUME_DAYS : 0 });
  }

  // Dev only: drop every session, as a restart or redeploy of the real server does.
  if (p === '/dev/restart' && method === 'POST') {
    sessions.clear();
    res.writeHead(204);
    return res.end();
  }

  if (p === '/auth/resume' && method === 'POST') {
    if (!RESUME_ENABLED) return json(res, 404, { error: 'Remember-me is not enabled on this server.', code: 'RESUME_DISABLED' });
    const body = await readBody(req);
    const saved = bundles.get(String(body.bundle || ''));
    if (!saved) return json(res, 400, { error: 'That saved sign-in is not valid here. Please sign in again.', code: 'RESUME_INVALID' });
    if (chains.get(saved.chain) !== body.bundle || Date.now() - saved.since > RESUME_DAYS * 86400000) {
      return json(res, 401, { error: 'Your saved sign-in on this device is no longer accepted. Please sign in again.', code: 'RESUME_EXPIRED', skipped: [] });
    }
    const old = sessionOf(req);
    if (old) sessions.delete(old.id);
    const s = newSession(saved.accounts[0]);
    for (const fields of saved.accounts.slice(1)) addAccount(s, fields);
    remember(s, saved.since, saved.chain);
    return json(res, 200, { ...publicSession(s), ...sealFor(s), skipped: [] }, { 'Set-Cookie': sessionCookie(s) });
  }

  const s = sessionOf(req);
  if (!s) return json(res, 401, { error: 'Not signed in.' });
  res.session = s;

  if (p === '/auth/resume' && ['GET', 'PUT', 'DELETE'].includes(method)) {
    if (!RESUME_ENABLED) return json(res, 404, { error: 'Remember-me is not enabled on this server.', code: 'RESUME_DISABLED' });
    if ((req.headers.authorization || '').startsWith('Bearer ')) return json(res, 403, { error: 'Cookie only.', code: 'COOKIE_REQUIRED' });
    if (method === 'DELETE') {
      s.remembered = false;
      res.writeHead(204);
      return res.end();
    }
    if (method === 'PUT' && !s.remembered) remember(s, s.since || Date.now(), s.chain);
    if (!s.remembered) return json(res, 404, { error: 'This session is not remembered on this device.', code: 'NOT_REMEMBERED' });
    return json(res, 200, sealFor(s));
  }

  if (p === '/auth/session' && method === 'GET') return json(res, 200, publicSession(s));

  if (p === '/auth/logout' && method === 'POST') {
    sessions.delete(s.id);
    if (s.chain) chains.delete(s.chain);          // logout revokes: the saved copy dies too
    res.writeHead(204, { 'Set-Cookie': clearCookie });
    return res.end();
  }

  /* ---- accounts ---- */

  if (p === '/accounts' && method === 'POST') {
    const body = await readBody(req);
    const refused = refuseLogin(body);
    if (refused) return json(res, refused.status, refused.body);

    const { hostname, email, readOnly = true } = body;
    const probe = { accounts: s.accounts.slice() };
    if (!addAccount(probe, { hostname, email, readOnly })) {
      return json(res, 409, { error: `This chat already has ${MAX_ACCOUNTS} accounts. Remove one first.`, code: 'ACCOUNT_LIMIT' });
    }
    if (needsTwoFactor(email)) return json(res, 200, challengeBody(newChallenge({ hostname, email, readOnly }, s.id)));

    addAccount(s, { hostname, email, readOnly });
    if (s.remembered) bumpVersion(s);
    return json(res, 200, publicSession(s));
  }

  const del = /^\/accounts\/([^/]+)$/.exec(p);
  if (del && method === 'DELETE') {
    const id = decodeURIComponent(del[1]);
    if (!s.accounts.some((a) => a.id === id)) return json(res, 404, { error: 'No such account in this session.' });
    s.accounts = s.accounts.filter((a) => a.id !== id);
    if (s.remembered) bumpVersion(s);
    if (!s.accounts.length && !s.profileId) {
      sessions.delete(s.id);                       // the last account ends the session
      if (s.chain) chains.delete(s.chain);
      res.writeHead(204, { 'Set-Cookie': clearCookie });
      return res.end();
    }
    res.writeHead(204);
    return res.end();
  }

  if (p === '/auth/token' && method === 'POST') {
    s.mcpToken = { token: 'sma_mcp_' + crypto.randomBytes(32).toString('base64url'), expiresAt: s.expiresAt };
    return json(res, 200, { token: s.mcpToken.token, expiresAt: s.mcpToken.expiresAt });
  }
  if (p === '/auth/token' && method === 'DELETE') {
    s.mcpToken = null;
    res.writeHead(204);
    return res.end();
  }

  /* ---- tools ---- */

  if (p === '/tools' && method === 'GET') return json(res, 200, toolList(s));

  if (p === '/tools/call' && method === 'POST') {
    const body = await readBody(req);
    const name = body.name;
    const args = { ...(body.arguments || {}) };
    const def = TOOLS.find((t) => t.name === name);
    if (!def) return json(res, 404, { error: `Unknown tool "${name}".` });

    const handle = typeof args.account === 'string' ? args.account : '';
    delete args.account;
    const r = resolveAccount(s, def, handle);
    if (r.error && r.status === 403) return json(res, 403, { error: r.error });
    if (r.error) return json(res, 200, { isError: true, content: r.error, account: null });

    // Fake latency so the running state of the tool card is visible.
    await new Promise((done) => setTimeout(done, Number(url.searchParams.get('delay') || 350)));
    const result = runTool(name, args, r.account);
    if (s.remembered) bumpVersion(s);            // as if the call refreshed (and rotated) a token
    return json(res, 200, { ...(result || { isError: true, content: 'not implemented in the stub' }), account: r.account.handle });
  }

  return json(res, 404, { error: 'no such endpoint' });
}

function sse(res, obj) { res.write('data: ' + JSON.stringify(obj) + '\n\n'); }
const delta = (d, finish = null) => ({ id: 'gen-stub', object: 'chat.completion.chunk', model: 'stub/model', choices: [{ index: 0, delta: d, finish_reason: finish }] });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function fakeCompletions(res, body, s) {
  const mailboxAccount = s.accounts.find((a) => a.role !== 'SysAdmin');
  const sysadmin = s.accounts.find((a) => a.role === 'SysAdmin');
  const session = { emailAddress: (mailboxAccount && mailboxAccount.emailAddress) || 'me@example.com' };
  // With several accounts the mailbox calls name theirs, as a real model would.
  const withAccount = (args, a) => (s.accounts.length > 1 && a ? { ...args, account: a.handle } : args);
  const messages = Array.isArray(body.messages) ? body.messages : [];
  const used = new Set(messages.filter((m) => m.role === 'assistant' && m.tool_calls)
    .flatMap((m) => m.tool_calls.map((t) => t.function.name)));
  const lastUser = [...messages].reverse().find((m) => m.role === 'user');
  const text = String((lastUser && lastUser.content) || '');
  const slow = /slow|stop|long/i.test(text);

  res.writeHead(200, { 'Content-Type': 'text/event-stream; charset=utf-8', 'Cache-Control': 'no-store', Connection: 'keep-alive' });
  res.write(': OPENROUTER PROCESSING\n\n');

  const wantsSend = /send/i.test(text) && !used.has('send_email');
  const wantsSpool = /spool|stuck/i.test(text);

  if (wantsSpool && sysadmin && !used.has('get_spool_messages')) {
    sse(res, delta({ role: 'assistant', content: 'Checking the spool as the system admin. ' }));
    await sleep(100);
    sse(res, delta({ tool_calls: [{ index: 0, id: 'call_spool', type: 'function', function: { name: 'get_spool_messages', arguments: JSON.stringify(withAccount({ take: 20 }, sysadmin)) } }] }));
    sse(res, delta({}, 'tool_calls'));
  } else if (wantsSpool && sysadmin) {
    for (const word of 'Two messages are stuck: **spool-771** to fabrikam.example (greylisted, 6 attempts) and **spool-772** to defunct.example (NXDOMAIN, 11 attempts — that one will never deliver).\n'.split(/(?<= )/)) {
      await sleep(25);
      sse(res, delta({ content: word }));
    }
    sse(res, delta({}, 'stop'));
  } else if (!used.has('list_folder_info_by_type')) {
    sse(res, delta({ role: 'assistant', content: 'Let me look at your folders. ' }));
    await sleep(120);
    sse(res, delta({ tool_calls: [{ index: 0, id: 'call_folders', type: 'function', function: { name: 'list_folder_info_by_type', arguments: '' } }] }));
    const argsText = JSON.stringify(withAccount({ folderType: 'mail' }, mailboxAccount));
    for (const frag of [argsText.slice(0, 8), argsText.slice(8, 20), argsText.slice(20)]) { await sleep(60); sse(res, delta({ tool_calls: [{ index: 0, function: { arguments: frag } }] })); }
    sse(res, delta({}, 'tool_calls'));
  } else if (!used.has('get_emails')) {
    await sleep(100);
    sse(res, delta({ role: 'assistant', content: null, tool_calls: [{ index: 0, id: 'call_mail', type: 'function', function: { name: 'get_emails', arguments: '' } }] }));
    const tail = s.accounts.length > 1 && mailboxAccount ? `,"account":${JSON.stringify(mailboxAccount.handle)}}` : '}';
    for (const frag of ['{"folderId":"', session.emailAddress + '/Inbox', '","take":5,"unreadOnly":true', tail]) {
      await sleep(60);
      sse(res, delta({ tool_calls: [{ index: 0, function: { arguments: frag } }] }));
    }
    sse(res, delta({}, 'tool_calls'));
  } else if (wantsSend) {
    await sleep(100);
    sse(res, delta({ role: 'assistant', tool_calls: [{ index: 0, id: 'call_send', type: 'function', function: { name: 'send_email', arguments: JSON.stringify(withAccount({ to: 'jen@example.org', subject: 'Lunch Thursday', body: '12:30 works.' }, mailboxAccount)) } }] }));
    sse(res, delta({}, 'tool_calls'));
  } else {
    const answer = [
      'You have **3 unread** messages in `', session.emailAddress, '/Inbox`:\n\n',
      '1. **Contoso Billing** — *Invoice 10423 is overdue* (today 08:14)\n',
      '2. **Build Server** — *build #17 succeeded* (today 07:02)\n',
      '3. **Contoso Security** — *Unusual sign-in blocked* (yesterday 11:26)\n\n',
      'The billing one looks like it needs an answer. Want me to open it?\n'
    ].join('').split(/(?<= )/);
    for (const word of answer) {
      await sleep(slow ? 300 : 25);
      if (res.writableEnded || res.destroyed) return;
      sse(res, delta({ content: word }));
    }
    sse(res, delta({}, 'stop'));
    sse(res, { id: 'gen-stub', usage: { prompt_tokens: 900, completion_tokens: 120, total_tokens: 1020 }, choices: [] });
  }

  res.write('data: [DONE]\n\n');
  res.end();
}

function serveStatic(res, p) {
  let rel = p === '/' ? 'index.html' : p.replace(/^\/+/, '');
  const file = path.resolve(ROOT, rel);
  if (!file.startsWith(ROOT + path.sep) && file !== path.join(ROOT, 'index.html')) {
    res.writeHead(403); return res.end('forbidden');
  }
  fs.readFile(file, (err, data) => {
    if (err) {
      if (fs.existsSync(file) && fs.statSync(file).isDirectory()) {
        res.writeHead(301, { Location: p.replace(/\/?$/, '/') + 'index.html' });
        return res.end();
      }
      res.writeHead(404, { 'Content-Type': 'text/plain' });
      return res.end('not found: ' + rel);
    }
    res.writeHead(200, { 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
    res.end(data);
  });
}

const seededCode = await seedProfile(newAccount);

server.listen(PORT, () => {
  // PORT=0 picks a free port; the tests parse it back off this first line.
  const port = server.address().port;
  console.log(`smartermail-agent stub listening on http://localhost:${port}${BASE}/`);
  console.log(`  mode        : ${MODE} (MODE=browser for browser-only; SEED_PROFILE=1 seeds a profile)`);
  if (seededCode) console.log(`  profile     : recovery code ${seededCode}`);
  console.log(`  static root : ${ROOT}`);
  console.log('  login       : any hostname/email; password "bad" → 401; private hostnames → 400');
  console.log('  two-factor  : 2fa*@… → challenge (code 123456); expired*@… → 403 CHANGE_PASSWORD_NEEDED');
  console.log('  roles       : admin / admin*@… / sysadmin*@… → SysAdmin; domainadmin*@… → DomainAdmin; else User');
  console.log(`  accounts    : POST /api/accounts adds (max ${MAX_ACCOUNTS}); DELETE /api/accounts/{id}`);
  console.log(`  tools       : ${TOOLS.map((t) => t.name + (t.write ? '*' : '')).join(', ')}  (* = write)`);
});
