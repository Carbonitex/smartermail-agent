/**
 * eval.mjs — run the local-model task suite (tasks.mjs) through the real chat
 * loop and score it (score.mjs). Dev only.
 *
 *   cd src/Agent/wwwroot/dev/local-model && npm install
 *   node eval.mjs --retrieval-only               # tool retrieval recall@k, no model (seconds)
 *   node eval.mjs --retrieval-only --holdout     # the same over retrieval-holdout.mjs (not tuned on)
 *   node eval.mjs                                # every task once: webgpu (macOS), q4f16, k=8
 *   node eval.mjs --reps 2 --out results/k8.json
 *   node eval.mjs --only u-send-email            # a task id, a tag (write, multistep…) or a role (user, sysadmin…)
 *   node eval.mjs --all-tools --only sysadmin    # no retrieval: the role's whole tool list
 *   OPENROUTER_API_KEY=… node eval.mjs --backend openrouter --model anthropic/claude-haiku-5.5
 *
 * Each task is built the way chat.js builds a turn: buildSystemPrompt for the
 * role's fake accounts (with the large-results section, as analysis is on by
 * default), the fixture through toolsToOpenAI plus analyze_result, and runTurn
 * with the local stream as its streamImpl and its guard() around callTool /
 * localTools. callTool answers from the task's mocks in /api/tools/call's
 * `{ isError, content }` shape.
 *
 * The system prompt tells the model to confirm before sending or deleting, so
 * a write task whose first turn ends without a write gets one more user turn,
 * "Yes, go ahead." (the simulated user confirming); `--no-confirm` turns that
 * off. Calls the local guard rejected (bad name or arguments) never reach the
 * mocks and are not scored as calls; they are counted as `rejected`, as are
 * repeats of a failed call that the guard answers itself.
 *
 * Other flags: --model (local model id or OpenRouter model), --max-new N,
 * --think N (reasoning tokens per round before </think> is forced; 0 = none),
 * --context N (token budget; --all-tools raises it to fit the list),
 * --max-rounds N (default 8), --verbose (raw output of every round),
 * --quiet (summary only).
 *
 * Writes JSON (config, per-run trace, metrics and score, summary) to --out,
 * default results/<backend>-<timestamp>.json (results/ is gitignored).
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import * as localLlm from '../../js/local-llm.js';
import { createLocalStream, loadLocalModel, selectTools, DEFAULT_LOCAL_MODEL, DEFAULT_TOOL_K, DEFAULT_CONTEXT_TOKENS, DEFAULT_MAX_NEW_TOKENS } from '../../js/local-llm.js';
import { runTurn, toolsToOpenAI, buildSystemPrompt, streamCompletion, OPENROUTER_URL } from '../../js/llm.js';
import { ArtifactStore, ANALYZE_RESULT_TOOL } from '../../js/artifacts.js';
import { TASKS, mockResult, USER, SYSADMIN } from './tasks.mjs';
import { HOLDOUT } from './retrieval-holdout.mjs';
import { scoreRun, summarise, neededGroups } from './score.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const argv = process.argv.slice(2);
const flag = (name) => argv.includes(`--${name}`);
const opt = (name, dflt) => { const i = argv.indexOf(`--${name}`); return i >= 0 && i + 1 < argv.length ? argv[i + 1] : dflt; };

const backend = opt('backend', 'local');
const device = opt('device', process.platform === 'darwin' ? 'webgpu' : 'cpu');
const dtype = opt('dtype', 'q4f16');
const k = Number(opt('k', String(DEFAULT_TOOL_K)));
const reps = Number(opt('reps', '1'));
const only = opt('only', null);
const allTools = flag('all-tools') || backend === 'openrouter';
const maxNewTokens = Number(opt('max-new', String(DEFAULT_MAX_NEW_TOKENS)));
const contextOpt = opt('context', null);
const maxRounds = Number(opt('max-rounds', '8'));
// Older local-llm.js builds have no thinking budget; undefined keeps their default.
const thinkBudget = opt('think', null) === null ? localLlm.DEFAULT_THINK_BUDGET : Number(opt('think', null));
const confirm = !flag('no-confirm');
const verbose = flag('verbose');
const quiet = flag('quiet');
const modelId = opt('model', backend === 'openrouter' ? 'anthropic/claude-haiku-5.5' : DEFAULT_LOCAL_MODEL);

/** The conversation's clock: fixed, so the prompt and the dates in the tasks agree. */
const NOW = new Date('2026-10-17T10:00:00');
const CONFIRM_TEXT = 'Yes, go ahead.';

