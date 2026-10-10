/**
 * profile-ui.js — the DOM for server-mode profiles: the passkey panel on the
 * login view, the "save to a profile" offer, the Profile menu, and the
 * one-time recovery-code dialog. The logic is in profile.js; chat.js owns the
 * session and hands in callbacks.
 */

import * as api from './api.js';
import * as profile from './profile.js';
import { prfSupported, passkeyErrorMessage, NoPrfError } from './passkey.js';

const $ = (id) => document.getElementById(id);

const el = {
  panel: $('passkey-panel'),
  panelText: $('passkey-text'),
  btnPasskey: $('btn-passkey'),
  btnRecoveryToggle: $('btn-recovery-toggle'),
  recoveryEntry: $('recovery-entry'),
  recoveryInput: $('f-recovery'),
  btnRecover: $('btn-recover'),
  footerPrivacy: $('footer-privacy'),

  offer: $('profile-offer'),
  btnOfferCreate: $('btn-offer-create'),
  btnOfferDismiss: $('btn-offer-dismiss'),

  menu: $('profile-menu'),
  btnProfile: $('btn-profile'),
  popover: $('profile-popover'),

  codeDialog: $('code-dialog'),
  codeValue: $('code-value'),
  codeCopy: $('code-copy'),
  codeDone: $('code-done')
};

let hooks = null;
let prf = false;
let view = null;          // GET /api/profile, while the menu is open or after a change
let offerDismissed = false;

/**
 * hooks: {
 *   getSession(), currentSettings() → { openRouterKey, model, toolsOff, allowChanges },
 *   onIdleChanged(minutes), getAllowChanges(), setAllowChanges(value),
 *   onSignedIn({ session, skipped, settings }), onSession(session), onEnded(message),
 *   notice(text, kind), setLoginError(text), closeOtherPopovers()
 * }
 */
export async function initProfileUi(h) {
  hooks = h;
  prf = profile.serverMode() && await prfSupported();

  el.btnPasskey.addEventListener('click', () => signIn());
  el.btnRecoveryToggle.addEventListener('click', () => {
    el.recoveryEntry.hidden = !el.recoveryEntry.hidden;
    if (!el.recoveryEntry.hidden) el.recoveryInput.focus();
  });
  el.btnRecover.addEventListener('click', () => recover());
  el.recoveryInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); recover(); } });

  el.btnOfferCreate.addEventListener('click', () => create(el.btnOfferCreate));
  el.btnOfferDismiss.addEventListener('click', () => { offerDismissed = true; renderOffer(); });

  el.btnProfile.addEventListener('click', (e) => { e.stopPropagation(); hooks.closeOtherPopovers(); toggle(); });
  el.popover.addEventListener('click', (e) => e.stopPropagation());

  el.codeCopy.addEventListener('click', async () => {
    try { await navigator.clipboard.writeText(el.codeValue.textContent); el.codeCopy.textContent = 'Copied'; } catch { selectCode(); }
    setTimeout(() => { el.codeCopy.textContent = 'Copy'; }, 1500);
  });
  el.codeDone.addEventListener('click', () => el.codeDialog.close());

  if (profile.serverMode()) {
    el.footerPrivacy.textContent = prf
      ? 'Sessions expire after 30 minutes idle (12 hours at most). Nothing is kept on this server unless you save a profile; ' +
        'a profile is encrypted with your passkey. The conversation itself lives in this tab.'
      : 'Sessions expire after 30 minutes idle (12 hours at most). The conversation lives in this tab and is gone when you close it.';
  }
}

/** Profiles replace "Remember me on this device" here: server mode, and a browser that can use them. */
export const usesProfiles = () => profile.serverMode() && prf;

/* ------------------------------------------------------------- login view */

/** The passkey panel, when this server keeps profiles and this browser can use them. */
export function renderLoginPanel({ adding = false, resumable = false } = {}) {
  const show = profile.serverMode() && prf && !adding && !resumable;
  el.panel.hidden = !show;
  if (!show) return;
  const h = profile.hint();
  el.panelText.textContent = h
    ? `This browser has a profile here${h.label ? ` (${h.label})` : ''}. Sign in with your passkey and your accounts and settings come back.`
    : 'Saved a profile on this server? Sign in with your passkey and your accounts and settings come back.';
  el.btnPasskey.className = 'btn ' + (h ? 'btn-primary' : 'btn-ghost');
  profile.prefetchLogin();
}

