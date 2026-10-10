/**
 * tasks.js — the Tasks dialog (server mode, DATA_KEY set): scheduled prompts
 * that the server runs while nobody is signed in, their editor, and their runs.
 *
 * The server holds each definition (it must read it unattended) but seals
 * every run's transcript to the profile's public key, so results are opened
 * here with the profile keys (profile.js). Schedules are five-field cron in
 * the browser's time zone; the editor offers presets and writes the cron.
 */

import * as api from './api.js';
import * as profile from './profile.js';
import { loadView, currentView } from './profile-ui.js';
import { renderMarkdown, prettyJson } from './markdown.js';
import { categoryOf } from './llm.js';

const $ = (id) => document.getElementById(id);

const el = {
  dialog: $('tasks-dialog'),
  body: $('tasks-body'),
  close: $('tasks-close'),
  button: $('btn-tasks')
};

let hooks = null;   // { getSession(), getToolList(), currentModel(), notice(text, kind), unlock() }
let listing = null; // GET /api/tasks
let poll = null;

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const EVERY_HOURS = [1, 2, 3, 4, 6, 8, 12];

export function initTasks(h) {
  hooks = h;
  el.button.addEventListener('click', () => open());
  el.close.addEventListener('click', () => el.dialog.close());
  el.dialog.addEventListener('close', () => { clearInterval(poll); poll = null; });
}

/** The header button: profile sessions on a server with scheduled tasks. */
export function renderTasksButton() {
  const s = hooks?.getSession();
  el.button.hidden = !(profile.tasksEnabled() && s && s.profile);
}

/** Unread results since the last look, as a badge on the button. */
export async function refreshBadge() {
  if (el.button.hidden) return;
  try {
    listing = await api.tasks();
    el.button.textContent = listing.unread ? `Tasks ● ${listing.unread}` : 'Tasks';
  } catch { /* keep the old text */ }
}

async function open() {
  if (typeof el.dialog.showModal === 'function' && !el.dialog.open) el.dialog.showModal();
  await renderList();
}

/* ----------------------------------------------------------------- list */

async function renderList() {
  clearInterval(poll);
  poll = null;
  el.body.replaceChildren(para('Loading…'));
  const [tasks, view] = await Promise.all([api.tasks().catch((e) => ({ error: e })), loadView()]);
  if (tasks.error) return void el.body.replaceChildren(para(`Could not load tasks: ${tasks.error.message}`, 'error'));
  listing = tasks;

  const parts = [];
  const delegated = (view?.accounts || []).filter((a) => a.delegated);
  if (!view?.hasTaskKey || !delegated.length) {
    parts.push(para(
      'Before a task can run: ' + [
        !view?.hasTaskKey ? 'save an OpenRouter key for tasks' : null,
        !delegated.length ? 'tick at least one account under "Accounts tasks may use"' : null
      ].filter(Boolean).join(', and ') + ', both in the Profile menu.', 'warn'));
  }
  if (view?.tasksPaused) parts.push(para('All tasks are paused (Profile menu).', 'warn'));

  if (!tasks.tasks.length) parts.push(para('No tasks yet. A task is a prompt the server runs on a schedule with the accounts you choose, for example "Every weekday at 7:00, summarise unread mail and email me the summary".'));

  for (const t of tasks.tasks) parts.push(taskCard(t, view));

  const actions = div('modal-actions');
  actions.append(button('New task', () => renderEditor(null), true));
  if (tasks.tasks.length) actions.append(button('All results', () => renderRuns(null)));
  parts.push(actions);
  el.body.replaceChildren(...parts);
}

function taskCard(t, view) {
  const d = t.definition;
  const card = div('task-card');
  const head = div('task-head');
  const name = document.createElement('strong');
  name.textContent = d ? d.name : '(unreadable task)';
  head.append(name, badge(t.enabled ? (t.status === 'ok' ? 'on' : t.status) : (t.status === 'ok' ? 'off' : `paused: ${t.status}`),
    t.enabled && t.status === 'ok' ? 'ok' : 'warn'));
  card.append(head);

  if (d) {
    card.append(para(`${describeCron(d.cron)} (${d.timeZone})` +
      (t.nextRunAt && t.enabled ? ` · next ${new Date(t.nextRunAt).toLocaleString()}` : '') +
      (t.lastRunAt ? ` · last ${new Date(t.lastRunAt).toLocaleString()}` : '')));
    const names = d.accountIds.map((id) => view?.accounts.find((a) => a.id === id)?.login || 'removed account');
    card.append(para(`As ${names.join(', ')} · ${d.allowedWrites.length ? `may change: ${d.allowedWrites.join(', ')} (at most ${d.maxWrites})` : 'read-only'}` +
      (d.emailAccountId ? ' · emails you the result' : '')));
  }

  const actions = div('task-actions');
  actions.append(
    button('Run now', (b) => run(b, t, false)),
    button('Test run', (b) => run(b, t, true), false, 'Changes are simulated and nothing is emailed'),
    button('Results', () => renderRuns(t)),
    button('Edit', () => renderEditor(t)),
    confirmButton('Delete', 'Click again to delete', async () => { await api.deleteTask(t.id); await renderList(); }));
  card.append(actions);
  return card;
}

