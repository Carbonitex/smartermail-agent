/**
 * artifact-ops.js — the deterministic operators the analysis sub-agent runs
 * over one artifact (a tool result too large for the chat's context).
 *
 * Pure functions, no DOM: the browser runs them inside a dedicated Worker
 * (artifact-worker.js) so a regular expression that backtracks forever can be
 * killed with terminate() instead of freezing the page. Node tests import them
 * directly. Scheduled tasks run a C# port (src/Agent/Llm/Artifacts/
 * ArtifactOperators.cs) that must produce byte-identical output; the shared
 * fixture tests/Agent.Tests/Fixtures/artifact-ops.json checks both.
 *
 * No code from the model runs here: it only chooses an operator and its
 * arguments (a pattern, a line range, a time window). Every output is capped at
 * OUTPUT_CAP characters and says how much it left out.
 */

export const OUTPUT_CAP = 8000;
export const LINE_CLIP = 500;
export const VALUE_CLIP = 200;
export const MAX_PATTERN = 1000;

/** The operators as OpenAI function tools, sorted by name (a stable request prefix). */
export const OPERATOR_TOOLS = [
  {
    name: 'artifact_between',
    description: 'Lines whose leading time of day (HH:mm:ss, or the time in an ISO date-time) falls between start and end, inclusive. Lines without a time belong to the line above. start > end wraps past midnight.',
    parameters: {
      type: 'object',
      properties: {
        start: { type: 'string', description: 'HH:mm or HH:mm:ss' },
        end: { type: 'string', description: 'HH:mm or HH:mm:ss (HH:mm includes that whole minute)' },
        limit: { type: 'integer', description: 'Lines to show, default 100, at most 500' }
      },
      required: ['start', 'end']
    }
  },
  {
    name: 'artifact_count',
    description: 'Count the lines matching a regular expression. With "by", group the count by a capture group (number or name) or, for JSON records, by a field path (a.b), and list the most frequent values.',
    parameters: {
      type: 'object',
      properties: {
        pattern: { type: 'string', description: 'Regular expression (no backreferences or lookarounds); "" matches every line' },
        by: { type: 'string', description: 'Capture group number or name, or a record field path' },
        flags: { type: 'string', description: 'Any of i (ignore case), m, s' },
        top: { type: 'integer', description: 'Values to list, default 20, at most 200' }
      },
      required: ['pattern']
    }
  },
  {
    name: 'artifact_fields',
    description: 'Extract the named capture groups (?<name>…) of a regular expression from every matching line: distinct counts per field plus the first rows as a table.',
    parameters: {
      type: 'object',
      properties: {
        pattern: { type: 'string', description: 'Regular expression with at least one named group' },
        flags: { type: 'string', description: 'Any of i, m, s' },
        limit: { type: 'integer', description: 'Rows to show, default 20, at most 200' }
      },
      required: ['pattern']
    }
  },
  {
    name: 'artifact_grep',
    description: 'Lines matching a regular expression, with their line numbers (#n). The total count is always reported, even when not every line is shown.',
    parameters: {
      type: 'object',
      properties: {
        pattern: { type: 'string', description: 'Regular expression (no backreferences or lookarounds)' },
        flags: { type: 'string', description: 'Any of i (ignore case), m, s' },
        context: { type: 'integer', description: 'Lines of context around each match, 0-5, default 0' },
        limit: { type: 'integer', description: 'Matches to show, default 50, at most 500' },
        invert: { type: 'boolean', description: 'Show the lines that do NOT match' }
      },
      required: ['pattern']
    }
  },
  {
    name: 'artifact_info',
    description: 'Size, line count, detected line format, time range and (for JSON records) field names.',
    parameters: { type: 'object', properties: {} }
  },
  {
    name: 'artifact_session',
    description: 'Every line carrying one session or message id, in order: SmarterMail log lines tag a connection as [id].',
    parameters: {
      type: 'object',
      properties: {
        id: { type: 'string', description: 'The id, with or without the brackets' },
        limit: { type: 'integer', description: 'Lines to show, default 200, at most 500' }
      },
      required: ['id']
    }
  },
  {
    name: 'artifact_slice',
    description: 'Lines by number: from (1 = first line; negative counts from the end, -20 = the last 20) and count.',
    parameters: {
      type: 'object',
      properties: {
        from: { type: 'integer', description: '1-based line number; negative counts from the end' },
        count: { type: 'integer', description: 'Lines to show, default 50, at most 500' }
      },
      required: ['from']
    }
  }
];

