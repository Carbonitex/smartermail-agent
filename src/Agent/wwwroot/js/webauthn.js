/**
 * webauthn.js — navigator.credentials, with a way past a password manager.
 *
 * Password-manager extensions (LastPass and others) replace the page's
 * navigator.credentials.create/get with their own. Some forward a dismissed
 * prompt to the browser; others reject it, and the ceremony fails instead of
 * reaching the browser's own passkey sheet (iCloud Keychain, Google Password
 * Manager, Windows Hello, a security key).
 *
 * So: when the page's method is not the browser's and the attempt fails, the
 * call is made once more on a pristine navigator.credentials taken from a
 * fresh same-origin iframe. If the extension got into that frame too, the
 * original error stands. A ceremony that went past the extension should stay
 * past it: pass the returned `native` on to its follow-up get(), so the second
 * prompt is not the extension's again.
 *
 * InvalidStateError ("already registered") is a real answer and never retried.
 */

/** navigator.credentials.create({ publicKey }) → { credential, native }. */
export function createCredential(publicKey, { native = false } = {}) {
  return call('create', publicKey, native);
}

/** navigator.credentials.get({ publicKey }) → { credential, native }. */
export function getCredential(publicKey, { native = false } = {}) {
  return call('get', publicKey, native);
}

async function call(kind, publicKey, forceNative) {
  const creds = globalThis.navigator.credentials;
  const own = creds[kind];
  // Taken synchronously, before any await: an extension that also patches
  // about:blank frames may not have reached a brand-new one yet.
  const frame = pristineFrame();
  const pristine = frame && frame.isNative(frame.creds[kind]) ? frame.creds[kind] : null;
  const viaFrame = async () => ({ credential: await pristine.call(frame.creds, { publicKey }), native: true });
  try {
    if (forceNative && pristine) return await viaFrame();
    const intercepted = !!frame && !frame.isNative(own);
    try {
      return { credential: await own.call(creds, { publicKey }), native: !intercepted };
    } catch (err) {
      if (!intercepted || !pristine || err?.name === 'InvalidStateError') throw err;
      return await viaFrame();
    }
  } finally {
    frame?.dispose();
  }
}

/**
 * A hidden same-origin iframe's navigator.credentials, and a native-code test
 * that uses that realm's toString (a patched Function.prototype.toString in
 * the page cannot lie to it). The frame stays attached until the ceremony
 * ends: a detached frame's promises never settle. Null when there is no DOM.
 */
function pristineFrame() {
  const doc = globalThis.document;
  if (!doc?.createElement) return null;
  let iframe;
  try {
    iframe = doc.createElement('iframe');
    iframe.hidden = true;
    iframe.setAttribute('aria-hidden', 'true');
    iframe.tabIndex = -1;
    (doc.body || doc.documentElement).appendChild(iframe);
    const win = iframe.contentWindow;
    const creds = win?.navigator?.credentials;
    const toString = win?.Function?.prototype?.toString;
    if (!creds || typeof toString !== 'function') throw new Error('no credentials in the frame');
    return {
      creds,
      isNative(fn) {
        try {
          return typeof fn === 'function' && !fn.name.startsWith('bound ') &&
            /^function \w*\(\) \{\s*\[native code\]\s*\}$/.test(toString.call(fn));
        } catch {
          return false;
        }
      },
      dispose() { iframe.remove(); }
    };
  } catch {
    iframe?.remove();
    return null;
  }
}
