/**
 * local-model-ui.js — EXPERIMENTAL: the chat's side of the in-browser model
 * (local-llm.js). Everything the chat needs to offer it lives here, so taking
 * the experiment out is this file, its CSS block, the bar in index.html and a
 * handful of marked lines in chat.js and tasks.js.
 *
 *  - an extra entry in the model picker (`local:<hub id>`), never sent anywhere:
 *    OpenRouter never sees it, scheduled tasks refuse it (tasks.js);
 *  - the bar under the header: load / progress / ready / unload, WebGPU check,
 *    the debug switch; and, for an OpenRouter model picked without a key, a
 *    key field (a local-model chat may have started without one);
 *  - runTurn's options for a local turn: `streamImpl` and the guarded tool
 *    runners (calls the model got wrong are answered here, not by the server);
 *  - the write gate: every write the model calls waits for Approve / Deny on a
 *    card in the chat before it reaches the server (below);
 *  - per round: the thinking text, tokens, tok/s and "tools offered: N of M",
 *    and behind the Debug switch the tools offered and the raw output.
 *
 * local-llm.js (and Transformers.js from the CDN) are imported on first use
 * only, so a page that never picks the local model loads neither.
 *
 * The pure helpers at the top are node-tested (dev/test/local-model-ui.test.mjs);
 * nothing touches the DOM until initLocalModelUi().
 */

export const LOCAL_MODEL_PREFIX = 'local:';
export const LOCAL_HUB_MODEL = 'LiquidAI/LFM3-1.7B-ONNX';
export const LOCAL_MODEL_ID = LOCAL_MODEL_PREFIX + LOCAL_HUB_MODEL;
export const LOCAL_MODEL_NAME = 'Local · LFM3 1.7B (in-browser, experimental)';
const SHORT_NAME = 'Local · LFM3 1.7B';
const DOWNLOAD_NOTE = 'about 1.4–1.7 GB the first time, cached by the browser after';

/** True for a picker value that names the in-browser model (never an OpenRouter id: those have no colon prefix). */
export const isLocalModel = (id) => typeof id === 'string' && id.startsWith(LOCAL_MODEL_PREFIX);

/** The model a scheduled task should start with: the chat's, unless that one only exists in this browser. */
export const taskModelFor = (id, fallback) => (isLocalModel(id) || !id ? fallback : id);

/** The picker's list with the local model first (once, whatever the list held). */
export function withLocalModel(models) {
  return [{ id: LOCAL_MODEL_ID, name: LOCAL_MODEL_NAME }, ...(models || []).filter((m) => m && !isLocalModel(m.id))];
}

/**
 * Transformers.js progress events (`initiate`, `download`, `progress`, `done`
 * per file, across the tokenizer's and the model's loads) folded into one
 * figure. Files whose size is not known yet count as 0 bytes.
 */
export function createProgressTracker() {
  const files = new Map();
  let active = '';
  const snapshot = () => {
    let loaded = 0;
    let total = 0;
    for (const f of files.values()) { loaded += f.loaded; total += f.total; }
    return { loaded, total, fraction: total > 0 ? Math.min(1, loaded / total) : null, files: files.size, active };
  };
  return {
    update(ev) {
      if (!ev || typeof ev.file !== 'string' || !ev.file) return snapshot();
      const f = files.get(ev.file) || { loaded: 0, total: 0 };
      if (ev.status === 'progress') {
        const total = Number(ev.total);
        const loaded = Number(ev.loaded);
        if (Number.isFinite(total) && total > 0) f.total = total;
        if (Number.isFinite(loaded) && loaded >= 0) f.loaded = Math.min(loaded, f.total || loaded);
        active = ev.file;
      } else if (ev.status === 'done') {
        if (f.total) f.loaded = f.total;
      } else if (ev.status !== 'initiate' && ev.status !== 'download') {
        return snapshot();
      }
      files.set(ev.file, f);
      return snapshot();
    },
    snapshot
  };
}

/** 1536 → "1.5 KB", 1.6e9 → "1.49 GB". */
export function formatBytes(n) {
  if (!Number.isFinite(n) || n < 0) return '?';
  if (n < 1024) return `${n} B`;
  if (n < 1024 ** 2) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 ** 3) return `${(n / 1024 ** 2).toFixed(n < 100 * 1024 ** 2 ? 1 : 0)} MB`;
  return `${(n / 1024 ** 3).toFixed(2)} GB`;
}