async function run(b, t, dryRun) {
  b.disabled = true;
  try {
    await api.runTask(t.id, dryRun);
    hooks.notice(`${dryRun ? 'Test run' : 'Run'} of "${t.definition?.name}" started. Its result appears under Results.`, 'info');
    await renderRuns(t, { watch: true });
  } catch (err) {
    hooks.notice(err.body?.error || err.message, 'error');
  } finally {
    b.disabled = false;
  }
}

/* --------------------------------------------------------------- editor */

function renderEditor(task) {
  clearInterval(poll);
  const view = currentView();
  const d = task?.definition;
  const form = document.createElement('form');
  form.className = 'task-form';
  form.noValidate = true;

  const nameInput = input('text', d?.name || '', 'Morning summary');
  const promptInput = document.createElement('textarea');
  promptInput.rows = 5;
  promptInput.value = d?.prompt || '';
  promptInput.placeholder = 'Summarise my unread mail from the last day: sender, subject, one line each. Flag anything that needs an answer today.';

  // Schedule
  const schedule = parseCron(d?.cron || '0 7 * * 1-5');
  const preset = select([
    ['weekdays', 'Every weekday at'], ['daily', 'Every day at'], ['weekly', 'Every week on'], ['hours', 'Every few hours'], ['custom', 'Custom (cron)']
  ], schedule.kind);
  const time = input('time', schedule.time || '07:00');
  const day = select(DAYS.map((n, i) => [String(i), n]), String(schedule.day ?? 1));
  const hours = select(EVERY_HOURS.map((n) => [String(n), n === 1 ? 'every hour' : `every ${n} hours`]), String(schedule.hours || 4));
  const cron = input('text', d?.cron || '', '0 7 * * 1-5');
  const zone = input('text', d?.timeZone || Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC');
  const scheduleRow = div('task-schedule');
  const syncSchedule = () => {
    const k = preset.value;
    time.hidden = !['weekdays', 'daily', 'weekly'].includes(k);
    day.hidden = k !== 'weekly';
    hours.hidden = k !== 'hours';
    cron.hidden = k !== 'custom';
  };
  preset.addEventListener('change', syncSchedule);
  scheduleRow.append(preset, day, time, hours, cron);
  syncSchedule();

  // Accounts (delegated, live ones only)
  const accounts = (view?.accounts || []).filter((a) => a.delegated);
  const accountBoxes = accounts.map((a) => checkbox(`${a.login} (${a.role}${a.readOnly ? ', read-only' : ''})`,
    d ? d.accountIds.includes(a.id) : accounts.length === 1, a.id));
  const accountList = div('task-checks');
  accountList.append(...(accountBoxes.length ? accountBoxes.map((c) => c.label) : [para('No account allows scheduled tasks yet (Profile menu).', 'warn')]));

  // Changes: write tools the session knows, for the chosen accounts' scopes
  const writes = div('task-checks task-writes');
  const writeBoxes = new Map();
  const renderWrites = () => {
    const chosen = accounts.filter((a, i) => accountBoxes[i].input.checked);
    const scopes = new Set(chosen.flatMap((a) => a.readOnly ? [] : a.role === 'SysAdmin' ? ['SysAdmin'] : a.role === 'DomainAdmin' ? ['Mailbox', 'DomainAdmin'] : ['Mailbox']));
    const tools = hooks.getToolList().filter((t) => t.write && scopes.has(t.scope));
    const keep = new Set([...writeBoxes].filter(([, c]) => c.input.checked).map(([n]) => n));
    if (!writeBoxes.size && d) d.allowedWrites.forEach((n) => keep.add(n));
    writeBoxes.clear();
    writes.replaceChildren();
    if (!tools.length) {
      writes.append(para(chosen.some((a) => !a.readOnly)
        ? 'Sign in with these accounts in this chat to choose from their tools.'
        : 'Read-only: the chosen accounts were signed in without "Allow changes".'));
      return;
    }
    const byCategory = new Map();
    for (const t of tools) {
      const c = categoryOf(t);
      if (!byCategory.has(c)) byCategory.set(c, []);
      byCategory.get(c).push(t);
    }
    for (const [category, list] of byCategory) {
      const fs = document.createElement('fieldset');
      fs.className = 'tools-group';
      const legend = document.createElement('legend');
      legend.textContent = category;
      fs.append(legend);
      for (const t of list) {
        const c = checkbox(t.destructive ? `${t.name} ⚠ destructive` : t.name, keep.has(t.name), t.name);
        if (t.destructive) c.label.classList.add('destructive');
        c.label.title = t.description || '';
        writeBoxes.set(t.name, c);
        fs.append(c.label);
      }
      writes.append(fs);
    }
  };
  accountBoxes.forEach((c) => c.input.addEventListener('change', renderWrites));
  renderWrites();

  const maxWrites = input('number', String(d?.maxWrites ?? 5));
  maxWrites.min = '1';
  maxWrites.max = '50';
  const model = input('text', d?.model || hooks.currentModel());
  const email = select([['', 'No, only keep it here'],
    ...accounts.filter((a) => !a.readOnly && a.role !== 'SysAdmin').map((a) => [a.id, `Yes, from and to ${a.login}`])], d?.emailAccountId || '');
  const enabled = checkbox('Enabled', task ? task.enabled : true);
  const error = para('', 'error');
  error.hidden = true;

  form.append(
    field('Name', nameInput),
    field('What should it do?', promptInput, 'Write it as you would ask in the chat. Nobody is there to answer questions while it runs.'),
    field('When', scheduleRow),
    field('Time zone', zone),
    field('Accounts', accountList),
    field('Changes it may make', writes,
      'Nothing is allowed unless ticked here. Mail it reads can contain instructions meant for it; it is told to ignore them, ' +
      'and the server refuses any change not on this list. Try a test run first.'),
    field('At most this many changes per run', maxWrites),
    field('Model', model),
    field('Email the result to you?', email),
    enabled.label,
    error);

  const actions = div('modal-actions');
  const save = button(task ? 'Save' : 'Create task', null, true);
  save.type = 'submit';
  actions.append(button('Cancel', () => renderList()), save);
  form.append(actions);

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    error.hidden = true;
    const body = {
      name: nameInput.value.trim(),
      prompt: promptInput.value.trim(),
      cron: buildCron(preset.value, { time: time.value, day: day.value, hours: hours.value, cron: cron.value }),
      timeZone: zone.value.trim(),
      accountIds: accounts.filter((a, i) => accountBoxes[i].input.checked).map((a) => a.id),
      allowedWrites: [...writeBoxes].filter(([, c]) => c.input.checked).map(([n]) => n),
      maxWrites: Number(maxWrites.value) || 1,
      model: model.value.trim(),
      emailAccountId: email.value || null,
      enabled: enabled.input.checked
    };
    save.disabled = true;
    try {
      if (task) await api.updateTask(task.id, body);
      else await api.createTask(body);
      await renderList();
    } catch (err) {
      error.textContent = err.body?.error || err.message;
      error.hidden = false;
    } finally {
      save.disabled = false;
    }
  });

  el.body.replaceChildren(form);
  nameInput.focus();
}

