/**
 * approvals.js — the "To approve" view of the Tasks dialog, and the approval
 * controls of the task editor.
 *
 * A task run may propose a write instead of making it. Each proposal is the
 * exact call: the server sealed it to this profile's public key, so it is
 * opened here (profile.js) and shown verbatim — the arguments first, every
 * value in full, as text. The model's note comes last and is labelled as
 * untrusted: the model wrote it after reading mail anyone can send. Approving
 * sends the hash of the arguments shown (approvals-core.js); the server runs
 * the call only if it matches, once. Destructive and admin changes (or a task
 * that asks for it) need a fresh passkey confirmation, bound by the server to
 * this session, this proposal and these arguments.
 */

import * as api from './api.js';
import * as profile from './profile.js';
import { loadView, currentView } from './profile-ui.js';
import { assertPasskey, passkeyErrorMessage } from './passkey.js';
import { prettyJson } from './markdown.js';
import {
  proposalHash, argumentRows, formatRemaining, defaultApprovalMode, statusText, groupByRun,
  revealText, revealWarning
} from './approvals-core.js';

const NOTE_WARNING = 'Written by the AI. It read mail that anyone can send, so this may be wrong or planted.';

/**
 * Renders the queue into `body`. ctx: { notice(text, kind), unlock(), back(), openRun(runId), onChange() }.
 */
export async function renderApprovals(body, ctx) {
  body.replaceChildren(para('Loading…'));
  const view = currentView() || await loadView();
  if (!view) return void body.replaceChildren(para('Open your profile first.', 'error'), backRow(ctx));

  if (!profile.hasKeys()) {
    body.replaceChildren(
      heading('To approve'),
      para('This page does not hold your profile keys (it was reloaded). Unlock to review proposed changes.', 'warn'),
      button('Unlock with passkey', async (b) => {
        b.disabled = true;
        try { await ctx.unlock(); await renderApprovals(body, ctx); } catch (err) { ctx.notice(err.message, 'error'); b.disabled = false; }
      }, true),
      backRow(ctx));
    return;
  }

  let pending, decided;
  try {
    [pending, decided] = await Promise.all([api.proposals('pending', { limit: 200 }), api.proposals('decided', { limit: 20 })]);
  } catch (err) {
    return void body.replaceChildren(para(`Could not load proposals: ${err.message}`, 'error'), backRow(ctx));
  }

  const open = async (p) => {
    try {
      const display = await profile.openSealedJson(view, p.display, `task-proposal|${view.id}|${p.id}`);
      const result = p.result ? await profile.openSealedJson(view, p.result, `task-proposal-result|${view.id}|${p.id}`) : null;
      return { ...p, display, openedResult: result };
    } catch {
      return { ...p, display: null, openedResult: null };
    }
  };
  pending = await Promise.all(pending.map(open));
  decided = await Promise.all(decided.map(open));

  const parts = [heading(`To approve${pending.length ? ` (${pending.length})` : ''}`)];
  parts.push(para('Each change below was proposed by a task run and has not happened. Check the arguments: they are exactly what will run. ' +
    'Approving runs it once, now.'));

  const live = pending.filter((p) => p.status === 'pending');
  if (!live.length) parts.push(para('Nothing is waiting for your approval.'));

  for (const group of groupByRun(live)) {
    const first = group.items[0];
    const section = div('approval-run');
    const head = div('approval-run-head');
    const title = document.createElement('span');
    title.textContent = `${first.display?.taskName || 'Task'} · run of ${new Date(first.createdAt).toLocaleString()}`;
    head.append(title, button('See what it read', () => ctx.openRun(first.runId)));
    if (group.items.length > 1) {
      head.append(confirmButton(`Deny all ${group.items.length}`, 'Click again to deny all', async () => {
        await api.denyRunProposals(group.runId);
        ctx.onChange?.();
        await renderApprovals(body, ctx);
      }, ctx));
    }
    section.append(head);
    for (const p of group.items) section.append(card(p, view, body, ctx));
    parts.push(section);
  }

  const expired = pending.filter((p) => p.status !== 'pending');
  const history = [...expired, ...decided];
  if (history.length) {
    const details = document.createElement('details');
    details.className = 'approval-history';
    const sum = document.createElement('summary');
    sum.textContent = `Recently decided (${history.length})`;
    details.append(sum);
    for (const p of history) details.append(decidedRow(p));
    parts.push(details);
    for (const p of decided.filter((x) => !x.read)) api.markProposalRead(p.id).catch(() => {});
  }

  parts.push(backRow(ctx));
  body.replaceChildren(...parts);
}