async function signIn() {
  hooks.setLoginError('');
  el.btnPasskey.disabled = true;
  const label = el.btnPasskey.textContent;
  el.btnPasskey.textContent = 'Waiting for your passkey…';
  try {
    await hooks.onSignedIn(await profile.signInWithPasskey());
  } catch (err) {
    hooks.setLoginError(signInMessage(err));
    if (err instanceof NoPrfError) el.recoveryEntry.hidden = false;
  } finally {
    el.btnPasskey.disabled = false;
    el.btnPasskey.textContent = label;
    profile.prefetchLogin();
  }
}

async function recover() {
  hooks.setLoginError('');
  el.btnRecover.disabled = true;
  try {
    const result = await profile.signInWithRecoveryCode(el.recoveryInput.value);
    el.recoveryInput.value = '';
    el.recoveryEntry.hidden = true;
    await hooks.onSignedIn(result);
    hooks.notice('Opened with your recovery code. Add a passkey for this device in the Profile menu, and make a new recovery code.', 'warn');
  } catch (err) {
    hooks.setLoginError(signInMessage(err));
  } finally {
    el.btnRecover.disabled = false;
  }
}

function signInMessage(err) {
  if (err instanceof api.ApiError) {
    if (err.code === 'PASSKEY_INVALID') return 'That passkey is not one of this server\'s profiles. If it locked "Remember me" on this device, use that instead.';
    if (err.code === 'RECOVERY_INVALID') return 'That recovery code is not valid here.';
    if (err.code === 'PROFILE_GONE') { profile.forgetHint(); return 'That profile no longer exists on this server.'; }
    if (err.status === 429) return 'Too many attempts. Wait a minute and try again.';
  }
  return passkeyErrorMessage(err, 'Signing in');
}

/* ------------------------------------------------------------ the offer */

/** The banner above an ordinary server-mode chat: save it to a profile. */
export function renderOffer() {
  const s = hooks?.getSession();
  const show = profile.serverMode() && prf && !!s && !s.profile && !offerDismissed &&
    Array.isArray(s.accounts) && s.accounts.length > 0;
  el.offer.hidden = !show;
  if (show) profile.prefetchRegister();
}

async function create(button) {
  button.disabled = true;
  const label = button.textContent;
  button.textContent = 'Waiting for your passkey…';
  try {
    const { session, recoveryCode } = await profile.createProfile({ settings: hooks.currentSettings() });
    offerDismissed = true;
    await hooks.onSession(session);
    hooks.notice('Saved to your profile on this server. Sign in with your passkey on any browser to get these accounts and settings back.', 'info');
    if (recoveryCode) showCode(recoveryCode);
  } catch (err) {
    hooks.notice(createMessage(err), 'error');
    profile.prefetchRegister();
  } finally {
    button.disabled = false;
    button.textContent = label;
    renderOffer();
    renderMenu();
  }
}

function createMessage(err) {
  if (err instanceof api.ApiError && err.body?.error) return `Could not create the profile: ${err.body.error}`;
  return `Could not create the profile: ${passkeyErrorMessage(err, 'Creating it')}`;
}

/* ------------------------------------------------------- recovery code */

function showCode(code) {
  el.codeValue.textContent = code;
  if (typeof el.codeDialog.showModal === 'function') el.codeDialog.showModal();
  selectCode();
}

function selectCode() {
  const range = document.createRange();
  range.selectNodeContents(el.codeValue);
  const sel = window.getSelection();
  sel.removeAllRanges();
  sel.addRange(range);
}

/* --------------------------------------------------------- profile menu */

export function renderMenu() {
  const s = hooks?.getSession();
  el.menu.hidden = !(profile.serverMode() && s && (s.profile || prf));
  el.btnProfile.textContent = s && s.profile ? 'Profile' : 'Profile ○';
  if (!el.popover.hidden) renderPopover();
}

function toggle(open = el.popover.hidden) {
  el.popover.hidden = !open;
  el.btnProfile.setAttribute('aria-expanded', String(open));
  if (open) refreshView();
}

export function closePopover() {
  if (!el.popover.hidden) toggle(false);
}