/* ---------------------------------------------------------- the session */

const FIXTURE_FILE = { user: 'tools-user.json', domainadmin: 'tools-domainadmin.json', sysadmin: 'tools-sysadmin.json', multi: 'tools-multi.json' };
const BASE_URL = 'https://mail.example.com';
const ACCOUNT_USER = { id: 'a1', role: 'User', handle: USER, username: 'user', emailAddress: USER, baseUrl: BASE_URL, readOnly: false };
const ACCOUNT_DOMAIN = { id: 'a2', role: 'DomainAdmin', handle: 'domainadmin@example.com', username: 'domainadmin', emailAddress: 'domainadmin@example.com', domain: 'example.com', baseUrl: BASE_URL, readOnly: false };
const ACCOUNT_SYS = { id: 'a3', role: 'SysAdmin', handle: SYSADMIN, username: 'admin', emailAddress: null, baseUrl: BASE_URL, readOnly: false };
/** The accounts each fixture was dumped with (ToolContractDumpTests). */
const ACCOUNTS = { user: [ACCOUNT_USER], domainadmin: [ACCOUNT_DOMAIN], sysadmin: [ACCOUNT_SYS], multi: [ACCOUNT_USER, ACCOUNT_SYS] };

const roles = {};
function role(name) {
  if (roles[name]) return roles[name];
  const contract = JSON.parse(fs.readFileSync(path.join(here, 'fixtures', FIXTURE_FILE[name]), 'utf8'));
  // chat.js turnTools(): the server's tools, then analyze_result (analysis is on by default).
  const tools = [...toolsToOpenAI(contract), ...toolsToOpenAI([ANALYZE_RESULT_TOOL])];
  const categories = new Map(contract.map((t) => [t.name, t.category]));
  categories.set(ANALYZE_RESULT_TOOL.name, 'Analysis');
  const writes = new Map(contract.map((t) => [t.name, t.write === true]));
  const system = buildSystemPrompt({ accounts: ACCOUNTS[name] }, { now: NOW, artifacts: true });
  roles[name] = {
    name, contract, tools, system,
    categoryOf: (n) => categories.get(n),
    isWrite: (n) => (writes.has(n) ? writes.get(n) : undefined),
    toolChars: JSON.stringify(tools).length
  };
  return roles[name];
}

function pickTasks() {
  if (!only) return TASKS;
  const keys = only.split(',');
  return TASKS.filter((t) => keys.some((key) => t.id === key || t.role === key || (t.tags || []).includes(key)));
}

/* ------------------------------------------------- retrieval only (no model) */

const RECALL_KS = [4, 6, 8, 10, 12, 16];