export const OPERATOR_NAMES = OPERATOR_TOOLS.map((t) => t.name);

/** A refusal the model can act on (bad pattern, missing argument). */
export class OpError extends Error {
  constructor(message) { super(message); this.name = 'OpError'; }
}

/* ------------------------------------------------------------------ prepare */

/**
 * Split an artifact body into lines (and records). `kind` is "text" or
 * "records" (body = a JSON array). `meta` is { handle, tool, truncated, originalChars }.
 */
export function prepare(kind, body, meta = {}) {
  const text = String(body ?? '');
  let records = null;
  let lines;
  if (kind === 'records') {
    try {
      const parsed = JSON.parse(text);
      records = Array.isArray(parsed) ? parsed : [parsed];
    } catch {
      records = null;
    }
  }
  if (records) {
    lines = records.map((r) => JSON.stringify(r));
  } else {
    lines = splitLines(text);
  }
  return { kind: records ? 'records' : 'text', lines, records, chars: text.length, meta };
}

/** \n-separated lines, a trailing \r dropped from each, no empty last line. */
export function splitLines(text) {
  if (!text) return [];
  const lines = text.split('\n');
  if (lines[lines.length - 1] === '') lines.pop();
  for (let i = 0; i < lines.length; i++) {
    const l = lines[i];
    if (l.endsWith('\r')) lines[i] = l.slice(0, -1);
  }
  return lines;
}

/* ---------------------------------------------------------------- dispatch */

/** Run one operator; returns the output text. Throws OpError for bad arguments. */
export function runOp(data, name, args = {}) {
  const a = args && typeof args === 'object' ? args : {};
  switch (name) {
    case 'artifact_info': return info(data);
    case 'artifact_slice': return slice(data, a);
    case 'artifact_grep': return grep(data, a);
    case 'artifact_count': return count(data, a);
    case 'artifact_fields': return fields(data, a);
    case 'artifact_between': return between(data, a);
    case 'artifact_session': return session(data, a);
    default: throw new OpError(`Unknown operator ${name}. Use one of: ${OPERATOR_NAMES.join(', ')}.`);
  }
}

/* ------------------------------------------------------------------ helpers */

function int(v, fallback, min, max) {
  const n = typeof v === 'number' ? v : typeof v === 'string' && v.trim() !== '' ? Number(v) : NaN;
  if (!Number.isFinite(n)) return fallback;
  return Math.min(max, Math.max(min, Math.trunc(n)));
}

function str(v) {
  return typeof v === 'string' ? v : v === undefined || v === null ? '' : String(v);
}

export function clip(s, max) {
  return s.length > max ? `${s.slice(0, max)}…[+${s.length - max} chars]` : s;
}

const lineItem = (data, i, sep = ':') => `#${i + 1}${sep} ${clip(data.lines[i], LINE_CLIP)}`;

/**
 * header + items, stopping before OUTPUT_CAP. `units` (optional) says how many
 * of the counted things each item is worth; the tail line reports what was left.
 */
function emit(header, items, { total, noun, reason = '', units = null } = {}) {
  let out = header;
  let shown = 0;
  let capped = false;
  for (let i = 0; i < items.length; i++) {
    const item = items[i];
    if (out.length + 1 + item.length > OUTPUT_CAP) { capped = true; break; }
    out += '\n' + item;
    shown += units ? units[i] : 1;
  }
  const all = total ?? items.length;
  const left = all - shown;
  if (left > 0) {
    const why = capped ? `output cap ${OUTPUT_CAP} chars` : reason || 'limit';
    out += `\n… ${left} more ${noun} not shown (${why})`;
  }
  return out;
}

