/**
 * storage.js — sessionStorage only, and only for things that are safe to keep
 * for the lifetime of a browser tab.
 *
 * NEVER stored: the SmarterMail password, tool results, message history,
 * anything about mail content. The SmarterMail session itself lives in an
 * HttpOnly cookie set by the server.
 */

const PREFIX = 'sma.';

function read(key, fallback = '') {
  try {
    const v = sessionStorage.getItem(PREFIX + key);
    return v === null ? fallback : v;
  } catch {
    return fallback;
  }
}

function write(key, value) {
  try {
    if (value === null || value === undefined || value === '') sessionStorage.removeItem(PREFIX + key);
    else sessionStorage.setItem(PREFIX + key, String(value));
  } catch {
    /* private mode / storage disabled — degrade silently */
  }
}

export const DEFAULT_MODEL = 'anthropic/claude-haiku-5.5';

export const storage = {
  get openRouterKey() { return read('orKey'); },
  set openRouterKey(v) { write('orKey', v); },

  get model() { return read('model', DEFAULT_MODEL) || DEFAULT_MODEL; },
  set model(v) { write('model', v); },

  get hostname() { return read('hostname'); },
  set hostname(v) { write('hostname', v); },

  get email() { return read('email'); },
  set email(v) { write('email', v); },

  get allowChanges() { return read('allowChanges') === '1'; },
  set allowChanges(v) { write('allowChanges', v ? '1' : ''); },

  /**
   * Tool categories switched off in the Tools menu. Stored as the ones that are
   * OFF so a category that appears later (a new account role) starts on.
   */
  get disabledCategories() {
    try {
      const v = JSON.parse(read('toolsOff', '[]'));
      return new Set(Array.isArray(v) ? v.filter((x) => typeof x === 'string') : []);
    } catch {
      return new Set();
    }
  },
  set disabledCategories(v) {
    const list = [...(v || [])];
    write('toolsOff', list.length ? JSON.stringify(list) : '');
  },

  /** The model analyze_result runs on; '' = the server's default (GET /api/config). */
  get analysisModel() { return read('analysisModel'); },
  set analysisModel(v) { write('analysisModel', v); },

  /** "Hand large results to an analysis model" switched off (on by default). */
  get analysisOff() { return read('analysisOff') === '1'; },
  set analysisOff(v) { write('analysisOff', v ? '1' : ''); },

  /** Wipe everything this app stored. Called on logout. */
  clear() {
    for (const k of ['orKey', 'model', 'hostname', 'email', 'allowChanges', 'toolsOff', 'analysisModel', 'analysisOff']) write(k, '');
  },

  /** Wipe only the credential-ish bits, keep UI preferences. */
  clearSecrets() {
    write('orKey', '');
  }
};
