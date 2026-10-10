/**
 * approvals-core.js — the parts of the approval queue that need no DOM, so
 * the node tests can reach them: the argument hash, how arguments are laid
 * out for review, and the editor's default mode per tool.
 *
 * The server canonicalises a proposed call once and seals the same argsJson
 * string into the copy only this browser can open. The browser shows that
 * string verbatim and hashes it, byte for byte, when the user approves:
 *
 *   argsHash = base64url(SHA-256(UTF-8("sma-proposal-v1\n" + tool + "\n" + accountId + "\n" + argsJson)))
 *
 * The server executes only when the hash matches what it stored, so what
 * the user saw is what runs. Nothing here re-serialises JSON.
 */

const enc = new TextEncoder();

function b64url(bytes) {
  let s = '';
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** The hash the server checks before it runs an approved proposal. */
export async function proposalHash(tool, accountId, argsJson) {
  const data = enc.encode(`sma-proposal-v1\n${tool}\n${accountId}\n${argsJson}`);
  return b64url(new Uint8Array(await globalThis.crypto.subtle.digest('SHA-256', data)));
}

/** Argument names whose values are addresses: highlighted, because that is where mail goes. */
const ADDRESS_KEY = /^(to|cc|bcc|from|replyto|forwardto|forwardaddress|forwardingaddress|email|emails|emailaddress|address|addresses|recipient|recipients|sender|senders|user|users|username|alias|aliases|target|destination|redirectto)$/i;

/** True for argument names that carry addresses, or values that look like one. */
export function isAddress(key, value) {
  if (ADDRESS_KEY.test(String(key).replace(/[_-]/g, ''))) return true;
  return typeof value === 'string' && /[^\s@]+@[^\s@]+\.[^\s@]+/.test(value);
}

/**
 * argsJson → rows for the review table, in the string's own (canonical) key
 * order: { key, text, kind } where kind is 'empty' | 'address' | 'long' |
 * 'json' | 'plain'. `text` is the full value, never shortened: strings as
 * they are, everything else as indented JSON. Not a JSON object → null (the
 * caller shows the raw string instead).
 */
export function argumentRows(argsJson) {
  let parsed;
  try { parsed = JSON.parse(argsJson); } catch { return null; }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;
  return Object.keys(parsed).map((key) => {
    const value = parsed[key];
    if (value === '' || value === null || (Array.isArray(value) && value.length === 0)) {
      return { key, text: '(empty)', kind: 'empty' };
    }
    const text = typeof value === 'string' ? value : JSON.stringify(value, null, 2);
    const kind = isAddress(key, value) ? 'address'
      : typeof value === 'object' ? 'json'
        : text.length > 160 || text.includes('\n') ? 'long'
          : 'plain';
    return { key, text, kind };
  });
}

/** "2 d 3 h", "4 h 10 min", "9 min", "expired". */
export function formatRemaining(ms) {
  if (!(ms > 0)) return 'expired';
  const minutes = Math.floor(ms / 60000);
  const d = Math.floor(minutes / 1440), h = Math.floor((minutes % 1440) / 60), m = minutes % 60;
  if (d) return `${d} d${h ? ` ${h} h` : ''}`;
  if (h) return `${h} h${m ? ` ${m} min` : ''}`;
  return `${Math.max(1, m)} min`;
}

/**
 * The editor's default for a write tool: approval for anything destructive
 * and for every domain-admin or system-admin tool, otherwise run directly.
 * The user may change any of them; the server enforces whatever is saved.
 */
export function defaultApprovalMode(tool) {
  return tool && (tool.destructive || (tool.scope && tool.scope !== 'Mailbox')) ? 'approve' : 'auto';
}

/** Words for a proposal's status. */
export function statusText(status) {
  switch (status) {
    case 'pending': return 'waiting for you';
    case 'executing': return 'running…';
    case 'executed': return 'done';
    case 'failed': return 'not done';
    case 'denied': return 'denied';
    case 'expired': return 'expired';
    case 'unknown': return 'outcome unknown';
    default: return status;
  }
}

/** Proposals grouped by run, newest run first, keeping each run's order. */
export function groupByRun(items) {
  const groups = new Map();
  for (const item of items) {
    if (!groups.has(item.runId)) groups.set(item.runId, []);
    groups.get(item.runId).push(item);
  }
  return [...groups.entries()].map(([runId, list]) => ({ runId, items: list }));
}
