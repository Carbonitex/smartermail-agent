/**
 * stub.mjs — shared harness for the tests that talk to a real
 * dev/stub-server.mjs over a socket. Not a test file itself.
 */

import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const STUB = path.resolve(HERE, '..', 'stub-server.mjs');

// Kept aside at import time, before any test swaps globalThis.fetch for a
// fake: the stub tests talk to a real socket and must not be answered by it.
export const realFetch = globalThis.fetch.bind(globalThis);

/** Spawn stub-server.mjs on a random port and wait for its first log line. */
export function startStub(env = {}) {
  const child = spawn(process.execPath, [STUB], {
    env: { ...process.env, PORT: '0', ALLOW_PRIVATE_HOSTS: 'true', ...env },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  return new Promise((resolve, reject) => {
    let out = '';
    const fail = (e) => { child.kill(); reject(e); };
    const timer = setTimeout(() => fail(new Error('stub did not start: ' + out)), 10000);
    child.stdout.on('data', (c) => {
      out += c;
      const m = /http:\/\/localhost:(\d+)(\/[^\s]*)/.exec(out);
      if (!m) return;
      clearTimeout(timer);
      child.stdout.resume();                       // keep draining so it never blocks
      resolve({
        child,
        base: `http://localhost:${m[1]}${m[2].replace(/\/$/, '')}`,
        stop: () => new Promise((r) => { child.once('exit', r); child.kill(); })
      });
    });
    child.stderr.on('data', (c) => { out += c; });
    child.on('error', fail);
  });
}

/** One request; returns { status, body, setCookie }. */
export async function call(base, method, p, body, cookie) {
  const res = await realFetch(base + p, {
    method,
    headers: {
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...(cookie ? { Cookie: cookie } : {})
    },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null, setCookie: res.headers.get('set-cookie') };
}

export const post = (base, p, body, cookie) => call(base, 'POST', p, body, cookie);

/** `sma_session=<id>` from a Set-Cookie header, ready for a Cookie header. */
export const cookieOf = (setCookie) => {
  const m = /sma_session=([^;]*)/.exec(setCookie || '');
  return m && m[1] ? `sma_session=${m[1]}` : null;
};

export const creds = (email, extra = {}) =>
  ({ hostname: 'mail.example.com', email, password: 'pw', readOnly: true, ...extra });
