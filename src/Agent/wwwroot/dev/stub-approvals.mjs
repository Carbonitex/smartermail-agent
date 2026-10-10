/**
 * stub-approvals.mjs — the approval queue for the dev stub (stub-profiles.mjs):
 * /api/tasks/proposals/*, and proposals made by fake task runs.
 *
 * Like the server, it canonicalises each proposed call once (keys sorted,
 * no whitespace), hashes it, and seals the display copy to the profile's
 * public key, so the browser opens it and computes the hash exactly as it
 * would in production. It does not verify WebAuthn: any credential with an
 * id is accepted for a step-up, but the ceremony is bound to the session,
 * the proposal and the hash as on the server.
 *
 * Fixtures, by the proposed arguments: any value containing "fail" makes the
 * "execution" a TOOL_ERROR; "unavailable" answers 503 ACCOUNT_UNAVAILABLE and
 * leaves the proposal pending.
 */

import crypto from 'node:crypto';
import { sealToPublicKey } from './stub-profiles.mjs';

const b64 = (buf) => Buffer.from(buf).toString('base64url');
const ceremonies = new Map();   // ceremonyId -> { binding, at }
const hostOf = (baseUrl) => { try { return new URL(baseUrl).host; } catch { return String(baseUrl); } };

/** The stub's write tools (stub-server.mjs), as a proposal describes them. */
export const WRITE_TOOLS = {
  send_email: { name: 'send_email', description: 'Send an email from the signed-in account. WRITE TOOL.', scope: 'Mailbox', category: 'Mail', destructive: false },
  domain_create_alias: { name: 'domain_create_alias', description: "Create an alias on the domain admin's own domain. WRITE TOOL.", scope: 'DomainAdmin', category: 'Domain', destructive: false },
  delete_domain: { name: 'delete_domain', description: 'Delete a domain and all of its data from the server. WRITE TOOL.', scope: 'SysAdmin', category: 'Domains', destructive: true },
  enable_dkim: { name: 'enable_dkim', description: 'Enable DKIM signing for a domain. WRITE TOOL.', scope: 'SysAdmin', category: 'DKIM', destructive: false }
};

/** Plausible arguments for a fake run's proposal of each stub write tool. */
export const SAMPLE_ARGS = {
  send_email: { to: 'billing@example.com', subject: 'Re: Invoice 1042', body: 'Thanks, received. We will pay by Friday.\n\n-- \nAlice' },
  domain_create_alias: { name: 'sales', targets: ['alice@example.com', 'bob@example.com'] },
  delete_domain: { domain: 'old-brand.example' },
  enable_dkim: { domain: 'example.com' }
};

export const APPROVALS ={ ttlHours: 72, maxTtlHours: 168, maxPending: 100, maxProposalsPerRun: 50, defaultProposalsPerRun: 10 };

/** The server's canonical form for the stub's own (plain) values: keys sorted by UTF-16 order, no whitespace. */
export function canonical(value) {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (value && typeof value === 'object') {
    return `{${Object.keys(value).sort().map((k) => `${JSON.stringify(k)}:${canonical(value[k])}`).join(',')}}`;
  }
  return JSON.stringify(value);
}

export const argsHashOf = (tool, accountId, argsJson) =>
  b64(crypto.createHash('sha256').update(`sma-proposal-v1\n${tool}\n${accountId}\n${argsJson}`, 'utf8').digest());

export const pendingCount = (profile) =>
  (profile.proposals || []).filter((x) => x.status === 'pending' && Date.parse(x.expiresAt) > Date.now()).length;

/**
 * A proposal, as a task run's gate would make it.
 * tool: { name, description, scope, destructive, category }; account: a stub account.
 */