/** JS RegExp from a pattern and flags; only i, m, s are honoured. */
export function compile(pattern, flags) {
  const p = str(pattern);
  if (p.length > MAX_PATTERN) throw new OpError(`Invalid pattern: longer than ${MAX_PATTERN} characters.`);
  const f = [...new Set(str(flags).split('').filter((c) => 'ims'.includes(c)))].sort().join('');
  try {
    return new RegExp(p, f);
  } catch (e) {
    throw new OpError(`Invalid pattern: ${e.message}`);
  }
}

/** Named groups in pattern order, read from the pattern text (the same rule in the C# port). */
export function groupNames(pattern) {
  const names = [];
  const re = /\(\?<([A-Za-z_][A-Za-z0-9_]*)>/g;
  let m;
  while ((m = re.exec(str(pattern)))) if (!names.includes(m[1])) names.push(m[1]);
  return names;
}

/** Seconds since midnight from a line's leading time, or null. */
export function lineTime(line) {
  const m = /^(?:[0-9]{4}-[0-9]{2}-[0-9]{2}[T ])?([0-9]{2}):([0-9]{2}):([0-9]{2})/.exec(line);
  if (!m) return null;
  const h = Number(m[1]), mi = Number(m[2]), s = Number(m[3]);
  if (h > 23 || mi > 59 || s > 59) return null;
  return h * 3600 + mi * 60 + s;
}

function parseClock(v, isEnd) {
  const m = /^([0-9]{1,2}):([0-9]{2})(?::([0-9]{2}))?$/.exec(str(v).trim());
  if (!m) return null;
  const h = Number(m[1]), mi = Number(m[2]);
  const s = m[3] === undefined ? (isEnd ? 59 : 0) : Number(m[3]);
  if (h > 23 || mi > 59 || s > 59) return null;
  return h * 3600 + mi * 60 + s;
}

const clock = (sec) => [Math.floor(sec / 3600), Math.floor(sec / 60) % 60, sec % 60].map((n) => String(n).padStart(2, '0')).join(':');

/** A record field by dot path, as text; undefined when absent. */
function fieldValue(record, path) {
  let cur = record;
  for (const part of path.split('.')) {
    if (cur === null || typeof cur !== 'object' || !Object.prototype.hasOwnProperty.call(cur, part)) return undefined;
    cur = cur[part];
  }
  if (cur === undefined) return undefined;
  return typeof cur === 'string' ? cur : JSON.stringify(cur);
}

function byCount(a, b) {
  if (b[1] !== a[1]) return b[1] - a[1];
  return a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0;
}

/* ---------------------------------------------------------------- operators */

function info(data) {
  const m = data.meta || {};
  const n = data.lines.length;
  const out = [
    `artifact ${m.handle || '?'} from ${m.tool || 'a tool'}: ${n} ${data.kind === 'records' ? 'records' : 'lines'}, ${data.chars} chars (${data.kind})`
  ];
  if (m.truncated) out.push(`truncated: only the first ${data.chars} of ${m.originalChars || '?'} chars were kept`);
  out.push(`line format: ${detectFormat(data)}`);
  let first = null, last = null;
  for (const l of data.lines) {
    const t = lineTime(l);
    if (t === null) continue;
    if (first === null) first = t;
    last = t;
  }
  if (first !== null) out.push(`time range: ${clock(first)} to ${clock(last)} (first and last timestamped lines)`);
  if (data.records) {
    const keys = [];
    for (const r of data.records.slice(0, 500)) {
      if (r && typeof r === 'object' && !Array.isArray(r)) for (const k of Object.keys(r)) if (!keys.includes(k)) keys.push(k);
    }
    if (keys.length) out.push(`fields: ${keys.slice(0, 60).join(', ')}${keys.length > 60 ? `, … ${keys.length - 60} more` : ''}`);
  }
  if (n) out.push(`first line: ${clip(data.lines[0], 300)}`);
  return emit(out[0], out.slice(1), { noun: 'lines' });
}

export function detectFormat(data) {
  if (data.kind === 'records') return 'JSON records, one per line';
  const sample = data.lines.slice(0, 200);
  if (!sample.length) return 'empty';
  const share = (re) => sample.filter((l) => re.test(l)).length / sample.length;
  if (share(/^[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)? \[[^\]]+\]/) >= 0.5) return 'SmarterMail log (HH:mm:ss.fff [session id] …)';
  if (share(/^[0-9]{2}:[0-9]{2}:[0-9]{2}/) >= 0.5) return 'timestamped lines (HH:mm:ss …)';
  if (share(/^[0-9]{4}-[0-9]{2}-[0-9]{2}[T ][0-9]{2}:[0-9]{2}:[0-9]{2}/) >= 0.5) return 'ISO-timestamped lines';
  return 'plain text';
}