function retrievalOnly(tasks) {
  const rows = [];
  for (const task of tasks) {
    const r = role(task.role);
    const groups = neededGroups(task);
    const first = [{ role: 'system', content: r.system }, { role: 'user', content: task.prompt }];
    // The confirming turn of a write task: the request, an answer asking to confirm, "yes".
    const confirmTurn = [...first, { role: 'assistant', content: 'Please confirm and I will go ahead.' }, { role: 'user', content: CONFIRM_TEXT }];
    const row = { id: task.id, role: task.role, tags: task.tags, groups: groups.length };
    for (const kk of RECALL_KS) {
      const offered = new Set(selectTools(r.tools, first, { k: kk, categoryOf: r.categoryOf }).map((t) => t.function.name));
      const hit = groups.filter((g) => g.some((n) => offered.has(n))).length;
      row[`r@${kk}`] = groups.length ? hit / groups.length : 1;
      if (kk === k) {
        row.offered = [...offered];
        row.missed = groups.filter((g) => !g.some((n) => offered.has(n))).map((g) => g.join('|'));
        if (isWriteTask(task)) {
          const again = new Set(selectTools(r.tools, confirmTurn, { k: kk, categoryOf: r.categoryOf }).map((t) => t.function.name));
          const writeGroups = groups.filter((g) => g.some((n) => r.isWrite(n)));
          row.confirmRecall = writeGroups.length ? writeGroups.filter((g) => g.some((n) => again.has(n))).length / writeGroups.length : 1;
        }
      }
    }
    rows.push(row);
  }
  const mean = (key, list = rows) => list.reduce((s, r) => s + r[key], 0) / (list.length || 1);
  const full = (key, list = rows) => list.filter((r) => r[key] === 1).length / (list.length || 1);
  console.log(`Retrieval recall over ${rows.length} tasks (group recall: a group counts when any of its alternatives is offered; "all" = every group of the task)\n`);
  const table = {};
  for (const scope of ['all', 'user', 'domainadmin', 'sysadmin', 'multi']) {
    const list = scope === 'all' ? rows : rows.filter((r) => r.role === scope);
    if (!list.length) continue;
    table[scope] = Object.fromEntries(RECALL_KS.map((kk) => [`k=${kk}`, `${pct(mean(`r@${kk}`, list))} (all ${pct(full(`r@${kk}`, list))})`]));
  }
  console.table(table);
  const writes = rows.filter((r) => r.confirmRecall !== undefined);
  if (writes.length) console.log(`Confirm turn ("${CONFIRM_TEXT}") at k=${k}: write tool still offered in ${pct(mean('confirmRecall', writes))} of ${writes.length} write tasks`);
  const misses = rows.filter((r) => r.missed && r.missed.length);
  if (misses.length) {
    console.log(`\nMisses at k=${k}:`);
    for (const r of misses) console.log(`  ${r.id.padEnd(22)} missing ${r.missed.join(', ')}\n  ${''.padEnd(22)} offered ${r.offered.join(', ')}`);
  }
  return rows;
}

/* ----------------------------------------------------------- one task run */

function isWriteTask(task) {
  const r = role(task.role);
  return (task.expect.calls || []).some((e) => [].concat(e.name).some((n) => r.isWrite(n)));
}

/** A mock's answer in /api/tools/call's shape: `{ isError, content }`, content a string. */
function toCallResult(value) {
  if (value && typeof value === 'object' && 'isError' in value && 'content' in value) {
    return { isError: !!value.isError, content: typeof value.content === 'string' ? value.content : JSON.stringify(value.content) };
  }
  return { isError: false, content: typeof value === 'string' ? value : JSON.stringify(value ?? null) };
}