export async function createProposal(profile, task, runId, tool, account, args, note) {
  profile.proposals ||= [];
  const argsJson = canonical(args);
  const argsHash = argsHashOf(tool.name, account.id, argsJson);
  const existing = profile.proposals.find((x) => x.status === 'pending' && x.taskId === task.id && x.argsHash === argsHash);
  if (existing) return existing;

  const id = b64(crypto.randomBytes(16));
  const approvals = task.definition.approvals || {};
  const now = Date.now();
  const display = {
    v: 1, tool: tool.name, description: tool.description || '', category: tool.category || '', scope: tool.scope,
    destructive: !!tool.destructive, accountId: account.id, accountLogin: account.emailAddress || account.username,
    accountRole: account.role, accountHost: hostOf(account.baseUrl), readOnly: !!account.readOnly,
    argsJson, argsHash, note: note || null, taskName: task.definition.name, taskId: task.id, runId
  };
  const proposal = {
    id, taskId: task.id, runId, status: 'pending',
    needsPasskey: !!tool.destructive || tool.scope !== 'Mailbox' || !!approvals.requirePasskey,
    createdAt: new Date(now).toISOString(),
    expiresAt: new Date(now + (approvals.ttlHours || APPROVALS.ttlHours) * 3600_000).toISOString(),
    decidedAt: null, executedAt: null, errorCode: null, errorMessage: null, read: false,
    display: await sealToPublicKey(Buffer.from(JSON.stringify(display)), profile.publicKey, `task-proposal|${profile.id}|${id}`),
    result: null,
    // Stub-only, never sent: what "execution" looks at.
    _tool: tool.name, _argsHash: argsHash, _argsJson: argsJson, _account: account.emailAddress || account.username
  };
  profile.proposals.push(proposal);
  return proposal;
}

const viewOf = (x) => {
  const { _tool, _argsHash, _argsJson, _account, ...rest } = x;
  void _tool; void _argsHash; void _argsJson; void _account;
  const expired = rest.status === 'pending' && Date.parse(rest.expiresAt) <= Date.now();
  return { ...rest, status: expired ? 'expired' : rest.status };
};

/**
 * Handles /tasks/proposals*. ctx as in stub-profiles.mjs; `s` the session, `profile` its profile.
 * Returns true when it answered.
 */