/** "Downloading 640 MB of 1.49 GB (42%)", or a byte count while the total is unknown. */
export function progressText(snap) {
  if (!snap || (!snap.loaded && !snap.total)) return 'Starting…';
  if (snap.fraction === null) return `Downloading ${formatBytes(snap.loaded)}…`;
  if (snap.fraction >= 1) return 'Preparing the model on the GPU…';
  return `Downloading ${formatBytes(snap.loaded)} of ${formatBytes(snap.total)} (${Math.floor(snap.fraction * 100)}%)`;
}

/**
 * One round's line from local-llm.js `stream.last`:
 * "1.2k in · 210 out · 18.3 tok/s · first token 1.4 s · tools offered: 8 of 59".
 */
export function formatRoundStats(local, toolCount = null) {
  if (!local) return '';
  const parts = [`${compact(local.promptTokens)} in`, `${compact(local.completionTokens)} out`];
  if (Number.isFinite(local.tokensPerSecond)) parts.push(`${local.tokensPerSecond.toFixed(1)} tok/s`);
  if (Number.isFinite(local.ttftMs)) parts.push(`first token ${(local.ttftMs / 1000).toFixed(1)} s`);
  if (Array.isArray(local.selectedTools)) {
    parts.push(`tools offered: ${local.selectedTools.length}${Number.isFinite(toolCount) ? ` of ${toolCount}` : ''}`);
  }
  const rejected = (local.validations || []).filter((v) => v && !v.ok).length + (local.parseErrors || []).length;
  if (rejected) parts.push(`${rejected} invalid call${rejected === 1 ? '' : 's'} answered locally`);
  return parts.join(' · ');
}

function compact(n) {
  if (!Number.isFinite(n)) return '?';
  if (n < 1000) return String(n);
  return `${(n / 1000).toFixed(n < 10000 ? 1 : 0).replace(/\.0$/, '')}k`;
}

/* ---------------------------------------------------------- the write gate */

/*
 * The system prompt asks every model to state a change and wait for the user's
 * yes in a separate turn. LFM2.5 2.6B ignored that in every eval run (32 of 32
 * writes went straight through), so for the local model the app asks instead:
 * a write tool call waits for Approve / Deny on a card in the chat, and nothing
 * reaches the server until Approve. OpenRouter turns never pass through here.
 *
 * Gated: every tool the tool list does not mark `write: false`, so a name the
 * list does not know counts as a write (fail closed). Read-only accounts are
 * not special-cased: the browser does not resolve `account` (the server does,
 * and the tool list already offers a write only where a read-write account can
 * run it), and a write the server would refuse with 403 costs one click, never
 * a change.
 */

export const GATE_DECLINED = 'The user declined this action. Do not retry it; ask what they want instead.';
export const GATE_CANCELLED = 'The user stopped this turn before approving this action, so it was not performed. Do not retry it; ask what they want instead.';

/** null when a tool runs without asking, else `{ destructive }` for the card. `info` = its tool list entry, or null. */
export function writeGate(info) {
  if (info && info.write === false) return null;
  return { destructive: !!(info && info.destructive === true) };
}

/** The tool result for a gated call that did not run: `'cancel'` (Stop), anything else is a decline. */
export function gateRefusal(outcome) {
  return { isError: true, content: outcome === 'cancel' ? GATE_CANCELLED : GATE_DECLINED };
}

/**
 * `callTool` behind the gate. A gated call runs only when
 * `confirm({ name, args, destructive, signal })` resolves to `'approve'`
 * before `signal` aborts; `'deny'`, `'cancel'`, any other value or a throw
 * answer the model with gateRefusal and make no call. No DOM: the card is
 * confirm's job (confirmCard below), so node tests pass a fake.
 */
export function gateCallTool(callTool, { toolInfo, confirm, signal = null }) {
  return async (name, args) => {
    const gate = writeGate(toolInfo(name));
    if (!gate) return callTool(name, args);
    if (signal && signal.aborted) return gateRefusal('cancel');
    let outcome;
    try {
      outcome = await untilAborted(confirm({ name, args: args || {}, destructive: gate.destructive, signal }), signal);
    } catch {
      outcome = 'deny';
    }
    if (signal && signal.aborted) return gateRefusal('cancel');
    return outcome === 'approve' ? callTool(name, args) : gateRefusal(outcome);
  };
}