/* ----------------------------------------------------------------- cards */

function card(p, view, body, ctx) {
  const d = p.display;
  const el = div('approval-card' + (d?.destructive ? ' destructive' : ''));
  if (!d) {
    el.append(para('This proposal could not be opened with your profile keys.', 'error'));
    el.append(actionsRow(button('Deny', async (b) => deny(b, p, body, ctx))));
    return el;
  }

  // What: the tool and its catalog description.
  const head = div('approval-head');
  const tool = document.createElement('code');
  tool.className = 'approval-tool';
  tool.textContent = d.tool;
  head.append(tool);
  if (d.destructive) head.append(badge('destructive', 'danger'));
  if (d.scope === 'DomainAdmin') head.append(badge('domain admin', 'warn'));
  if (d.scope === 'SysAdmin') head.append(badge('system admin', 'warn'));
  if (p.needsPasskey) head.append(badge('passkey', 'info'));
  const expires = document.createElement('span');
  expires.className = 'approval-expiry';
  expires.textContent = `expires in ${formatRemaining(new Date(p.expiresAt).getTime() - Date.now())}`;
  expires.title = new Date(p.expiresAt).toLocaleString();
  head.append(expires);
  el.append(head);
  if (d.description) el.append(para(d.description));

  // Who.
  const who = div('approval-who');
  const whoText = document.createElement('span');
  whoText.textContent = `As ${d.accountLogin} · ${roleName(d.accountRole)} on ${d.accountHost}`;
  who.append(whoText, badge(d.readOnly ? 'read-only' : 'read-write', d.readOnly ? 'ok' : 'warn'));
  el.append(who);

  // The arguments, verbatim, with hidden characters made visible (display only;
  // the hash below is over d.argsJson itself).
  el.append(argumentsTable(d.argsJson));
  const raw = document.createElement('details');
  raw.className = 'approval-raw';
  const rawSum = document.createElement('summary');
  rawSum.textContent = 'Raw JSON (exactly what runs)';
  const rawPre = document.createElement('pre');
  rawPre.className = 'code';
  const rawWarning = reveal(rawPre, d.argsJson);
  raw.append(rawSum, rawPre);
  el.append(raw);
  if (rawWarning) raw.open = true;

  // The model's note, last, as untrusted text.
  if (d.note) {
    const note = div('approval-note');
    const label = document.createElement('p');
    label.className = 'approval-note-label';
    label.textContent = NOTE_WARNING;
    const text = document.createElement('p');
    text.className = 'approval-note-text';
    const noteWarning = reveal(text, d.note);
    note.append(label, text);
    if (noteWarning) note.append(warningLine(noteWarning));
    el.append(note);
  }

  const outcome = div('approval-outcome');
  outcome.hidden = true;
  const approve = button(p.needsPasskey ? 'Approve with passkey…' : 'Approve', null, true);
  const denyButton = button('Deny', async (b) => deny(b, p, body, ctx));
  approve.addEventListener('click', () => approveFlow(approve, denyButton, p, outcome, view, ctx));
  el.append(actionsRow(approve, denyButton), outcome);
  return el;
}

/**
 * Approve: hash what is shown; for a passkey proposal, first fetch a ceremony
 * bound to that hash (the button then asks for the passkey on a second click,
 * so the WebAuthn prompt runs inside a fresh user gesture); then approve.
 */