function slice(data, a) {
  const n = data.lines.length;
  const count = int(a.count, 50, 1, 500);
  const from = int(a.from, 1, -1e9, 1e9);
  const start = from < 0 ? Math.max(0, n + from) : Math.max(0, from - 1);
  const end = Math.min(n, start + count);
  if (start >= n) return `lines ${start + 1}- of ${n}: past the end`;
  const items = [];
  for (let i = start; i < end; i++) items.push(lineItem(data, i));
  return emit(`lines ${start + 1}-${end} of ${n}`, items, { noun: 'lines' });
}

function grep(data, a) {
  const re = compile(a.pattern, a.flags);
  const invert = a.invert === true || a.invert === 'true';
  const context = int(a.context, 0, 0, 5);
  const limit = int(a.limit, 50, 1, 500);
  const n = data.lines.length;

  const hits = [];
  for (let i = 0; i < n; i++) if (re.test(data.lines[i]) !== invert) hits.push(i);
  const isHit = context ? new Set(hits) : null;

  const items = [];
  const units = [];
  let printed = -1;   // last line index already printed
  for (const i of hits.slice(0, limit)) {
    const parts = [];
    const from = Math.max(0, i - context, printed + 1);
    if (context > 0 && printed >= 0 && from > printed + 1) parts.push('--');
    for (let j = from; j < i; j++) parts.push(lineItem(data, j, '-'));
    if (i > printed) parts.push(lineItem(data, i, ':'));
    let last = i;
    for (let j = i + 1; j <= Math.min(n - 1, i + context); j++) {
      if (isHit.has(j)) break;   // the next match prints itself
      parts.push(lineItem(data, j, '-'));
      last = j;
    }
    printed = Math.max(printed, last);
    items.push(parts.join('\n'));
    units.push(1);
  }
  const header = `${hits.length} ${invert ? 'non-matching' : 'matching'} lines of ${n}`;
  return emit(header, items, { total: hits.length, noun: invert ? 'non-matching lines' : 'matching lines', reason: `limit ${limit}`, units });
}

function count(data, a) {
  const pattern = str(a.pattern);
  const re = compile(pattern, a.flags);
  const by = str(a.by).trim();
  const top = int(a.top, 20, 1, 200);
  const n = data.lines.length;
  const names = groupNames(pattern);

  let mode = null;
  if (by) {
    if (/^[0-9]+$/.test(by)) mode = 'index';
    else if (names.includes(by)) mode = 'name';
    else if (data.records) mode = 'field';
    else throw new OpError(`"by" must be a capture group number or a named group of the pattern${names.length ? ` (${names.join(', ')})` : ''}.`);
  }

  let matches = 0;
  const counts = new Map();
  for (let i = 0; i < n; i++) {
    const line = data.lines[i];
    if (!mode) { if (re.test(line)) matches++; continue; }
    const m = re.exec(line);
    if (!m) continue;
    matches++;
    let v;
    if (mode === 'index') v = m[Number(by)];
    else if (mode === 'name') v = m.groups ? m.groups[by] : undefined;
    else v = fieldValue(data.records[i], by);
    const key = v === undefined || v === null ? '(none)' : clip(String(v), VALUE_CLIP);
    counts.set(key, (counts.get(key) || 0) + 1);
  }

  const header = `${matches} matching lines of ${n}`;
  if (!mode) return header;
  const sorted = [...counts.entries()].sort(byCount);
  const items = sorted.slice(0, top).map(([v, c]) => `${c}\t${v}`);
  return emit(`${header}; ${sorted.length} distinct values of ${by}`, items,
    { total: sorted.length, noun: 'values', reason: `top ${top}` });
}

