/**
 * local-llm.js — EXPERIMENTAL: a small model running on the user's own machine
 * (Transformers.js + WebGPU) as an alternative to OpenRouter for the chat loop.
 *
 * The seam is llm.js `runTurn({ streamImpl })`: `createLocalStream()` returns a
 * function with exactly streamCompletion's signature and result, so the tool
 * loop, tool cards, elision and artifacts all work unchanged. What a 2–3B model
 * needs on top of that lives here:
 *
 *  - tool retrieval: the agent offers up to ~250 tools (10–25K+ tokens of
 *    schema); a small model gets the top `k` for the conversation (BM25 over
 *    names, descriptions and categories), plus every tool already called;
 *  - its native tool-call format, parsed into OpenAI `tool_calls`;
 *  - argument coercion and validation against the tool's JSON schema, with
 *    invalid calls answered locally as an error the model can retry from;
 *  - a context budget (default 16K tokens) instead of a 128K+ window.
 *
 * Default model: LiquidAI LFM2.5-2.6B (ONNX, q4 / q4f16). It is a reasoning
 * model: its chat template opens every answer with `<think>`, and it calls tools
 * as a Python list between special markers:
 *
 *   <think>…</think><|tool_call_start|>[get_emails(folderId='a@b.c/Inbox', take=5)]<|tool_call_end|>
 *
 * Bare JSON calls (`{"name":…,"arguments":{…}}`, `<tool_call>…</tool_call>`)
 * are accepted too, for prompts or other models that produce them.
 *
 * Public API (everything else is internal):
 *
 *   loadLocalModel({ modelId, dtype, device, onProgress, transformers })
 *       → { model, tokenizer, modelId, dtype, device, loadMs, dispose() }
 *   createLocalStream({ getModel, k, maxTools, contextTokens, maxNewTokens, … })
 *       → stream(opts)            drop-in for llm.js streamCompletion
 *         stream.guard({ callTool, localTools })  → wrapped pair for runTurn, which
 *                                  answers the calls this module rejected
 *         stream.last             diagnostics of the latest round (raw output, timings…)
 *   selectTools(tools, messages, { k, … })   → subset of `tools`, input order
 *   rankTools(tools, query, { … })           → [{ name, score }], best first
 *   parseModelOutput(text, { tools })        → { content, reasoning, calls, errors }
 *   parsePythonicCalls(text)                 → { calls, error }
 *   coerceArguments(args, schema)            → { value, errors, notes }
 *   validateToolCall(call, toolsByName)      → { ok, name, args, error }
 *   fitToBudget(messages, { budget, … })     → { messages, tokens, steps, overBudget }
 *   toTemplateMessages(messages)             → messages for the chat template
 *
 * `tools` everywhere is what runTurn hands its streamImpl: OpenAI function
 * tools (`{ type:'function', function:{ name, description, parameters } }`).
 * Contract tools (`{ name, description, inputSchema, category }`) are accepted
 * too; a function tool has no category, so pass `categoryOf` to use one.
 *
 * Pure apart from loadLocalModel; no top-level import of Transformers.js (Node
 * passes the npm module in, the browser imports the pinned CDN build lazily).
 */

import { normaliseUsage, elideOldToolResults, clampToolResult } from './llm.js';
import { isArtifactStub } from './artifacts.js';

export const DEFAULT_LOCAL_MODEL = 'LiquidAI/LFM2.5-2.6B-ONNX';
export const TRANSFORMERS_VERSION = '4.3.1';
/** Self-contained browser build (onnxruntime-web bundled; its wasm comes from the same CDN). */
export const TRANSFORMERS_CDN_URL = `https://cdn.jsdelivr.net/npm/@huggingface/transformers@${TRANSFORMERS_VERSION}/dist/transformers.min.js`;

export const DEFAULT_CONTEXT_TOKENS = 16384;
export const DEFAULT_MAX_NEW_TOKENS = 2048;
export const DEFAULT_TOOL_K = 8;

/** The model card's recommended sampling (LFM2.5-2.6B generation_config.json). */
export const LOCAL_SAMPLING = Object.freeze({ do_sample: true, temperature: 0.1, top_k: 50, repetition_penalty: 1.1 });

/** Appended to the system prompt: the model only ever sees a subset of the tools. */
export const LOCAL_TOOL_NOTE = 'Only the tools in the list below are available right now. Call them by their exact names with the argument names from their schemas. If none of them fits the request, say so instead of guessing.';

export const TOOL_CALL_START = '<|tool_call_start|>';
export const TOOL_CALL_END = '<|tool_call_end|>';
const THINK_OPEN = '<think>';
const THINK_CLOSE = '</think>';

export class LocalModelError extends Error {
  constructor(message, { code = null } = {}) {
    super(message);
    this.name = 'LocalModelError';
    this.code = code;
  }
}

/* -------------------------------------------------------------- tool shapes */

/** `{ name, description, parameters, category }` from a function tool or a contract tool. */
export function toolInfo(t) {
  if (!t || typeof t !== 'object') return null;
  if (t.function && typeof t.function === 'object' && t.function.name) {
    const f = t.function;
    return { name: f.name, description: f.description || '', parameters: f.parameters || emptySchema(), category: t.category || '' };
  }
  if (t.name) {
    return { name: t.name, description: t.description || '', parameters: t.inputSchema || t.parameters || emptySchema(), category: t.category || '' };
  }
  return null;
}

const emptySchema = () => ({ type: 'object', properties: {} });

/** name → toolInfo for a tool list. */
export function toolsByName(tools) {
  const map = new Map();
  for (const t of tools || []) {
    const info = toolInfo(t);
    if (info && !map.has(info.name)) map.set(info.name, info);
  }
  return map;
}

/** The shape LFM's template documents: `{ name, description, parameters }`, no OpenAI wrapper. */
function templateTool(info) {
  return { name: info.name, description: info.description, parameters: info.parameters };
}

/* ---------------------------------------------------------- tool retrieval */

const STOPWORDS = new Set(('a an and are as at be by can could do does for from get give have how i if in into is it its '
  + 'me my of on or our please show tell than that the their them then there these this to us was we what when where '
  + 'which who will with would you your all any some just also want need like let make one about up out new').split(' '));

/**
 * Words users say → words the tool descriptions use. Expanded on the query side
 * at a lower weight, so "meeting" still finds calendar tools.
 */
const SYNONYMS = {
  mail: ['email', 'message'], email: ['mail', 'message'], emails: ['email'], message: ['email'], inbox: ['email', 'folder'],
  unread: ['email', 'read'], letter: ['email'], reply: ['respond', 'email'], answer: ['reply'], forward: ['email'],
  send: ['email'], compose: ['send', 'draft'], write: ['send', 'draft'], draft: ['email'],
  meeting: ['calendar', 'event', 'appointment'], appointment: ['calendar', 'event'], event: ['calendar'],
  schedule: ['calendar', 'event'], agenda: ['calendar', 'event'], invite: ['meeting', 'event'], today: ['calendar'], tomorrow: ['calendar'],
  person: ['contact'], people: ['contact'], address: ['contact', 'email'], phone: ['contact'],
  todo: ['task'], reminder: ['task'], note: ['notes'], notes: ['note'],
  add: ['create', 'new'], create: ['add', 'new'], new: ['create'], change: ['update', 'set'], edit: ['update'], rename: ['update'],
  delete: ['remove'], remove: ['delete'], trash: ['delete', 'remove'], erase: ['delete'],
  find: ['search'], look: ['search'], search: ['find'], list: ['get'],
  spam: ['filter', 'block', 'junk'], junk: ['spam'], block: ['blocked', 'sender'], rule: ['filter'], filter: ['rule'],
  attachment: ['attachments', 'file'], file: ['attachment'], pdf: ['attachment'],
  user: ['users', 'account'], account: ['user'], mailbox: ['user', 'folder'], alias: ['aliases'],
  domain: ['domains'], server: ['system'], queue: ['spool'], certificate: ['ssl', 'certificates'], cert: ['certificate', 'ssl'],
  log: ['logs', 'search'], logs: ['log'], blocked: ['block'], ip: ['address'],
  signature: ['signatures'], forwarding: ['forward'], password: ['passwords'], group: ['groups']
};