/** `promise`, or `'cancel'` as soon as `signal` aborts (a confirm that never settles cannot hold Stop up). */
function untilAborted(promise, signal) {
  if (!signal) return promise;
  return new Promise((resolve, reject) => {
    const onAbort = () => resolve('cancel');
    signal.addEventListener('abort', onAbort, { once: true });
    Promise.resolve(promise).then(resolve, reject).finally(() => signal.removeEventListener('abort', onAbort));
  });
}

/* ----------------------------------------------------------------- the bar */

// { bar, messages, scroll(), isBusy(), hasKey(), saveKey(key), toolCount(), categoryOf(name), isWrite(name),
//   toolInfo(name), accountLabel(handle), argumentsTable(argsJson) }
let hooks = null;
let llm = null;       // the local-llm.js module, once imported
let stream = null;    // createLocalStream(), one per page
let loading = null;   // Promise of loadLocalModel's result while loading / once loaded
let loaded = null;    // that result
let status = { state: 'idle', text: '' };   // idle | loading | ready | error
let modelId = '';     // the picker's current value

const DEBUG_KEY = 'sma.localDebug';
const debugOn = () => { try { return sessionStorage.getItem(DEBUG_KEY) === '1'; } catch { return false; } };

export function initLocalModelUi(h) {
  hooks = h;
  hooks.messages.classList.toggle('local-debug-on', debugOn());
}

const hasWebGpu = () => typeof navigator !== 'undefined' && !!navigator.gpu;

/** Show the bar for the picker's value: the local model's status, or a key field for an OpenRouter model without a key. */
export function render(id) {
  modelId = id;
  const bar = hooks && hooks.bar;
  if (!bar) return;
  if (isLocalModel(id)) renderLocal(bar);
  else if (!hooks.hasKey()) renderKeyField(bar);
  else { bar.hidden = true; bar.replaceChildren(); }
}

function renderLocal(bar) {
  bar.hidden = false;
  bar.className = 'local-model-bar';
  const name = node('strong', '', SHORT_NAME);
  const text = node('span', 'local-model-status');
  const actions = node('span', 'local-model-actions');

  if (!hasWebGpu()) {
    bar.classList.add('warn');
    text.textContent = 'This browser has no WebGPU, which the in-browser model needs. Use a recent Chrome or Edge on a machine with a GPU, or pick an OpenRouter model.';
  } else if (status.state === 'loading') {
    text.textContent = status.text || 'Loading…';
    const p = node('progress', 'local-model-progress');
    if (status.fraction !== null && status.fraction !== undefined) { p.max = 1; p.value = status.fraction; }
    actions.append(p);
  } else if (status.state === 'ready') {
    text.textContent = status.text;
    const off = button('Unload', unload, 'Free the GPU memory; the download stays cached');
    off.disabled = hooks.isBusy();
    actions.append(off);
  } else {
    if (status.state === 'error') bar.classList.add('warn');
    text.textContent = status.state === 'error'
      ? status.text
      : `Experimental: runs on this device with WebGPU and loads on the first message (${DOWNLOAD_NOTE}). No OpenRouter key needed.`;
    actions.append(button(status.state === 'error' ? 'Retry' : 'Load model', () => { ensureLoaded().catch(() => {}); }));
  }

  const debug = document.createElement('label');
  debug.className = 'local-model-debug';
  const box = document.createElement('input');
  box.type = 'checkbox';
  box.checked = debugOn();
  box.addEventListener('change', () => {
    try { sessionStorage.setItem(DEBUG_KEY, box.checked ? '1' : '0'); } catch { /* storage disabled */ }
    hooks.messages.classList.toggle('local-debug-on', box.checked);
  });
  debug.append(box, document.createTextNode(' Debug'));
  debug.title = 'Show the tools offered and the raw model output for each round';
  actions.append(debug);

  bar.replaceChildren(name, text, actions);
}

function renderKeyField(bar) {
  bar.hidden = false;
  bar.className = 'local-model-bar warn';
  const text = node('span', 'local-model-status', 'This model runs on OpenRouter and needs your API key (kept in this tab only).');
  const form = document.createElement('form');
  form.className = 'local-model-actions';
  const input = document.createElement('input');
  input.type = 'password';
  input.placeholder = 'sk-or-v1-…';
  input.autocomplete = 'off';
  input.spellcheck = false;
  input.className = 'local-model-key';
  input.setAttribute('aria-label', 'OpenRouter API key');
  const use = button('Use key', null);
  use.type = 'submit';
  form.append(input, use);
  form.addEventListener('submit', (e) => {
    e.preventDefault();
    const key = input.value.trim();
    if (!key) return input.focus();
    hooks.saveKey(key);
    render(modelId);
  });
  bar.replaceChildren(text, form);
}

