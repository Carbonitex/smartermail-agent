/**
 * chat.js — the UI: login view (also used to add an account), chat view,
 * accounts bar, streaming bubbles, tool cards, stop, message queue, model
 * picker, tool-group filter, MCP token menu.
 *
 * State lives in this module only. Nothing about the conversation is written to
 * storage of any kind; a reload starts a fresh conversation. In server mode a
 * profile (profile.js, profile-ui.js) keeps the accounts and settings on the
 * server, passkey-encrypted, and tasks.js runs prompts there on a schedule.
 */

import * as api from './api.js';
import * as resume from './resume.js';
import * as profile from './profile.js';
import {
  initProfileUi, usesProfiles, renderLoginPanel, renderOffer, renderMenu as renderProfileMenu,
  closePopover as closeProfilePopover
} from './profile-ui.js';
import { initTasks, renderTasksButton, refreshBadge as refreshTasksBadge } from './tasks.js';
import { storage, DEFAULT_MODEL } from './storage.js';
import { renderMarkdown, escapeHtml, prettyJson } from './markdown.js';
import {
  runTurn, addUsage, toolsToOpenAI, buildSystemPrompt, fetchToolModels, MAX_TOOL_ROUNDS,
  roleLabel, hasMailbox, toolGroups, filterToolsByCategory, sessionAccounts
} from './llm.js';
import { ArtifactStore, ANALYZE_RESULT_TOOL, ARTIFACT_THRESHOLD, DEFAULT_ANALYSIS_MODEL, formatSize } from './artifacts.js';
import { runAnalyzeResult } from './subagent.js';

/** analyze_result as sent to OpenRouter: one object, so every request serialises it identically. */
const ANALYZE_RESULT_FUNCTION = toolsToOpenAI([ANALYZE_RESULT_TOOL])[0];

/* ------------------------------------------------------------------ state */

const state = {
  session: null,        // { expiresAt, maxAccounts, accounts: [{ id, handle, role, … }] }
  resumable: false,     // cookie session alive but this tab has no OpenRouter key: key alone resumes
  toolList: [],         // the contract tool list from GET /api/tools
  tools: [],            // OpenAI-shaped definitions actually sent (after the Tools filter)
  toolNames: [],
  disabled: new Set(),  // tool categories switched off in the Tools menu
  allowChangesDefault: false,   // profile setting: accounts added to a profile chat start read-write
  addMode: false,       // the login view is adding an account to the live chat
  messages: [],         // OpenAI message array, in memory only
  conversationId: null, // random id per conversation: OpenRouter's session_id (sticky routing keeps its cache warm)
  usage: null,          // token usage summed over this conversation (llm.js addUsage shape), for a later UI
  artifacts: new ArtifactStore(),   // large tool results of this conversation, in this tab's memory only (artifacts.js)
  analysisDefaults: { model: DEFAULT_ANALYSIS_MODEL, threshold: ARTIFACT_THRESHOLD },   // GET /api/config "analysis"
  busy: false,
  abort: null,          // AbortController for the active turn
  queue: [],            // messages typed while a turn is streaming
  autoScroll: true,
  twoFactor: null,      // { challengeId, method, emailAddress, expiresAt, hostname, add }
  tfaTimer: null,       // setInterval id for the expiry countdown
  resumeEnabled: false, // the server offers "Remember me on this device" (RESUME_KEY set)
  resumeDays: 0,
  prf: false,           // this browser can lock the saved sign-in with a passkey (WebAuthn PRF)
  pendingRemember: null,// { lock } chosen on the login form, applied once the sign-in completes
  recovering: null,     // Promise while a dead session is being resumed from the saved bundle
  notices: []           // [text, kind] to show once the chat view is up (after a resume)
};

/* ------------------------------------------------------------------- refs */

const $ = (id) => document.getElementById(id);

const el = {
  loginView: $('view-login'),
  chatView: $('view-chat'),
  loginForm: $('login-form'),
  loginTitle: $('login-title'),
  loginTagline: $('login-tagline'),
  loginLegend: $('login-legend'),
  loginFooter: $('login-footer'),
  fsOpenRouter: $('fs-openrouter'),
  loginCancel: $('login-cancel'),
  hostname: $('f-hostname'),
  email: $('f-email'),
  password: $('f-password'),
  allowChanges: $('f-allow-changes'),
  key: $('f-key'),
  loginError: $('login-error'),
  loginSubmit: $('login-submit'),
  rememberOptions: $('remember-options'),
  rememberHint: $('remember-hint'),
  remember: $('f-remember'),
  lockOption: $('lock-option'),
  passkeyLock: $('f-passkey-lock'),
  rememberedPanel: $('remembered-panel'),
  rememberedText: $('remembered-text'),
  btnResume: $('btn-resume'),
  btnForgetDevice: $('btn-forget-device'),

  tfaView: $('view-twofactor'),
  tfaForm: $('tfa-form'),
  tfaText: $('tfa-text'),
  tfaCode: $('f-tfa-code'),
  tfaExpiry: $('tfa-expiry'),
  tfaAttempts: $('tfa-attempts'),
  tfaError: $('tfa-error'),
  tfaSubmit: $('tfa-submit'),
  tfaBack: $('tfa-back'),

  accountsBar: $('accounts-bar'),
  btnAddAccount: $('btn-add-account'),
  btnTools: $('btn-tools'),
  toolsPopover: $('tools-popover'),
  deviceMenu: $('device-menu'),
  btnDevice: $('btn-device'),
  devicePopover: $('device-popover'),
  deviceStatus: $('device-status'),
  deviceActions: $('device-actions'),
  toolsGroups: $('tools-groups'),
  toolsCount: $('tools-count'),
  btnMcp: $('btn-mcp'),
  mcpPopover: $('mcp-popover'),
  mcpUrl: $('mcp-url'),
  mcpStatus: $('mcp-status'),
  mcpReveal: $('mcp-reveal'),
  mcpConfig: $('mcp-config'),
  mcpError: $('mcp-error'),
  mcpGenerate: $('mcp-generate'),
  mcpCopy: $('mcp-copy'),
  mcpRevoke: $('mcp-revoke'),
  modelSelect: $('model-select'),
  analysisOn: $('f-analysis'),
  analysisModel: $('f-analysis-model'),
  analysisModels: $('analysis-models'),
  analysisHint: $('analysis-hint'),
  btnNewChat: $('btn-new-chat'),
  btnLogout: $('btn-logout'),

  messages: $('messages'),
  emptyState: $('empty-state'),
  emptySub: $('empty-sub'),
  emptyChips: $('empty-chips'),
  queueBar: $('queue-bar'),
  queueText: $('queue-bar-text'),
  queueClear: $('queue-clear'),

  composer: $('composer'),
  input: $('composer-input'),
  send: $('btn-send'),
  stop: $('btn-stop')
};

/* ------------------------------------------------------------------- boot */

api.onUnauthorized((err) => {
  // A coded 401 is a rejected password or 2FA code (login, add account,
  // two-factor) and is shown where it happened. An uncoded one means the
  // session itself is gone.
  if (err && err.code) return;
  if (state.recovering) return;
  if (state.session) {
    state.busy = false;
    // A restart or redeploy drops every session; a device that remembers the
    // sign-in (and can open it without a prompt) picks the chat back up.
    if (canResumeQuietly()) return void recoverSession();
    showLogin('Your SmarterMail session expired. Log in again.');
  }
});

api.trackResumeVersion(() => resume.store.version, onNewerBundle);
api.onActivity(() => profile.touchWarm());

init();

async function init() {
  wireLogin();
  wireTwoFactor();
  wireChat();
  wireRemember();

  el.hostname.value = storage.hostname;
  el.email.value = storage.email;
  el.allowChanges.checked = storage.allowChanges;
  el.key.value = storage.openRouterKey;
  state.disabled = storage.disabledCategories;

  applyAnalysisConfig(await profile.loadConfig());
  await initProfileUi(profileHooks);
  initTasks(taskHooks);
  await loadResumeConfig();

  // A cookie may still be valid (reload / back button).
  try {
    const s = await api.session();
    // A profile unlocked by a passkey in the last half hour (another tab, a
    // reload, a restarted browser) reopens its settings without the key prompt.
    if (s && s.profile && s.profile.unlocked && !profile.hasKeys()) {
      const settings = await profile.reopenWarm(s.profile.id);
      if (settings) applyProfileSettings(settings);
    }
    if (await enterSession(s, 'Signed in to SmarterMail already — paste your OpenRouter key to continue.')) return;
  } catch {
    /* not signed in — normal */
  }

  // No live session. A device that remembers the sign-in resumes it; a locked
  // one waits for the user to unlock it (WebAuthn needs a click).
  if (state.resumeEnabled && resume.store.has() && !resume.store.locked) {
    await resumeAndEnter();
    return;
  }
  showLogin();
}

/**
 * A live SessionResponse: straight into the chat when this tab has the
 * OpenRouter key; otherwise the key alone continues it. The session cookie is
 * shared by every tab (and survives a resume) but the key lives in this tab's
 * sessionStorage, so a new tab, window or browser restart lands on the second
 * branch. False when the session has no accounts.
 */
async function enterSession(s, keyMessage) {
  const first = sessionAccounts(s)[0];
  // A profile session may be empty (every stored account needs a fresh sign-in), but a
  // locked one is not usable until its passkey has been used again.
  if (s && s.profile && !s.profile.unlocked) return false;
  if (!first && !(s && s.profile)) return false;
  if (storage.openRouterKey) {
    await enterChat(s);
    return true;
  }
  showLogin(keyMessage);
  state.resumable = true;
  el.rememberedPanel.hidden = true;
  el.rememberOptions.hidden = true;   // the session exists; its remember setting is already decided
  renderLoginPanel({ resumable: true });
  if (first) {
    el.hostname.value = hostOf(first.baseUrl);
    el.email.value = first.emailAddress || first.username || '';
  }
  el.key.focus();
  return true;
}