/* ----------------------------------------------------------------- runs */

/** 41200 -> "41k", 1234 -> "1.2k", 800 -> "800"; null/undefined -> null. */
export function formatCount(n) {
  if (n == null || !Number.isFinite(n)) return null;
  if (n < 1000) return String(n);
  if (n < 10000) return `${(n / 1000).toFixed(1).replace(/\.0$/, '')}k`;
  if (n < 1000000) return `${Math.round(n / 1000)}k`;
  return `${(n / 1000000).toFixed(1).replace(/\.0$/, '')}M`;
}

/** "41k in / 1.2k out", or "" when the run has no counts (older runs, failures before a model call). */
export function formatTokens(r) {
  const i = formatCount(r?.promptTokens), o = formatCount(r?.completionTokens);
  if (i == null && o == null) return '';
  return `${i ?? '?'} in / ${o ?? '?'} out`;
}

async function renderRuns(task, { watch = false } = {}) {
  clearInterval(poll);
  poll = null;
  const draw = async () => {
    let runs;
    try {
      runs = await api.taskRuns(task?.id);
    } catch (err) {
      return el.body.replaceChildren(para(`Could not load results: ${err.message}`, 'error'));
    }
    const parts = [heading(task ? `Results: ${task.definition?.name}` : 'All results')];
    if (!runs.length) parts.push(para('No runs yet.'));
    const names = new Map((listing?.tasks || []).map((t) => [t.id, t.definition?.name || 'task']));
    for (const r of runs) {
      const row = div('run-row' + (r.read ? '' : ' unread'));
      const text = document.createElement('span');
      text.textContent = `${new Date(r.startedAt).toLocaleString()} · ${task ? '' : names.get(r.taskId) + ' · '}` +
        `${r.trigger === 'schedule' ? 'scheduled' : 'manual'}${r.dryRun ? ', test' : ''} · ` +
        `${r.status === 'running' ? 'running…' : r.status}${r.errorCode && r.status !== 'running' ? ` (${r.errorMessage || r.errorCode})` : ''}` +
        (r.status !== 'running' ? ` · ${r.toolCalls} tool call${r.toolCalls === 1 ? '' : 's'}, ${r.writes} change${r.writes === 1 ? '' : 's'}` +
          (formatTokens(r) ? ` · ${formatTokens(r)}` : '') : '');
      row.append(text);
      if (r.status !== 'running') row.append(button('Open', () => renderRun(r, task)));
      parts.push(row);
    }
    const actions = div('modal-actions');
    actions.append(button('Back', () => renderList()));
    parts.push(actions);
    el.body.replaceChildren(...parts);
    if (!runs.some((r) => r.status === 'running')) { clearInterval(poll); poll = null; }
  };
  await draw();
  if (watch) poll = setInterval(draw, 3000);
}

