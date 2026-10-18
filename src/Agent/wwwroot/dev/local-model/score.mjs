/**
 * score.mjs — pure scoring for the local-model evaluation (dev only, no I/O).
 *
 *   scoreRun(task, trace) -> { pass, checks, firstToolCorrect, allRequiredCalled,
 *                              argsValid, forbiddenCalled, ... }
 *   summarise(results)    -> aggregate pass rates overall / by role / by tag
 *   neededTools(task)     -> Set of tool names a correct run must see (retrieval recall@k)
 *
 * trace = { calls: [{ name, args, isError }], finalText, rounds, error? }
 */

const asList = n => (Array.isArray(n) ? n : [n]);
const safe = (fn, ...args) => { try { return !!fn(...args); } catch { return false; } };

/** Names a `calls` entry accepts. */
export const entryNames = entry => asList(entry.name);

/** Every tool name a correct run may need: required calls (any alternative) plus anyOf groups. */
export function neededTools(task) {
  const out = new Set();
  for (const e of task.expect.calls || []) for (const n of entryNames(e)) out.add(n);
  for (const g of task.expect.anyOf || []) for (const n of g) out.add(n);
  return out;
}

/**
 * The same requirement as groups: a run needs at least one tool from each
 * group, so a retrieval that surfaces any alternative counts (recall over groups).
 */
export function neededGroups(task) {
  return [...(task.expect.calls || []).map(entryNames), ...(task.expect.anyOf || []).map(g => [...g])];
}

/**
 * Match required calls against the trace. Unordered: each entry takes any
 * not-yet-used call that satisfies it. Ordered: entries must be satisfied by
 * calls at increasing positions. Returns per entry { named, matched }.
 */
function matchEntries(entries, calls, ordered) {
  const used = new Set();
  let cursor = 0;
  return entries.map(entry => {
    const names = entryNames(entry);
    const nameOk = c => names.includes(c.name);
    const argsOk = c => !entry.args || safe(entry.args, c.args || {});
    const start = ordered ? cursor : 0;
    let named = false, hit = -1;
    for (let i = start; i < calls.length; i++) {
      if (!ordered && used.has(i)) continue;
      if (!nameOk(calls[i])) continue;
      named = true;
      if (argsOk(calls[i])) { hit = i; break; }
    }
    if (!named && ordered) {
      // name present only before the cursor: out of order, still counts as called
      named = calls.some(nameOk);
    }
    if (hit >= 0) { used.add(hit); cursor = hit + 1; }
    return { named, matched: hit >= 0 };
  });
}

