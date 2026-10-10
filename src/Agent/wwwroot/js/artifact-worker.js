/**
 * artifact-worker.js — runs the artifact operators (artifact-ops.js) off the
 * page's thread. One worker per analyze_result call holds one artifact; the
 * page terminates it when an operator runs too long (a pattern that backtracks
 * forever) and when the call is over.
 *
 * Loaded as a module worker by URL relative to subagent.js, so under the
 * content-versioned asset path (./v/<version>/js/) like every other module.
 *
 * Messages in:  { type: 'load', kind, body, meta } then { type: 'op', id, op, args }
 * Messages out: { id, ok: true, text } or { id, ok: false, error }
 */

import { prepare, runOp } from './artifact-ops.js';

let data = null;

/** Pure message handler, exported for node tests (worker_threads). */
export function handleMessage(msg) {
  if (!msg || typeof msg !== 'object') return { id: null, ok: false, error: 'Bad message.' };
  if (msg.type === 'load') {
    data = prepare(msg.kind, msg.body, msg.meta || {});
    return { id: msg.id ?? null, ok: true, text: '' };
  }
  if (msg.type === 'op') {
    if (!data) return { id: msg.id, ok: false, error: 'No artifact is loaded.' };
    try {
      return { id: msg.id, ok: true, text: runOp(data, msg.op, msg.args || {}) };
    } catch (e) {
      return { id: msg.id, ok: false, error: (e && e.message) || String(e) };
    }
  }
  return { id: msg.id ?? null, ok: false, error: `Unknown message type ${msg.type}.` };
}

if (typeof WorkerGlobalScope !== 'undefined' && globalThis instanceof WorkerGlobalScope) {
  globalThis.onmessage = (e) => globalThis.postMessage(handleMessage(e.data));
}