/** Moves focus to the key field when it is showing (the user tried to send without a key). */
export function focusKeyField() {
  const input = hooks && hooks.bar && hooks.bar.querySelector('.local-model-key');
  if (input) input.focus();
}

function setStatus(next) {
  status = next;
  if (isLocalModel(modelId)) render(modelId);
}

/** Loads the model once (memoised); the bar shows the progress. Rejects with local-llm.js's LocalModelError. */
export function ensureLoaded() {
  if (loading) return loading;
  const tracker = createProgressTracker();
  let last = 0;
  setStatus({ state: 'loading', text: 'Loading the runtime…', fraction: null });
  loading = (async () => {
    llm = llm || await import('./local-llm.js');
    const r = await llm.loadLocalModel({
      modelId: LOCAL_HUB_MODEL,
      onProgress: (ev) => {
        const snap = tracker.update(ev);
        const t = Date.now();
        if (t - last < 150 && ev.status === 'progress') return;   // a few repaints a second is plenty
        last = t;
        setStatus({ state: 'loading', text: progressText(snap), fraction: snap.fraction });
      }
    });
    loaded = r;
    setStatus({ state: 'ready', text: `Ready · ${r.device} · ${r.dtype} · loaded in ${(r.loadMs / 1000).toFixed(1)} s` });
    return r;
  })();
  loading.catch((err) => {
    loading = null;
    loaded = null;
    setStatus({ state: 'error', text: err && err.message ? err.message : String(err) });
  });
  return loading;
}

async function unload() {
  const r = loaded;
  loading = null;
  loaded = null;
  setStatus({ state: 'idle', text: '' });
  try { if (r) await r.dispose(); } catch { /* already gone */ }
}

/**
 * runTurn's options for a local turn: the stream, and callTool / localTools
 * wrapped so a call the model got wrong is answered here with what to fix, and
 * a write waits for the user (the gate, inside the guard: a call the guard
 * answers itself never asks). `signal` is the turn's: Stop cancels a pending card.
 */
export async function turnOptions({ callTool, localTools, signal = null }) {
  llm = llm || await import('./local-llm.js');
  if (!stream) {
    stream = llm.createLocalStream({
      getModel: ensureLoaded,
      categoryOf: (name) => hooks.categoryOf(name),
      isWrite: (name) => hooks.isWrite(name)
    });
  }
  const gated = gateCallTool(callTool, { toolInfo: (name) => hooks.toolInfo(name), confirm: confirmCard, signal });
  const guarded = stream.guard({ callTool: gated, localTools });
  return { streamImpl: stream, callTool: guarded.callTool, localTools: guarded.localTools };
}

const WRITE_NOTE = 'The in-browser model wants to make this change. Nothing is sent to the mail server until you approve.';
const DESTRUCTIVE_NOTE = 'Destructive: this deletes, disables or disconnects something, and it cannot be undone from here. Check every value before you approve.';

/**
 * The gate's card, under the call's tool card: tool, account, every argument in
 * full (approvals.js's review table: text nodes only, hidden characters shown
 * as ⟦U+XXXX⟧, long values in a scrolling block) and Approve / Deny. Resolves
 * to 'approve', 'deny' or 'cancel' (the turn's signal aborted). Neither button
 * takes focus, so an Enter meant for the message box cannot approve anything.
 */
