/**
 * artifacts.js — large tool results kept out of the chat's context.
 *
 * A tool result longer than ARTIFACT_THRESHOLD characters is kept here, in this
 * tab's memory, as an "artifact" (r1, r2, …), and the chat model gets a short
 * stub instead: size, the first and last lines, the small fields of the JSON
 * around it, and a pointer to the analyze_result tool. analyze_result
 * (subagent.js) hands the artifact and a question to a cheaper analysis model
 * that reads it with the operators in artifact-ops.js.
 *
 * Nothing here is ever written to storage or sent to the agent server: a
 * reload drops every artifact (the stub then answers "expired"). The store is
 * an LRU with a total and a per-artifact cap.
 *
 * Pure (no DOM), node-tested. The scheduled-task port is
 * src/Agent/Llm/Artifacts/ArtifactStore.cs; both build the same stub.
 */

export const ARTIFACT_THRESHOLD = 20000;
export const MAX_TOTAL_CHARS = 64 * 1024 * 1024;
export const MAX_ARTIFACT_CHARS = 16 * 1024 * 1024;
export const DEFAULT_ANALYSIS_MODEL = 'openai/gpt-6-luna';

const HEAD_LINES = 30;
const HEAD_CHARS = 2500;
const TAIL_LINES = 10;
const TAIL_CHARS = 800;
const STUB_LINE_CLIP = 200;
const META_FIELDS = 20;
const META_CLIP = 300;

/** The synthetic tool the chat model calls; handled in the browser, never sent to /api/tools/call. */
export const ANALYZE_RESULT_TOOL = {
  name: 'analyze_result',
  description:
    'Ask a question about a large tool result that was kept as an artifact: a result that came back as ' +
    '{"artifact":"r1",…} instead of its full content. A separate analysis model reads the whole result with ' +
    'search, count and extract tools and answers with the evidence lines. Prefer it over re-reading the result ' +
    'in pages. Ask one focused question per call, and say what you need (a count, the top values, the lines ' +
    'about one message, a time window).',
  inputSchema: {
    type: 'object',
    properties: {
      artifact: { type: 'string', description: 'The artifact handle from the stub, e.g. "r1"' },
      question: { type: 'string', description: 'What to find out from the artifact' }
    },
    required: ['artifact', 'question']
  }
};

/** Is this tool-message content an artifact stub (so history elision keeps it)? */
export function isArtifactStub(content) {
  return typeof content === 'string' && content.startsWith('{"artifact":"');
}

/* ------------------------------------------------------------------ unwrap */

/**
 * The part of a result worth analysing. A JSON object whose bulk is one string
 * (search_log_files' `content`) becomes that text; one whose bulk is one array
 * (a user or spool list) becomes records; a bare array is records; anything
 * else is the text as it came. `meta` keeps the object's small fields.
 *
 * @returns {{ kind: 'text'|'records', body: string, field: string|null, meta: object|null }}
 */
export function unwrap(content) {
  const s = String(content ?? '');
  const t = s.trimStart();
  if (t[0] !== '{' && t[0] !== '[') return { kind: 'text', body: s, field: null, meta: null };
  let parsed;
  try { parsed = JSON.parse(s); } catch { return { kind: 'text', body: s, field: null, meta: null }; }

  if (Array.isArray(parsed)) return { kind: 'records', body: s, field: null, meta: null };
  if (!parsed || typeof parsed !== 'object') return { kind: 'text', body: s, field: null, meta: null };

  // The dominant property: a string or array holding at least half the result.
  let best = null;
  let bestSize = 0;
  for (const [k, v] of Object.entries(parsed)) {
    let size = 0;
    if (typeof v === 'string') size = v.length;
    else if (Array.isArray(v)) size = JSON.stringify(v).length;
    else continue;
    if (size > bestSize) { best = k; bestSize = size; }
  }
  if (best === null || bestSize < s.length / 2) {
    // No single bulk field: pretty-print so the operators see one field per line.
    return { kind: 'text', body: JSON.stringify(parsed, null, 1), field: null, meta: null };
  }

  const meta = {};
  let n = 0;
  for (const [k, v] of Object.entries(parsed)) {
    if (k === best || n >= META_FIELDS) continue;
    if (v === null || typeof v === 'number' || typeof v === 'boolean') { meta[k] = v; n++; continue; }
    if (typeof v === 'string') { meta[k] = clipText(v, META_CLIP); n++; continue; }
    const j = JSON.stringify(v);
    if (j.length <= META_CLIP) { meta[k] = v; n++; }
  }
  const v = parsed[best];
  return Array.isArray(v)
    ? { kind: 'records', body: JSON.stringify(v), field: best, meta }
    : { kind: 'text', body: v, field: best, meta };
}

function clipText(s, max) {
  return s.length > max ? `${s.slice(0, max)}…[+${s.length - max} chars]` : s;
}

/** \n-separated lines without trailing \r or an empty last line (artifact-ops.js splitLines). */
function lines(text) {
  if (!text) return [];
  const out = text.split('\n');
  if (out[out.length - 1] === '') out.pop();
  return out.map((l) => (l.endsWith('\r') ? l.slice(0, -1) : l));
}

/** At most `maxLines` lines, each clipped, until `maxChars` is used. */
function take(list, maxLines, maxChars) {
  const out = [];
  let used = 0;
  for (const l of list) {
    if (out.length >= maxLines) break;
    const c = clipText(l, STUB_LINE_CLIP);
    if (used + c.length > maxChars) break;
    out.push(c);
    used += c.length;
  }
  return out;
}