/* ------------------------------------------------------ server profiles */

/** What a profile keeps for the chat. */
function currentSettings() {
  return {
    openRouterKey: storage.openRouterKey,
    model: el.modelSelect.value || storage.model || DEFAULT_MODEL,
    toolsOff: [...state.disabled],
    allowChanges: !!state.allowChangesDefault,
    analysis: analysisOn(),
    analysisModel: storage.analysisModel
  };
}

/** Saves the settings to the profile when this is a profile chat and the page holds its keys. */
function syncProfileSettings() {
  if (!state.session || !state.session.profile || !profile.hasKeys()) return;
  profile.saveSettings(currentSettings())
    .catch((err) => addNotice(`Could not save your settings to the profile: ${err.message}`, 'warn'));
}

/** Settings opened from a profile take over this tab's. */
function applyProfileSettings(settings) {
  if (settings.openRouterKey) { storage.openRouterKey = settings.openRouterKey; el.key.value = settings.openRouterKey; }
  if (settings.model) storage.model = settings.model;
  if (Array.isArray(settings.toolsOff)) { state.disabled = new Set(settings.toolsOff); storage.disabledCategories = state.disabled; }
  if (typeof settings.allowChanges === 'boolean') state.allowChangesDefault = settings.allowChanges;
  if (typeof settings.analysis === 'boolean') storage.analysisOff = !settings.analysis;
  if (typeof settings.analysisModel === 'string') storage.analysisModel = settings.analysisModel;
  renderAnalysisSettings();
}

const profileHooks = {
  getSession: () => state.session,
  currentSettings,
  setLoginError: (msg) => setLoginError(msg),
  notice: (text, kind) => {
    if (state.session && !el.chatView.hidden) addNotice(text, kind);
    else state.notices.push([text, kind]);
  },
  closeOtherPopovers: () => { closeToolsPopover(); closeMcpPopover(); closeDevicePopover(); },

  /** Settings: the profile's idle timeout changed (the view's effective value, in minutes). */
  onIdleChanged(minutes) {
    if (!state.session || !state.session.profile) return;
    state.session = { ...state.session, profile: { ...state.session.profile, idleMinutes: minutes } };
    profile.followSession(state.session);
  },

  /** Settings: whether accounts added to this profile chat may make changes by default. */
  getAllowChanges: () => !!state.allowChangesDefault,
  setAllowChanges(value) {
    state.allowChangesDefault = !!value;
    syncProfileSettings();
  },

  /** A passkey or recovery-code sign-in: settings back from the profile, then into the chat. */
  async onSignedIn({ session, skipped, settings }) {
    if (settings) applyProfileSettings(settings);
    forgetDevice();   // a profile chat is remembered by the profile, not by this browser
    const notices = skippedNotices(skipped);
    if (!sessionAccounts(session).length) {
      notices.push(['None of your saved accounts could be signed in. Add them again with "+ Add account"; they stay in your profile.', 'warn']);
    }

    if (state.session && !el.chatView.hidden) {
      await applySession(session);
      for (const [text, kind] of notices) addNotice(text, kind);
      addNotice('Profile unlocked on this page.', 'info');
      return;
    }
    state.notices.push(...notices);
    if (!await enterSession(session, 'Profile opened — paste your OpenRouter key to continue. It will be saved to your profile.')) showLogin();
  },

  /** The chat moved onto a new profile session (just created). */
  async onSession(session) {
    forgetDevice();
    await applySession(session);
  },

  /** The profile is gone (deleted): back to an empty login. */
  onEnded(message) {
    storage.clear();
    state.disabled = new Set();
    state.allowChangesDefault = false;
    el.key.value = '';
    showLogin(message);
  }
};

const taskHooks = {
  getSession: () => state.session,
  getToolList: () => state.toolList,
  currentModel: () => el.modelSelect.value || storage.model || DEFAULT_MODEL,
  notice: (text, kind) => addNotice(text, kind),
  unlock: async () => profileHooks.onSignedIn(await profile.signInWithPasskey())
};

/* ------------------------------------------------------------ login view */

function showLogin(message, webmailHost) {
  el.chatView.hidden = true;
  el.tfaView.hidden = true;
  el.loginView.hidden = false;
  closeToolsPopover();
  setLoginMode(false);
  stopTfaCountdown();
  state.twoFactor = null;
  state.session = null;
  state.resumable = false;
  state.toolList = [];
  state.tools = [];
  state.messages = [];
  resetConversation();
  state.queue = [];
  closeDevicePopover();
  renderRememberedPanel();
  setLoginError(message || '', webmailHost);
  (el.key.value ? el.password : el.key).focus?.();
}

/**
 * The login form doubles as "add an account": the OpenRouter key is already
 * held, Cancel goes back to the chat, and nothing about the conversation is
 * touched.
 */
function setLoginMode(add) {
  state.addMode = add;
  el.fsOpenRouter.hidden = add;
  el.loginCancel.hidden = !add;
  el.loginFooter.hidden = add;
  el.loginTitle.textContent = add ? 'Add an account' : 'SmarterMail Agent';
  el.loginTagline.textContent = add
    ? 'Sign in to another mailbox, a domain admin, or a system admin. It joins this chat; the conversation is kept.'
    : usesProfiles()
      ? 'Chat with your own mailbox. Your server, your key.'
      : 'Chat with your own mailbox. Your server, your key, nothing stored.';
  el.loginLegend.textContent = add ? 'The account to add' : 'Your SmarterMail';
  el.loginSubmit.textContent = add ? 'Add account' : 'Log in';
  // An added account joins the session's own remember setting.
  el.rememberOptions.hidden = add || !state.resumeEnabled;
  if (add) el.rememberedPanel.hidden = true;
  renderLoginPanel({ adding: add, resumable: state.resumable });
}

/** Open the login form in add mode, over the live chat. */
function showAddAccount(message) {
  if (!state.session) return;
  closeToolsPopover();
  setLoginMode(true);
  const first = sessionAccounts(state.session)[0];
  if (!el.hostname.value && first) el.hostname.value = hostOf(first.baseUrl);
  el.email.value = '';
  el.password.value = '';
  // A profile may choose to start new accounts read-write (Profile → Settings); otherwise read-only.
  el.allowChanges.checked = !!(state.session.profile && state.allowChangesDefault);
  setLoginError(message || '');
  el.chatView.hidden = true;
  el.tfaView.hidden = true;
  el.loginView.hidden = false;
  (el.hostname.value ? el.email : el.hostname).focus();
}

/** Back from the add-account form (or its two-factor step) to the chat. */
function returnToChat() {
  stopTfaCountdown();
  state.twoFactor = null;
  el.password.value = '';
  setLoginError('');
  setLoginMode(false);
  el.loginView.hidden = true;
  el.tfaView.hidden = true;
  el.chatView.hidden = false;
  el.input.focus();
}

/**
 * The error line. `webmailHost` adds the link-styled hint used for the 403s
 * that can only be cleared inside SmarterMail webmail itself.
 */
function setLoginError(msg, webmailHost) {
  el.loginError.replaceChildren();
  if (msg) {
    el.loginError.appendChild(document.createTextNode(msg));   // verbatim, as text
    const host = safeHost(webmailHost);
    if (host) {
      const a = document.createElement('a');
      a.className = 'error-link';
      a.href = 'https://' + host + '/';
      a.target = '_blank';
      a.rel = 'noopener noreferrer';
      a.textContent = 'Open your SmarterMail webmail';
      el.loginError.append(document.createElement('br'), a);
    }
  }
  el.loginError.hidden = !msg;
}

/** Hostname the user typed, reduced to something safe to put in an href. */
function safeHost(input) {
  const h = String(input || '').trim().replace(/^https?:\/\//i, '').replace(/[/?#].*$/, '');
  return /^[A-Za-z0-9.\-]+(:\d+)?$/.test(h) ? h : '';
}

function wireLogin() {
  el.loginForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    setLoginError('');

    const hostname = el.hostname.value.trim();
    const email = el.email.value.trim();
    const password = el.password.value;
    const key = el.key.value.trim();
    const readOnly = !el.allowChanges.checked;

    // Signed in already (cookie), only the key missing: resume that session. Typing a
    // password instead means a fresh login, handled below.
    if (state.resumable && !state.addMode && !password) return resumeWithKey(key);

    if (!hostname || !email || !password) return setLoginError('Server, email and password are all required.');
    if (state.addMode) return submitAddAccount({ hostname, email, password, readOnly });
    if (!key) return setLoginError('An OpenRouter API key is required — the chat runs in your browser against your own key.');

    const remember = state.resumeEnabled && el.remember.checked;
    el.loginSubmit.disabled = true;
    el.loginSubmit.textContent = 'Logging in…';
    try {
      // The passkey comes first, while the click still counts as a user gesture
      // (WebAuthn refuses without one); the login round trip could outlast it.
      let lock = null;
      if (remember && state.prf && el.passkeyLock.checked) {
        el.loginSubmit.textContent = 'Creating the passkey…';
        try {
          lock = await resume.createLock();
        } catch (err) {
          setLoginError(passkeyMessage(err, 'Creating the passkey'));
          return;
        }
        el.loginSubmit.textContent = 'Logging in…';
      }

      const s = await api.login({ hostname, email, password, readOnly });
      // The password is never stored, never kept in a variable beyond here.
      el.password.value = '';
      // Saved before the two-factor detour so the key survives into that step.
      storage.openRouterKey = key;
      storage.hostname = hostname;
      storage.email = email;
      storage.allowChanges = !readOnly;
      state.pendingRemember = remember ? { lock } : null;

      if (s && s.twoFactorRequired) {
        showTwoFactor({ ...s, hostname });
        return;
      }
      await applyRememberChoice(s);
      await enterChat(s);
    } catch (err) {
      setLoginError(loginMessage(err), err.status === 403 ? hostname : '');
    } finally {
      el.loginSubmit.disabled = false;
      el.loginSubmit.textContent = 'Log in';
    }
  });
}