export async function handle(ctx, profile, s, body) {
  const { p, method, json, res } = ctx;
  if (!p.startsWith('/tasks/proposals')) return false;
  profile.proposals ||= [];
  const find = (id) => profile.proposals.find((x) => x.id === decodeURIComponent(id));
  const locked = () => (s.unlocked === false ? (json(res, 409, { error: 'Unlock your profile with your passkey first.', code: 'PROFILE_LOCKED' }), true) : false);
  const notPending = (x) => {
    if (x.status === 'expired' || (x.status === 'pending' && Date.parse(x.expiresAt) <= Date.now())) {
      json(res, 410, { error: 'This proposal expired. Run the task again for a fresh one.', code: 'PROPOSAL_EXPIRED' });
      return true;
    }
    if (x.status !== 'pending') {
      json(res, 409, { error: 'This proposal was already decided.', code: 'PROPOSAL_NOT_PENDING' });
      return true;
    }
    return false;
  };
  const missing = () => (json(res, 404, { error: 'No such proposal.', code: 'PROPOSAL_NOT_FOUND' }), true);

  if (p === '/tasks/proposals' && method === 'GET') {
    const q = new URL(ctx.req.url, 'http://x').searchParams;
    const status = ['decided', 'all'].includes(q.get('status')) ? q.get('status') : 'pending';
    const taskId = q.get('taskId');
    const limit = Math.min(200, Math.max(1, Number(q.get('limit')) || 50));
    const list = profile.proposals
      .filter((x) => (!taskId || x.taskId === taskId) &&
        (status === 'all' || (status === 'pending' ? x.status === 'pending' : x.status !== 'pending')))
      .slice().reverse().slice(0, limit).map(viewOf);
    json(res, 200, list);
    return true;
  }

  if (p === '/tasks/proposals/deny' && method === 'POST') {
    if (locked()) return true;
    let denied = 0;
    for (const x of profile.proposals) {
      if (x.runId === body.runId && x.status === 'pending') { x.status = 'denied'; x.decidedAt = new Date().toISOString(); x.read = true; denied++; }
    }
    json(res, 200, { denied });
    return true;
  }

  const one = /^\/tasks\/proposals\/([^/]+)(\/.*)?$/.exec(p);
  if (!one) return false;
  const x = find(one[1]);
  const tail = one[2] || '';

  if (!tail && method === 'GET') return x ? (json(res, 200, viewOf(x)), true) : missing();

  if (tail === '/read' && method === 'POST') {
    if (x) x.read = true;
    res.writeHead(204);
    res.end();
    return true;
  }

  if (tail === '/deny' && method === 'POST') {
    if (locked()) return true;
    if (!x) return missing();
    if (x.status !== 'pending') return json(res, 409, { error: 'This proposal was already decided.', code: 'PROPOSAL_NOT_PENDING' }), true;
    x.status = 'denied';
    x.decidedAt = new Date().toISOString();
    x.read = true;
    res.writeHead(204);
    res.end();
    return true;
  }

  if (tail === '/approve/options' && method === 'POST') {
    if (locked()) return true;
    if (!/^[A-Za-z0-9_-]{43}$/.test(String(body.argsHash || ''))) {
      return json(res, 400, { error: 'Send the hash of the arguments you are approving.', code: 'PROPOSAL_MISMATCH' }), true;
    }
    if (!x) return missing();
    if (notPending(x)) return true;
    if (!x.needsPasskey) return json(res, 200, { passkey: false }), true;
    const ceremonyId = b64(crypto.randomBytes(16));
    ceremonies.set(ceremonyId, { binding: `${s.id}|${x.id}|${body.argsHash}`, at: Date.now() });
    json(res, 200, {
      passkey: true,
      ceremonyId,
      options: {
        challenge: b64(crypto.randomBytes(32)), timeout: 120000, rpId: ctx.host,
        allowCredentials: [...profile.passkeys.keys()].map((id) => ({ type: 'public-key', id })),
        userVerification: 'required'
      }
    });
    return true;
  }

  if (tail === '/approve' && method === 'POST') {
    if (locked()) return true;
    if (!x) return missing();
    if (notPending(x)) return true;
    if (x.needsPasskey) {
      if (!body.ceremonyId || !body.credential) {
        return json(res, 401, { error: 'Confirm this change with your passkey.', code: 'PASSKEY_REQUIRED' }), true;
      }
      const c = ceremonies.get(String(body.ceremonyId));
      ceremonies.delete(String(body.ceremonyId));
      if (!c || Date.now() - c.at > 120000 || c.binding !== `${s.id}|${x.id}|${body.argsHash}` || !body.credential.rawId) {
        return json(res, 401, { error: 'The passkey did not confirm this change. Nothing ran.', code: 'PASSKEY_INVALID' }), true;
      }
    }
    if (body.argsHash !== x._argsHash) {
      return json(res, 409, { error: 'What you approved is not what was proposed. Nothing ran; reload and check it again.', code: 'PROPOSAL_MISMATCH' }), true;
    }
    if (x._argsJson.includes('unavailable')) {
      return json(res, 503, { error: 'The mail server did not answer. Nothing ran; the change is still waiting. Try again later.', code: 'ACCOUNT_UNAVAILABLE' }), true;
    }

    const failed = x._argsJson.includes('fail');
    x.status = failed ? 'failed' : 'executed';
    x.errorCode = failed ? 'TOOL_ERROR' : null;
    x.errorMessage = failed ? 'The change was attempted and SmarterMail reported an error; see the result.' : null;
    x.decidedAt = x.executedAt = new Date().toISOString();
    x.read = true;
    const content = failed
      ? JSON.stringify({ success: false, error: 'API call failed: InternalServerError', statusCode: 500 })
      : JSON.stringify({ success: true, message: `${x._tool} done (stub)` });
    x.result = await sealToPublicKey(Buffer.from(JSON.stringify({ v: 1, isError: failed, content, account: x._account, durationMs: 42 })),
      profile.publicKey, `task-proposal-result|${profile.id}|${x.id}`);
    json(res, 200, { status: x.status, errorCode: x.errorCode, errorMessage: x.errorMessage, result: x.result });
    return true;
  }

  return false;
}