/** Keep the first `max` chars of a text body, cut at a line end when there is one. */
function truncateText(body, max) {
  if (body.length <= max) return body;
  const nl = body.lastIndexOf('\n', max - 1);
  return body.slice(0, nl > max / 2 ? nl + 1 : max);
}

/** Keep whole records from the start while their JSON fits in `max`. */
function truncateRecords(records, max) {
  const kept = [];
  let size = 2;
  for (const r of records) {
    const j = JSON.stringify(r);
    if (size + j.length + 1 > max) break;
    kept.push(r);
    size += j.length + 1;
  }
  return kept;
}

/* ------------------------------------------------------------------- stub */

/**
 * The model-facing stand-in for an artifact: one line of JSON, keys in a fixed
 * order, at most a few KB. Deterministic, so the history it lands in stays a
 * byte-stable cache prefix.
 */
export function buildStub(artifact, { scope = 'tab' } = {}) {
  const stub = {
    artifact: artifact.handle,
    tool: artifact.tool,
    kind: artifact.kind,
    chars: artifact.chars,
    [artifact.kind === 'records' ? 'records' : 'lines']: artifact.count
  };
  if (artifact.field) stub.field = artifact.field;
  if (artifact.truncated) stub.truncated = `only the first ${artifact.chars} of ${artifact.originalChars} chars were kept`;
  if (artifact.meta && Object.keys(artifact.meta).length) stub.meta = artifact.meta;
  stub.head = artifact.head;
  if (artifact.tail.length) stub.tail = artifact.tail;
  stub.note = `The full result (${artifact.chars} chars) is kept ${scope === 'run' ? 'for this run' : 'in this browser tab'} as artifact ${artifact.handle}; ` +
    'only the lines above are shown here. To answer from all of it, call analyze_result with this artifact and a question.';
  return JSON.stringify(stub);
}

/* ------------------------------------------------------------------ store */

export class ArtifactStore {
  constructor({ threshold = ARTIFACT_THRESHOLD, maxTotal = MAX_TOTAL_CHARS, maxEach = MAX_ARTIFACT_CHARS } = {}) {
    this.threshold = threshold;
    this.maxTotal = maxTotal;
    this.maxEach = maxEach;
    this.items = new Map();   // handle → artifact, least recently used first
    this.total = 0;
    this.next = 1;
  }

  get size() { return this.items.size; }

  /**
   * A finished tool call's result. Over the threshold (and not an error) it
   * becomes an artifact: resolves `{ content: stub, artifact }`; otherwise
   * `{ content: clamped text, artifact: null }`.
   */
  capture(tool, args, result, clamp = (s) => s) {
    const text = typeof result?.content === 'string' ? result.content : JSON.stringify(result?.content ?? null);
    if (!result || result.isError || text.length <= this.threshold || tool === ANALYZE_RESULT_TOOL.name) {
      return { content: clamp(text), artifact: null };
    }
    const artifact = this.add(tool, args, text);
    return { content: buildStub(artifact), artifact };
  }

  /** Keep `text` (a tool's result) as a new artifact; evicts the least recently used to fit. */
  add(tool, args, text) {
    const u = unwrap(text);
    const originalChars = u.body.length;
    let body = u.body;
    let truncated = false;
    let records = null;
    if (u.kind === 'records') {
      try { records = JSON.parse(body); } catch { records = []; }
      if (!Array.isArray(records)) records = [records];
      if (body.length > this.maxEach) {
        records = truncateRecords(records, this.maxEach);
        body = JSON.stringify(records);
        truncated = true;
      }
    } else if (body.length > this.maxEach) {
      body = truncateText(body, this.maxEach);
      truncated = true;
    }

    const all = records ? records.map((r) => JSON.stringify(r)) : lines(body);
    const head = take(all, HEAD_LINES, HEAD_CHARS);
    const rest = all.slice(head.length);
    const tail = take(rest.slice(-TAIL_LINES).reverse(), TAIL_LINES, TAIL_CHARS).reverse();

    const handle = `r${this.next++}`;
    const { account: _account, ...shownArgs } = args && typeof args === 'object' ? args : {};
    const artifact = {
      handle,
      tool,
      args: shownArgs,
      kind: u.kind,
      field: u.field,
      meta: u.meta,
      body,
      chars: body.length,
      originalChars,
      truncated,
      count: all.length,
      head,
      tail,
      createdAt: Date.now()
    };

    while (this.items.size && this.total + artifact.chars > this.maxTotal) this.evictOldest();
    this.items.set(handle, artifact);
    this.total += artifact.chars;
    return artifact;
  }

  /** The artifact for a handle (marks it recently used), or null when unknown or evicted. */
  get(handle) {
    const h = String(handle || '').trim();
    const a = this.items.get(h);
    if (!a) return null;
    this.items.delete(h);
    this.items.set(h, a);
    return a;
  }

  /** Was this handle ever issued by this store (so a miss means it expired)? */
  issued(handle) {
    const m = /^r([0-9]+)$/.exec(String(handle || '').trim());
    return !!m && Number(m[1]) < this.next;
  }

  evictOldest() {
    const [h, a] = this.items.entries().next().value;
    this.items.delete(h);
    this.total -= a.chars;
  }

  clear() {
    this.items.clear();
    this.total = 0;
  }
}

/** "3.0 MB", "412 KB", "18,000 chars" — for a badge. */
export function formatSize(chars) {
  if (chars >= 1024 * 1024) return `${(chars / (1024 * 1024)).toFixed(1)} MB`;
  if (chars >= 10 * 1024) return `${Math.round(chars / 1024)} KB`;
  return `${chars.toLocaleString('en-US')} chars`;
}