/** The profile view the tasks dialog also uses. Null outside a profile session. */
export async function loadView() {
  const s = hooks?.getSession();
  if (!s || !s.profile) { view = null; return null; }
  try {
    view = await api.profile();
  } catch {
    view = null;
  }
  return view;
}

export const currentView = () => view;

async function refreshView() {
  renderPopover();
  await loadView();
  renderPopover();
}

function renderPopover() {
  const s = hooks.getSession();
  const parts = [];
  const add = (node) => { parts.push(node); return node; };

  add(title('Your profile on this server'));

  if (!s || !s.profile) {
    add(para(prf
      ? 'This chat is not saved. A profile keeps its accounts and your settings on this server, encrypted with a passkey, so you can sign in from any browser.'
      : 'This browser cannot create a passkey that encrypts a profile (no PRF support). Try a recent Chrome, Edge, Safari or Firefox.'));
    if (prf) add(button('Create profile', (b) => create(b), true));
    return el.popover.replaceChildren(...parts);
  }

  if (!profile.hasKeys()) {
    add(para('This page does not hold your profile keys (it was reloaded). Chatting works; to save settings or read task results, unlock it again.', 'warn'));
    add(button('Unlock with passkey', async (b) => {
      await busy(b, async () => {
        const result = await profile.signInWithPasskey();
        await hooks.onSignedIn(result);
      });
    }, true));
  }

  if (!view) {
    add(para('Loading…'));
    return el.popover.replaceChildren(...parts);
  }

  // Settings
  add(section('Settings'));
  const idleRow = add(document.createElement('label'));
  idleRow.className = 'profile-row';
  const idleText = document.createElement('span');
  idleText.textContent = 'Sign out after idle for';
  const idle = document.createElement('select');
  idle.className = 'profile-input';
  for (const [value, text] of idleChoices(view.idle)) {
    const o = document.createElement('option');
    o.value = value;
    o.textContent = text;
    idle.appendChild(o);
  }
  idle.value = view.idle.minutes == null ? '' : String(view.idle.minutes);
  idle.addEventListener('change', () => busy(idle, async () => {
    try {
      view = await api.setProfileIdle(idle.value === '' ? null : Number(idle.value));
    } catch (err) {
      idle.value = view.idle.minutes == null ? '' : String(view.idle.minutes);
      throw err;
    }
    hooks.onIdleChanged(view.idle.minutes ?? view.idle.defaultMinutes);
    renderPopover();
  }));
  idleRow.append(idleText, idle);
  add(para('Applies to every browser signed in to this profile. A new tab or a restarted browser also opens it without the passkey until then.'));

  const allowLabel = add(document.createElement('label'));
  allowLabel.className = 'tools-option';
  const allow = document.createElement('input');
  allow.type = 'checkbox';
  allow.checked = hooks.getAllowChanges();
  allow.disabled = !profile.hasKeys();
  allow.addEventListener('change', () => hooks.setAllowChanges(allow.checked));
  const allowText = document.createElement('span');
  allowText.textContent = 'Accounts I add start with changes allowed';
  allowLabel.append(allow, allowText);

  // Passkeys
  add(section('Passkeys'));
  for (const p of view.passkeys) {
    const row = add(document.createElement('div'));
    row.className = 'profile-row';
    const text = document.createElement('span');
    text.textContent = `${p.label || 'Passkey'} · ${p.lastUsedAt ? 'used ' + new Date(p.lastUsedAt).toLocaleDateString() : 'added ' + new Date(p.createdAt).toLocaleDateString()}`;
    row.appendChild(text);
    if (view.passkeys.length > 1) row.appendChild(button('Remove', (b) => busy(b, async () => { await api.deletePasskey(p.id); await refreshView(); })));
  }
  if (profile.hasKeys() && profile.canWrapKey()) {
    add(button('Add a passkey', (b) => busy(b, async () => {
      view = await profile.addPasskey();
      hooks.notice('Passkey added. It opens this profile too.', 'info');
      renderPopover();
    })));
  }

  // Recovery code
  add(section('Recovery code'));
  add(para(view.recovery ? 'Set. It opens the profile if every passkey is lost.' : 'Not set. Without one, losing every passkey loses the profile.', view.recovery ? '' : 'warn'));
  if (profile.hasKeys() && profile.canWrapKey()) {
    add(button(view.recovery ? 'Make a new recovery code' : 'Make a recovery code', (b) => busy(b, async () => {
      showCode(await profile.newRecoveryCode(view.id));
      await refreshView();
    })));
  }

  // Scheduled tasks
  if (view.tasksEnabled) {
    add(section('Scheduled tasks'));
    add(para(view.hasTaskKey
      ? 'An OpenRouter key for tasks is saved (sealed with this server\'s key).'
      : 'Tasks need their own OpenRouter key, kept on this server. Use one with a spending limit.', view.hasTaskKey ? '' : 'warn'));
    const keyRow = add(document.createElement('div'));
    keyRow.className = 'profile-row';
    const input = document.createElement('input');
    input.type = 'password';
    input.placeholder = view.hasTaskKey ? 'Replace the task key…' : 'sk-or-v1-…';
    input.autocomplete = 'off';
    input.className = 'profile-input';
    keyRow.append(input, button('Save', (b) => busy(b, async () => {
      if (!input.value.trim()) return;
      view = await api.setTaskKey(input.value.trim());
      input.value = '';
      renderPopover();
    })));
    if (view.hasTaskKey) add(button('Remove the task key', (b) => busy(b, async () => { view = await api.setTaskKey(null); renderPopover(); })));

    add(para('Accounts tasks may use while you are away (their sign-in is then sealed with this server\'s key, so whoever runs the server could use them too):'));
    for (const a of view.accounts) {
      const label = document.createElement('label');
      label.className = 'tools-option';
      const cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.checked = a.delegated;
      cb.disabled = !a.live;
      cb.addEventListener('change', () => busy(cb, async () => {
        try {
          view = await api.setDelegation(a.id, cb.checked);
        } catch (err) {
          cb.checked = !cb.checked;
          throw err;
        }
        renderPopover();
      }));
      const name = document.createElement('span');
      name.textContent = `${a.login || 'account'}${a.live ? '' : a.state === 'rejected' ? ' — sign in again' : ' — not signed in'}`;
      label.append(cb, name);
      add(label);
    }
    add(button(view.tasksPaused ? 'Resume all tasks' : 'Pause all tasks', (b) => busy(b, async () => {
      view = await api.setTasksPaused(!view.tasksPaused);
      renderPopover();
    })));
  }

  // Leaving
  add(section('This profile'));
  const del = add(button('Delete profile…', null));
  del.addEventListener('click', () => {
    if (del.dataset.armed !== '1') {
      del.dataset.armed = '1';
      del.textContent = 'Click again: delete it and sign out everywhere';
      del.classList.add('btn-danger');
      return;
    }
    busy(del, async () => {
      await api.deleteProfile();
      profile.lock();
      profile.forgetHint();
      await profile.forgetWarm();
      hooks.onEnded('Your profile was deleted from this server, and its accounts were signed out.');
    });
  });

  el.popover.replaceChildren(...parts);
}