function fields(data, a) {
  const pattern = str(a.pattern);
  const names = groupNames(pattern);
  if (!names.length) throw new OpError('The pattern has no named groups. Use (?<name>…) for each field to extract.');
  const re = compile(pattern, a.flags);
  const limit = int(a.limit, 20, 1, 200);
  const n = data.lines.length;

  const rows = [];
  const distinct = names.map(() => new Map());
  let matches = 0;
  for (let i = 0; i < n; i++) {
    const m = re.exec(data.lines[i]);
    if (!m) continue;
    matches++;
    const values = names.map((name) => {
      const v = m.groups ? m.groups[name] : undefined;
      return v === undefined ? '' : clip(v, VALUE_CLIP);
    });
    values.forEach((v, k) => distinct[k].set(v, (distinct[k].get(v) || 0) + 1));
    if (rows.length < limit) rows.push(`#${i + 1}\t${values.join('\t')}`);
  }

  const summary = names.map((name, k) => {
    const sorted = [...distinct[k].entries()].sort(byCount);
    const shown = sorted.slice(0, 5).map(([v, c]) => `${v === '' ? '(empty)' : v} x${c}`).join(', ');
    return `distinct ${name}: ${sorted.length}${shown ? ` (top: ${shown})` : ''}`;
  });
  const items = [...summary, ['line', ...names].join('\t'), ...rows];
  const units = [...summary.map(() => 0), 0, ...rows.map(() => 1)];
  return emit(`${matches} matching lines of ${n}; fields: ${names.join(', ')}`, items,
    { total: matches, noun: 'rows', reason: `limit ${limit}`, units });
}

function between(data, a) {
  const start = parseClock(a.start, false);
  const end = parseClock(a.end, true);
  if (start === null || end === null) throw new OpError('start and end must be times of day, HH:mm or HH:mm:ss.');
  const limit = int(a.limit, 100, 1, 500);
  const n = data.lines.length;
  const inRange = start <= end ? (t) => t >= start && t <= end : (t) => t >= start || t <= end;

  const hits = [];
  let current = null;
  for (let i = 0; i < n; i++) {
    const t = lineTime(data.lines[i]);
    if (t !== null) current = t;
    if (current !== null && inRange(current)) hits.push(i);
  }
  const items = hits.slice(0, limit).map((i) => lineItem(data, i));
  return emit(`${hits.length} lines between ${clock(start)} and ${clock(end)} of ${n}`, items,
    { total: hits.length, noun: 'lines', reason: `limit ${limit}` });
}

function session(data, a) {
  const id = str(a.id).trim().replace(/^\[/, '').replace(/\]$/, '');
  if (!id) throw new OpError('id is required.');
  const limit = int(a.limit, 200, 1, 500);
  const tag = `[${id}]`;
  const n = data.lines.length;
  const hits = [];
  for (let i = 0; i < n; i++) if (data.lines[i].includes(tag)) hits.push(i);
  const items = hits.slice(0, limit).map((i) => lineItem(data, i));
  return emit(`${hits.length} lines with ${clip(tag, VALUE_CLIP)} of ${n}`, items,
    { total: hits.length, noun: 'lines', reason: `limit ${limit}` });
}