async function runTask(task, rep, ctx) {
  const r = role(task.role);
  const messages = [{ role: 'system', content: r.system }, { role: 'user', content: task.prompt }];
  const calls = [];
  const rejected = [];
  const rounds = [];
  const turns = [];
  let error = null;
  let confirmed = false;
  let wroteBeforeConfirm = false;
  const t0 = performance.now();

  let reached = false;
  const callTool = async (name, args) => {
    reached = true;
    const result = toCallResult(mockResult(task, name, args));
    calls.push({ name, args, isError: result.isError });
    if (!confirmed && r.isWrite(name)) wroteBeforeConfirm = true;
    return result;
  };
  const localTools = { [ANALYZE_RESULT_TOOL.name]: async (args) => {
    reached = true;
    calls.push({ name: ANALYZE_RESULT_TOOL.name, args, isError: true });
    return { isError: true, content: 'No artifact with that handle.' };
  } };

  const turn = async () => {
    const stream = ctx.stream;
    const guarded = stream.guard ? stream.guard({ callTool, localTools }) : { callTool, localTools };
    const out = await runTurn({
      messages,
      tools: r.tools,
      apiKey: ctx.apiKey,
      model: modelId,
      url: OPENROUTER_URL,
      sessionId: `eval-${task.id}-${rep}`,
      maxToolRounds: maxRounds,
      callTool: guarded.callTool,
      localTools: guarded.localTools,
      isWrite: r.isWrite,
      artifacts: new ArtifactStore(),
      streamImpl: stream,
      ui: {
        onAssistantEnd: (full, t) => {
          const d = stream.last || null;
          const reasoning = t.reasoning || '';
          const round = {
            finishReason: t.finishReason,
            calls: t.toolCalls.map((c) => c.function.name),
            promptTokens: d ? d.promptTokens : (t.usage && t.usage.prompt_tokens) || null,
            completionTokens: d ? d.completionTokens : (t.usage && t.usage.completion_tokens) || null,
            thinkingTokens: reasoning && ctx.countTokens ? ctx.countTokens(reasoning) : null,
            thinkingCut: d ? !!d.thinkingCut : false,
            ttftMs: d ? d.ttftMs : null,
            tokensPerSecond: d ? d.tokensPerSecond : null,
            totalMs: d ? d.totalMs : null,
            offered: d ? d.selectedTools : null,
            budgetSteps: d ? d.budgetSteps : [],
            parseErrors: d ? d.parseErrors : [],
            invalid: d ? d.validations.filter((v) => !v.ok).map((v) => ({ name: v.name, error: v.error.split('\n')[0] })) : [],
            coerced: d ? d.validations.flatMap((v) => v.notes) : [],
            raw: d ? d.raw : null,
            content: full
          };
          rounds.push(round);
          if (verbose) {
            console.log(`  --- round ${rounds.length} [${(round.offered || []).join(', ')}] prompt ${round.promptTokens} TTFT ${fmtS(round.ttftMs)} ${fmt(round.tokensPerSecond)} tok/s`);
            console.log(indent(round.raw || full || '(no output)'));
          }
        },
        onToolStart: () => { reached = false; },
        onToolEnd: (call, result) => {
          // The guard answered it locally (invalid, or a repeat of a failed call): not in `calls`.
          if (!reached) rejected.push({ name: call.name, args: call.args, error: String(result.content).split('\n')[0] });
          if (verbose) console.log(`  TOOL ${call.name}(${call.argsText}) → ${result.isError ? 'ERROR ' : ''}${String(result.content).slice(0, 200)}`);
        }
      }
    });
    turns.push(out);
  };

  try {
    await turn();
    if (confirm && isWriteTask(task) && !calls.some((c) => r.isWrite(c.name)) && lastAssistantText(messages)) {
      confirmed = true;
      messages.push({ role: 'user', content: CONFIRM_TEXT });
      await turn();
    }
  } catch (e) {
    error = e && e.message ? e.message : String(e);
  }

  const trace = { calls, finalText: lastAssistantText(messages), rounds: rounds.length, error };
  const score = scoreRun(task, trace);
  const groups = neededGroups(task);
  const firstOffered = rounds[0] && rounds[0].offered ? new Set(rounds[0].offered) : null;
  const recall = firstOffered && groups.length ? groups.filter((g) => g.some((n) => firstOffered.has(n))).length / groups.length : null;
  return {
    id: task.id, role: task.role, tags: task.tags, rep,
    pass: score.pass,
    failed: score.checks.filter((c) => !c.ok).map((c) => c.name + (c.detail ? ` (${c.detail})` : '')),
    score,
    trace,
    rejected,
    confirmed,
    wroteBeforeConfirm,
    wallMs: performance.now() - t0,
    recall,
    offeredRound1: rounds[0] ? rounds[0].offered : null,
    rounds
  };
}

function lastAssistantText(messages) {
  for (let i = messages.length - 1; i >= 0; i--) {
    const m = messages[i];
    if (m.role === 'user') return '';
    if (m.role === 'assistant' && typeof m.content === 'string' && m.content.trim()) return m.content;
  }
  return '';
}

/* ----------------------------------------------------------------- report */