/** The idle timeouts offered: the server default, then the usual steps within the server's range. */
function idleChoices({ minutes, defaultMinutes, minMinutes, maxMinutes }) {
  const label = (m) => (m < 60 ? `${m} min` : m % 60 ? `${(m / 60).toFixed(1)} h` : `${m / 60} h`);
  const steps = new Set([5, 15, 30, 60, 120, 240, 480, 720, 1440].filter((m) => m >= minMinutes && m <= maxMinutes));
  if (minutes != null) steps.add(minutes);
  return [['', `Default (${label(defaultMinutes)})`], ...[...steps].sort((a, b) => a - b).map((m) => [String(m), label(m)])];
}

async function busy(control, fn) {
  control.disabled = true;
  try {
    await fn(control);
  } catch (err) {
    hooks.notice(passkeyErrorMessage(err, 'That'), 'error');
  } finally {
    control.disabled = false;
  }
}

function title(text) {
  const p = document.createElement('p');
  p.className = 'popover-title';
  p.textContent = text;
  return p;
}

function section(text) {
  const h = document.createElement('p');
  h.className = 'profile-section';
  h.textContent = text;
  return h;
}

function para(text, kind = '') {
  const p = document.createElement('p');
  p.className = 'hint profile-hint' + (kind ? ' ' + kind : '');
  p.textContent = text;
  return p;
}

function button(text, onClick, primary = false) {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = 'btn btn-small ' + (primary ? 'btn-primary' : 'btn-ghost');
  b.textContent = text;
  if (onClick) b.addEventListener('click', () => onClick(b));
  return b;
}