async function renderRun(summary, task) {
  clearInterval(poll);
  poll = null;
  const parts = [heading('Result')];
  let transcript = null;
  try {
    const run = await api.taskRun(summary.id);
    if (!profile.hasKeys()) {
      parts.push(para('This page does not hold your profile keys (it was reloaded). Unlock to read the result.', 'warn'));
      parts.push(button('Unlock with passkey', async (b) => {
        b.disabled = true;
        try { await hooks.unlock(); await renderRun(summary, task); } catch (err) { hooks.notice(err.message, 'error'); b.disabled = false; }
      }, true));
    } else {
      transcript = await profile.openTranscript(currentView() || await loadView(), run);
      api.markRunRead(run.id).catch(() => {});
    }
  } catch (err) {
    parts.push(para(`Could not open the result: ${err.message}`, 'error'));
  }

  if (transcript) {
    parts.push(para(`${transcript.taskName} · ${new Date(transcript.startedAt).toLocaleString()} · ${transcript.model}` +
      (transcript.dryRun ? ' · test run (changes simulated)' : '') + (transcript.emailed ? ' · emailed' : '') +
      (formatTokens(summary) ? ` · ${formatTokens(summary)}` : '')));
    const report = div('msg assistant run-report');
    report.innerHTML = renderMarkdown(transcript.final || transcript.error || '(no report)');   // escape-first renderer
    parts.push(report);
    const steps = transcript.steps.filter((s) => s.kind === 'tool');
    if (steps.length) {
      const details = document.createElement('details');
      details.className = 'run-steps';
      const sum = document.createElement('summary');
      sum.textContent = `${steps.length} tool call${steps.length === 1 ? '' : 's'}`;
      details.append(sum);
      for (const s of steps) {
        const item = div('run-step' + (s.isError ? ' error' : '') + (s.simulated ? ' simulated' : ''));
        const head = document.createElement('div');
        head.className = 'run-step-head';
        head.textContent = `${s.tool}${s.account ? ' as ' + s.account : ''}${s.simulated ? ' — simulated' : ''}${s.isError ? ' — failed or refused' : ''}`;
        const pre = document.createElement('pre');
        pre.className = 'code';
        pre.textContent = `${prettyJson(s.arguments || '{}')}\n\n${prettyJson(s.content || '')}`;
        item.append(head, pre);
        details.append(item);
      }
      parts.push(details);
    }
    for (const n of transcript.steps.filter((s) => s.kind === 'notice')) parts.push(para(n.content, 'warn'));
  }

  const actions = div('modal-actions');
  actions.append(button('Back', () => renderRuns(task)));
  parts.push(actions);
  el.body.replaceChildren(...parts);
  refreshBadge();
}

/* ------------------------------------------------------------- schedule */