/** Tokens for retrieval: lower-case, camelCase and snake_case split, stop words out, light stemming. */
export function tokenize(text) {
  const out = [];
  const spaced = String(text || '')
    // An address or a host name says "address" / "domain", not "example" and "com".
    .replace(/[\w.+-]+@[\w-]+(?:\.[\w-]+)+/g, ' address ')
    .replace(/\b[\w-]+(?:\.[\w-]+)*\.(?:com|net|org|edu|gov|io|dev|co|uk|de|info|biz|local|example|test)\b/gi, ' domain ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .toLowerCase();
  for (const raw of spaced.split(/[^a-z0-9]+/)) {
    if (!raw || raw.length < 2 || STOPWORDS.has(raw)) continue;
    out.push(stem(raw));
  }
  return out;
}

function stem(word) {
  let w = word;
  if (w.length > 4 && w.endsWith('ies')) w = w.slice(0, -3) + 'y';
  else if (w.length > 4 && (w.endsWith('sses') || w.endsWith('xes') || w.endsWith('ches') || w.endsWith('shes'))) w = w.slice(0, -2);
  else if (w.length > 3 && w.endsWith('s') && !w.endsWith('ss') && !w.endsWith('us')) w = w.slice(0, -1);
  if (w.length > 5 && w.endsWith('ing')) w = w.slice(0, -3);
  else if (w.length > 4 && w.endsWith('ed') && !w.endsWith('eed')) w = w.slice(0, -2);
  return w;
}

/** Same rule as llm.js's READ_NAME: names that only read. */
const READ_NAME = /^(domain_)?(get|list|search|check|read|download|expand|count|find)_|^analyze_result$/;

/** Stems that ask for a change; without one, tools that change things rank a little lower. */
const WRITE_INTENT = new Set(['send', 'reply', 'respond', 'forward', 'delete', 'remove', 'move', 'create', 'add', 'new', 'update',
  'change', 'edit', 'set', 'mark', 'flag', 'block', 'unblock', 'enable', 'disable', 'rename', 'reset', 'clear', 'compose', 'write',
  'draft', 'upload', 'accept', 'decline', 'cancel', 'trust', 'run', 'stop', 'start', 'restart', 'kill', 'purge', 'empty', 'archive',
  'save', 'subscribe', 'unsubscribe', 'invite', 'schedule', 'book', 'turn', 'allow', 'deny', 'ban', 'unban', 'assign', 'grant', 'revoke'].map(stem));

const READ_VERBS = new Set(['list', 'search', 'find', 'check', 'read', 'view', 'see', 'look', 'count', 'download', 'expand'].map(stem));

/** Field weights for BM25F-lite: a word in the name counts most. */
const FIELD_WEIGHTS = { name: 3, category: 1.5, description: 1, params: 0.5 };
const INDEXES = new WeakMap();

/**
 * A BM25 index over a tool list. Cached per array, so building it every round
 * of a turn costs nothing. `categoryOf(name)` adds a category the tool itself
 * does not carry (function tools have none).
 */
export function buildToolIndex(tools, { categoryOf = null } = {}) {
  const cached = !categoryOf && Array.isArray(tools) ? INDEXES.get(tools) : null;
  if (cached) return cached;

  const docs = [];
  const df = new Map();
  for (const t of tools || []) {
    const info = toolInfo(t);
    if (!info) continue;
    const category = info.category || (categoryOf && categoryOf(info.name)) || '';
    const props = info.parameters && info.parameters.properties ? Object.keys(info.parameters.properties).filter((p) => p !== 'account') : [];
    const tf = new Map();
    const seen = new Set();
    let len = 0;
    const add = (text, w, counts) => {
      for (const tok of tokenize(text)) {
        tf.set(tok, (tf.get(tok) || 0) + w);
        len += w;
        if (counts) seen.add(tok);
      }
    };
    add(info.name, FIELD_WEIGHTS.name, true);
    add(category, FIELD_WEIGHTS.category, true);
    add(info.description.slice(0, 600), FIELD_WEIGHTS.description, true);
    // Argument names add a little to a tool's own score but not to rarity: nearly
    // every mailbox tool takes a folderId, which would make "folder" worthless.
    add(props.join(' '), FIELD_WEIGHTS.params, false);
    for (const tok of seen) df.set(tok, (df.get(tok) || 0) + 1);
    docs.push({ name: info.name, tf, len, title: new Set([...tokenize(info.name), ...tokenize(category)]) });
  }
  const avgLen = docs.reduce((s, d) => s + d.len, 0) / (docs.length || 1);
  const index = { docs, df, avgLen, n: docs.length };
  if (!categoryOf && Array.isArray(tools)) INDEXES.set(tools, index);
  return index;
}

/** Weighted query terms: the text's tokens at `weight`, their synonyms at 0.4 × weight. */
function queryTerms(text, weight, into = new Map()) {
  for (const tok of tokenize(text)) {
    into.set(tok, Math.max(into.get(tok) || 0, weight));
    for (const syn of SYNONYMS[tok] || []) {
      const s = stem(syn);
      into.set(s, Math.max(into.get(s) || 0, weight * 0.4));
    }
  }
  return into;
}

/**
 * BM25 scores of every tool for a query (a string, or a `Map` term → weight).
 * Unless the query asks for a change (a write verb at weight ≥ 0.5), tools
 * whose names do not read are scaled by `writePrior`, so "show my unread mail"
 * prefers get_emails to set_email_properties. Tools that match nothing are left out.
 */
export function rankTools(tools, query, { index = null, categoryOf = null, k1 = 1.2, b = 0.75, writePrior = 0.7 } = {}) {
  const idx = index || buildToolIndex(tools, { categoryOf });
  const terms = query instanceof Map ? query : queryTerms(query, 1);
  const wantsChange = [...terms].some(([t, w]) => w >= 0.5 && WRITE_INTENT.has(t));
  const out = [];
  for (const d of idx.docs) {
    let score = 0;
    for (const [term, qw] of terms) {
      const f = d.tf.get(term);
      if (!f) continue;
      const df = idx.df.get(term) || 0;
      const idf = Math.log(1 + (idx.n - df + 0.5) / (df + 0.5));
      score += qw * idf * (f * (k1 + 1)) / (f + k1 * (1 - b + b * d.len / idx.avgLen));
    }
    if (score > 0 && !wantsChange && !READ_NAME.test(d.name)) score *= writePrior;
    if (score > 0) out.push({ name: d.name, score });
  }
  out.sort((x, y) => y.score - x.score || (x.name < y.name ? -1 : 1));
  return out;
}

/** Plain text of a message's content (string or content-part array). */
function textOf(content) {
  if (typeof content === 'string') return content;
  if (Array.isArray(content)) return content.map((p) => (typeof p === 'string' ? p : (p && p.text) || '')).join(' ');
  return '';
}

/**
 * The retrieval query for a conversation: the latest user message in full, the
 * one before it at half weight (follow-ups like "and the second one?"), and the
 * latest assistant text lightly (it often names what comes next).
 */
export function retrievalQuery(messages) {
  const terms = new Map();
  const list = messages || [];
  let users = 0;
  let assistantSeen = false;
  for (let i = list.length - 1; i >= 0 && users < 2; i--) {
    const m = list[i];
    if (!m) continue;
    if (m.role === 'user') queryTerms(textOf(m.content), users++ === 0 ? 1 : 0.5, terms);
    else if (m.role === 'assistant' && !assistantSeen && textOf(m.content)) {
      assistantSeen = true;
      queryTerms(textOf(m.content).slice(0, 1000), 0.3, terms);
    }
  }
  return terms;
}

/** Tool names called anywhere in the conversation, most recent first. */
export function calledToolNames(messages) {
  const seen = [];
  const list = messages || [];
  for (let i = list.length - 1; i >= 0; i--) {
    const m = list[i];
    if (!m || m.role !== 'assistant' || !Array.isArray(m.tool_calls)) continue;
    for (let j = m.tool_calls.length - 1; j >= 0; j--) {
      const n = m.tool_calls[j] && m.tool_calls[j].function && m.tool_calls[j].function.name;
      if (n && !seen.includes(n)) seen.push(n);
    }
  }
  return seen;
}

/** Reciprocal-rank fusion of several best-first name lists. */
export function fuseRankings(rankings, { k = 60 } = {}) {
  const score = new Map();
  for (const list of rankings) {
    (list || []).forEach((name, i) => score.set(name, (score.get(name) || 0) + 1 / (k + i + 1)));
  }
  return [...score.entries()].sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : 1)).map(([n]) => n);
}