function confirmCard({ name, args, destructive, signal }) {
  const card = node('div', `tool-card open local-confirm${destructive ? ' destructive' : ''}`);
  card.setAttribute('role', 'group');
  card.setAttribute('aria-label', `Approve ${name}?`);
  const handle = typeof args.account === 'string' ? args.account : '';
  const status = node('span', 'tool-status running', destructive ? 'destructive: approve?' : 'approve?');
  const head = node('div', 'tool-head');
  head.append(
    node('span', 'tool-caret', '▶'),
    node('span', 'tool-name', name),
    node('span', 'tool-account', hooks.accountLabel(handle) || 'the account that can run it'),
    node('span', 'tool-summary'),
    status
  );
  const body = node('div', 'tool-body');
  const { account: _account, ...shown } = args;
  const actions = node('div', 'local-confirm-actions');
  body.append(node('p', 'local-confirm-note', destructive ? DESTRUCTIVE_NOTE : WRITE_NOTE),
    hooks.argumentsTable(JSON.stringify(shown)), actions);
  card.append(head, body);
  hooks.messages.appendChild(card);
  hooks.scroll();

  return new Promise((resolve) => {
    let done = false;
    const settle = (outcome) => {
      if (done) return;
      done = true;
      if (signal) signal.removeEventListener('abort', onAbort);
      actions.remove();
      card.classList.remove('open');
      card.classList.add('settled');
      status.className = `tool-status ${outcome === 'approve' ? 'ok' : 'err'}`;
      status.textContent = outcome === 'approve' ? '✓ approved' : outcome === 'deny' ? '✕ declined' : '✕ cancelled';
      head.addEventListener('click', () => card.classList.toggle('open'));
      resolve(outcome);
    };
    const onAbort = () => settle('cancel');
    const approve = button(destructive ? `Approve ${name}` : 'Approve', () => settle('approve'));
    approve.className = `btn btn-small ${destructive ? 'btn-danger' : 'btn-primary'}`;
    actions.append(approve, button('Deny', () => settle('deny'), 'The model is told you declined and asked not to retry'));
    if (signal) {
      if (signal.aborted) return settle('cancel');
      signal.addEventListener('abort', onAbort, { once: true });
    }
  });
}

/**
 * runTurn's `ui` for a local turn: the same callbacks, plus the thinking text
 * (a collapsed "Thinking" block above the answer) and, after each round, its
 * stats line and the Debug details.
 */
export function wrapUi(ui) {
  let round = null;
  const ensureRound = () => {
    if (!round) {
      round = node('div', 'local-round');
      hooks.messages.appendChild(round);
    }
    return round;
  };
  let think = null;
  return {
    ...ui,
    onAssistantStart() {
      round = null;
      think = null;
      if (ui.onAssistantStart) ui.onAssistantStart();
    },
    onReasoning(chunk, full) {
      if (!think) {
        const d = document.createElement('details');
        d.className = 'local-think';
        d.append(node('summary', '', 'Thinking…'), node('pre', 'tool-pre'));
        ensureRound().appendChild(d);
        think = d;
      }
      think.lastChild.textContent = full;
      hooks.scroll();
    },
    onAssistantEnd(full, turn) {
      if (ui.onAssistantEnd) ui.onAssistantEnd(full, turn);
      if (think) think.firstChild.textContent = 'Thinking';
      const local = turn && turn.local;
      if (!local) return;
      // A separate block after the answer bubble (the thinking one sits above it).
      const after = node('div', 'local-round');
      after.append(node('div', 'local-stats', formatRoundStats(local, hooks.toolCount())), debugDetails(local));
      hooks.messages.appendChild(after);
      hooks.scroll();
    }
  };
}

function debugDetails(local) {
  const d = document.createElement('details');
  d.className = 'local-debug';
  d.append(node('summary', '', 'Round details'));
  const add = (label, text) => {
    d.append(node('div', 'tool-label', label), node('pre', 'tool-pre', text));
  };
  add(`Tools offered (${(local.selectedTools || []).length})`, (local.selectedTools || []).join('\n') || '(none)');
  const timing = [
    `setup ${ms(local.setupMs)}`, `first token ${ms(local.ttftMs)}`, `total ${ms(local.totalMs)}`,
    Number.isFinite(local.prefillTokensPerSecond) ? `prefill ${local.prefillTokensPerSecond.toFixed(0)} tok/s` : ''
  ].filter(Boolean).join(' · ');
  add('Timing', timing);
  if ((local.budgetSteps || []).length) add('Context budget', local.budgetSteps.join('\n'));
  const bad = (local.validations || []).filter((v) => v && !v.ok).map((v) => `${v.name || '?'}: ${v.error}`);
  if ((local.parseErrors || []).length || bad.length) add('Answered locally', [...(local.parseErrors || []), ...bad].join('\n'));
  add('Raw output', local.raw || '');
  return d;
}

const ms = (v) => (Number.isFinite(v) ? `${(v / 1000).toFixed(2)} s` : '?');

function node(tag, className, text) {
  const n = document.createElement(tag);
  if (className) n.className = className;
  if (text !== undefined) n.textContent = text;
  return n;
}

function button(text, onClick, title) {
  const b = node('button', 'btn btn-ghost btn-small', text);
  b.type = 'button';
  if (title) b.title = title;
  if (onClick) b.addEventListener('click', onClick);
  return b;
}
