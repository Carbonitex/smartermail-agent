/**
 * triggers.js — the "When something happens" part of the task editor:
 * condition-triggered tasks (server mode with DATA_KEY and TRIGGERS_ENABLED).
 *
 * A trigger reads one tool as one delegated account every N minutes and
 * fires when a predicate over the JSON result holds. The predicate is a
 * small JSON tree the server parses and evaluates (see the server's
 * Tasks/Triggers/Predicate.cs); this module builds it from a form of rows,
 * shows it as JSON on request, and runs "Test probe" so paths can be
 * picked by clicking the real result.
 *
 * Everything shown from a probe result is untrusted text (mail subjects,
 * senders): it is only ever set with textContent.
 *
 * The top half is pure (no DOM at import time) so node tests can load it.
 */

/* ------------------------------------------------------------------ pure */

export const VALUE_OPS = [
  ['gt', '>'], ['gte', '≥'], ['lt', '<'], ['lte', '≤'], ['eq', '='], ['ne', '≠'],
  ['contains', 'contains'], ['startsWith', 'starts with'], ['endsWith', 'ends with'], ['matches', 'matches (regex)'],
  ['daysUntilLt', 'is less than N days away'], ['daysUntilGt', 'is more than N days away'],
  ['daysAgoLt', 'is less than N days ago'], ['daysAgoGt', 'is more than N days ago'],
  ['exists', 'exists']
];
const OP_NAMES = new Set(VALUE_OPS.map(([v]) => v));
const NUMBER_OPS = new Set(['gt', 'gte', 'lt', 'lte', 'daysUntilLt', 'daysUntilGt', 'daysAgoLt', 'daysAgoGt']);
const STRING_OPS = new Set(['contains', 'startsWith', 'endsWith', 'matches']);
export const COUNT_OPS = ['gte', 'gt', 'eq', 'ne', 'lt', 'lte'];

const NAME_RE = /^[A-Za-z_@-][A-Za-z0-9_@-]*$/;

/** ['$', 'emails', 3, 'from'] → '$.emails[3].from' (strings are names, numbers indices, '*' a wildcard). */
export function pathText(segments) {
  let out = '$';
  for (const s of segments.slice(segments[0] === '$' ? 1 : 0)) {
    if (s === '*') out += '[*]';
    else if (typeof s === 'number') out += `[${s}]`;
    else out += NAME_RE.test(s) ? `.${s}` : `[${JSON.stringify(s)}]`;
  }
  return out;
}