function report(results) {
  const tasksById = new Map(TASKS.map((t) => [t.id, t]));
  const s = summarise(results.map((x) => ({ task: tasksById.get(x.id), score: x.score })));
  const line = (label, v) => `${label.padEnd(16)} ${pct(v.passRate).padStart(5)}  (${v.passed}/${v.n})  first-tool ${pct(v.firstToolRate)}  required ${pct(v.requiredRate)}  args ${pct(v.argsRate)}`;
  console.log(`\nPASS ${pct(s.passRate)} (${s.passed}/${s.total})`);
  console.log(line('overall', s.rates));
  for (const [kk, v] of Object.entries(s.byRole)) console.log(line(`role ${kk}`, v));
  for (const [kk, v] of Object.entries(s.byTag)) console.log(line(`tag ${kk}`, v));

  const wall = results.map((x) => x.wallMs);
  const r1 = results.map((x) => x.rounds[0]).filter(Boolean);
  const all = results.flatMap((x) => x.rounds);
  const nums = (list, key) => list.map((x) => x[key]).filter((v) => typeof v === 'number' && Number.isFinite(v));
  const recalls = results.map((x) => x.recall).filter((v) => v !== null);
  console.log(`\navg rounds ${s.avgRounds.toFixed(2)}  avg calls ${s.avgCalls.toFixed(2)}  rejected calls ${results.reduce((n, x) => n + x.rejected.length, 0)}  confirm turns ${results.filter((x) => x.confirmed).length}  wrote without asking ${results.filter((x) => x.wroteBeforeConfirm).length}`);
  console.log(`wall per task: median ${fmtS(median(wall))}  p90 ${fmtS(p90(wall))}  max ${fmtS(Math.max(...wall))}  total ${fmtS(wall.reduce((a, b) => a + b, 0))}`);
  console.log(`TTFT (round 1): median ${fmtS(median(nums(r1, 'ttftMs')))}  p90 ${fmtS(p90(nums(r1, 'ttftMs')))};  all rounds median ${fmtS(median(nums(all, 'ttftMs')))}  p90 ${fmtS(p90(nums(all, 'ttftMs')))}`);
  console.log(`prompt tokens (round 1): median ${median(nums(r1, 'promptTokens'))}  max ${Math.max(0, ...nums(r1, 'promptTokens'))};  all rounds max ${Math.max(0, ...nums(all, 'promptTokens'))}`);
  console.log(`thinking cut at the budget: ${all.filter((r) => r.thinkingCut).length} of ${all.length} rounds;  hit max_new_tokens: ${all.filter((r) => r.finishReason === 'length').length}`);
  console.log(`decode: median ${fmt(median(nums(all, 'tokensPerSecond')))} tok/s;  thinking tokens per round: median ${median(nums(all, 'thinkingTokens'))}  p90 ${p90(nums(all, 'thinkingTokens'))}  max ${Math.max(0, ...nums(all, 'thinkingTokens'))}`);
  if (recalls.length) console.log(`retrieval recall (round 1 offered vs needed groups): ${pct(recalls.reduce((a, b) => a + b, 0) / recalls.length)}, full ${pct(recalls.filter((v) => v === 1).length / recalls.length)}`);

  const fails = results.filter((x) => !x.pass);
  if (fails.length) {
    console.log('\nFailures:');
    for (const x of fails) {
      console.log(`  ${x.id}#${x.rep}: ${x.failed.join('; ')}`);
      console.log(`      calls: ${x.trace.calls.map((c) => `${c.name}(${JSON.stringify(c.args).slice(0, 140)})${c.isError ? '!' : ''}`).join(' → ') || '(none)'}${x.rejected.length ? `  rejected: ${x.rejected.map((c) => c.name).join(', ')}` : ''}${x.trace.error ? `  ERROR ${x.trace.error}` : ''}`);
      console.log(`      final: ${JSON.stringify((x.trace.finalText || '').slice(0, 200))}`);
    }
  }
  return s;
}

/* -------------------------------------------------------------------- main */

