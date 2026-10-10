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
  if (typeof value !== 'string') return false;
  // Hidden characters (some count as \s, e.g. U+FEFF) must not break an address up so it escapes the check.
  const visible = [...value].filter((ch) => !isHiddenChar(ch)).join('');
  return /[^\s@]+@[^\s@]+\.[^\s@]+/.test(visible);
}

/* ------------------------------------------------- characters that hide */

/**
 * Characters that change how text looks without looking like anything:
 * controls (Cc, except tab and newline, which the review shows as layout),
 * format characters (Cf: bidi embeddings / overrides / isolates U+202A–202E,
 * U+2066–2069, zero-width U+200B–200F, U+2060–2064, U+FEFF, soft hyphen,
 * tags…), private-use and lone surrogates, line / paragraph separators, every
 * space but U+0020, and the blank-looking fillers and variation selectors.
 * Text arguments are shown with each of these as a visible ⟦U+XXXX⟧ marker.
 */
const HIDDEN_CHAR = /^(?:(?![\t\n])\p{Cc}|\p{Cf}|\p{Co}|\p{Cs}|\p{Zl}|\p{Zp}|(?! )\p{Zs}|[\u034F\u115F\u1160\u17B4\u17B5\u180B-\u180F\u3164\uFFA0\uFE00-\uFE0F]|[\u{E0100}-\u{E01EF}])$/u;

/** "U+202E" for a code point. */
export function codePointLabel(cp) {
  return `U+${cp.toString(16).toUpperCase().padStart(4, '0')}`;
}

/** True for a single character (code point) that would be invisible or reorder text. */
export function isHiddenChar(ch) {
  return HIDDEN_CHAR.test(ch);
}

/**
 * Split text for display so nothing in it can hide or rearrange what the
 * reviewer reads. Returns `{ parts, hidden, nonAscii }`:
 *
 *  - `parts`: `{ kind: 'text', text }`, `{ kind: 'hidden', text: '⟦U+202E⟧',
 *    code }` in place of each hidden character, and (with `address`)
 *    `{ kind: 'nonascii', text: <the character>, code }` for every non-ASCII
 *    character, which look-alike addresses are made of (Cyrillic а for a…);
 *  - `hidden`: how many hidden characters were replaced;
 *  - `nonAscii`: the distinct non-ASCII code points' labels (address only).
 *
 * Display only: the hash is always over the original argsJson, untouched.
 */
export function revealText(text, { address = false } = {}) {
  const s = String(text ?? '');
  const parts = [];
  const nonAscii = new Set();
  let hidden = 0;
  let run = '';
  const flush = () => { if (run) { parts.push({ kind: 'text', text: run }); run = ''; } };
  for (const ch of s) {
    const cp = ch.codePointAt(0);
    if (isHiddenChar(ch)) {
      flush();
      parts.push({ kind: 'hidden', text: `⟦${codePointLabel(cp)}⟧`, code: codePointLabel(cp) });
      hidden++;
    } else if (address && cp > 0x7e) {
      flush();
      parts.push({ kind: 'nonascii', text: ch, code: codePointLabel(cp) });
      nonAscii.add(codePointLabel(cp));
    } else {
      run += ch;
    }
  }
  flush();
  return { parts, hidden, nonAscii: [...nonAscii] };
}

/**
 * The warning to show next to a value, or null: hidden characters anywhere,
 * non-ASCII characters in an address.
 */
export function revealWarning({ hidden, nonAscii }) {
  const out = [];
  if (hidden) out.push(`${hidden} hidden or direction-changing character${hidden === 1 ? '' : 's'}, shown as ⟦U+…⟧`);
  if (nonAscii && nonAscii.length) {
    out.push(`non-ASCII characters in an address (${nonAscii.slice(0, 6).join(', ')}${nonAscii.length > 6 ? ', …' : ''}): it may only look like the address you expect`);
  }
  return out.length ? `Check this value: ${out.join('; ')}.` : null;
}

/**
 * argsJson → rows for the review table, in the string's own (canonical) key
 * order: { key, text, kind } where kind is 'empty' | 'address' | 'long' |
 * 'json' | 'plain'. `text` is the full value, never shortened: strings as
 * they are, everything else as indented JSON. Not a JSON object → null (the
 * caller shows the raw string instead). The display goes through revealText
 * (rows only describe the value; the hash is over argsJson itself).
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