async function approveFlow(b, denyButton, p, outcome, view, ctx) {
  const d = p.display;
  b.disabled = true;
  denyButton.disabled = true;
  try {
    let body;
    const ceremony = b._ceremony && Date.now() - b._ceremony.at < 100000 ? b._ceremony : null;
    b._ceremony = null;
    if (ceremony) {
      // Second click: straight to the passkey prompt, nothing awaited before it.
      const credential = await assertPasskey(ceremony.options);
      body = { argsHash: ceremony.hash, ceremonyId: ceremony.ceremonyId, credential };
    } else {
      const hash = await proposalHash(d.tool, d.accountId, d.argsJson);
      if (hash !== d.argsHash) throw new Error('This proposal does not verify; it was not approved.');
      body = { argsHash: hash };
      if (p.needsPasskey) {
        const options = await api.approveOptions(p.id, hash);
        if (options.passkey) {
          b._ceremony = { ...options, hash, at: Date.now() };
          b.textContent = 'Confirm with passkey';
          b.disabled = false;
          denyButton.disabled = false;
          return;
        }
      }
    }

    b.textContent = 'Running…';
    const res = await api.approveProposal(p.id, body);
    let result = null;
    if (res.result) {
      try { result = await profile.openSealedJson(view, res.result, `task-proposal-result|${view.id}|${p.id}`); } catch { result = null; }
    }
    showOutcome(outcome, res.status, res.errorMessage, result);
    b.remove();
    denyButton.remove();
    ctx.onChange?.();
  } catch (err) {
    b._ceremony = null;
    b.textContent = p.needsPasskey ? 'Approve with passkey…' : 'Approve';
    b.disabled = false;
    denyButton.disabled = false;
    const message = err.code === 'ACCOUNT_UNAVAILABLE' ? 'The mail server did not answer. Nothing ran; it is still waiting. Try again later.'
      : err.code === 'PASSKEY_INVALID' ? 'The passkey did not confirm this change (it may have taken too long). Nothing ran; try again.'
        : err.code ? (err.body?.error || err.message)
          : passkeyErrorMessage(err, 'Confirming');
    showOutcome(outcome, 'error', message, null);
    if (err.code === 'PROPOSAL_NOT_PENDING' || err.code === 'PROPOSAL_EXPIRED') {
      b.remove();
      denyButton.remove();
      ctx.onChange?.();
    }
  }
}

async function deny(b, p, body, ctx) {
  b.disabled = true;
  try {
    await api.denyProposal(p.id);
    ctx.onChange?.();
    await renderApprovals(body, ctx);
  } catch (err) {
    ctx.notice(err.body?.error || err.message, 'error');
    b.disabled = false;
  }
}

function showOutcome(outcome, status, message, result) {
  outcome.hidden = false;
  outcome.className = 'approval-outcome ' + (status === 'executed' ? 'ok' : status === 'error' ? 'error' : 'warn');
  const parts = [];
  const head = document.createElement('p');
  head.textContent = status === 'executed' ? 'Done.' : status === 'error' ? message : `${statusText(status)}${message ? `: ${message}` : ''}`;
  parts.push(head);
  if (result && result.content) {
    const pre = document.createElement('pre');
    pre.className = 'code';
    pre.textContent = prettyJson(result.content);
    parts.push(pre);
  }
  outcome.replaceChildren(...parts);
}

function decidedRow(p) {
  const row = div('approval-decided');
  const text = document.createElement('span');
  const d = p.display;
  const when = p.executedAt || p.decidedAt || p.expiresAt;
  text.textContent = `${d ? d.tool : '(unreadable)'}${d ? ` as ${d.accountLogin}` : ''} · ${statusText(p.status)}` +
    (p.errorMessage ? ` (${p.errorMessage})` : '') + (when ? ` · ${new Date(when).toLocaleString()}` : '');
  row.append(text);
  if (d || p.openedResult) {
    const details = document.createElement('details');
    const sum = document.createElement('summary');
    sum.textContent = 'Details';
    const pre = document.createElement('pre');
    pre.className = 'code';
    reveal(pre, (d ? `${d.argsJson}` : '') + (p.openedResult?.content ? `\n\n${prettyJson(p.openedResult.content)}` : ''));
    details.append(sum, pre);
    row.append(details);
  }
  return row;
}

function argumentsTable(argsJson) {
  const rows = argumentRows(argsJson);
  const table = document.createElement('table');
  table.className = 'approval-args';
  if (!rows) {
    const pre = document.createElement('pre');
    pre.className = 'code';
    const warning = reveal(pre, argsJson);
    if (!warning) return pre;
    const wrap = div('approval-args-raw');
    wrap.append(pre, warningLine(warning));
    return wrap;
  }
  if (!rows.length) {
    const tr = table.insertRow();
    tr.insertCell().textContent = '(no arguments)';
  }
  for (const r of rows) {
    const tr = table.insertRow();
    const k = tr.insertCell();
    k.className = 'approval-arg-key';
    const keyWarning = reveal(k, r.key);
    const v = tr.insertCell();
    const value = document.createElement(r.kind === 'json' || r.kind === 'long' ? 'pre' : 'span');
    value.className = `approval-arg-value ${r.kind}`;
    const warning = reveal(value, r.text, { address: r.kind === 'address' });
    v.append(value);
    if (warning || keyWarning) {
      tr.classList.add('suspicious');
      v.append(warningLine(warning || keyWarning));
    }
  }
  return table;
}