/** cron → { kind, time, day, hours } for the editor; anything else is "custom". */
export function parseCron(cron) {
  const f = String(cron || '').trim().split(/\s+/);
  if (f.length === 5) {
    const [m, h, dom, mon, dow] = f;
    const hm = /^\d+$/.test(m) && /^\d+$/.test(h) ? `${h.padStart(2, '0')}:${m.padStart(2, '0')}` : null;
    if (hm && dom === '*' && mon === '*') {
      if (dow === '*') return { kind: 'daily', time: hm };
      if (dow === '1-5') return { kind: 'weekdays', time: hm };
      if (/^[0-6]$/.test(dow)) return { kind: 'weekly', time: hm, day: Number(dow) };
    }
    const every = /^\*\/(\d+)$/.exec(h);
    if (m === '0' && every && dom === '*' && mon === '*' && dow === '*' && EVERY_HOURS.includes(Number(every[1]))) {
      return { kind: 'hours', hours: Number(every[1]) };
    }
    if (m === '0' && h === '*' && dom === '*' && mon === '*' && dow === '*') return { kind: 'hours', hours: 1 };
  }
  return { kind: 'custom' };
}

export function buildCron(kind, { time = '07:00', day = '1', hours = '4', cron = '' } = {}) {
  const [h, m] = String(time || '07:00').split(':').map((x) => String(Number(x) || 0));
  switch (kind) {
    case 'daily': return `${m} ${h} * * *`;
    case 'weekdays': return `${m} ${h} * * 1-5`;
    case 'weekly': return `${m} ${h} * * ${day}`;
    case 'hours': return Number(hours) === 1 ? '0 * * * *' : `0 */${hours} * * *`;
    default: return String(cron || '').trim();
  }
}

export function describeCron(cron) {
  const p = parseCron(cron);
  switch (p.kind) {
    case 'daily': return `Every day at ${p.time}`;
    case 'weekdays': return `Every weekday at ${p.time}`;
    case 'weekly': return `Every ${DAYS[p.day]} at ${p.time}`;
    case 'hours': return p.hours === 1 ? 'Every hour' : `Every ${p.hours} hours`;
    default: return `Cron ${cron}`;
  }
}

/* -------------------------------------------------------------- helpers */

function div(className) { const d = document.createElement('div'); d.className = className; return d; }

function heading(text) { const h = document.createElement('h3'); h.className = 'modal-subtitle'; h.textContent = text; return h; }

function para(text, kind = '') {
  const p = document.createElement('p');
  p.className = 'hint task-hint' + (kind ? ' ' + kind : '');
  p.textContent = text;
  return p;
}

function badge(text, kind) {
  const b = document.createElement('span');
  b.className = 'task-badge ' + kind;
  b.textContent = text;
  return b;
}

function button(text, onClick, primary = false, title = '') {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = 'btn btn-small ' + (primary ? 'btn-primary' : 'btn-ghost');
  b.textContent = text;
  if (title) b.title = title;
  if (onClick) b.addEventListener('click', () => onClick(b));
  return b;
}

function confirmButton(text, armedText, fn) {
  const b = button(text, null);
  b.addEventListener('click', async () => {
    if (b.dataset.armed !== '1') { b.dataset.armed = '1'; b.textContent = armedText; b.classList.add('btn-danger'); return; }
    b.disabled = true;
    try { await fn(); } catch (err) { hooks.notice(err.body?.error || err.message, 'error'); b.disabled = false; }
  });
  return b;
}

function input(type, value = '', placeholder = '') {
  const i = document.createElement('input');
  i.type = type;
  i.value = value;
  if (placeholder) i.placeholder = placeholder;
  i.autocomplete = 'off';
  return i;
}

function select(options, value) {
  const s = document.createElement('select');
  for (const [v, label] of options) {
    const o = document.createElement('option');
    o.value = v;
    o.textContent = label;
    s.append(o);
  }
  s.value = value;
  return s;
}

function checkbox(text, checked, value = '') {
  const label = document.createElement('label');
  label.className = 'tools-option';
  const input = document.createElement('input');
  input.type = 'checkbox';
  input.checked = !!checked;
  input.value = value;
  const span = document.createElement('span');
  span.textContent = text;
  label.append(input, span);
  return { label, input };
}

function field(labelText, control, hint = '') {
  const wrap = div('task-field');
  const label = document.createElement('p');
  label.className = 'task-label';
  label.textContent = labelText;
  wrap.append(label, control);
  if (hint) wrap.append(para(hint));
  return wrap;
}