async function main() {
  // --holdout: the requests of retrieval-holdout.mjs instead of the tasks (--only: id, id prefix or role).
  const tasks = flag('holdout')
    ? HOLDOUT.filter((h) => !only || only.split(',').some((key) => h.id.startsWith(key) || h.role === key))
      .map((h) => ({ id: h.id, role: h.role, prompt: h.prompt, tags: ['holdout'], expect: { anyOf: h.groups } }))
    : pickTasks();
  if (!tasks.length) throw new Error(`No task matches --only ${only}`);
  if (flag('retrieval-only')) {
    const set = tasks;
    const rows = retrievalOnly(set);
    const out = opt('out', null);
    if (out) writeJson(out, { config: { mode: 'retrieval-only', k, ks: RECALL_KS }, rows });
    return;
  }

  const ctx = { stream: null, countTokens: null, apiKey: null };
  let loaded = null;
  let contextTokens = contextOpt ? Number(contextOpt) : DEFAULT_CONTEXT_TOKENS;
  if (backend === 'openrouter') {
    ctx.apiKey = process.env.OPENROUTER_API_KEY;
    if (!ctx.apiKey) throw new Error('--backend openrouter needs OPENROUTER_API_KEY');
    ctx.stream = (o) => streamCompletion(o);
  } else {
    const transformers = await import('@huggingface/transformers');
    loaded = await loadLocalModel({ modelId, dtype, device, transformers });
    ctx.countTokens = (text) => loaded.tokenizer.encode(text).length;
    if (allTools && !contextOpt) {
      // The whole list has to fit, with room for the conversation and the answer.
      const biggest = Math.max(...[...new Set(tasks.map((t) => t.role))].map((n) => role(n).toolChars));
      contextTokens = Math.max(DEFAULT_CONTEXT_TOKENS, Math.ceil(biggest / 3) + 8192 + maxNewTokens);
    }
  }

  const config = { backend, modelId, device, dtype, k: allTools ? 'all' : k, reps, only, maxNewTokens, thinkBudget: thinkBudget ?? null, contextTokens, maxRounds, confirm, loadMs: loaded ? loaded.loadMs : null, node: process.version, startedAt: new Date().toISOString() };
  if (!quiet) console.log(`eval: ${tasks.length} tasks × ${reps} — ${JSON.stringify(config)}`);

  const results = [];
  for (let rep = 1; rep <= reps; rep++) {
    for (const task of tasks) {
      if (backend === 'local') {
        // A fresh stream per run, as each run is its own conversation; same model.
        const r = role(task.role);
        ctx.stream = createLocalStream({
          getModel: async () => loaded,
          k: allTools ? r.tools.length : k,
          maxTools: allTools ? r.tools.length : null,
          categoryOf: r.categoryOf,
          contextTokens,
          maxNewTokens,
          thinkBudget,
          isWrite: r.isWrite
        });
      }
      if (verbose) console.log(`\n=== ${task.id}#${rep}: ${task.prompt}`);
      const res = await runTask(task, rep, ctx);
      results.push(res);
      if (!quiet) {
        const r1 = res.rounds[0] || {};
        console.log(`${res.pass ? 'PASS' : 'FAIL'} ${`${res.id}#${rep}`.padEnd(26)} ${fmtS(res.wallMs).padStart(7)}  rounds ${res.rounds.length}  calls ${res.trace.calls.map((c) => c.name + (c.isError ? '!' : '')).join(',') || '-'}${res.rejected.length ? `  rejected ${res.rejected.map((c) => c.name).join(',')}` : ''}${res.confirmed ? '  [confirmed]' : ''}  prompt ${r1.promptTokens ?? '-'}  TTFT ${fmtS(r1.ttftMs)}${res.recall !== null && res.recall < 1 ? `  RECALL ${pct(res.recall)}` : ''}${res.pass ? '' : `  ← ${res.failed[0]}`}`);
      }
    }
  }

  const summary = report(results);
  const out = opt('out', path.join(here, 'results', `${backend}-${allTools ? 'all' : `k${k}`}-${new Date().toISOString().replace(/[:.]/g, '-')}.json`));
  writeJson(out, { config, summary, results });
  if (loaded) await loaded.dispose();
}

function writeJson(file, data) {
  const target = path.isAbsolute(file) ? file : path.resolve(process.cwd(), file);
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, JSON.stringify(data, (key, v) => (typeof v === 'function' ? undefined : v), 2));
  console.log(`\nwrote ${target}`);
}

const pct = (v) => (typeof v === 'number' && Number.isFinite(v) ? `${Math.round(v * 100)}%` : '-');
function fmt(n) { return typeof n === 'number' && Number.isFinite(n) ? n.toFixed(1) : '-'; }
function fmtS(n) { return typeof n === 'number' && Number.isFinite(n) ? `${(n / 1000).toFixed(1)}s` : '-'; }
function indent(t) { return String(t).split('\n').map((l) => `    | ${l}`).join('\n'); }
function quantile(list, q) {
  if (!list.length) return null;
  const s = [...list].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.floor(q * s.length))];
}
const median = (list) => quantile(list, 0.5);
const p90 = (list) => quantile(list, 0.9);

main().catch((e) => { console.error(e); process.exit(1); });
