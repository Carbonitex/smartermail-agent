/**
 * stub-predicate.mjs — dev only: a small JavaScript stand-in for the
 * server's predicate engine (Tasks/Triggers/Predicate.cs), so the stub can
 * answer "Test probe" with an evaluation. Same node forms and operators;
 * none of the server's limits, regex timeout or new-item memory (every item
 * counts as new, as in a real Test probe).
 */

import { parsePath, pathText } from '../js/triggers.js';

const COMPARE = ['eq', 'ne', 'gt', 'gte', 'lt', 'lte'];

function select(value, segments, at = []) {
  if (!segments.length) return [{ value, at }];
  const [head, ...rest] = segments;
  if (head === '*') {
    if (Array.isArray(value)) return value.flatMap((v, i) => select(v, rest, [...at, i]));
    if (value && typeof value === 'object') return Object.entries(value).flatMap(([k, v]) => select(v, rest, [...at, k]));
    return [];
  }
  if (typeof head === 'number') return Array.isArray(value) && head < value.length ? select(value[head], rest, [...at, head]) : [];
  return value && typeof value === 'object' && !Array.isArray(value) && head in value ? select(value[head], rest, [...at, head]) : [];
}

function compare(op, a, b) {
  if (typeof a === 'number' && typeof b === 'number') {
    return { eq: a === b, ne: a !== b, gt: a > b, gte: a >= b, lt: a < b, lte: a <= b }[op];
  }
  if (op !== 'eq' && op !== 'ne') return false;
  if (typeof a !== typeof b || (a === null) !== (b === null)) return false;
  return op === 'eq' ? a === b : a !== b;
}

function test(node, v, now) {
  const op = node.op;
  if (COMPARE.includes(op)) return compare(op, v, node.value);
  if (op === 'exists') return v !== null && v !== undefined;
  if (typeof v !== 'string') return false;
  const s = v.toLowerCase(), x = String(node.value).toLowerCase();
  if (op === 'contains') return s.includes(x);
  if (op === 'startsWith') return s.startsWith(x);
  if (op === 'endsWith') return s.endsWith(x);
  if (op === 'matches') return new RegExp(node.value, 'i').test(v);
  const t = Date.parse(v);
  if (Number.isNaN(t)) return false;
  const ahead = (t - now) / 86400000;
  return { daysUntilLt: ahead < node.value, daysUntilGt: ahead > node.value, daysAgoLt: ahead <= 0 && -ahead < node.value, daysAgoGt: -ahead > node.value }[op] ?? false;
}

function evaluate(node, value, base, now, evidence) {
  if (node.all || node.any) {
    const list = node.all || node.any;
    const results = list.map((child) => { const mine = []; return { ok: evaluate(child, value, base, now, mine), mine }; });
    const ok = node.all ? results.every((r) => r.ok) : results.some((r) => r.ok);
    if (ok && evidence) for (const r of results) if (r.ok) evidence.push(...r.mine);
    return ok;
  }
  if (node.not) return !evaluate(node.not, value, base, now, null);
  if (node.count || node.new) {
    const spec = node.count || node.new;
    const items = select(value, parsePath(spec.items) || []);
    const matched = items.filter((it) => !spec.where || evaluate(spec.where, it.value, [...base, ...it.at], now, null));
    const ok = node.count ? compare(node.op, matched.length, node.value) : matched.length > 0;
    if (ok && evidence) for (const m of matched.slice(0, 21)) evidence.push({ path: pathText([...base, ...m.at]), value: m.value });
    return ok;
  }
  let any = false;
  for (const hit of select(value, parsePath(node.path) || [])) {
    if (!test(node, hit.value, now)) continue;
    any = true;
    if (evidence && evidence.length <= 20) evidence.push({ path: pathText([...base, ...hit.at]), value: hit.value });
  }
  return any;
}

/** A rough "describe" for the stub's UI. */
function describe(node) {
  if (node.all || node.any) return '(' + (node.all || node.any).map(describe).join(node.all ? ' and ' : ' or ') + ')';
  if (node.not) return `not ${describe(node.not)}`;
  if (node.count) return `the number of items in ${node.count.items}${node.count.where ? ` where ${describe(node.count.where)}` : ''} ${node.op} ${node.value}`;
  if (node.new) return `a new item in ${node.new.items}${node.new.where ? ` where ${describe(node.new.where)}` : ''}`;
  return `${node.path} ${node.op}${node.value === undefined ? '' : ' ' + JSON.stringify(node.value)}`;
}

/** { value, matched, truncated, errors, description } — the shape POST /api/tasks/probe returns. */
export function evaluatePredicate(when, result, now = Date.now()) {
  if (!when || typeof when !== 'object' || Array.isArray(when)) {
    return { value: false, matched: [], truncated: false, errors: ['when: the condition must be a JSON object.'], description: null };
  }
  try {
    const evidence = [];
    const value = evaluate(when, result, [], now, evidence);
    return { value, matched: value ? evidence.slice(0, 20) : [], truncated: value && evidence.length > 20, errors: [], description: describe(when) };
  } catch (err) {
    return { value: false, matched: [], truncated: false, errors: [`when: ${err.message}`], description: null };
  }
}