async function resumeWithKey(key) {
  if (!key) return setLoginError('Paste your OpenRouter API key to continue — the chat runs in your browser against your own key.');
  el.loginSubmit.disabled = true;
  el.loginSubmit.textContent = 'Continuing…';
  try {
    const s = await api.session();   // still alive? an expired one bounces to the full login
    if (!sessionAccounts(s).length) return showLogin();
    storage.openRouterKey = key;
    await enterChat(s);
    syncProfileSettings();   // a profile chat keeps the key for the next browser
  } catch (err) {
    if (err instanceof api.ApiError && err.status === 401) return showLogin('Your session has expired. Please sign in again.');
    setLoginError(loginMessage(err));
  } finally {
    el.loginSubmit.disabled = false;
    el.loginSubmit.textContent = 'Log in';
  }
}

async function submitAddAccount({ hostname, email, password, readOnly }) {
  el.loginSubmit.disabled = true;
  el.loginSubmit.textContent = 'Adding…';
  try {
    const s = await api.addAccount({ hostname, email, password, readOnly });
    el.password.value = '';
    if (s && s.twoFactorRequired) {
      showTwoFactor({ ...s, hostname, add: true });
      return;
    }
    returnToChat();
    await applySession(s, { added: true });
  } catch (err) {
    // A dead session bounced to the real login view already (uncoded 401).
    if (!state.session) return;
    setLoginError(loginMessage(err), err.status === 403 ? hostname : '');
  } finally {
    el.loginSubmit.disabled = false;
    el.loginSubmit.textContent = state.addMode ? 'Add account' : 'Log in';
  }
}

function loginMessage(err) {
  if (err instanceof api.ApiError) {
    if (err.status === 409 || err.code === 'ACCOUNT_LIMIT') {
      return (err.body && err.body.error) || 'This chat already has as many accounts as it can hold. Remove one first.';
    }
    // The server's own wording is the useful one now that failures carry a code
    // (including 429 HOST_THROTTLED, which says which server and for how long).
    if (err.body && err.body.error) return err.body.error;
    if (err.status === 401) return 'SmarterMail rejected those credentials.';
    if (err.status === 429) return 'Too many login attempts. Wait a minute and try again.';
    if (err.status === 400) return err.message || 'That server address was refused.';
    if (err.status === 0) return err.message;
  }
  return err.message || 'Login failed.';
}

/* -------------------------------------------------------- two-factor view */

/** challenge: { challengeId, method, emailAddress, expiresAt, hostname } */
function showTwoFactor(challenge) {
  state.twoFactor = challenge;
  el.loginView.hidden = true;
  el.chatView.hidden = true;
  el.tfaView.hidden = false;

  const who = challenge.emailAddress || storage.email || 'your account';
  el.tfaText.textContent = challenge.method === 'email'
    ? `SmarterMail emailed a code to ${who}. Enter it below.`
    : `Enter the 6-digit code from your authenticator app for ${who}`;

  el.tfaBack.textContent = challenge.add ? 'Back to the chat' : 'Back to login';

  el.tfaCode.value = '';
  setTfaError('');
  setTfaAttempts(null);
  startTfaCountdown(challenge.expiresAt);
  el.tfaCode.focus();
}

function setTfaError(msg) {
  el.tfaError.textContent = msg || '';
  el.tfaError.hidden = !msg;
}

function setTfaAttempts(n) {
  if (typeof n !== 'number') { el.tfaAttempts.hidden = true; el.tfaAttempts.textContent = ''; return; }
  el.tfaAttempts.textContent = n === 1 ? '1 attempt left before this code is cancelled.' : `${n} attempts left before this code is cancelled.`;
  el.tfaAttempts.hidden = false;
}

function stopTfaCountdown() {
  if (state.tfaTimer) { clearInterval(state.tfaTimer); state.tfaTimer = null; }
}

/** Live "expires in m:ss", falling back to the absolute time if the date is odd. */
function startTfaCountdown(expiresAt) {
  stopTfaCountdown();
  const until = Date.parse(expiresAt || '');
  if (!Number.isFinite(until)) {
    el.tfaExpiry.textContent = 'This code expires shortly.';
    return;
  }
  const tick = () => {
    const left = Math.floor((until - Date.now()) / 1000);
    if (left <= 0) {
      stopTfaCountdown();
      el.tfaExpiry.textContent = 'This code has expired — go back and log in again.';
      return;
    }
    const m = Math.floor(left / 60);
    const s = String(left % 60).padStart(2, '0');
    el.tfaExpiry.textContent = `This code expires in ${m}:${s} (${new Date(until).toLocaleTimeString()}).`;
  };
  tick();
  state.tfaTimer = setInterval(tick, 1000);
}

function wireTwoFactor() {
  // Digits and spaces only while typing; spaces are stripped on submit.
  el.tfaCode.addEventListener('input', () => {
    const cleaned = el.tfaCode.value.replace(/[^0-9 ]/g, '');
    if (cleaned !== el.tfaCode.value) el.tfaCode.value = cleaned;
  });

  el.tfaBack.addEventListener('click', () => {
    if (state.twoFactor && state.twoFactor.add && state.session) return returnToChat();
    showLogin();
    el.password.focus();
  });

  el.tfaForm.addEventListener('submit', async (e) => {
    e.preventDefault();                              // Enter submits through here
    if (!state.twoFactor) return showLogin('That verification step is no longer available. Log in again.');

    const code = el.tfaCode.value.replace(/\s+/g, '');
    if (!code) return setTfaError('Enter the code to continue.');

    setTfaError('');
    el.tfaSubmit.disabled = true;
    el.tfaSubmit.textContent = 'Verifying…';
    try {
      const adding = !!state.twoFactor.add;
      const s = await api.twoFactor(state.twoFactor.challengeId, code);
      stopTfaCountdown();
      state.twoFactor = null;
      el.tfaCode.value = '';
      if (adding && state.session) {
        returnToChat();
        await applySession(s, { added: true });
        return;
      }
      // Same code path as a normal login, including the OpenRouter key check.
      await applyRememberChoice(s);
      if (!storage.openRouterKey) {
        showLogin('Signed in to SmarterMail — paste your OpenRouter key to continue.');
        return;
      }
      await enterChat(s);
    } catch (err) {
      handleTwoFactorError(err);
    } finally {
      el.tfaSubmit.disabled = false;
      el.tfaSubmit.textContent = 'Verify';
    }
  });
}

function handleTwoFactorError(err) {
  const host = state.twoFactor && state.twoFactor.hostname;
  if (err instanceof api.ApiError) {
    if (err.status === 410 || err.code === 'CHALLENGE_EXPIRED') {
      // The challenge is dead. Adding an account: back to the add form, chat
      // intact. Otherwise back to login with the reason shown there.
      if (state.twoFactor && state.twoFactor.add && state.session) {
        stopTfaCountdown();
        state.twoFactor = null;
        showAddAccount(err.message || 'That verification code request expired. Try adding the account again.');
        return;
      }
      showLogin(err.message || 'That verification code request expired. Log in again.');
      el.password.focus();
      return;
    }
    if (err.status === 429) {
      // HOST_THROTTLED carries its own wording (which server, how long); the per-IP
      // limiter's bare 429 has no body, so err.message would only say "HTTP 429".
      setTfaError((err.body && err.body.error) || 'Too many attempts. Wait a minute and try again.');
      return;
    }
    if (err.status === 401) {
      setTfaError(err.message || 'That code was not accepted.');
      setTfaAttempts(err.attemptsLeft);
      el.tfaCode.value = '';
      el.tfaCode.focus();
      return;
    }
  }
  setTfaError(err.message || 'Verification failed.');
}

/* ------------------------------------------------------------- chat view */

async function enterChat(session) {
  stopTfaCountdown();
  state.twoFactor = null;
  state.session = session;
  profile.followSession(session);
  state.queue = [];
  state.toolList = [];
  state.tools = [];
  state.messages = [{ role: 'system', content: systemPrompt() }];
  resetConversation();

  setLoginMode(false);
  renderAccounts();
  closeMcpPopover();
  renderMcpMenu();
  renderDeviceMenu();
  renderServerUi();
  el.loginView.hidden = true;
  el.tfaView.hidden = true;
  el.chatView.hidden = false;
  clearTranscript();
  el.input.focus();
  for (const [text, kind] of state.notices.splice(0)) addNotice(text, kind);

  await loadTools();
  loadModels();
}

/** Profile offer, Profile menu and Tasks button follow the session. */
function renderServerUi() {
  $('composer-hint').textContent = state.session && state.session.profile
    ? 'Enter sends · Shift+Enter for a new line · the conversation is not stored; your profile keeps accounts and settings'
    : 'Enter sends · Shift+Enter for a new line · nothing here is stored anywhere';
  renderOffer();
  renderProfileMenu();
  renderTasksButton();
  refreshTasksBadge();
}

/** A new conversation: a fresh OpenRouter session id, usage totals and artifact store. */
function resetConversation() {
  state.conversationId = null;
  state.usage = null;
  state.artifacts = new ArtifactStore({ threshold: state.analysisDefaults.threshold });
}