/**
 * The tools to offer the model this round, as a subset of `tools` in their
 * original order (the server sorts them by name, so the prompt stays stable).
 *
 *  - `always`: names kept whenever present (default: analyze_result once the
 *    history holds an artifact stub, since it is the only way to read one);
 *  - every tool already called in the conversation, most recent first, up to
 *    `maxSticky`, so a multi-step follow-up still has its tools;
 *  - for each word of the request, the best tool with that word in its name or
 *    category (up to k/2 of these), so a second topic is not crowded out;
 *  - then the best BM25 matches until `k` tools are chosen. `rankings` are
 *    extra best-first name lists (e.g. an embedding ranker) fused with BM25 by
 *    reciprocal rank, for a hybrid ranker later;
 *  - never more than `maxTools` (default k + 4) in all.
 *
 * With nothing to go on (no match at all) the first `k` tools are returned.
 */
export function selectTools(tools, messages, {
  k = DEFAULT_TOOL_K, maxTools = null, maxSticky = 6, always = null, rankings = null, categoryOf = null
} = {}) {
  const list = (tools || []).filter((t) => toolInfo(t));
  const cap = maxTools || k + 4;
  if (list.length <= k) return list.slice();

  const names = new Set(list.map((t) => toolInfo(t).name));
  const chosen = [];
  const take = (n) => { if (names.has(n) && !chosen.includes(n) && chosen.length < cap) chosen.push(n); };

  const pinned = always || (hasArtifactStub(messages) ? ['analyze_result'] : []);
  pinned.forEach(take);
  calledToolNames(messages).slice(0, maxSticky).forEach(take);

  const index = buildToolIndex(list, { categoryOf });
  const query = retrievalQuery(messages);
  const lexical = rankTools(list, query, { index }).map((r) => r.name);
  const ranked = rankings && rankings.length ? fuseRankings([lexical, ...rankings]) : lexical;

  // Coverage first: for each word the user actually wrote, the best tool with that word in
  // its name or category, so one dominant topic ("email message …") cannot crowd out a
  // second one ("… folders") that only a few tools are named for.
  // Nouns only: a verb ("add", "list") names a whole family of tools, not a topic.
  const words = [...query].filter(([t, w]) => w >= 0.5 && !WRITE_INTENT.has(t) && !READ_VERBS.has(t))
    .sort((a, b) => b[1] - a[1] || (index.df.get(a[0]) || 0) - (index.df.get(b[0]) || 0)).map(([t]) => t);
  const titled = new Map(index.docs.map((d) => [d.name, d.title]));
  let covered = 0;
  for (const word of words) {
    if (covered >= Math.ceil(k / 2)) break;
    const hit = ranked.find((n) => titled.get(n) && titled.get(n).has(word));
    if (hit && !chosen.includes(hit)) { take(hit); covered++; }
  }
  // Retrieval always gets some fresh slots, however many tools are pinned or sticky.
  const target = Math.min(cap, Math.max(k, chosen.length + Math.ceil(k / 2)));
  for (const n of ranked) { if (chosen.length >= target) break; take(n); }
  if (!ranked.length) for (const t of list) { if (chosen.length >= k) break; take(toolInfo(t).name); }

  const keep = new Set(chosen);
  return list.filter((t) => keep.has(toolInfo(t).name));
}

function hasArtifactStub(messages) {
  return (messages || []).some((m) => m && m.role === 'tool' && isArtifactStub(m.content));
}

/* ------------------------------------------------------- pythonic tool calls */

/**
 * Parse LFM's pythonic call list, `[fn(a=1, b='x'), g()]`, the text between
 * the tool-call markers. Values are Python literals (strings in either quote,
 * numbers, True/False/None, lists, tuples, dicts) or JSON. Lenient where a
 * small model slips: a missing `[`, a missing closing `)`/`]` at the end (cut
 * off), trailing commas, a bare word as a string. Positional arguments are
 * kept apart (`positional`) for the validator to place.
 *
 * @returns {{calls: Array<{name:string, args:object, positional:Array}>, error: string|null}}
 */
export function parsePythonicCalls(text) {
  const p = new PyParser(String(text || ''));
  const calls = [];
  try {
    p.ws();
    const bracketed = p.peek() === '[';
    if (bracketed) p.pos++;
    for (;;) {
      p.ws();
      if (p.eof()) break;
      if (bracketed && p.peek() === ']') { p.pos++; break; }
      calls.push(p.call());
      p.ws();
      if (p.peek() === ',') { p.pos++; continue; }
      if (bracketed && p.peek() === ']') { p.pos++; break; }
      if (p.eof()) break;
      throw p.fail('expected "," or "]" after a call');
    }
    p.ws();
    if (!p.eof()) throw p.fail('unexpected text after the call list');
    return { calls, error: null };
  } catch (e) {
    return { calls, error: e.message };
  }
}

class PyParser {
  constructor(src) { this.src = src; this.pos = 0; }
  eof() { return this.pos >= this.src.length; }
  peek() { return this.src[this.pos]; }
  ws() { while (!this.eof() && /\s/.test(this.src[this.pos])) this.pos++; }
  fail(msg) { return new Error(`${msg} at character ${this.pos}`); }

  ident() {
    const m = /^[A-Za-z_][\w.-]*/.exec(this.src.slice(this.pos));
    if (!m) throw this.fail('expected a name');
    this.pos += m[0].length;
    return m[0];
  }

  call() {
    const name = this.ident();
    this.ws();
    if (this.peek() !== '(') throw this.fail(`expected "(" after ${name}`);
    this.pos++;
    const args = {};
    const positional = [];
    for (;;) {
      this.ws();
      if (this.eof()) break; // cut off: keep what we have
      if (this.peek() === ')') { this.pos++; break; }
      const save = this.pos;
      const m = /^([A-Za-z_]\w*)\s*=(?!=)/.exec(this.src.slice(this.pos));
      if (m) {
        this.pos += m[0].length;
        this.ws();
        args[m[1]] = this.value();
      } else {
        this.pos = save;
        positional.push(this.value());
      }
      this.ws();
      if (this.peek() === ',') { this.pos++; continue; }
      if (this.peek() === ')') { this.pos++; break; }
      if (this.eof()) break;
      throw this.fail('expected "," or ")" in the arguments');
    }
    return { name, args, positional };
  }