/**
 * Fill `node` with `text` so that nothing in it hides: invisible and
 * direction-changing characters become ⟦U+XXXX⟧ markers, and in an address
 * every non-ASCII character is marked (look-alike letters). Text nodes only,
 * never HTML. Returns the warning to show, or null.
 */
function reveal(node, text, { address = false } = {}) {
  const r = revealText(text, { address });
  node.replaceChildren(...r.parts.map((part) => {
    if (part.kind === 'text') return document.createTextNode(part.text);
    const mark = document.createElement('span');
    mark.className = part.kind === 'hidden' ? 'char-hidden' : 'char-nonascii';
    mark.textContent = part.text;
    mark.title = part.kind === 'hidden'
      ? `${part.code}: an invisible or direction-changing character`
      : `${part.code}: not a plain ASCII letter`;
    return mark;
  }));
  return revealWarning(r);
}

function warningLine(text) {
  const p = document.createElement('p');
  p.className = 'approval-arg-warning';
  p.textContent = text;
  return p;
}

/* --------------------------------------------------------------- editor */

/**
 * The editor's per-tool mode control: run directly or ask first. `initial`
 * is the saved mode, or undefined for the default (approval for destructive
 * and admin tools).
 */
export function modeSelect(tool, initial) {
  const s = document.createElement('select');
  s.className = 'approval-mode';
  for (const [value, label] of [['auto', 'runs on its own'], ['approve', 'asks me first']]) {
    const o = document.createElement('option');
    o.value = value;
    o.textContent = label;
    s.append(o);
  }
  s.value = initial || defaultApprovalMode(tool);
  s.title = 'Asks me first: the run only proposes the change; you approve it under Tasks → To approve.';
  return s;
}

/**
 * The approval settings below the tool list: proposals per run, how long they
 * wait, and "passkey for every approval". Returns { elements, read() }.
 */
export function approvalFields(saved, limits = {}) {
  const maxProposals = numberInput(saved?.maxProposals ?? limits.defaultProposalsPerRun ?? 10, 0, limits.maxProposalsPerRun ?? 50);
  const ttlDays = numberInput(Math.round(((saved?.ttlHours ?? limits.ttlHours ?? 72) / 24) * 10) / 10, 1 / 24,
    (limits.maxTtlHours ?? 168) / 24);
  ttlDays.step = 'any';
  const passkey = document.createElement('label');
  passkey.className = 'tools-option';
  const box = document.createElement('input');
  box.type = 'checkbox';
  box.checked = !!saved?.requirePasskey;
  const span = document.createElement('span');
  span.textContent = 'Ask for my passkey for every approval (always asked for destructive and admin changes)';
  passkey.append(box, span);

  return {
    maxProposals,
    ttlDays,
    passkey,
    read: () => ({
      maxProposals: Number(maxProposals.value) >= 0 ? Math.round(Number(maxProposals.value)) : 10,
      ttlHours: Math.max(1, Math.round((Number(ttlDays.value) || 3) * 24)),
      requirePasskey: box.checked
    })
  };
}

/* -------------------------------------------------------------- helpers */

function roleName(role) {
  return role === 'SysAdmin' ? 'system admin' : role === 'DomainAdmin' ? 'domain admin' : 'user';
}

function numberInput(value, min, max) {
  const i = document.createElement('input');
  i.type = 'number';
  i.value = String(value);
  i.min = String(min);
  i.max = String(max);
  i.autocomplete = 'off';
  return i;
}

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

function button(text, onClick, primary = false) {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = 'btn btn-small ' + (primary ? 'btn-primary' : 'btn-ghost');
  b.textContent = text;
  if (onClick) b.addEventListener('click', () => onClick(b));
  return b;
}

function confirmButton(text, armedText, fn, ctx) {
  const b = button(text, null);
  b.addEventListener('click', async () => {
    if (b.dataset.armed !== '1') { b.dataset.armed = '1'; b.textContent = armedText; b.classList.add('btn-danger'); return; }
    b.disabled = true;
    try { await fn(); } catch (err) { ctx.notice(err.body?.error || err.message, 'error'); b.disabled = false; }
  });
  return b;
}

function actionsRow(...buttons) {
  const row = div('task-actions');
  row.append(...buttons);
  return row;
}

function backRow(ctx) {
  const actions = div('modal-actions');
  actions.append(button('Back', () => ctx.back()));
  return actions;
}