/** '$.a["b c"][2][*]' → ['a', 'b c', 2, '*'] (null when it is not a path). */
export function parsePath(text) {
  let s = String(text ?? '').trim();
  if (s.startsWith('$')) s = s.slice(1);
  const out = [];
  let i = 0;
  while (i < s.length) {
    if (s[i] === '.' || (i === 0 && out.length === 0 && /[A-Za-z0-9_@-]/.test(s[i]))) {
      if (s[i] === '.') i++;
      const m = /^[A-Za-z0-9_@-]+/.exec(s.slice(i));
      if (!m) return null;
      out.push(m[0]);
      i += m[0].length;
    } else if (s[i] === '[') {
      const rest = s.slice(i);
      let m;
      if ((m = /^\[\*\]/.exec(rest))) { out.push('*'); i += 3; }
      else if ((m = /^\[(\d{1,6})\]/.exec(rest))) { out.push(Number(m[1])); i += m[0].length; }
      else if ((m = /^\[("(?:[^"\\]|\\.)*")\]/.exec(rest))) { out.push(JSON.parse(m[1])); i += m[0].length; }
      else return null;
    } else return null;
  }
  return out;
}

/**
 * A clicked value's path split around its innermost array index: the items
 * ('$.emails[*]') and the path inside one item ('from.address'). Null when
 * the path goes through no array.
 */
export function splitAtItems(segments) {
  const last = segments.map((s) => typeof s === 'number' || s === '*').lastIndexOf(true);
  if (last < 0) return null;
  const items = pathText([...segments.slice(0, last), '*']);
  const inner = segments.slice(last + 1);
  return { items, relative: inner.length ? pathText(inner).replace(/^\$\.?/, '') : '$' };
}

/**
 * The path of `segments` relative to an items path ('$.emails[*]'), or null
 * when the value is not inside one of those items.
 */
export function relativeTo(itemsPath, segments) {
  const items = parsePath(itemsPath);
  if (!items || items.length > segments.length) return null;
  for (let i = 0; i < items.length; i++) {
    const a = items[i], b = segments[i];
    if (a === '*' ? !(typeof b === 'number' || typeof b === 'string') : a !== b) return null;
  }
  const inner = segments.slice(items.length);
  return inner.length ? pathText(inner).replace(/^\$\.?/, '') : '$';
}

/** The text in a value box → what the predicate carries for that operator. */
export function valueFor(op, text) {
  const t = String(text ?? '').trim();
  if (op === 'exists') return undefined;
  if (NUMBER_OPS.has(op)) return t === '' || !Number.isFinite(Number(t)) ? t : Number(t);
  if (STRING_OPS.has(op)) return String(text ?? '');
  // eq / ne: a number, true, false or null as such; "quoted" text as that string; anything else a string.
  if (/^".*"$/s.test(t)) { try { return JSON.parse(t); } catch { /* not a JSON string: keep it as typed */ } }
  if (t !== '' && Number.isFinite(Number(t))) return Number(t);
  if (t === 'true') return true;
  if (t === 'false') return false;
  if (t === 'null') return null;
  return String(text ?? '');
}

/** What the value box shows; a string that would read back as something else is shown quoted. */
const valueText = (v, op) => {
  if (v === undefined) return '';
  if (typeof v !== 'string') return JSON.stringify(v);
  const ambiguous = (op === 'eq' || op === 'ne') && valueFor(op, v) !== v;
  return ambiguous ? JSON.stringify(v) : v;
};

function rowNode(r) {
  const node = { path: r.path, op: r.op };
  const v = valueFor(r.op, r.value);
  if (v !== undefined) node.value = v;
  return node;
}

/**
 * The editor's form → the predicate JSON.
 * model: { mode: 'values'|'count'|'new', join: 'all'|'any', rows: [{ path, op, value }],
 *          items, key, countOp, countValue }
 */
export function buildPredicate(m) {
  const rows = (m.rows || []).filter((r) => String(r.path || '').trim());
  const nodes = rows.map(rowNode);
  const joined = nodes.length === 0 ? null : nodes.length === 1 ? nodes[0] : { [m.join === 'any' ? 'any' : 'all']: nodes };
  if (m.mode === 'count') {
    const count = { items: m.items };
    if (joined) count.where = joined;
    return { count, op: m.countOp || 'gte', value: Number(m.countValue ?? 1) };
  }
  if (m.mode === 'new') {
    const n = { items: m.items };
    if (String(m.key || '').trim()) n.key = m.key.trim();
    if (joined) n.where = joined;
    return { new: n };
  }
  return joined;
}

function rowOf(node) {
  if (!node || typeof node !== 'object' || typeof node.path !== 'string' || !OP_NAMES.has(node.op)) return null;
  if (Object.keys(node).some((k) => !['path', 'op', 'value'].includes(k))) return null;
  return { path: node.path, op: node.op, value: valueText(node.value, node.op) };
}

function rowsOf(node) {
  if (node == null) return { join: 'all', rows: [] };
  const key = node.all ? 'all' : node.any ? 'any' : null;
  if (key && Object.keys(node).length === 1 && Array.isArray(node[key])) {
    const rows = node[key].map(rowOf);
    return rows.every(Boolean) ? { join: key, rows } : null;
  }
  const row = rowOf(node);
  return row ? { join: 'all', rows: [row] } : null;
}

/** The predicate JSON → the editor's form, or null when the form cannot show it (then it stays JSON). */
export function modelOf(p) {
  if (p == null) return { mode: 'values', join: 'all', rows: [], items: '', key: '', countOp: 'gte', countValue: 1 };
  if (typeof p !== 'object' || Array.isArray(p)) return null;
  if (p.count && typeof p.count === 'object') {
    if (Object.keys(p).some((k) => !['count', 'op', 'value'].includes(k)) || Object.keys(p.count).some((k) => !['items', 'where'].includes(k))) return null;
    const where = rowsOf(p.count.where);
    return where && { mode: 'count', ...where, items: p.count.items || '', key: '', countOp: p.op || 'gte', countValue: p.value ?? 1 };
  }
  if (p.new && typeof p.new === 'object') {
    if (Object.keys(p).length !== 1 || Object.keys(p.new).some((k) => !['items', 'where', 'key'].includes(k)) || Array.isArray(p.new.key)) return null;
    const where = rowsOf(p.new.where);
    return where && { mode: 'new', ...where, items: p.new.items || '', key: p.new.key || '', countOp: 'gte', countValue: 1 };
  }
  const values = rowsOf(p);
  return values && { mode: 'values', ...values, items: '', key: '', countOp: 'gte', countValue: 1 };
}

/** "every 15 min · last checked 10:42 (false)" for the task card. */
export function describeTrigger(trigger, status, now = Date.now()) {
  if (!trigger) return '';
  const parts = [`On condition · every ${trigger.everyMinutes} min`];
  if (trigger.activeHours) parts.push(`only ${trigger.activeHours}`);
  parts.push(trigger.action === 'alert' ? 'emails you an alert' : 'runs the prompt');
  if (status?.lastProbeAt) {
    const t = new Date(status.lastProbeAt);
    const same = new Date(now).toDateString() === t.toDateString();
    parts.push(`last checked ${same ? t.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : t.toLocaleString()}` +
      (status.lastValue == null ? '' : ` (${status.lastValue ? 'true' : 'false'})`));
  } else {
    parts.push('not checked yet');
  }
  if (status?.firesToday) parts.push(`${status.firesToday} today`);
  if (status?.probeFailures) parts.push(`${status.probeFailures} failed check${status.probeFailures === 1 ? '' : 's'} in a row`);
  return parts.join(' · ');
}

/** The tool's own arguments from its schema (the injected `account` left out). */
export function schemaFields(inputSchema) {
  const props = inputSchema?.properties || {};
  const required = new Set(inputSchema?.required || []);
  return Object.entries(props)
    .filter(([name]) => name !== 'account')
    .map(([name, s]) => ({
      name,
      type: s?.type === 'integer' || s?.type === 'number' ? 'number' : s?.type === 'boolean' ? 'boolean' : s?.type === 'string' ? 'string' : 'json',
      required: required.has(name),
      description: s?.description || '',
      default: s?.default
    }));
}

/**
 * Starting points. The result shapes of these tools have not been checked on
 * every server version, so the conditions accept a few likely field names;
 * "Test probe" shows the real JSON, and clicking a value fixes the path.
 */
export const EXAMPLES = [
  {
    id: 'spool', label: 'Spool backing up (system admin)', role: 'SysAdmin', tool: 'get_spool_message_counts', arguments: {},
    when: { any: ['$.waiting', '$.counts.waiting', '$.data.waiting', '$.total'].map((path) => ({ path, op: 'gt', value: 500 })) },
    everyMinutes: 5, fire: 'edge', holdFor: 2, action: 'alert'
  },
  {
    id: 'certs', label: 'Certificate expiring (system admin)', role: 'SysAdmin', tool: 'get_ssl_certificates', arguments: {},
    when: { count: { items: '$.certificates[*]', where: { any: ['expiration', 'expirationDate', 'expires', 'validTo'].map((path) => ({ path, op: 'daysUntilLt', value: 14 })) } }, op: 'gte', value: 1 },
    everyMinutes: 360, fire: 'edge', holdFor: 1, action: 'alert'
  },
  {
    id: 'mail', label: 'New mail from a sender or about a subject', role: 'User', tool: 'get_emails',
    arguments: (account) => ({ folderId: `${account?.login || 'me@example.com'}/Inbox`, take: 25, query: 'is:unread' }),
    when: { new: { items: '$.emails[*]', key: 'uid', where: { any: [{ path: 'senderAddress', op: 'matches', value: '@vendor\\.example$' }, { path: 'subject', op: 'contains', value: 'invoice' }] } } },
    everyMinutes: 5, fire: 'edge', holdFor: 1, action: 'run'
  },
  {
    id: 'throttled', label: 'A user is throttled (system admin)', role: 'SysAdmin', tool: 'get_throttled_users', arguments: {},
    when: { count: { items: '$.users[*]' }, op: 'gte', value: 1 },
    everyMinutes: 15, fire: 'edge', holdFor: 1, action: 'run'
  }
];

const roleAllows = (role, scope) =>
  scope === 'Mailbox' ? role === 'User' || role === 'DomainAdmin' : scope === 'DomainAdmin' ? role === 'DomainAdmin' : scope === 'SysAdmin' && role === 'SysAdmin';

/* ------------------------------------------------------------------- DOM */

/**
 * The editor section. opts:
 *   trigger      — the saved trigger (or null)
 *   accounts     — delegated profile accounts [{ id, login, role, live }]
 *   tools        — the session's tool list (GET /api/tools)
 *   minInterval  — TRIGGER_MIN_INTERVAL_MINUTES
 *   probe(body)  — POST /api/tasks/probe
 *   onAction(a)  — called with 'run' | 'alert' when "Then" changes
 * Returns { element, value(), accountId(), action() }; value() throws an
 * Error with a user-facing message when the form is incomplete.
 */
export function buildTriggerEditor(opts) {
  const t = opts.trigger || null;
  const root = el('div', 'trigger-editor');

  // ---- examples
  const examples = select([['', 'Start from an example…'], ...EXAMPLES.map((e) => [e.id, e.label])], '');

  // ---- check: account, tool, arguments
  const accounts = opts.accounts || [];
  const account = select(accounts.map((a) => [a.id, `${a.login} (${a.role})${a.live ? '' : ' — not signed in here'}`]), t?.probe?.accountId || accounts[0]?.id || '');
  const tool = document.createElement('select');
  const argsBox = el('div', 'trigger-args');
  let argInputs = [];
  let savedArgs = t?.probe?.arguments || {};

  const chosenAccount = () => accounts.find((a) => a.id === account.value);
  const readTools = () => {
    const a = chosenAccount();
    return (opts.tools || []).filter((x) => !x.write && (!a || roleAllows(a.role, x.scope)));
  };
  const renderTools = (want) => {
    const list = readTools();
    const keep = want || tool.value || t?.probe?.tool;
    tool.replaceChildren(...list.map((x) => option(x.name, x.name)));
    if (keep && !list.some((x) => x.name === keep)) tool.append(option(keep, `${keep} (not available to this account here)`));
    tool.value = keep || list[0]?.name || '';
  };
  const renderArgs = () => {
    const def = (opts.tools || []).find((x) => x.name === tool.value);
    argInputs = [];
    argsBox.replaceChildren();
    for (const f of schemaFields(def?.inputSchema)) {
      const current = savedArgs[f.name];
      let control;
      if (f.type === 'boolean') {
        control = document.createElement('input');
        control.type = 'checkbox';
        control.checked = current === undefined ? !!f.default : !!current;
      } else if (f.type === 'json') {
        control = document.createElement('textarea');
        control.rows = 2;
        control.value = current === undefined ? '' : JSON.stringify(current);
      } else {
        control = input(f.type === 'number' ? 'number' : 'text', current === undefined ? '' : String(current), f.default === undefined ? '' : String(f.default));
      }
      control.title = f.description;
      const label = el('label', 'trigger-arg');
      const name = el('span', 'trigger-arg-name');
      name.textContent = f.name + (f.required ? ' *' : '');
      label.append(name, control);
      argsBox.append(label);
      argInputs.push({ f, control });
    }
    if (!argInputs.length) argsBox.append(para('This tool takes no arguments.'));
  };
  const args = () => {
    const out = {};
    for (const { f, control } of argInputs) {
      if (f.type === 'boolean') { if (control.checked || f.required) out[f.name] = control.checked; continue; }
      const raw = control.value.trim();
      if (raw === '') { if (f.required) throw new Error(`The tool needs "${f.name}".`); continue; }
      if (f.type === 'number') out[f.name] = Number(raw);
      else if (f.type === 'json') {
        try { out[f.name] = JSON.parse(raw); } catch { throw new Error(`"${f.name}" must be JSON.`); }
      } else out[f.name] = control.value;
    }
    return out;
  };
  account.addEventListener('change', () => { renderTools(); renderArgs(); });
  tool.addEventListener('change', () => { savedArgs = {}; renderArgs(); });
  renderTools();
  renderArgs();

  // ---- condition
  const initial = modelOf(t?.when ?? null);
  let model = initial || modelOf(null);
  let jsonMode = !initial;
  const mode = select([['values', 'Values in the result'], ['count', 'Count of items where…'], ['new', 'New items in…']], model.mode);
  const join = select([['all', 'all of these hold'], ['any', 'any of these holds']], model.join);
  const items = input('text', model.items, '$.emails[*]');
  const key = input('text', model.key, 'uid (optional)');
  const countOp = select(COUNT_OPS.map((o) => [o, VALUE_OPS.find(([v]) => v === o)[1]]), String(model.countOp));
  const countValue = input('number', String(model.countValue ?? 1));
  const rowsBox = el('div', 'trigger-rows');
  const jsonBox = document.createElement('textarea');
  jsonBox.rows = 8;
  jsonBox.className = 'trigger-json';
  jsonBox.spellcheck = false;
  const toggleJson = button(jsonMode ? 'Edit as a form' : 'Edit as JSON');
  let lastPath = null;   // the path input a click in the result fills

  const itemsRow = el('div', 'trigger-inline');
  itemsRow.append(labelled('Items', items));
  const keyRow = labelled('Identified by', key);
  const countRow = el('div', 'trigger-inline');
  countRow.append(text('Fires when that number is'), countOp, countValue);

  const addRow = (r = { path: '', op: 'gt', value: '' }) => {
    const row = el('div', 'trigger-row');
    const path = input('text', r.path, mode.value === 'values' ? '$.counts.waiting' : 'subject');
    path.classList.add('trigger-path');
    const op = select(VALUE_OPS, r.op);
    const value = input('text', r.value, '500');
    const syncValue = () => { value.hidden = op.value === 'exists'; };
    op.addEventListener('change', syncValue);
    syncValue();
    const remove = button('✕', () => { row.remove(); });
    remove.title = 'Remove';
    path.addEventListener('focus', () => { lastPath = { input: path, relative: mode.value !== 'values' }; });
    row.append(path, op, value, remove);
    row._get = () => ({ path: path.value.trim(), op: op.value, value: value.value });
    rowsBox.append(row);
    return row;
  };
  const rows = () => [...rowsBox.children].map((r) => r._get());
  const formModel = () => ({ mode: mode.value, join: join.value, rows: rows(), items: items.value.trim(), key: key.value.trim(), countOp: countOp.value, countValue: Number(countValue.value) });

  const condition = el('div', 'trigger-condition');
  const formPart = el('div', 'trigger-form');
  const joinRow = el('div', 'trigger-inline');
  joinRow.append(text('Fires when'), join);
  const addButton = button('+ Add a check', () => addRow());
  formPart.append(mode, itemsRow, keyRow, joinRow, rowsBox, addButton, countRow);
  condition.append(formPart, jsonBox, toggleJson);

  const syncMode = () => {
    itemsRow.hidden = mode.value === 'values';
    keyRow.hidden = mode.value !== 'new';
    countRow.hidden = mode.value !== 'count';
    formPart.hidden = jsonMode;
    jsonBox.hidden = !jsonMode;
    toggleJson.textContent = jsonMode ? 'Edit as a form' : 'Edit as JSON';
  };
  const loadModel = (m) => {
    mode.value = m.mode; join.value = m.join; items.value = m.items || ''; key.value = m.key || '';
    countOp.value = String(m.countOp || 'gte'); countValue.value = String(m.countValue ?? 1);
    rowsBox.replaceChildren();
    for (const r of m.rows) addRow(r);
    if (!m.rows.length && m.mode === 'values') addRow();
  };
  loadModel(model);
  if (jsonMode) jsonBox.value = JSON.stringify(t?.when ?? null, null, 2);
  mode.addEventListener('change', syncMode);
  items.addEventListener('focus', () => { lastPath = { input: items, items: true }; });
  key.addEventListener('focus', () => { lastPath = { input: key, relative: true }; });
  toggleJson.addEventListener('click', () => {
    if (!jsonMode) {
      jsonBox.value = JSON.stringify(buildPredicate(formModel()), null, 2);
      jsonMode = true;
    } else {
      let parsed;
      try { parsed = JSON.parse(jsonBox.value); } catch { return void showError('That is not valid JSON.'); }
      const m = modelOf(parsed);
      if (!m) return void showError('This condition is too complex for the form; keep editing it as JSON.');
      loadModel(m);
      jsonMode = false;
    }
    showError('');
    syncMode();
  });
  syncMode();

  const when = () => {
    if (jsonMode) {
      try { return JSON.parse(jsonBox.value); } catch { throw new Error('The condition is not valid JSON.'); }
    }
    const m = formModel();
    if (m.mode !== 'values' && !m.items) throw new Error('Say which items to look at (click a value inside a list in the test result).');
    const p = buildPredicate(m);
    if (!p) throw new Error('Add at least one check to the condition.');
    return p;
  };

  // ---- test probe
  const testButton = button('Test probe', null, true);
  const testOut = el('div', 'trigger-test');
  const errorLine = para('', 'error');
  errorLine.hidden = true;
  function showError(message) { errorLine.textContent = message; errorLine.hidden = !message; }

  const insertPath = (segments) => {
    if (!lastPath) {
      // No box chosen yet: fill the first empty check, as a path from the root.
      const first = [...rowsBox.querySelectorAll('.trigger-path')].find((i) => !i.value) || null;
      lastPath = first ? { input: first, relative: mode.value !== 'values' } : null;
      if (!lastPath) return;
    }
    if (lastPath.items) {
      const split = splitAtItems(segments);
      lastPath.input.value = split ? split.items : pathText(segments);
    } else if (lastPath.relative && items.value.trim()) {
      const rel = relativeTo(items.value.trim(), segments);
      lastPath.input.value = rel ?? pathText(segments);
    } else if (lastPath.relative) {
      const split = splitAtItems(segments);
      if (split) { items.value = split.items; lastPath.input.value = split.relative; } else lastPath.input.value = pathText(segments);
    } else {
      lastPath.input.value = pathText(segments);
    }
    lastPath.input.focus();
  };

  testButton.addEventListener('click', async () => {
    showError('');
    let body;
    try {
      body = { accountId: account.value, tool: tool.value, arguments: args() };
      try { body.when = when(); } catch { /* test the read alone */ }
    } catch (err) { return void showError(err.message); }
    if (!chosenAccount()?.live) return void showError('Sign in to this account in this chat to test it (Profile menu → unlock).');
    testButton.disabled = true;
    testOut.replaceChildren(para('Checking…'));
    try {
      const r = await opts.probe(body);
      const parts = [];
      if (r.isError || !r.json) parts.push(para(r.isError ? 'The tool answered with an error:' : 'The result is not JSON, so a condition cannot read it:', 'warn'));
      const ev = r.evaluation;
      if (ev) {
        if (ev.errors?.length) parts.push(para(ev.errors.join(' '), ev.description ? 'warn' : 'error'));
        if (ev.description) {
          parts.push(para(`Right now the condition is ${ev.value ? 'TRUE' : 'false'}: ${ev.description}` +
            (ev.value && ev.matched?.length ? ` (${ev.matched.length}${ev.truncated ? '+' : ''} matched)` : '') +
            (mode.value === 'new' ? ' — with no history here, every item counts as new.' : ''), ev.value ? 'ok' : ''));
        }
        if (ev.matched?.length) {
          const d = document.createElement('details');
          const s = document.createElement('summary');
          s.textContent = 'What matched';
          const pre = document.createElement('pre');
          pre.className = 'code';
          pre.textContent = JSON.stringify(ev.matched, null, 2);
          d.append(s, pre);
          parts.push(d);
        }
      }
      if (r.json) {
        parts.push(para('Click a value to use its path in the box you last clicked.'));
        let parsed;
        try { parsed = JSON.parse(r.result); } catch { parsed = undefined; }
        parts.push(parsed === undefined ? pre(r.result) : jsonTree(parsed, insertPath));
      } else {
        parts.push(pre(r.result));
      }
      if (r.truncated) parts.push(para('(The result was cut for display.)'));
      testOut.replaceChildren(...parts);
    } catch (err) {
      testOut.replaceChildren();
      showError(err.body?.error || err.message);
    } finally {
      testButton.disabled = false;
    }
  });

  // ---- timing and action
  const minEvery = Math.max(1, Number(opts.minInterval) || 5);
  const every = input('number', String(t?.everyMinutes ?? Math.max(15, minEvery)));
  every.min = String(minEvery);
  every.max = '1440';
  const fire = select([['edge', 'once when it becomes true'], ['level', 'every time it is true']], t?.fire || 'edge');
  const holdFor = input('number', String(t?.holdFor ?? 1));
  holdFor.min = '1';
  holdFor.max = '10';
  const cooldown = input('number', t?.cooldownMinutes && t.cooldownMinutes !== t.everyMinutes ? String(t.cooldownMinutes) : '', 'same as the check interval');
  const activeHours = input('text', t?.activeHours || '', 'e.g. * 7-19 * * 1-5 (optional)');
  const action = select([['run', 'run the prompt with what matched'], ['alert', 'just email me (no AI)']], t?.action || 'run');
  action.addEventListener('change', () => opts.onAction?.(action.value));

  const timing = el('div', 'trigger-inline');
  timing.append(text('Check every'), every, text('minutes; fire'), fire, text('after'), holdFor, text('true check(s) in a row.'));
  const timing2 = el('div', 'trigger-inline');
  timing2.append(text('At least'), cooldown, text('minutes between two firings.'));

  examples.addEventListener('change', () => {
    const ex = EXAMPLES.find((e) => e.id === examples.value);
    examples.value = '';
    if (!ex) return;
    const a = accounts.find((x) => x.role === ex.role) || (ex.role === 'User' ? accounts.find((x) => x.role === 'DomainAdmin') : null);
    if (a) account.value = a.id;
    renderTools(ex.tool);
    savedArgs = typeof ex.arguments === 'function' ? ex.arguments(a) : ex.arguments;
    renderArgs();
    const m = modelOf(ex.when);
    if (m) { loadModel(m); jsonMode = false; } else { jsonBox.value = JSON.stringify(ex.when, null, 2); jsonMode = true; }
    syncMode();
    every.value = String(Math.max(minEvery, ex.everyMinutes));
    fire.value = ex.fire;
    holdFor.value = String(ex.holdFor);
    action.value = ex.action;
    opts.onAction?.(action.value);
    showError(a ? '' : `None of the accounts tasks may use is a ${ex.role === 'SysAdmin' ? 'system admin' : 'mailbox'}.`);
    testOut.replaceChildren(para('The field names in examples are a best guess. Run "Test probe" and click the real value to fix the paths.', 'warn'));
  });

  const checkRow = el('div', 'trigger-inline');
  checkRow.append(labelled('As', account), labelled('Read', tool));
  root.append(
    examples,
    field('Check', checkRow, argsBox),
    field('Condition', condition, testButton, testOut),
    field('Timing', timing, timing2, labelled('Only between (cron, task time zone)', activeHours)),
    field('Then', action),
    errorLine);

  return {
    element: root,
    accountId: () => account.value,
    action: () => action.value,
    value() {
      if (!account.value || !tool.value) throw new Error('Choose the account and the tool the condition checks.');
      const n = Number(every.value);
      return {
        probe: { accountId: account.value, tool: tool.value, arguments: args() },
        everyMinutes: n,
        when: when(),
        fire: fire.value,
        holdFor: Number(holdFor.value) || 1,
        cooldownMinutes: cooldown.value.trim() ? Number(cooldown.value) : n,
        action: action.value,
        activeHours: activeHours.value.trim() || null
      };
    }
  };
}

/** A collapsible view of a JSON value whose leaves are buttons that report their path. Text only. */
export function jsonTree(value, onPick, { maxChildren = 200, maxDepth = 12 } = {}) {
  const build = (v, segs, depth) => {
    if (v !== null && typeof v === 'object') {
      const d = document.createElement('details');
      d.open = depth < 2;
      const s = document.createElement('summary');
      const entries = Array.isArray(v) ? v.map((x, i) => [i, x]) : Object.entries(v);
      s.textContent = `${segs.length ? labelOf(segs[segs.length - 1]) : '$'} ${Array.isArray(v) ? `[${v.length}]` : `{${entries.length}}`}`;
      d.append(s);
      if (depth >= maxDepth) { d.append(para('…')); return d; }
      const list = el('div', 'json-children');
      for (const [k, x] of entries.slice(0, maxChildren)) list.append(build(x, [...segs, k], depth + 1));
      if (entries.length > maxChildren) list.append(para(`… ${entries.length - maxChildren} more`));
      d.append(list);
      return d;
    }
    const row = el('div', 'json-leaf');
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'json-pick';
    b.title = pathText(segs);
    const k = document.createElement('span');
    k.className = 'json-key';
    k.textContent = segs.length ? `${labelOf(segs[segs.length - 1])}: ` : '';
    const val = document.createElement('span');
    val.className = 'json-value';
    const shown = JSON.stringify(v);
    val.textContent = shown.length > 200 ? shown.slice(0, 200) + '…' : shown;
    b.append(k, val);
    b.addEventListener('click', () => onPick(segs));
    row.append(b);
    return row;
  };
  const tree = el('div', 'json-tree');
  tree.append(build(value, [], 0));
  return tree;
}

const labelOf = (seg) => (typeof seg === 'number' ? `[${seg}]` : seg);

/* -------------------------------------------------------------- helpers */

function el(tag, className) { const e = document.createElement(tag); if (className) e.className = className; return e; }

function text(t) { const s = document.createElement('span'); s.className = 'trigger-text'; s.textContent = t; return s; }

function para(t, kind = '') {
  const p = document.createElement('p');
  p.className = 'hint task-hint' + (kind ? ' ' + kind : '');
  p.textContent = t;
  return p;
}

function pre(t) { const p = document.createElement('pre'); p.className = 'code trigger-raw'; p.textContent = t; return p; }

function option(value, label) { const o = document.createElement('option'); o.value = value; o.textContent = label; return o; }

function select(options, value) {
  const s = document.createElement('select');
  for (const [v, label] of options) s.append(option(v, label));
  s.value = value;
  return s;
}

function input(type, value = '', placeholder = '') {
  const i = document.createElement('input');
  i.type = type;
  i.value = value;
  if (placeholder) i.placeholder = placeholder;
  i.autocomplete = 'off';
  return i;
}

function button(label, onClick, primary = false) {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = 'btn btn-small ' + (primary ? 'btn-primary' : 'btn-ghost');
  b.textContent = label;
  if (onClick) b.addEventListener('click', () => onClick(b));
  return b;
}

function labelled(label, control) {
  const l = el('label', 'trigger-labelled');
  const s = el('span', 'trigger-text');
  s.textContent = label;
  l.append(s, control);
  return l;
}

function field(label, ...controls) {
  const wrap = el('div', 'task-field');
  const p = el('p', 'task-label');
  p.textContent = label;
  wrap.append(p, ...controls);
  return wrap;
}