  value() {
    this.ws();
    const c = this.peek();
    if (c === '"' || c === "'") return this.string();
    if (c === '[') return this.seq('[', ']');
    if (c === '(') return this.seq('(', ')');
    if (c === '{') return this.dict();
    const num = /^[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?/.exec(this.src.slice(this.pos));
    if (num) { this.pos += num[0].length; return Number(num[0]); }
    const word = /^[A-Za-z_][\w.@/-]*/.exec(this.src.slice(this.pos));
    if (word) {
      this.pos += word[0].length;
      switch (word[0]) {
        case 'True': case 'true': return true;
        case 'False': case 'false': return false;
        case 'None': case 'null': return null;
        default:
          if (this.peek() === '(') throw this.fail(`a call (${word[0]}(…)) cannot be an argument value`);
          return word[0]; // a bare word: most likely an unquoted string
      }
    }
    throw this.fail('expected a value');
  }

  string() {
    const q = this.peek();
    const triple = this.src.startsWith(q.repeat(3), this.pos);
    const end = triple ? q.repeat(3) : q;
    this.pos += end.length;
    let out = '';
    while (!this.eof()) {
      if (this.src.startsWith(end, this.pos)) { this.pos += end.length; return out; }
      const ch = this.src[this.pos++];
      if (ch !== '\\') { out += ch; continue; }
      const e = this.src[this.pos++];
      switch (e) {
        case 'n': out += '\n'; break;
        case 't': out += '\t'; break;
        case 'r': out += '\r'; break;
        case 'b': out += '\b'; break;
        case 'f': out += '\f'; break;
        case '0': out += '\0'; break;
        case 'u': {
          const hex = this.src.slice(this.pos, this.pos + 4);
          if (/^[0-9a-fA-F]{4}$/.test(hex)) { out += String.fromCharCode(parseInt(hex, 16)); this.pos += 4; } else out += '\\u';
          break;
        }
        case 'x': {
          const hex = this.src.slice(this.pos, this.pos + 2);
          if (/^[0-9a-fA-F]{2}$/.test(hex)) { out += String.fromCharCode(parseInt(hex, 16)); this.pos += 2; } else out += '\\x';
          break;
        }
        case undefined: break;
        default: out += e; // \\ \' \" and anything unknown: the character itself
      }
    }
    throw this.fail('unterminated string');
  }

  seq(open, close) {
    this.pos++;
    const out = [];
    for (;;) {
      this.ws();
      if (this.eof()) throw this.fail(`unterminated ${open}…${close}`);
      if (this.peek() === close) { this.pos++; return out; }
      out.push(this.value());
      this.ws();
      if (this.peek() === ',') { this.pos++; continue; }
      if (this.peek() === close) { this.pos++; return out; }
      throw this.fail(`expected "," or "${close}"`);
    }
  }

  dict() {
    this.pos++;
    const out = {};
    for (;;) {
      this.ws();
      if (this.eof()) throw this.fail('unterminated {…}');
      if (this.peek() === '}') { this.pos++; return out; }
      const key = this.value();
      this.ws();
      if (this.peek() !== ':') throw this.fail('expected ":" in a dict');
      this.pos++;
      out[String(key)] = this.value();
      this.ws();
      if (this.peek() === ',') { this.pos++; continue; }
      if (this.peek() === '}') { this.pos++; return out; }
      throw this.fail('expected "," or "}"');
    }
  }
}

/* ------------------------------------------------------------ JSON tool calls */

/**
 * Calls written as JSON instead of the native format: `{"name","arguments"}`
 * (or `parameters`/`args`), `{"function":{…}}`, `{"tool_calls":[…]}`, an array
 * of those, optionally in a ```json fence or <tool_call> tags. Null when the
 * text is not unmistakably a call, so an ordinary JSON answer stays an answer.
 */
export function parseJsonCalls(text) {
  let t = String(text || '').trim();
  const tagged = [...t.matchAll(/<tool_call>([\s\S]*?)(?:<\/tool_call>|$)/g)];
  const bodies = tagged.length && !t.replace(/<tool_call>[\s\S]*?(?:<\/tool_call>|$)/g, '').trim()
    ? tagged.map((m) => m[1].trim())
    : [t.replace(/^```(?:json|python)?\s*\n?/i, '').replace(/\n?```$/, '').trim()];

  const calls = [];
  for (const body of bodies) {
    let v;
    try { v = JSON.parse(body); } catch { return null; }
    const items = Array.isArray(v) ? v : v && Array.isArray(v.tool_calls) ? v.tool_calls : [v];
    if (!items.length) return null;
    for (const item of items) {
      const c = jsonCall(item);
      if (!c) return null;
      calls.push(c);
    }
  }
  return calls.length ? calls : null;
}

function jsonCall(item) {
  if (!item || typeof item !== 'object' || Array.isArray(item)) return null;
  const f = item.function && typeof item.function === 'object' ? item.function : item;
  if (typeof f.name !== 'string' || !f.name) return null;
  const hasArgs = ['arguments', 'parameters', 'args'].some((key) => key in f);
  if (!hasArgs && f === item && Object.keys(item).length !== 1) return null;
  let args = f.arguments ?? f.parameters ?? f.args ?? {};
  if (typeof args === 'string') {
    try { args = args.trim() ? JSON.parse(args) : {}; } catch { return { name: f.name, args: {}, positional: [], error: 'arguments were not valid JSON' }; }
  }
  if (!args || typeof args !== 'object' || Array.isArray(args)) return { name: f.name, args: {}, positional: [], error: 'arguments must be an object' };
  return { name: f.name, args, positional: [] };
}

/* ----------------------------------------------------------- whole output */

/**
 * Split one completion into its parts: reasoning (inside <think>…</think>; the
 * template opens the tag, so by default the text starts inside it), visible
 * content with every bit of tool-call markup removed, and the calls.
 *
 * `tools` (names or a tool list) lets a marker-less pythonic answer such as
 * `[get_emails(take=3)]` count as a call when every name in it is known.
 *
 * @returns {{content:string, reasoning:string, calls:Array<{name, args, positional, error?}>, errors:string[], truncatedCall:boolean}}
 */
export function parseModelOutput(text, { startInThink = true, tools = null } = {}) {
  let raw = String(text || '').replace(/<\|im_end\|>/g, '').replace(/<\|endoftext\|>/g, '');
  let reasoning = '';
  const errors = [];

  // Reasoning: up to </think>, or to the first call if the model never closed it.
  let rest = raw;
  const opens = rest.trimStart().startsWith(THINK_OPEN);
  if (startInThink || opens) {
    if (opens) rest = rest.trimStart().slice(THINK_OPEN.length);
    const close = rest.indexOf(THINK_CLOSE);
    const call = rest.indexOf(TOOL_CALL_START);
    if (close >= 0 && (call < 0 || close < call)) {
      reasoning = rest.slice(0, close);
      rest = rest.slice(close + THINK_CLOSE.length);
    } else if (call >= 0) {
      reasoning = rest.slice(0, call);
      rest = rest.slice(call);
    } else {
      // Never left the thinking: there is no answer.
      return { content: '', reasoning: rest.trim(), calls: [], errors, truncatedCall: false };
    }
  }

  const calls = [];
  let content = '';
  let truncatedCall = false;
  let i = 0;
  for (;;) {
    const s = rest.indexOf(TOOL_CALL_START, i);
    if (s < 0) { content += rest.slice(i); break; }
    content += rest.slice(i, s);
    const e = rest.indexOf(TOOL_CALL_END, s + TOOL_CALL_START.length);
    const body = rest.slice(s + TOOL_CALL_START.length, e < 0 ? rest.length : e);
    if (e < 0) truncatedCall = true;
    const fromJson = /^\s*[[{]\s*["{]/.test(body) ? parseJsonCalls(body) : null;
    if (fromJson) calls.push(...fromJson);
    else {
      const r = parsePythonicCalls(body);
      calls.push(...r.calls);
      if (r.error) errors.push(`Could not read the tool call ${JSON.stringify(body.trim().slice(0, 200))}: ${r.error}`);
    }
    if (e < 0) break;
    i = e + TOOL_CALL_END.length;
  }

  content = content.replace(/<\/?think>/g, '').replace(/<\|tool_call_(?:start|end)\|>/g, '').trim();

  // No native calls: the whole answer may still be a call in JSON or bare pythonic form.
  if (!calls.length && content) {
    const json = parseJsonCalls(content);
    if (json) { calls.push(...json); content = ''; } else {
      const known = knownNames(tools);
      if (known && /^\[?\s*[A-Za-z_][\w.-]*\s*\(/.test(content)) {
        const r = parsePythonicCalls(content);
        if (!r.error && r.calls.length && r.calls.every((c) => known.has(c.name))) { calls.push(...r.calls); content = ''; }
      }
    }
  }

  return { content, reasoning: reasoning.trim(), calls, errors, truncatedCall };
}

function knownNames(tools) {
  if (!tools) return null;
  if (tools instanceof Set) return tools;
  if (tools instanceof Map) return new Set(tools.keys());
  return new Set((tools || []).map((t) => (typeof t === 'string' ? t : toolInfo(t) && toolInfo(t).name)).filter(Boolean));
}

/* --------------------------------------------------------- argument checks */

const typesOf = (schema) => {
  if (!schema || typeof schema !== 'object') return [];
  if (Array.isArray(schema.type)) return schema.type;
  if (schema.type) return [schema.type];
  if (Array.isArray(schema.anyOf || schema.oneOf)) return (schema.anyOf || schema.oneOf).flatMap(typesOf);
  if (schema.properties) return ['object'];
  return [];
};

/** camelCase of `folder_id` / `Folder-Id`, for matching a slipped argument name. */
const camel = (s) => String(s).toLowerCase().replace(/[_-]+([a-z0-9])/g, (_, c) => c.toUpperCase());
const fold = (s) => String(s).toLowerCase().replace(/[_-]/g, '');

/**
 * Coerce `value` toward a JSON schema where it is safe: "5" → 5, 5 → "5",
 * "true" → true, a lone value → [value] for an array, a JSON string → the
 * object or array it holds, an array → "a, b" for a string (recipient lists),
 * an enum matched case-insensitively, property names matched case- and
 * underscore-insensitively. Anything that cannot be made to fit is an error.
 *
 * @returns {{value:any, errors:string[], notes:string[]}}
 */
export function coerceArguments(args, schema) {
  const errors = [];
  const notes = [];
  const value = coerceValue(args && typeof args === 'object' ? args : {}, schema || emptySchema(), '', errors, notes, true);
  return { value, errors, notes };
}

function coerceValue(v, schema, path, errors, notes, top = false) {
  const types = typesOf(schema);
  const label = path || 'arguments';

  if (v === null || v === undefined) {
    if (!types.length || types.includes('null')) return v;
    errors.push(`${label} must not be null`);
    return v;
  }

  if (types.includes('object') || top) return coerceObject(v, schema, path, errors, notes);

  if (types.includes('array')) {
    let arr = v;
    if (typeof v === 'string' && /^\s*\[/.test(v)) {
      try { const parsed = JSON.parse(v); if (Array.isArray(parsed)) arr = parsed; } catch { /* not JSON: wrap below */ }
    }
    if (!Array.isArray(arr)) { notes.push(`${label}: wrapped a single value in a list`); arr = [arr]; }
    return arr.map((x, i) => coerceValue(x, schema.items || {}, `${label}[${i}]`, errors, notes));
  }

  let out = v;
  if (types.length && !types.some((t) => matchesType(v, t))) {
    out = convertScalar(v, types);
    if (out === undefined) {
      errors.push(`${label} should be ${types.join(' or ')}, got ${JSON.stringify(v).slice(0, 80)}`);
      return v;
    }
    notes.push(`${label}: converted ${typeof v} to ${types.find((t) => matchesType(out, t))}`);
  }

  if (Array.isArray(schema.enum) && schema.enum.length && !schema.enum.includes(out)) {
    const hit = typeof out === 'string' ? schema.enum.find((e) => typeof e === 'string' && e.toLowerCase() === out.toLowerCase()) : undefined;
    if (hit !== undefined) out = hit;
    else errors.push(`${label} must be one of ${schema.enum.map((e) => JSON.stringify(e)).join(', ')}, got ${JSON.stringify(out)}`);
  }
  return out;
}

function matchesType(v, t) {
  switch (t) {
    case 'string': return typeof v === 'string';
    case 'integer': return typeof v === 'number' && Number.isInteger(v);
    case 'number': return typeof v === 'number' && Number.isFinite(v);
    case 'boolean': return typeof v === 'boolean';
    case 'array': return Array.isArray(v);
    case 'object': return v !== null && typeof v === 'object' && !Array.isArray(v);
    case 'null': return v === null;
    default: return true;
  }
}

function convertScalar(v, types) {
  for (const t of types) {
    if (t === 'integer') {
      if (typeof v === 'string' && /^\s*[-+]?\d+(?:\.0+)?\s*$/.test(v)) return Math.trunc(Number(v));
      if (typeof v === 'boolean') continue;
    } else if (t === 'number') {
      if (typeof v === 'string' && v.trim() !== '' && Number.isFinite(Number(v))) return Number(v);
    } else if (t === 'boolean') {
      if (typeof v === 'string' && /^(true|yes|1)$/i.test(v.trim())) return true;
      if (typeof v === 'string' && /^(false|no|0)$/i.test(v.trim())) return false;
      if (v === 1 || v === 0) return v === 1;
    } else if (t === 'string') {
      if (typeof v === 'number' || typeof v === 'boolean') return String(v);
      if (Array.isArray(v) && v.every((x) => typeof x === 'string' || typeof x === 'number')) return v.join(', ');
      if (v && typeof v === 'object') return JSON.stringify(v);
    }
  }
  return undefined;
}

function coerceObject(v, schema, path, errors, notes) {
  let obj = v;
  if (typeof v === 'string' && /^\s*\{/.test(v)) {
    try { obj = JSON.parse(v); notes.push(`${path || 'arguments'}: parsed a JSON string`); } catch { /* fall through */ }
  }
  if (!obj || typeof obj !== 'object' || Array.isArray(obj)) {
    errors.push(`${path || 'arguments'} must be an object`);
    return v;
  }
  const props = (schema && schema.properties) || {};
  const names = Object.keys(props);
  const out = {};
  for (const [key, val] of Object.entries(obj)) {
    let name = key;
    if (!(key in props) && names.length) {
      const hit = names.find((n) => n === camel(key)) || names.find((n) => fold(n) === fold(key));
      if (hit) { notes.push(`${path ? path + '.' : ''}${key}: renamed to ${hit}`); name = hit; }
    }
    if (!(name in props) && names.length && schema.additionalProperties === false) {
      notes.push(`${path ? path + '.' : ''}${key}: dropped (not an argument of this tool)`);
      continue;
    }
    if (val === null && !(schema.required || []).includes(name) && !typesOf(props[name]).includes('null')) {
      continue; // an optional argument sent as None: leave it out
    }
    out[name] = name in props ? coerceValue(val, props[name], path ? `${path}.${name}` : name, errors, notes) : val;
  }
  for (const req of schema && Array.isArray(schema.required) ? schema.required : []) {
    if (!(req in out)) errors.push(`missing required argument "${path ? path + '.' : ''}${req}"`);
  }
  return out;
}

/** One line per argument: `folderId (string, required): Folder to list …`. */
export function describeArguments(schema, { maxDescription = 80 } = {}) {
  const props = (schema && schema.properties) || {};
  const req = new Set((schema && schema.required) || []);
  return Object.entries(props).map(([n, p]) => {
    const t = typesOf(p).join('|') || 'any';
    const en = Array.isArray(p.enum) ? ` one of ${p.enum.map((e) => JSON.stringify(e)).join('/')}` : '';
    const d = p.description ? `: ${String(p.description).slice(0, maxDescription)}` : '';
    return `- ${n} (${t}${req.has(n) ? ', required' : ''}${en})${d}`;
  }).join('\n');
}

/**
 * Check one parsed call against the full tool list (not just the subset shown:
 * a tool the model remembers from earlier is still a valid call). Positional
 * arguments are placed in schema order (required first).
 *
 * @param {{name:string, args?:object, positional?:Array, error?:string}} call
 * @param {Map<string,object>} byName  from toolsByName()
 * @returns {{ok:boolean, name:string, args:object, error:string|null, notes:string[]}}
 */
export function validateToolCall(call, byName) {
  const name = call && call.name ? String(call.name) : '';
  const args = { ...((call && call.args) || {}) };
  const info = byName.get(name);
  if (!info) {
    const near = suggestNames(name, byName);
    return {
      ok: false, name, args, notes: [],
      error: `There is no tool named "${name}".${near.length ? ` Did you mean ${near.map((n) => `"${n}"`).join(' or ')}?` : ''} Call one of the listed tools by its exact name.`
    };
  }
  if (call.error) {
    return { ok: false, name, args, notes: [], error: `The call to ${name} could not be read: ${call.error}. Expected arguments:\n${describeArguments(info.parameters)}` };
  }

  const positional = (call && call.positional) || [];
  if (positional.length) {
    const props = Object.keys((info.parameters && info.parameters.properties) || {});
    const req = (info.parameters && info.parameters.required) || [];
    const order = [...req, ...props.filter((p) => !req.includes(p))].filter((p) => !(p in args));
    if (positional.length > order.length) {
      return { ok: false, name, args, notes: [], error: `${name} got ${positional.length} unnamed arguments; name every argument (name=value). Expected arguments:\n${describeArguments(info.parameters)}` };
    }
    positional.forEach((v, i) => { args[order[i]] = v; });
  }

  const { value, errors, notes } = coerceArguments(args, info.parameters);
  if (errors.length) {
    return {
      ok: false, name, args: value, notes,
      error: `Invalid arguments for ${name}: ${errors.join('; ')}. Fix them and call ${name} again. Expected arguments:\n${describeArguments(info.parameters)}`
    };
  }
  return { ok: true, name, args: value, error: null, notes };
}

/** Up to two real names close to a wrong one: by spelling first, then by meaning. */
function suggestNames(name, byName) {
  const all = [...byName.keys()];
  const near = all
    .map((n) => ({ n, d: editDistance(name.toLowerCase(), n.toLowerCase()) }))
    .filter((x) => x.d <= Math.max(2, Math.floor(name.length / 3)))
    .sort((a, b) => a.d - b.d || (a.n < b.n ? -1 : 1))
    .map((x) => x.n);
  const out = near.slice(0, 2);
  if (out.length < 2) {
    for (const r of rankTools([...byName.values()], name.replace(/[_-]/g, ' '), { writePrior: 1 })) {
      if (out.length >= 2) break;
      if (!out.includes(r.name)) out.push(r.name);
    }
  }
  return out;
}

function editDistance(a, b) {
  if (Math.abs(a.length - b.length) > 8) return 99;
  let prev = Array.from({ length: b.length + 1 }, (_, j) => j);
  for (let i = 1; i <= a.length; i++) {
    const cur = [i];
    for (let j = 1; j <= b.length; j++) cur[j] = Math.min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
    prev = cur;
  }
  return prev[b.length];
}

/** JSON with sorted keys, so the same arguments always give the same key. */
export function canonicalJson(v) {
  if (Array.isArray(v)) return `[${v.map(canonicalJson).join(',')}]`;
  if (v && typeof v === 'object') return `{${Object.keys(v).sort().map((k) => `${JSON.stringify(k)}:${canonicalJson(v[k])}`).join(',')}}`;
  return JSON.stringify(v === undefined ? null : v);
}

/* ------------------------------------------------------------ chat template */

/** Reasoning of a tool-call round, keyed by the toolCalls array runTurn puts on its message. */
const REASONING = new WeakMap();

/**
 * OpenAI messages → what LFM's template takes: tool_call arguments as objects
 * (the template refuses a JSON string), reasoning back on the current turn's
 * tool-call rounds (the template keeps <think> only after the last user
 * message, as the model was trained), everything else as is.
 */
export function toTemplateMessages(messages, { preserveThinking = true } = {}) {
  const out = [];
  for (const m of messages || []) {
    if (!m || !m.role) continue;
    if (m.role === 'assistant') {
      const msg = { role: 'assistant', content: textOf(m.content) };
      if (Array.isArray(m.tool_calls) && m.tool_calls.length) {
        msg.tool_calls = m.tool_calls.map((c) => {
          const f = (c && c.function) || {};
          let a = f.arguments;
          if (typeof a === 'string') { try { a = a.trim() ? JSON.parse(a) : {}; } catch { a = {}; } }
          if (!a || typeof a !== 'object' || Array.isArray(a)) a = {};
          return { id: c.id, type: 'function', function: { name: f.name || 'unknown', arguments: a } };
        });
        const r = preserveThinking ? REASONING.get(m.tool_calls) : null;
        if (r) msg.reasoning = r;
      }
      if (!msg.content && !msg.tool_calls) continue;
      out.push(msg);
    } else if (m.role === 'tool') {
      out.push({ role: 'tool', content: typeof m.content === 'string' ? m.content : JSON.stringify(m.content ?? null) });
    } else {
      out.push({ role: m.role, content: typeof m.content === 'string' ? m.content : textOf(m.content) });
    }
  }
  return out;
}

/** The system message with the local tool note appended (a copy; history is never touched). */
function withToolNote(messages, note) {
  if (!note) return messages;
  const i = messages.findIndex((m) => m && m.role === 'system');
  const copy = messages.slice();
  if (i >= 0) copy[i] = { ...messages[i], content: `${textOf(messages[i].content)}\n\n${note}` };
  else copy.unshift({ role: 'system', content: note });
  return copy;
}

/* ----------------------------------------------------------- context budget */

/** Rough token count: ~3.2 characters per token for JSON-heavy chat, plus framing. */
export function estimateTokens(messages, { charsPerToken = 3.2, perMessage = 6 } = {}) {
  let chars = 0;
  for (const m of messages || []) {
    if (!m) continue;
    chars += textOf(m.content).length;
    if (Array.isArray(m.tool_calls)) chars += JSON.stringify(m.tool_calls).length;
  }
  return Math.ceil(chars / charsPerToken) + perMessage * (messages || []).length;
}

/**
 * Make the history fit `budget` tokens, cheapest loss first, without touching
 * the caller's messages (the trimmed copy is only what the model sees):
 *
 *  1. old results → stubs: elideOldToolResults (llm.js) on the copy with
 *     keepTurns 0 and a low threshold, so only the current turn keeps its results;
 *  2. the current turn's largest tool results clamped (clampToolResult), down to
 *     `minResultChars`;
 *  3. whole old turns dropped, oldest first (a turn = a user message and what
 *     follows it, so tool calls and their results always leave together). The
 *     system message and the latest user turn always stay.
 *
 * `countTokens(messages)` defaults to estimateTokens; pass the tokenizer's count
 * for an exact fit. `extraTokens` covers what is not in `messages` (tool list).
 *
 * @returns {{messages:Array, tokens:number, steps:string[], overBudget:boolean}}
 */
export function fitToBudget(messages, {
  budget, countTokens = estimateTokens, extraTokens = 0, isWrite = null, elideThreshold = 600, minResultChars = 1500
} = {}) {
  let msgs = (messages || []).map((m) => (m ? { ...m } : m));
  const steps = [];
  const count = () => countTokens(msgs) + extraTokens;
  let tokens = count();
  if (!budget || tokens <= budget) return { messages: msgs, tokens, steps, overBudget: false };

  const n = elideOldToolResults(msgs, { threshold: elideThreshold, keepTurns: 0, isWrite });
  if (n) { steps.push(`elided ${n} old tool result${n === 1 ? '' : 's'}`); tokens = count(); }

  const lastUser = msgs.map((m) => m && m.role).lastIndexOf('user');
  for (let limit = 16000; tokens > budget && limit >= minResultChars; limit = Math.floor(limit / 2)) {
    let clamped = 0;
    msgs.forEach((m, i) => {
      if (m && m.role === 'tool' && i > lastUser && typeof m.content === 'string' && m.content.length > limit) {
        m.content = clampToolResult(m.content, limit);
        clamped++;
      }
    });
    if (clamped) { steps.push(`clamped ${clamped} result${clamped === 1 ? '' : 's'} to ${limit} chars`); tokens = count(); }
  }

  while (tokens > budget) {
    const sys = msgs.findIndex((m) => m && m.role === 'system');
    const users = msgs.map((m, i) => (m && m.role === 'user' ? i : -1)).filter((i) => i >= 0);
    if (users.length < 2) break;
    const start = sys === 0 ? 1 : 0;
    const end = users[1]; // everything before the second user message goes
    if (end <= start) break;
    msgs.splice(start, end - start);
    steps.push('dropped the oldest turn');
    tokens = count();
  }
  return { messages: msgs, tokens, steps, overBudget: tokens > budget };
}

/* --------------------------------------------------------------- streaming */

/**
 * Turns generated token ids into text pieces as they arrive. Implements the
 * streamer interface Transformers.js `generate` calls (`put(ids[][])`,
 * `end()`); the first `put` is the prompt and is skipped. A piece is held back
 * while it ends in an incomplete UTF-8 sequence.
 */
export function createTokenStreamer(tokenizer, onText, { onToken = null } = {}) {
  let prompt = true;
  let cache = [];
  let printed = 0;
  const ids = [];
  const decode = (arr) => tokenizer.decode(arr, { skip_special_tokens: true });
  return {
    ids,
    put(value) {
      if (prompt) { prompt = false; return; }
      const toks = value[0] || [];
      for (const t of toks) { ids.push(t); cache.push(t); if (onToken) onToken(t); }
      const text = decode(cache);
      if (text.endsWith('�')) return;
      const piece = text.slice(printed);
      if (/\s$/.test(text) && cache.length > 32) { cache = []; printed = 0; } else printed = text.length;
      if (piece) onText(piece);
    },
    end() {
      if (cache.length) {
        const piece = decode(cache).slice(printed);
        cache = []; printed = 0;
        if (piece) onText(piece);
      }
    }
  };
}

/**
 * What of the raw output may be shown while it streams: reasoning to
 * `onReasoning`, answer text to `onContent`, tool-call markup to neither. Text
 * that might be the start of a marker is held until it is not. An answer that
 * opens like a JSON call (`{`, `[`, a fence, <tool_call>) is held to the end,
 * where parseModelOutput decides; the caller then emits what is left.
 */
export function createOutputSplitter({ onContent = null, onReasoning = null, startInThink = true } = {}) {
  let phase = startInThink ? 'think' : 'content';
  let pending = '';
  let inCall = false;
  let deferred = false;
  let content = '';
  let reasoning = '';
  let started = false; // first non-space answer character seen

  const emitContent = (s) => {
    if (!s) return;
    if (!started) { s = s.replace(/^\s+/, ''); if (!s) return; started = true; }
    content += s;
    if (onContent) onContent(s, content);
  };
  const emitReasoning = (s) => {
    if (!s) return;
    if (!reasoning) { s = s.replace(/^\s+/, ''); if (!s) return; }
    reasoning += s;
    if (onReasoning) onReasoning(s, reasoning);
  };
  const holdFor = (text, markers) => {
    let hold = 0;
    for (const mk of markers) {
      for (let n = Math.min(mk.length - 1, text.length); n > hold; n--) {
        if (text.endsWith(mk.slice(0, n))) { hold = n; break; }
      }
    }
    return hold;
  };

  function drain(final) {
    for (;;) {
      if (phase === 'think') {
        if (!reasoning && pending.trimStart().startsWith(THINK_OPEN)) pending = pending.trimStart().slice(THINK_OPEN.length);
        const close = pending.indexOf(THINK_CLOSE);
        const call = pending.indexOf(TOOL_CALL_START);
        if (close >= 0 && (call < 0 || close < call)) {
          emitReasoning(pending.slice(0, close));
          pending = pending.slice(close + THINK_CLOSE.length);
          phase = 'content';
          continue;
        }
        if (call >= 0) { emitReasoning(pending.slice(0, call)); pending = pending.slice(call); phase = 'content'; continue; }
        const hold = final ? 0 : holdFor(pending, [THINK_CLOSE, TOOL_CALL_START, THINK_OPEN]);
        emitReasoning(pending.slice(0, pending.length - hold));
        pending = pending.slice(pending.length - hold);
        return;
      }
      if (inCall) {
        const e = pending.indexOf(TOOL_CALL_END);
        if (e < 0) { if (final) pending = ''; return; }
        pending = pending.slice(e + TOOL_CALL_END.length);
        inCall = false;
        continue;
      }
      if (deferred) return;
      if (!started) {
        const t = pending.trimStart();
        if (/^(?:[{[]|```|<tool_call>)/.test(t)) { deferred = true; return; }
        if (!t && !final) return;
        if (t.length < 11 && '<tool_call>'.startsWith(t) && !final) return;
      }
      const s = pending.indexOf(TOOL_CALL_START);
      if (s >= 0) { emitContent(pending.slice(0, s)); pending = pending.slice(s + TOOL_CALL_START.length); inCall = true; continue; }
      const hold = final ? 0 : holdFor(pending, [TOOL_CALL_START, THINK_CLOSE]);
      emitContent(pending.slice(0, pending.length - hold).replace(/<\/?think>/g, ''));
      pending = pending.slice(pending.length - hold);
      return;
    }
  }

  return {
    push(text) { pending += text; drain(false); },
    end() { drain(true); return { content, reasoning, deferred }; },
    get content() { return content; },
    get reasoning() { return reasoning; }
  };
}

/* ------------------------------------------------------------- the stream */

let callSeq = 0;
const newCallId = (n) => `call_local_${Date.now().toString(36)}_${(callSeq++).toString(36)}_${n}`;

/**
 * A streamImpl for llm.js runTurn backed by a local model.
 *
 * Options:
 *   getModel        async () => { model, tokenizer } (e.g. a memoised loadLocalModel)
 *   k, maxTools     tool retrieval (selectTools); `always`, `categoryOf` passed through
 *   rankTools       optional async (messages, tools) => best-first names, fused with BM25
 *   contextTokens   prompt + output budget (default 16K); maxNewTokens (default 2048)
 *   sampling        generate() options (default LOCAL_SAMPLING)
 *   toolNote        appended to the system prompt (default LOCAL_TOOL_NOTE; '' for none)
 *   isWrite         name => boolean, for the budget's elision (as runTurn's)
 *   preserveThinking  re-send this turn's reasoning on its tool-call rounds (default true)
 *   now             clock for timings (tests)
 *
 * The returned function takes streamCompletion's options (apiKey, model, url,
 * sessionId are ignored) and resolves to `{ content, reasoning, toolCalls,
 * finishReason, usage, usageSummary, aborted }`, plus `local` diagnostics.
 *
 * Calls that fail validation still come back in `toolCalls` (runTurn must answer
 * every id), with the arguments as parsed; `stream.guard({ callTool, localTools })`
 * wraps runTurn's tool runners so those calls are answered here with an
 * `isError` explaining what to fix, and never reach the server.
 */
export function createLocalStream({
  getModel,
  k = DEFAULT_TOOL_K,
  maxTools = null,
  always = null,
  categoryOf = null,
  rankTools: extraRanker = null,
  contextTokens = DEFAULT_CONTEXT_TOKENS,
  maxNewTokens = DEFAULT_MAX_NEW_TOKENS,
  sampling = LOCAL_SAMPLING,
  toolNote = LOCAL_TOOL_NOTE,
  isWrite = null,
  preserveThinking = true,
  now = () => (globalThis.performance ? performance.now() : Date.now())
} = {}) {
  if (typeof getModel !== 'function') throw new TypeError('createLocalStream needs getModel()');
  const rejected = new Map(); // name \0 canonical args → error, for the latest round

  async function stream({ messages, tools, signal, onContent, onReasoning, onToolCallProgress }) {
    rejected.clear();
    const empty = { content: '', reasoning: '', toolCalls: [], finishReason: null, usage: null, usageSummary: null, aborted: true };
    if (signal && signal.aborted) return empty;

    const t0 = now();
    const { model, tokenizer } = await getModel();
    if (signal && signal.aborted) return empty;

    // 1. Tools for this round.
    const all = tools || [];
    const byName = toolsByName(all);
    const rankings = extraRanker ? [await extraRanker(messages, all)].filter(Array.isArray) : null;
    const selected = selectTools(all, messages, { k, maxTools, always, categoryOf, rankings });
    const templateTools = selected.map((t) => templateTool(toolInfo(t)));

    // 2. History that fits: estimate first, then the tokenizer's exact count.
    const promptBudget = Math.max(1024, contextTokens - maxNewTokens);
    const toolTokens = Math.ceil(JSON.stringify(templateTools).length / 3.2);
    const render = (msgs) => tokenizer.apply_chat_template(
      toTemplateMessages(withToolNote(msgs, toolNote), { preserveThinking }),
      { tools: templateTools.length ? templateTools : null, add_generation_prompt: true, return_dict: true }
    );
    let budget = promptBudget;
    let fitted = fitToBudget(messages, { budget, extraTokens: toolTokens, isWrite });
    let inputs = render(fitted.messages);
    let promptTokens = seqLength(inputs);
    for (let tries = 0; promptTokens > promptBudget && tries < 3; tries++) {
      budget = Math.floor(budget * (promptBudget / promptTokens) * 0.95);
      fitted = fitToBudget(messages, { budget, extraTokens: toolTokens, isWrite });
      inputs = render(fitted.messages);
      promptTokens = seqLength(inputs);
    }

    // 3. Generate, streaming what may be shown.
    const splitter = createOutputSplitter({ onContent, onReasoning, startInThink: true });
    let firstTokenAt = null;
    let lastToken = null;
    const streamer = createTokenStreamer(tokenizer, (piece) => splitter.push(piece), {
      onToken: (t) => { if (firstTokenAt === null) firstTokenAt = now(); lastToken = t; }
    });
    const stop = (ids) => ids.map(() => !!(signal && signal.aborted));
    const tGen = now();
    let genError = null;
    try {
      await model.generate({ ...inputs, ...sampling, max_new_tokens: maxNewTokens, streamer, stopping_criteria: [stop] });
    } catch (e) {
      genError = e;
    }
    const tEnd = now();
    if (genError && !(signal && signal.aborted)) throw new LocalModelError(`The local model failed: ${genError.message || genError}`, { code: 'generate_failed' });
    splitter.end();

    const aborted = !!(signal && signal.aborted);
    const completionTokens = streamer.ids.length;
    const raw = tokenizer.decode(streamer.ids, { skip_special_tokens: false });
    const eosIds = eosTokenIds(model, tokenizer);
    const hitLimit = !aborted && completionTokens >= maxNewTokens && !eosIds.has(String(lastToken));

    // 4. Parse, validate.
    const parsed = parseModelOutput(raw, { startInThink: true, tools: byName });
    const toolCalls = [];
    const validations = [];
    if (!aborted && !(hitLimit && parsed.truncatedCall)) {
      parsed.calls.forEach((c, n) => {
        const v = validateToolCall(c, byName);
        validations.push(v);
        const call = { id: newCallId(n), type: 'function', function: { name: v.name || 'unknown', arguments: JSON.stringify(v.args || {}) } };
        if (!v.ok) rejected.set(rejectKey(call.function.name, JSON.parse(call.function.arguments)), v.error);
        toolCalls.push(call);
      });
      // Unreadable call markup with no call recovered: answer it as an error round.
      if (!toolCalls.length && parsed.errors.length) {
        const call = { id: newCallId(0), type: 'function', function: { name: 'invalid_tool_call', arguments: '{}' } };
        rejected.set(rejectKey('invalid_tool_call', {}), `${parsed.errors.join(' ')} Write tool calls as [tool_name(arg="value", other=2)].`);
        toolCalls.push(call);
      }
    }
    if (preserveThinking && toolCalls.length && parsed.reasoning) REASONING.set(toolCalls, parsed.reasoning);

    // What the splitter held back (a deferred answer that was not a call after all).
    const shown = splitter.content;
    if (onContent && parsed.content && parsed.content !== shown && parsed.content.startsWith(shown)) {
      onContent(parsed.content.slice(shown.length), parsed.content);
    }
    if (onToolCallProgress && toolCalls.length) {
      onToolCallProgress(toolCalls.map((c, index) => ({ index, id: c.id, type: 'function', name: c.function.name, argumentsText: c.function.arguments })));
    }

    let finishReason = 'stop';
    if (aborted) finishReason = null;
    else if (toolCalls.length) finishReason = 'tool_calls';
    else if (hitLimit) finishReason = 'length';

    const usage = { prompt_tokens: promptTokens, completion_tokens: completionTokens, total_tokens: promptTokens + completionTokens };
    const decodeMs = firstTokenAt !== null ? tEnd - firstTokenAt : 0;
    stream.last = {
      raw,
      selectedTools: templateTools.map((t) => t.name),
      budgetSteps: fitted.steps,
      promptTokens,
      completionTokens,
      setupMs: tGen - t0,
      ttftMs: firstTokenAt !== null ? firstTokenAt - tGen : null,
      totalMs: tEnd - t0,
      tokensPerSecond: completionTokens > 1 && decodeMs > 0 ? (completionTokens - 1) / (decodeMs / 1000) : null,
      prefillTokensPerSecond: firstTokenAt !== null ? promptTokens / ((firstTokenAt - tGen) / 1000) : null,
      parseErrors: parsed.errors,
      validations
    };

    return {
      content: aborted ? splitter.content : parsed.content,
      reasoning: parsed.reasoning,
      toolCalls,
      finishReason,
      usage,
      usageSummary: normaliseUsage(usage),
      aborted,
      local: stream.last
    };
  }

  /** Wrap runTurn's callTool / localTools so rejected calls are answered locally. */
  stream.guard = ({ callTool, localTools = null } = {}) => {
    const check = (name, args) => rejected.get(rejectKey(name, args || {}));
    const guarded = {
      callTool: async (name, args) => {
        const err = check(name, args);
        return err ? { isError: true, content: err } : callTool(name, args);
      },
      localTools: null
    };
    if (localTools) {
      guarded.localTools = {};
      for (const [n, fn] of Object.entries(localTools)) {
        guarded.localTools[n] = async (args, call) => {
          const err = check(n, args);
          return err ? { isError: true, content: err } : fn(args, call);
        };
      }
    }
    return guarded;
  };
  /** The error a rejected call of the latest round will be answered with, or undefined. */
  stream.rejection = (name, args) => rejected.get(rejectKey(name, args || {}));
  stream.last = null;
  return stream;
}

const rejectKey = (name, args) => `${name}\u0000${canonicalJson(args)}`;

function seqLength(inputs) {
  const ids = inputs && (inputs.input_ids || inputs);
  if (ids && Array.isArray(ids.dims)) return ids.dims[ids.dims.length - 1];
  if (Array.isArray(ids)) return Array.isArray(ids[0]) ? ids[0].length : ids.length;
  return 0;
}

function eosTokenIds(model, tokenizer) {
  const ids = new Set();
  const add = (v) => { if (Array.isArray(v)) v.forEach(add); else if (v !== null && v !== undefined) ids.add(String(v)); };
  add(model && model.generation_config && model.generation_config.eos_token_id);
  add(model && model.config && model.config.eos_token_id);
  add(tokenizer && tokenizer.eos_token_id);
  return ids;
}

/* ------------------------------------------------------------------ loading */

/**
 * Load the tokenizer and model. `transformers` is the Transformers.js module:
 * Node passes `await import('@huggingface/transformers')`; in the browser it is
 * imported from TRANSFORMERS_CDN_URL when not given. In a browser the default
 * device is WebGPU, and its absence is a clear error rather than a silent,
 * unusably slow wasm fallback. `dtype` defaults to q4f16 where the GPU has
 * shader-f16, else q4 (and q4 off the browser).
 *
 * `onProgress` gets Transformers.js progress events ({ status, file, progress,
 * loaded, total }).
 */
export async function loadLocalModel({
  modelId = DEFAULT_LOCAL_MODEL,
  dtype = null,
  device = null,
  onProgress = null,
  transformers = null
} = {}) {
  const browser = typeof window !== 'undefined' && typeof document !== 'undefined';
  let f16 = false;
  if (browser && (!device || device === 'webgpu')) {
    const gpu = globalThis.navigator && navigator.gpu;
    if (!gpu) throw new LocalModelError('This browser has no WebGPU, which the local model needs. Use a recent Chrome, Edge or Safari, or pick an OpenRouter model.', { code: 'no_webgpu' });
    let adapter = null;
    try { adapter = await gpu.requestAdapter({ powerPreference: 'high-performance' }); } catch { adapter = null; }
    if (!adapter) throw new LocalModelError('WebGPU is present but no GPU adapter is available (blocked, or disabled in this browser).', { code: 'no_webgpu' });
    f16 = !!(adapter.features && adapter.features.has && adapter.features.has('shader-f16'));
  }
  const tf = transformers || await import(/* webpackIgnore: true */ TRANSFORMERS_CDN_URL);
  const dev = device || (browser ? 'webgpu' : 'cpu');
  const dt = dtype || (browser && f16 ? 'q4f16' : 'q4');

  const t0 = Date.now();
  const progress_callback = onProgress || undefined;
  const tokenizer = await tf.AutoTokenizer.from_pretrained(modelId, { progress_callback });
  const model = await tf.AutoModelForCausalLM.from_pretrained(modelId, { dtype: dt, device: dev, progress_callback });
  return {
    model,
    tokenizer,
    modelId,
    dtype: dt,
    device: dev,
    loadMs: Date.now() - t0,
    dispose: () => (model && typeof model.dispose === 'function' ? model.dispose() : undefined)
  };
}