export function scoreRun(task, trace) {
  const expect = task.expect || {};
  const calls = (trace && trace.calls) || [];
  const finalText = (trace && trace.finalText) || '';
  const entries = expect.calls || [];
  const checks = [];
  const add = (name, ok, detail = '') => checks.push({ name, ok: !!ok, detail });

  add('no-error', !trace || !trace.error, trace && trace.error ? String(trace.error) : '');

  const m = matchEntries(entries, calls, !!expect.ordered);
  entries.forEach((e, i) => {
    const label = e.desc || entryNames(e).join('|');
    add(`call: ${label}`, m[i].matched,
      m[i].matched ? '' : m[i].named ? 'called, but arguments did not match' : `no call to ${entryNames(e).join(' or ')}`);
  });
  const allRequiredCalled = m.every(x => x.named);
  const argsValid = m.every(x => x.matched);
  if (expect.ordered && entries.length > 1) {
    // the greedy ordered matcher already enforces order; report it on its own for the table
    const unordered = matchEntries(entries, calls, false).every(x => x.matched);
    add('order', !unordered || argsValid, unordered && !argsValid ? 'required calls present but out of order' : '');
  }

  for (const g of expect.anyOf || []) {
    add(`anyOf: ${g.join('|')}`, calls.some(c => g.includes(c.name)), `none of ${g.join(', ')} called`);
  }

  let forbiddenCalled = false;
  if (expect.forbid) {
    const bad = calls.filter(c => (Array.isArray(expect.forbid) ? expect.forbid.includes(c.name) : safe(expect.forbid, c)));
    forbiddenCalled = bad.length > 0;
    add('forbidden', !forbiddenCalled, forbiddenCalled ? `called ${bad.map(c => c.name).join(', ')}` : '');
  }

  if (expect.noTools) add('no tool calls', calls.length === 0, calls.length ? `called ${calls.map(c => c.name).join(', ')}` : '');
  if (expect.maxCalls !== undefined) add(`at most ${expect.maxCalls} calls`, calls.length <= expect.maxCalls, `${calls.length} calls`);

  const answerOk = !expect.answer || safe(expect.answer, finalText);
  add('final answer present', finalText.trim().length > 0, '');
  if (expect.answer) add(`answer: ${expect.answerDesc || 'predicate'}`, answerOk, answerOk ? '' : finalText.slice(0, 120));

  // first tool: for a tool task, the first call is one the task needs; for a no-tool task, there is none
  let firstToolCorrect;
  if (expect.noTools) firstToolCorrect = calls.length === 0;
  else if (!entries.length && !(expect.anyOf || []).length) firstToolCorrect = !calls.some(c => forbiddenFirst(expect, c)) ;
  else {
    const first = calls[0];
    const pool = expect.ordered ? new Set(entryNames(entries[0] || { name: [] })) : neededTools(task);
    for (const g of expect.anyOf || []) if (!expect.ordered || !entries.length) g.forEach(n => pool.add(n));
    firstToolCorrect = !!first && pool.has(first.name);
  }

  const errorCalls = calls.filter(c => c.isError).length;
  const needed = neededTools(task);
  const strayCalls = calls.filter(c => !needed.has(c.name)).map(c => c.name);

  return {
    pass: checks.every(c => c.ok),
    checks,
    firstToolCorrect,
    allRequiredCalled,
    argsValid,
    forbiddenCalled,
    answerOk,
    errorCalls,
    strayCalls,
    callCount: calls.length,
    rounds: (trace && trace.rounds) || 0
  };
}

function forbiddenFirst(expect, call) {
  if (!expect.forbid) return false;
  return Array.isArray(expect.forbid) ? expect.forbid.includes(call.name) : safe(expect.forbid, call);
}

const rate = (n, d) => (d ? n / d : 0);

/**
 * results: [{ task, trace?, score? }] (score is computed from trace when absent).
 * Returns { total, passed, passRate, rates, byRole, byTag, avgCalls, avgRounds, failures }.
 */
export function summarise(results) {
  const rows = results.map(r => ({ task: r.task, score: r.score || scoreRun(r.task, r.trace) }));
  const tally = list => {
    const n = list.length;
    const c = key => list.filter(r => r.score[key]).length;
    return {
      n, passed: c('pass'), passRate: rate(c('pass'), n),
      firstToolRate: rate(c('firstToolCorrect'), n), requiredRate: rate(c('allRequiredCalled'), n),
      argsRate: rate(c('argsValid'), n), forbiddenRate: rate(c('forbiddenCalled'), n)
    };
  };
  const group = keyFn => {
    const out = {};
    for (const r of rows) for (const k of keyFn(r.task)) (out[k] = out[k] || []).push(r);
    return Object.fromEntries(Object.entries(out).map(([k, v]) => [k, tally(v)]));
  };
  const all = tally(rows);
  return {
    total: all.n, passed: all.passed, passRate: all.passRate,
    rates: all,
    byRole: group(t => [t.role]),
    byTag: group(t => t.tags || []),
    avgCalls: rate(rows.reduce((s, r) => s + r.score.callCount, 0), rows.length),
    avgRounds: rate(rows.reduce((s, r) => s + r.score.rounds, 0), rows.length),
    failures: rows.filter(r => !r.score.pass).map(r => ({ id: r.task.id, failed: r.score.checks.filter(c => !c.ok).map(c => c.name + (c.detail ? ` (${c.detail})` : '')) }))
  };
}