function conversationId() {
  if (!state.conversationId) {
    state.conversationId = globalThis.crypto && typeof crypto.randomUUID === 'function'
      ? `sma-${crypto.randomUUID()}`
      : `sma-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
  }
  return state.conversationId;
}

/** The system prompt for the current accounts and Tools-menu selection. */
function systemPrompt() {
  return buildSystemPrompt(state.session, { disabledCategories: presentDisabled(), artifacts: analysisOn() });
}

/** Swap the system prompt in place: the conversation after it is untouched. */
function refreshSystemPrompt() {
  if (!state.session) return;
  const prompt = { role: 'system', content: systemPrompt() };
  if (state.messages[0] && state.messages[0].role === 'system') state.messages[0] = prompt;
  else state.messages.unshift(prompt);
}

/**
 * A new SessionResponse arrived (account added, removed, or dropped by the
 * server's sweeper). Re-render the bar, reload the tool list, swap the system
 * prompt in place and tell the user in the transcript.
 */
async function applySession(next, { added = false, removed = null } = {}) {
  const before = sessionAccounts(state.session);
  const after = sessionAccounts(next);
  state.session = next;
  profile.followSession(next);
  renderAccounts();
  renderMcpMenu();
  renderDeviceMenu();
  renderServerUi();

  const gained = after.filter((a) => !before.some((b) => b.id === a.id));
  const lost = before.filter((b) => !after.some((a) => a.id === b.id));

  await loadTools({ quiet: true });
  refreshSystemPrompt();
  renderEmptyState();

  for (const a of gained) {
    const again = before.some((b) => b.handle === a.handle);
    addNotice(`${again ? 'Signed in again' : 'Added'}: ${accountTitle(a)} — ${roleLabel(a.role)}, ${a.readOnly ? 'read-only' : 'changes allowed'}. ${toolCountText()}`, 'info');
  }
  for (const a of lost) {
    if (gained.some((g) => g.handle === a.handle)) continue;   // replaced, already announced
    const why = removed === a.id ? 'Removed' : 'Signed out by the server (its token could not be refreshed)';
    addNotice(`${why}: ${accountTitle(a)}. ${toolCountText()}`, removed === a.id ? 'info' : 'warn');
  }
  if (added && !gained.length) addNotice(`Account list updated. ${toolCountText()}`, 'info');
}

async function loadTools({ quiet = false } = {}) {
  try {
    const list = await api.tools();
    state.toolList = Array.isArray(list) ? list : [];
    applyToolFilter();
    if (!state.toolList.length && !quiet) addNotice('The server reported no tools. The agent can still chat but cannot read your mail.', 'warn');
  } catch (err) {
    if (err instanceof api.ApiError && err.status === 401) return;
    state.toolList = [];
    state.tools = [];
    state.toolNames = [];
    renderToolsMenu();
    addNotice(`Could not load the tool list: ${err.message}`, 'error');
  }
}

/** Recompute what is sent to OpenRouter from the tool list and the Tools menu. */
function applyToolFilter() {
  const sent = filterToolsByCategory(state.toolList, state.disabled);
  state.toolNames = sent.map((t) => t.name);
  state.tools = toolsToOpenAI(sent);
  renderToolsMenu();
}

/** Switched-off categories that this session actually has. */
function presentDisabled() {
  const present = new Set(toolGroups(state.toolList).flatMap((g) => g.categories.map((c) => c.name)));
  return [...state.disabled].filter((c) => present.has(c));
}

function toolCountText() {
  const total = state.toolList.length;
  const sent = state.tools.length;
  return sent === total
    ? `The agent now has ${total} tool${total === 1 ? '' : 's'}.`
    : `The agent now has ${sent} of ${total} tools (some groups are switched off in Tools).`;
}

/* ---------------------------------------------------------- accounts bar */

const ROLE_CLASS = { User: 'role-user', DomainAdmin: 'role-domain', SysAdmin: 'role-sys' };

/** How an account is named to the user: its email, or a sysadmin's username. */
function accountTitle(a) {
  return (a && (a.role === 'SysAdmin' ? (a.username || a.handle) : (a.emailAddress || a.username || a.handle))) || '';
}

function renderAccounts() {
  const accounts = sessionAccounts(state.session);
  const many = accounts.length > 1;
  el.accountsBar.replaceChildren();
  el.accountsBar.classList.toggle('many', many);

  for (const a of accounts) {
    const li = document.createElement('li');
    li.className = 'acct-chip ' + (ROLE_CLASS[a.role] || 'role-user');
    li.title = `${a.handle} — ${roleLabel(a.role)} on ${hostOf(a.baseUrl)}, ${a.readOnly ? 'read-only' : 'changes allowed'}`;

    // A single ordinary mailbox looks as it always has: no role badge.
    if (many || a.role !== 'User') {
      const role = document.createElement('span');
      role.className = 'acct-role';
      role.textContent = roleLabel(a.role);
      li.appendChild(role);
    }

    const text = document.createElement('span');
    text.className = 'acct-text';
    const name = document.createElement('span');
    name.className = 'acct-name hdr-email';
    name.textContent = accountTitle(a);
    const host = document.createElement('span');
    host.className = 'acct-host hdr-host';
    host.textContent = hostOf(a.baseUrl);
    text.append(name, host);
    li.appendChild(text);

    const badge = document.createElement('span');
    badge.className = 'badge ' + (a.readOnly ? 'badge-readonly' : 'badge-write');
    badge.textContent = a.readOnly ? (many ? 'RO' : 'read-only') : (many ? 'RW' : 'changes allowed');
    badge.title = a.readOnly ? 'This account only has read-only tools' : 'This account can make changes';
    li.appendChild(badge);

    // The last account goes with "Log out"; an × on it would do the same thing. In a profile
    // chat it would not (the profile and its other settings stay), so it is offered there too.
    if (many || (state.session && state.session.profile)) {
      const x = document.createElement('button');
      x.type = 'button';
      x.className = 'acct-remove';
      x.textContent = '×';
      x.title = state.session && state.session.profile
        ? `Remove ${accountTitle(a)} from this chat and from your profile`
        : `Remove ${accountTitle(a)} from this chat`;
      x.setAttribute('aria-label', `Remove ${accountTitle(a)}`);
      x.disabled = state.busy;
      x.addEventListener('click', () => removeAccount(a));
      li.appendChild(x);
    }

    el.accountsBar.appendChild(li);
  }

  const max = state.session && Number(state.session.maxAccounts);
  const full = Number.isFinite(max) && max > 0 && accounts.length >= max;
  el.btnAddAccount.disabled = full || state.busy;
  el.btnAddAccount.title = full
    ? `This chat already holds ${max} accounts, the most it can. Remove one to add another.`
    : 'Sign in to another mailbox, a domain admin or a system admin in this same chat';

  const admin = accounts.some((a) => !hasMailbox(a.role) || a.role === 'DomainAdmin');
  el.input.placeholder = admin ? 'Ask about your mail, domain or server…' : 'Ask about your mailbox…';
}

async function removeAccount(account) {
  if (state.busy) return;
  const accounts = sessionAccounts(state.session);
  try {
    await api.removeAccount(account.id);
  } catch (err) {
    if (err instanceof api.ApiError && err.status === 401) return;   // already bounced
    if (!(err instanceof api.ApiError && err.status === 404)) {
      addNotice(`Could not remove ${accountTitle(account)}: ${err.message}`, 'error');
      return;
    }
  }
  if (accounts.length <= 1 && !state.session.profile) {
    // The server revoked it; the saved sign-in only held that account.
    forgetDevice();
    showLogin('That was the last account, so the session has ended.');
    return;
  }
  try {
    await applySession(await api.session(), { removed: account.id });
  } catch (err) {
    if (!(err instanceof api.ApiError && err.status === 401)) addNotice(`Could not refresh the accounts: ${err.message}`, 'error');
  }
}

/**
 * Re-read the session after every turn: the server's sweeper drops an account
 * whose token can no longer be refreshed, and it should leave the bar (and
 * the tool list and system prompt) as soon as that is noticed.
 */
async function syncSession() {
  if (!state.session) return;
  let next;
  try {
    next = await api.session();
  } catch {
    return;   // 401 bounced to login already; anything else: try again next turn
  }
  const ids = (s) => sessionAccounts(s).map((a) => `${a.id}:${a.readOnly}`).sort().join('|');
  if (next && ids(next) !== ids(state.session)) await applySession(next);
  else if (next) { state.session = next; profile.followSession(next); renderMcpMenu(); }
  renderDeviceMenu();
  refreshTasksBadge();
}

/* ------------------------------------------- remember me on this device */

/*
 * The server keeps nothing: when remembering is on, this browser holds a
 * sealed bundle (js/resume.js) and presents it after a restart, a redeploy or
 * an expired session. Every resume, and every token refresh on the server,
 * replaces the bundle; api.js reports newer versions and onNewerBundle saves
 * them. A copy that falls behind (the tab was closed as a refresh happened and
 * the session then expired) simply fails to resume: sign in again.
 */

async function loadResumeConfig() {
  try {
    const config = await api.resumeConfig();
    state.resumeEnabled = !!(config && config.enabled);
    state.resumeDays = (config && config.days) || 0;
  } catch {
    state.resumeEnabled = false;   // an older server, or it is unreachable: no option
  }
  // A server that keeps profiles offers those instead of a sign-in held by this browser.
  if (usesProfiles()) state.resumeEnabled = false;
  state.prf = state.resumeEnabled && await resume.prfSupported();
  el.lockOption.hidden = !state.prf || !el.remember.checked;
  if (state.resumeDays) {
    el.rememberHint.textContent = `Stay signed in across restarts, for up to ${state.resumeDays} days. ` +
      'Your password is not kept: this browser holds a sealed token only this service can open. ' +
      'Anyone with this browser profile can use it until you log out.';
  }
  setLoginMode(state.addMode);
}

function wireRemember() {
  el.remember.addEventListener('change', () => {
    el.lockOption.hidden = !state.prf || !el.remember.checked;
    if (!el.remember.checked) el.passkeyLock.checked = false;
  });

  el.btnResume.addEventListener('click', async () => {
    setLoginError('');
    el.btnResume.disabled = true;
    try {
      if (resume.store.locked && !resume.hasKey()) {
        el.rememberedText.textContent = 'Waiting for your passkey…';
        try {
          await resume.unlock();   // inside the click: WebAuthn wants a user gesture
        } catch (err) {
          renderRememberedPanel();
          setLoginError(passkeyMessage(err, 'Unlocking'));
          return;
        }
      }
      await resumeAndEnter();
    } finally {
      el.btnResume.disabled = false;
    }
  });

  el.btnForgetDevice.addEventListener('click', () => {
    forgetDevice();
    renderRememberedPanel();
    setLoginError('This device no longer remembers your sign-in.');
  });

  el.btnDevice.addEventListener('click', (e) => { e.stopPropagation(); closeToolsPopover(); closeMcpPopover(); toggleDevicePopover(); });
  el.devicePopover.addEventListener('click', (e) => e.stopPropagation());
}

/** A saved bundle that can be presented without asking the user for anything. */
function canResumeQuietly() {
  return state.resumeEnabled && resume.store.has() && (!resume.store.locked || resume.hasKey());
}

/**
 * Present the saved bundle. Resolves { session, skipped }, or null when there
 * is nothing to present. A dead bundle (expired, invalid, feature switched off)
 * is discarded before the error is rethrown; a transient failure keeps it.
 *
 * Serialised across tabs with the Web Locks API: two tabs presenting the same
 * bundle at once would rotate it twice, and the loser's failure would discard
 * the winner's fresh copy. Inside the lock a live session (the cookie is shared,
 * so another tab may have just resumed) wins over presenting anything.
 */
async function resumeSaved() {
  const run = async () => {
    try {
      const live = await api.session();
      if (sessionAccounts(live).length) return { session: live, skipped: [] };
    } catch {
      /* no live session: go on */
    }

    const record = resume.store.record;
    if (!record) return null;
    const bundle = await resume.store.open();
    try {
      const r = await api.resume(bundle);
      await resume.store.save(r, { force: true });
      const { bundle: _b, version: _v, rememberedUntil: _u, skipped, ...session } = r;
      return { session, skipped: Array.isArray(skipped) ? skipped : [] };
    } catch (err) {
      if (err instanceof api.ApiError && ['RESUME_EXPIRED', 'RESUME_INVALID', 'RESUME_DISABLED'].includes(err.code)) {
        resume.store.clear(record);
        if (!resume.store.has()) resume.forgetKey();
      }
      throw err;
    }
  };
  const locks = globalThis.navigator && navigator.locks;
  return locks && typeof locks.request === 'function' ? locks.request('sma-resume', run) : run();
}

/** Resume from the saved bundle into the chat (or the key prompt). Failure lands on the login view. */
async function resumeAndEnter() {
  el.rememberedPanel.hidden = false;
  el.rememberedText.textContent = 'Signing you back in on this device…';
  el.btnResume.hidden = true;
  el.btnForgetDevice.hidden = true;
  try {
    const r = await resumeSaved();
    if (!r) return showLogin();
    state.notices.push(...skippedNotices(r.skipped));
    if (!await enterSession(r.session, 'Signed back in on this device — paste your OpenRouter key to continue.')) showLogin();
  } catch (err) {
    showLogin(resumeFailureMessage(err));
  }
}

/**
 * The session died mid-chat (a restart or redeploy, or it idled out). Resume
 * in place: the conversation, tool list and system prompt carry on.
 */
function recoverSession() {
  if (state.recovering) return state.recovering;
  state.recovering = (async () => {
    try {
      const r = await resumeSaved();
      if (!r) throw new Error('');
      state.session = r.session;
      renderAccounts();
      renderDeviceMenu();
      await loadTools({ quiet: true });
      refreshSystemPrompt();
      renderEmptyState();
      addNotice('The server had lost this session, so this device signed you back in. If the last answer stopped short, send your message again.', 'info');
      for (const [text, kind] of skippedNotices(r.skipped)) addNotice(text, kind);
    } catch (err) {
      showLogin(resumeFailureMessage(err) || 'Your SmarterMail session expired. Log in again.');
    } finally {
      state.recovering = null;
    }
  })();
  return state.recovering;
}

function resumeFailureMessage(err) {
  if (err instanceof resume.LockedError) return 'Unlock the saved sign-in with your passkey, or sign in below.';
  if (err instanceof api.ApiError) {
    if (['RESUME_EXPIRED', 'RESUME_INVALID', 'RESUME_DISABLED'].includes(err.code)) {
      return (err.body && err.body.error) || 'Your saved sign-in on this device has expired. Please sign in again.';
    }
    if (err.status === 429 && !err.code) return 'Too many attempts. Wait a minute, then choose "Sign back in".';
    if (err.body && err.body.error) return err.body.error;
    if (err.status === 0) return err.message;
  }
  // A failed decrypt (the passkey's key does not open this copy) or anything unexpected.
  return err && err.message ? `Could not sign back in on this device: ${err.message}` : '';
}

function skippedNotices(skipped) {
  const why = {
    REJECTED: 'its mail server no longer accepts the saved sign-in',
    EXPIRED: 'its saved sign-in had expired',
    UNAVAILABLE: 'its mail server did not answer',
    BLOCKED_HOST: 'its server address is no longer allowed',
    ACCOUNT_LIMIT: 'this chat is full',
    THROTTLED: 'too many failed sign-ins to its server just now; try again later'
  };
  return (skipped || []).map((s) => [
    `Not signed back in: ${s.login} on ${hostOf(s.baseUrl)} — ${why[s.reason] || 'it could not be restored'}. Add it again with "+ Add account".`,
    'warn'
  ]);
}

/**
 * After a password sign-in: remember it on this device if that box was ticked,
 * replacing any older saved sign-in; otherwise drop whatever was saved.
 */
async function applyRememberChoice(session) {
  const choice = state.pendingRemember;
  state.pendingRemember = null;
  if (!choice) {
    forgetDevice();
    return;
  }
  try {
    const r = await api.enableResume();
    await resume.store.save(r, { force: true, lock: choice.lock || null });
    if (session) session.remembered = true;
  } catch (err) {
    state.notices.push([`Could not turn on "Remember me on this device": ${err.message}`, 'warn']);
  }
}

/** Drop the saved sign-in and its in-memory key. The server session is not touched. */
function forgetDevice() {
  resume.store.clear();
  resume.forgetKey();
  el.btnDevice.classList.remove('stale');
}

/** api.js saw X-Resume-Version newer than ours: fetch and save the new bundle. */
let bundleFetch = null;
function onNewerBundle(version) {
  if (!state.resumeEnabled || bundleFetch || version <= resume.store.version) return;
  bundleFetch = (async () => {
    try {
      const r = await api.getResume();
      // False when the saved copy is passkey-locked and this page never unlocked it.
      const saved = await resume.store.save(r);
      el.btnDevice.classList.toggle('stale', !saved && resume.store.locked && r.version > resume.store.version);
      renderDevicePopover();
    } catch {
      /* try again on the next announcement */
    } finally {
      bundleFetch = null;
    }
  })();
}

function renderRememberedPanel() {
  const show = state.resumeEnabled && resume.store.has() && !state.addMode && !state.resumable;
  el.rememberedPanel.hidden = !show;
  if (!show) return;
  const locked = resume.store.locked && !resume.hasKey();
  el.rememberedText.textContent = locked
    ? 'This device remembers your sign-in, locked with a passkey.'
    : 'This device remembers your sign-in.';
  el.btnResume.textContent = locked ? 'Unlock with passkey' : 'Sign back in';
  el.btnResume.hidden = false;
  el.btnForgetDevice.hidden = false;
}

function renderDeviceMenu() {
  el.deviceMenu.hidden = !state.resumeEnabled || !state.session || !!state.session.profile;
  renderDevicePopover();
}

function toggleDevicePopover(open = el.devicePopover.hidden) {
  if (open) renderDevicePopover();
  el.devicePopover.hidden = !open;
  el.btnDevice.setAttribute('aria-expanded', String(open));
  if (open) el.deviceActions.querySelector('button')?.focus();
}

function closeDevicePopover() {
  if (!el.devicePopover.hidden) toggleDevicePopover(false);
}

/** Status line + actions: remember on/off, passkey lock on/off. */
function renderDevicePopover() {
  if (!state.session) return;
  const remembered = !!state.session.remembered && resume.store.has();
  const locked = remembered && resume.store.locked;
  const stale = el.btnDevice.classList.contains('stale');

  el.deviceStatus.textContent = !remembered
    ? 'This device does not keep you signed in: a restart, or 30 minutes idle, means signing in again.'
    : `This device keeps you signed in${state.resumeDays ? ` for up to ${state.resumeDays} days from your sign-in` : ''}` +
      (locked ? ', locked with a passkey.' : '. Anyone using this browser profile can use it until you log out.') +
      (stale ? ' Unlock it to keep the saved copy up to date.' : '');

  const button = (text, fn, primary = false) => {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'btn btn-small ' + (primary ? 'btn-primary' : 'btn-ghost');
    b.textContent = text;
    b.addEventListener('click', () => runDeviceAction(b, fn));
    return b;
  };

  const actions = [];
  if (!remembered) {
    actions.push(button('Remember me on this device', () => rememberHere(false), true));
    if (state.prf) actions.push(button('Remember me, locked with a passkey', () => rememberHere(true)));
  } else {
    if (stale) actions.push(button('Unlock with passkey', unlockHere, true));
    if (locked) actions.push(button('Remove the passkey lock', () => relock(false)));
    else if (state.prf) actions.push(button('Lock it with a passkey', () => relock(true)));
    actions.push(button('Stop remembering this device', stopRemembering));
  }
  el.deviceActions.replaceChildren(...actions);
}

async function runDeviceAction(b, fn) {
  b.disabled = true;
  try {
    await fn();
  } catch (err) {
    addNotice(err instanceof resume.LockedError ? err.message : passkeyMessage(err, 'That'), 'error');
  } finally {
    b.disabled = false;
    renderDeviceMenu();
  }
}

async function rememberHere(withLock) {
  const lock = withLock ? await resume.createLock() : null;   // first: needs the click's gesture
  const r = await api.enableResume();
  await resume.store.save(r, { force: true, lock });
  state.session = { ...state.session, remembered: true };
  addNotice(withLock ? 'This device now keeps you signed in, locked with a passkey.' : 'This device now keeps you signed in.', 'info');
}

/** Add or remove the lock. The live session hands out the current bundle, so no unlock is needed. */
async function relock(withLock) {
  const lock = withLock ? await resume.createLock() : null;
  const r = await api.getResume();
  await resume.store.save(r, { force: true, lock });
  el.btnDevice.classList.remove('stale');
  addNotice(withLock ? 'The saved sign-in is now locked with a passkey.' : 'The passkey lock was removed.', 'info');
}

async function unlockHere() {
  await resume.unlock();
  const r = await api.getResume();
  await resume.store.save(r, { force: true });
  el.btnDevice.classList.remove('stale');
}

async function stopRemembering() {
  await api.disableResume();
  forgetDevice();
  state.session = { ...state.session, remembered: false };
  addNotice('This device no longer keeps you signed in.', 'info');
}

/** WebAuthn failures in words. NotAllowedError is a cancel, a timeout, or no gesture. */
function passkeyMessage(err, what) {
  if (err && err.name === 'NotAllowedError') return `${what} with a passkey was cancelled or timed out. Try again, or untick the passkey option.`;
  if (err && err.name === 'InvalidStateError') return 'This device already has a passkey for this site that cannot be used here.';
  if (err && err.name === 'OperationError') return 'That passkey does not open the saved sign-in on this device.';
  if (err instanceof api.ApiError && err.body && err.body.error) return err.body.error;
  return (err && err.message) || `${what} failed.`;
}

/* ------------------------------------------------- large results (analysis) */

/*
 * Results over the threshold stay in this tab (artifacts.js); the chat model
 * gets a stub and asks analyze_result (subagent.js), which runs a second model
 * with the user's key. The setting lives in sessionStorage, and in the profile
 * when there is one.
 */

/** GET /api/config's `analysis` block, when the server sends one. */
function applyAnalysisConfig(config) {
  const a = config && config.analysis;
  if (a && typeof a.defaultModel === 'string' && a.defaultModel) state.analysisDefaults.model = a.defaultModel;
  if (a && Number.isFinite(a.artifactThresholdChars) && a.artifactThresholdChars > 0) state.analysisDefaults.threshold = a.artifactThresholdChars;
  state.artifacts = new ArtifactStore({ threshold: state.analysisDefaults.threshold });
  renderAnalysisSettings();
}

function analysisOn() { return !storage.analysisOff; }
function analysisModel() { return storage.analysisModel || state.analysisDefaults.model; }

/** The tools sent with a turn: the server's (after the Tools menu), plus analyze_result at the end when on. */
function turnTools() {
  return analysisOn() && state.tools.length ? [...state.tools, ANALYZE_RESULT_FUNCTION] : state.tools;
}

function renderAnalysisSettings() {
  if (!el.analysisOn) return;
  el.analysisOn.checked = analysisOn();
  el.analysisModel.value = storage.analysisModel;
  el.analysisModel.placeholder = state.analysisDefaults.model;
  el.analysisModel.disabled = !analysisOn();
  el.analysisHint.textContent =
    `Results over ${state.analysisDefaults.threshold.toLocaleString('en-US')} characters stay in this tab; the chat model gets a summary ` +
    'and asks the analysis model about them, with your OpenRouter key. Off: such results are cut at 60,000 characters.';
}

function wireAnalysisSettings() {
  if (!el.analysisOn) return;
  el.analysisOn.addEventListener('change', () => {
    storage.analysisOff = !el.analysisOn.checked;
    renderAnalysisSettings();
    refreshSystemPrompt();
    syncProfileSettings();
  });
  el.analysisModel.addEventListener('change', () => {
    storage.analysisModel = el.analysisModel.value.trim();
    renderAnalysisSettings();
    syncProfileSettings();
  });
}

/** analyze_result, run here: the sub-agent's calls and cost go on the tool card. */
async function analyzeLocally(args, call, cards, signal) {
  const ref = cards.get(call.id);
  const panel = ref ? addAnalysisPanel(ref) : null;
  const r = await runAnalyzeResult({
    store: state.artifacts,
    args,
    apiKey: storage.openRouterKey,
    model: analysisModel(),
    signal,
    url: completionsUrl(),
    sessionId: `${conversationId()}-analysis`,
    ui: {
      onStep: (step) => panel && panel.step(step),
      onUsage: (round, total) => {
        state.usage = addUsage(state.usage, round);
        if (panel) panel.usage(total);
      }
    }
  });
  if (panel) panel.finish(r);
  return { isError: r.isError, content: r.content };
}

/** The nested view on an analyze_result card: model, rounds, tokens, cost, and each operator call. */
function addAnalysisPanel(ref) {
  const wrap = document.createElement('div');
  wrap.className = 'tool-section analysis-panel';
  const label = document.createElement('div');
  label.className = 'tool-label';
  label.textContent = 'Analysis';
  const meta = document.createElement('div');
  meta.className = 'analysis-meta';
  meta.textContent = `${analysisModel()} · working…`;
  const list = document.createElement('div');
  list.className = 'analysis-steps';
  wrap.append(label, meta, list);
  ref.body.appendChild(wrap);
  const rows = new Map();
  let totals = null;

  const metaText = (r) => {
    const parts = [r ? r.model : analysisModel()];
    if (r) parts.push(r.mode === 'direct' ? 'read whole' : `${r.rounds} round${r.rounds === 1 ? '' : 's'}, ${r.steps.length} call${r.steps.length === 1 ? '' : 's'}`);
    if (totals) {
      parts.push(`${compactCount(totals.promptTokens)} in / ${compactCount(totals.completionTokens)} out`);
      if (totals.cost) parts.push(formatCost(totals.cost));
    }
    if (r && r.partial) parts.push('partial');
    return parts.join(' · ');
  };

  return {
    step(s) {
      let row = rows.get(s.id);
      if (!row) {
        row = document.createElement('details');
        row.className = 'analysis-step';
        row.appendChild(document.createElement('summary'));
        row.appendChild(document.createElement('pre')).className = 'tool-pre';
        rows.set(s.id, row);
        list.appendChild(row);
      }
      const first = s.done ? String(s.text).split('\n')[0] : 'running…';
      row.classList.toggle('error', !!s.isError);
      row.firstChild.textContent = `${s.op} ${summarise(s.args)} → ${first.length > 90 ? first.slice(0, 90) + '…' : first}`;
      row.lastChild.textContent = s.done ? s.text : '';
      scroll();
    },
    usage(total) { totals = total; meta.textContent = `${metaText(null)} · working…`; },
    finish(r) {
      totals = r.usage || totals;
      meta.textContent = metaText(r);
      const summary = ref.head.querySelector('.tool-summary');
      if (summary && totals && totals.cost) summary.textContent = `${summary.textContent} · ${formatCost(totals.cost)}`;
    }
  };
}

function compactCount(n) {
  if (!Number.isFinite(n)) return '?';
  if (n < 1000) return String(n);
  if (n < 1000000) return `${(n / 1000).toFixed(n < 10000 ? 1 : 0).replace(/\.0$/, '')}k`;
  return `${(n / 1000000).toFixed(1).replace(/\.0$/, '')}M`;
}

function formatCost(c) { return `$${c < 0.01 ? c.toFixed(4) : c.toFixed(3)}`; }

/**
 * The original tool's card: an "artifact r3 · 3.0 MB" badge, and in its
 * details a download of the full result (a Blob URL made on click, never uploaded).
 */
function markArtifact(ref, artifact) {
  const badge = document.createElement('span');
  badge.className = 'tool-artifact';
  badge.textContent = `artifact ${artifact.handle} · ${formatSize(artifact.chars)}`;
  badge.title = 'The full result is kept in this tab; the model got a summary and can ask analyze_result about it.';
  ref.head.insertBefore(badge, ref.head.querySelector('.tool-status'));

  const line = document.createElement('div');
  line.className = 'tool-section artifact-download';
  const a = document.createElement('a');
  a.href = '#';
  a.textContent = `Download the full result (${formatSize(artifact.chars)}${artifact.truncated ? ', truncated' : ''})`;
  a.addEventListener('click', (e) => {
    e.preventDefault();
    const kept = state.artifacts.get(artifact.handle);
    if (kept !== artifact) { a.replaceWith(document.createTextNode('This result is no longer kept in this tab.')); return; }
    const blob = new Blob([kept.body], { type: kept.kind === 'records' ? 'application/json' : 'text/plain' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `${kept.tool}-${kept.handle}.${kept.kind === 'records' ? 'json' : 'txt'}`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
  });
  line.appendChild(a);
  ref.body.appendChild(line);
}

/* ------------------------------------------------------------ tools menu */

function renderToolsMenu() {
  const groups = toolGroups(state.toolList);
  el.toolsGroups.replaceChildren();
  el.btnTools.hidden = !groups.length;

  for (const g of groups) {
    const box = document.createElement('fieldset');
    box.className = 'tools-group';
    const legend = document.createElement('legend');
    legend.textContent = g.label;
    box.appendChild(legend);

    for (const c of g.categories) {
      const label = document.createElement('label');
      label.className = 'tools-option';
      const cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.checked = !state.disabled.has(c.name);
      cb.addEventListener('change', () => {
        if (cb.checked) state.disabled.delete(c.name);
        else state.disabled.add(c.name);
        storage.disabledCategories = state.disabled;
        applyToolFilter();
        refreshSystemPrompt();
        syncProfileSettings();
      });
      const name = document.createElement('span');
      name.textContent = c.name;
      const count = document.createElement('span');
      count.className = 'tools-option-count';
      count.textContent = String(c.count);
      label.append(cb, name, count);
      box.appendChild(label);
    }
    el.toolsGroups.appendChild(box);
  }

  const total = state.toolList.length;
  const sent = state.tools.length;
  el.toolsCount.textContent = `${sent} of ${total} tool${total === 1 ? '' : 's'} sent with each message.`;
  el.btnTools.textContent = sent === total ? 'Tools' : `Tools ${sent}/${total}`;
}

function toggleToolsPopover(open = el.toolsPopover.hidden) {
  el.toolsPopover.hidden = !open;
  el.btnTools.setAttribute('aria-expanded', String(open));
  if (open) el.toolsPopover.querySelector('input')?.focus();
}

function closeToolsPopover() {
  if (!el.toolsPopover.hidden) toggleToolsPopover(false);
}

/* -------------------------------------------------------------- MCP menu */

/**
 * State comes from SessionResponse.mcpToken; the token itself is only in the
 * reveal box, right after generating, and is wiped when the popover closes.
 */
function renderMcpMenu() {
  const t = state.session && state.session.mcpToken;
  const active = !!(t && t.active);
  el.mcpUrl.textContent = api.mcpUrl();
  el.mcpStatus.textContent = active
    ? `A token is active until ${t.expiresAt ? new Date(t.expiresAt).toLocaleString() : 'the session ends'}.`
    : 'No token. Generate one to connect a client.';
  el.mcpGenerate.textContent = active ? 'Replace token' : 'Generate token';
  el.mcpRevoke.hidden = !active;
  el.btnMcp.textContent = active ? 'MCP ●' : 'MCP';
}

function setMcpError(msg) {
  el.mcpError.textContent = msg || '';
  el.mcpError.hidden = !msg;
}

function hideMcpToken() {
  el.mcpConfig.value = '';
  el.mcpReveal.hidden = true;
  el.mcpCopy.hidden = true;
}

function toggleMcpPopover(open = el.mcpPopover.hidden) {
  if (open) closeToolsPopover();
  else hideMcpToken();
  setMcpError('');
  el.mcpPopover.hidden = !open;
  el.btnMcp.setAttribute('aria-expanded', String(open));
  if (open) { renderMcpMenu(); el.mcpGenerate.focus(); }
}

function closeMcpPopover() {
  if (!el.mcpPopover.hidden) toggleMcpPopover(false);
  else hideMcpToken();
}

function setMcpToken(active, expiresAt = null) {
  if (state.session) state.session = { ...state.session, mcpToken: { active, expiresAt: active ? expiresAt : null } };
  renderMcpMenu();
}

async function generateMcpToken() {
  setMcpError('');
  el.mcpGenerate.disabled = true;
  try {
    const { token, expiresAt } = await api.createMcpToken();
    const config = { mcpServers: { smartermail: { url: api.mcpUrl(), headers: { Authorization: `Bearer ${token}` } } } };
    el.mcpConfig.value = JSON.stringify(config, null, 2);
    el.mcpReveal.hidden = false;
    el.mcpCopy.hidden = false;
    setMcpToken(true, expiresAt);
    el.mcpConfig.focus();
    el.mcpConfig.select();
  } catch (err) {
    if (!(err instanceof api.ApiError && err.status === 401)) setMcpError(`Could not create a token: ${err.message}`);
  } finally {
    el.mcpGenerate.disabled = false;
  }
}

async function revokeMcpToken() {
  setMcpError('');
  el.mcpRevoke.disabled = true;
  try {
    await api.revokeMcpToken();
    hideMcpToken();
    setMcpToken(false);
  } catch (err) {
    if (!(err instanceof api.ApiError && err.status === 401)) setMcpError(`Could not revoke the token: ${err.message}`);
  } finally {
    el.mcpRevoke.disabled = false;
  }
}

async function copyMcpConfig() {
  try {
    await navigator.clipboard.writeText(el.mcpConfig.value);
    el.mcpCopy.textContent = 'Copied';
    setTimeout(() => { el.mcpCopy.textContent = 'Copy'; }, 1500);
  } catch {
    el.mcpConfig.select();   // no clipboard permission: leave it selected for Ctrl/Cmd+C
  }
}

/* ----------------------------------------------------------- empty state */

const CHIPS = {
  mailbox: [
    "What's unread in my inbox?",
    'Summarise the last 10 emails I received.',
    "What's on my calendar this week?",
    'Find emails from support about the last invoice.',
    'Which folders have the most unread mail?'
  ],
  domain: [
    'List the users in my domain.',
    'Which aliases does my domain have?'
  ],
  sys: [
    "What's stuck in the spool?",
    'Which domains are on this server?',
    'Are any certificates close to expiring?'
  ]
};

/** Suggestions for the roles present; a single mailbox gets the usual five. */
function renderEmptyState() {
  const accounts = sessionAccounts(state.session);
  const roles = new Set(accounts.map((a) => a.role || 'User'));
  const mailbox = accounts.some((a) => hasMailbox(a.role));
  const domain = roles.has('DomainAdmin');
  const sys = roles.has('SysAdmin');

  let chips;
  if (!domain && !sys) chips = CHIPS.mailbox;
  else {
    chips = [
      ...(mailbox ? CHIPS.mailbox.slice(0, 2) : []),
      ...(domain ? CHIPS.domain : []),
      ...(sys ? CHIPS.sys : [])
    ];
    if (mailbox && sys) chips.push('Find the latest bounce in my inbox, then check that domain on the server.');
  }

  el.emptyChips.replaceChildren(...chips.map((text) => {
    const b = document.createElement('button');
    b.className = 'chip';
    b.type = 'button';
    b.textContent = text;
    return b;
  }));

  const reach = [
    mailbox ? 'your mail, calendar, contacts, tasks and notes' : null,
    domain ? "your domain's users, aliases, DKIM and mailing lists" : null,
    sys ? 'the server itself: domains, spool, security, certificates' : null
  ].filter(Boolean);
  el.emptySub.textContent = reach.length > 1
    ? `The agent can work with ${reach.slice(0, -1).join(', ')} and ${reach[reach.length - 1]} — across ${accounts.length} account${accounts.length === 1 ? '' : 's'}.`
    : `The agent can ${sys ? 'work with' : 'read'} ${reach[0] || 'your mail, calendar, contacts, tasks and notes'} through your own server.`;
}

function loadModels() {
  const current = storage.model || DEFAULT_MODEL;
  setModelOptions([{ id: current, name: current }], current);
  fetchToolModels()
    .then((models) => {
      if (el.analysisModels) {
        el.analysisModels.replaceChildren(...models.map((m) => {
          const o = document.createElement('option');
          o.value = m.id;
          return o;
        }));
      }
      if (!models.some((m) => m.id === current)) models.unshift({ id: current, name: current + ' (current)' });
      setModelOptions(models, current);
    })
    .catch(() => { /* keep the single fallback option */ });
}

function setModelOptions(models, selected) {
  el.modelSelect.replaceChildren();
  for (const m of models) {
    const o = document.createElement('option');
    o.value = m.id;
    o.textContent = m.free ? `${m.name} (free)` : m.name;
    el.modelSelect.appendChild(o);
  }
  el.modelSelect.value = selected;
  if (!el.modelSelect.value && models.length) el.modelSelect.value = models[0].id;
}

function wireChat() {
  wireAnalysisSettings();
  el.modelSelect.addEventListener('change', () => { storage.model = el.modelSelect.value; syncProfileSettings(); });

  el.btnNewChat.addEventListener('click', () => {
    if (state.busy) stopTurn();
    state.messages = state.session ? [{ role: 'system', content: systemPrompt() }] : [];
    resetConversation();
    state.queue = [];
    renderQueue();
    clearTranscript();
    el.input.focus();
  });

  el.btnLogout.addEventListener('click', async () => {
    if (state.busy) stopTurn();
    closeMcpPopover();
    closeProfilePopover();
    const wasProfile = !!(state.session && state.session.profile);
    try { await api.logout(); } catch { /* best effort */ }
    storage.clear();
    renderAnalysisSettings();
    forgetDevice();   // the server revoked the tokens; the saved copy is dead anyway
    profile.lock();
    await profile.forgetWarm();
    state.disabled = new Set();
    state.allowChangesDefault = false;
    el.password.value = '';
    el.key.value = '';
    showLogin(wasProfile
      ? 'Logged out. Your profile stays on this server: sign in with your passkey to come back.'
      : 'Logged out. The session was destroyed on the server.');
  });

  el.btnAddAccount.addEventListener('click', () => showAddAccount());
  el.loginCancel.addEventListener('click', () => returnToChat());

  el.btnTools.addEventListener('click', (e) => { e.stopPropagation(); closeMcpPopover(); closeDevicePopover(); closeProfilePopover(); toggleToolsPopover(); });
  el.toolsPopover.addEventListener('click', (e) => e.stopPropagation());
  el.btnMcp.addEventListener('click', (e) => { e.stopPropagation(); closeToolsPopover(); closeDevicePopover(); closeProfilePopover(); toggleMcpPopover(); });
  el.mcpPopover.addEventListener('click', (e) => e.stopPropagation());
  el.mcpGenerate.addEventListener('click', generateMcpToken);
  el.mcpRevoke.addEventListener('click', revokeMcpToken);
  el.mcpCopy.addEventListener('click', copyMcpConfig);
  document.addEventListener('click', () => { closeToolsPopover(); closeMcpPopover(); closeDevicePopover(); closeProfilePopover(); });
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && !el.toolsPopover.hidden) { closeToolsPopover(); el.btnTools.focus(); }
    if (e.key === 'Escape' && !el.mcpPopover.hidden) { closeMcpPopover(); el.btnMcp.focus(); }
    if (e.key === 'Escape' && !el.devicePopover.hidden) { closeDevicePopover(); el.btnDevice.focus(); }
  });

  el.composer.addEventListener('submit', (e) => { e.preventDefault(); submitInput(); });

  el.input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); submitInput(); }
  });
  el.input.addEventListener('input', autosize);

  el.stop.addEventListener('click', stopTurn);

  el.queueClear.addEventListener('click', () => {
    state.queue = [];
    el.messages.querySelectorAll('.msg.queued').forEach((n) => n.remove());
    renderQueue();
  });

  el.messages.addEventListener('scroll', () => {
    const gap = el.messages.scrollHeight - el.messages.scrollTop - el.messages.clientHeight;
    state.autoScroll = gap < 60;
  });

  // Delegated: the chips are rebuilt whenever the accounts change.
  el.emptyChips.addEventListener('click', (e) => {
    const chip = e.target.closest('.chip');
    if (!chip) return;
    el.input.value = chip.textContent;
    autosize();
    submitInput();
  });
}

function autosize() {
  el.input.style.height = 'auto';
  el.input.style.height = Math.min(el.input.scrollHeight, 180) + 'px';
}

/* ------------------------------------------------------------ transcript */

function clearTranscript() {
  renderEmptyState();
  el.messages.replaceChildren(el.emptyState);
  el.emptyState.hidden = false;
  state.autoScroll = true;
}

function hideEmptyState() { el.emptyState.hidden = true; }

function scroll(force = false) {
  if (force || state.autoScroll) {
    requestAnimationFrame(() => { el.messages.scrollTop = el.messages.scrollHeight; });
  }
}

function addUserBubble(text, queued = false) {
  hideEmptyState();
  const d = document.createElement('div');
  d.className = 'msg user' + (queued ? ' queued' : '');
  d.textContent = text;                       // textContent: never innerHTML for user text
  el.messages.appendChild(d);
  scroll(true);
  return d;
}

function addNotice(text, kind = 'info') {
  hideEmptyState();
  const d = document.createElement('div');
  d.className = 'msg notice ' + kind;
  d.textContent = text;                       // textContent
  el.messages.appendChild(d);
  scroll();
  return d;
}

function addAssistantBubble() {
  hideEmptyState();
  const d = document.createElement('div');
  d.className = 'msg assistant';
  d.innerHTML = '<span class="cursor"></span>';
  el.messages.appendChild(d);
  scroll();
  return d;
}

/** The only place model text reaches innerHTML — via the escape-first renderer. */
function paintAssistant(node, text, streaming) {
  node.innerHTML = renderMarkdown(text) + (streaming ? '<span class="cursor"></span>' : '');
  scroll();
}

function addToolCard(call) {
  hideEmptyState();
  const card = document.createElement('div');
  card.className = 'tool-card';

  const head = document.createElement('div');
  head.className = 'tool-head';
  // The account a call names gets its own tag instead of a slot in the summary.
  const named = call.args && typeof call.args.account === 'string' ? call.args.account : '';
  const as = accountFor(named);
  head.innerHTML =
    '<span class="tool-caret">▶</span>' +
    `<span class="tool-name">${escapeHtml(call.name)}</span>` +
    `<span class="tool-account"${as ? '' : ' hidden'}>${escapeHtml(as)}</span>` +
    `<span class="tool-summary">${escapeHtml(summarise(call.args, !!named))}</span>` +
    '<span class="tool-status running"><span class="spin">◌</span> running</span>';
  head.addEventListener('click', () => card.classList.toggle('open'));

  const body = document.createElement('div');
  body.className = 'tool-body';
  body.appendChild(section('Arguments', prettyJson(call.argsText || '{}')));

  card.append(head, body);
  el.messages.appendChild(card);
  scroll();
  return { card, head, body };
}

function finishToolCard(ref, result, artifact = null) {
  const status = ref.head.querySelector('.tool-status');
  if (result.isError) {
    ref.card.classList.add('error');
    status.className = 'tool-status err';
    status.textContent = '✕ failed';
    ref.card.classList.add('open');           // failures open themselves; successes stay collapsed
  } else {
    status.className = 'tool-status ok';
    status.textContent = '✓ done';
  }
  // The server says which account the call actually ran as; that beats the
  // argument (which may have been left out when only one account qualified).
  const tag = ref.head.querySelector('.tool-account');
  if (result.account && (!tag.hidden || sessionAccounts(state.session).length > 1)) {
    tag.textContent = accountFor(result.account);
    tag.title = `Ran as ${result.account}`;
    tag.hidden = false;
  }
  const text = String(result.content ?? '');
  ref.body.appendChild(section('Result', prettyJson(text)));
  if (artifact) markArtifact(ref, artifact);
  const summary = ref.head.querySelector('.tool-summary');
  if (!result.isError) summary.textContent = summary.textContent || `${text.length} chars`;
  scroll();
}

function section(label, text) {
  const wrap = document.createElement('div');
  wrap.className = 'tool-section';
  const l = document.createElement('div');
  l.className = 'tool-label';
  l.textContent = label;
  const pre = document.createElement('pre');
  pre.className = 'tool-pre';
  pre.textContent = text.length > 20000 ? text.slice(0, 20000) + '\n…[truncated for display]' : text;
  wrap.append(l, pre);
  return wrap;
}

/** A handle shortened for a tool card: "as matt@…", "as sysadmin admin@host". */
function accountFor(handle) {
  if (!handle) return '';
  const a = sessionAccounts(state.session).find((x) => x.handle === handle);
  return 'as ' + (a ? accountTitle(a) + (a.role === 'SysAdmin' ? ` (${roleLabel(a.role)})` : '') : handle);
}

function summarise(args, skipAccount = false) {
  if (!args || typeof args !== 'object') return '';
  const parts = [];
  for (const [k, v] of Object.entries(args)) {
    if (v === null || v === undefined || v === '') continue;
    if (skipAccount && k === 'account') continue;
    let s = typeof v === 'object' ? JSON.stringify(v) : String(v);
    if (s.length > 40) s = s.slice(0, 40) + '…';
    parts.push(`${k}: ${s}`);
    if (parts.length === 3) break;
  }
  return parts.join(', ');
}

/* --------------------------------------------------------------- sending */

function submitInput() {
  const text = el.input.value.trim();
  if (!text) return;
  el.input.value = '';
  autosize();

  if (state.busy) {
    // Queue it; it is delivered as one message when the current turn ends.
    const node = addUserBubble(text, true);
    state.queue.push({ text, node });
    renderQueue();
    return;
  }
  send(text);
}

function renderQueue() {
  const n = state.queue.length;
  el.queueBar.hidden = n === 0;
  el.queueText.textContent = n === 0 ? '' : `${n} message${n === 1 ? '' : 's'} queued — sent when this response finishes`;
}

async function send(text) {
  addUserBubble(text);
  state.messages.push({ role: 'user', content: text });
  await drive();
}

async function drive() {
  setBusy(true);
  const controller = new AbortController();
  state.abort = controller;

  let bubble = null;
  let buffer = '';
  const cards = new Map();

  try {
    await runTurn({
      messages: state.messages,
      tools: turnTools(),
      apiKey: storage.openRouterKey,
      model: el.modelSelect.value || storage.model || DEFAULT_MODEL,
      signal: controller.signal,
      maxToolRounds: MAX_TOOL_ROUNDS,
      url: completionsUrl(),
      sessionId: conversationId(),
      callTool: (name, args) => api.callTool(name, args),
      // analyze_result never reaches the server: it runs here, on this tab's artifacts.
      localTools: { [ANALYZE_RESULT_TOOL.name]: (args, call) => analyzeLocally(args, call, cards, controller.signal) },
      artifacts: analysisOn() ? state.artifacts : null,
      ui: {
        onUsage(round) { state.usage = addUsage(state.usage, round); },
        onAssistantStart() { bubble = null; buffer = ''; },
        onContent(chunk, full) {
          if (!bubble) bubble = addAssistantBubble();
          buffer = full;
          paintAssistant(bubble, buffer, true);
        },
        onAssistantEnd(full) {
          if (bubble) paintAssistant(bubble, full || buffer, false);
          bubble = null;
        },
        onToolStart(call) { cards.set(call.id, addToolCard(call)); },
        onToolEnd(call, result, artifact) {
          const ref = cards.get(call.id);
          if (ref) finishToolCard(ref, result, artifact);
          cards.delete(call.id);
        },
        onNotice(text, kind) { addNotice(text, kind); }
      }
    });
  } catch (err) {
    if (err && err.name === 'AbortError') {
      addNotice('Stopped.', 'info');
    } else if (err instanceof api.ApiError && err.status === 401) {
      // onUnauthorized already bounced to login
    } else {
      addNotice(err.message || String(err), 'error');
    }
  } finally {
    // Any tool card still spinning (aborted mid-flight) gets closed out.
    for (const ref of cards.values()) finishToolCard(ref, { isError: true, content: 'Cancelled.' });
    state.abort = null;
    setBusy(false);
  }

  await syncSession();
  await flushQueue();
}

async function flushQueue() {
  if (!state.queue.length || state.busy) return;
  const items = state.queue.splice(0, state.queue.length);
  for (const q of items) {
    q.node.classList.remove('queued');
  }
  renderQueue();
  const combined = items.map((q) => q.text).join('\n\n');
  state.messages.push({ role: 'user', content: combined });
  await drive();
}

function setBusy(busy) {
  state.busy = busy;
  el.send.hidden = busy;
  el.stop.hidden = !busy;
  el.btnNewChat.disabled = false;
  // Accounts only change between turns, so a turn's tools and prompt hold still.
  if (state.session) renderAccounts();
}

function stopTurn() {
  if (state.abort) state.abort.abort();
}

/* ---------------------------------------------------------------- helpers */

/**
 * Dev-only completions endpoint override (`?llmUrl=./dev-completions`), used by
 * dev/stub-server.mjs to exercise the whole loop without an OpenRouter key.
 *
 * Deliberately refused anywhere but localhost: honouring it in production would
 * let a crafted link redirect the user's OpenRouter key to a third party.
 */
function completionsUrl() {
  const local = ['localhost', '127.0.0.1', '[::1]', ''].includes(location.hostname);
  if (!local) return undefined;
  const v = new URLSearchParams(location.search).get('llmUrl');
  return v || undefined;
}

function hostOf(url) {
  try { return new URL(url).host; } catch { return url || ''; }
}
