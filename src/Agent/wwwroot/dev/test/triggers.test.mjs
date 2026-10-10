/**
 * triggers.test.mjs — the condition editor's pure helpers (path builder,
 * predicate <-> form round trip) and the stub's condition-task endpoints.
 */

import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';

import {
  pathText, parsePath, splitAtItems, relativeTo, valueFor, buildPredicate, modelOf, describeTrigger, schemaFields, EXAMPLES
} from '../../js/triggers.js';
import { evaluatePredicate } from '../stub-predicate.mjs';
import { startStub, call, post, cookieOf, creds } from './stub.mjs';

/* ------------------------------------------------------------ paths */

test('pathText / parsePath round trip, quoting odd names', () => {
  for (const segs of [[], ['a'], ['a', 0, 'b'], ['emails', '*', 'from'], ['odd name', 3], ['x-y', '@id'], ['1abc']]) {
    const text = pathText(segs);
    assert.deepEqual(parsePath(text), segs, text);
  }
  assert.equal(pathText(['emails', 3, 'from']), '$.emails[3].from');
  assert.equal(pathText(['odd name']), '$["odd name"]');
  assert.deepEqual(parsePath('subject'), ['subject']);
  assert.deepEqual(parsePath('$["a\\"b"]'), ['a"b']);
  assert.equal(parsePath('$.a..b'), null);
  assert.equal(parsePath('$.a[x]'), null);
});

test('a clicked value splits into items and the path inside one item', () => {
  assert.deepEqual(splitAtItems(['emails', 3, 'from', 'address']), { items: '$.emails[*]', relative: 'from.address' });
  assert.deepEqual(splitAtItems(['data', 'users', 0]), { items: '$.data.users[*]', relative: '$' });
  assert.deepEqual(splitAtItems(['a', 1, 'b', 2, 'c']), { items: '$.a[1].b[*]', relative: 'c' });
  assert.equal(splitAtItems(['counts', 'waiting']), null);
});

test('relativeTo matches [*] against any index', () => {
  assert.equal(relativeTo('$.emails[*]', ['emails', 7, 'subject']), 'subject');
  assert.equal(relativeTo('$.emails[*]', ['emails', 7]), '$');
  assert.equal(relativeTo('$.emails[*]', ['other', 7, 'subject']), null);
  assert.equal(relativeTo('$.emails[2]', ['emails', 3, 'subject']), null);
});

/* ------------------------------------------------------------ values and predicates */

test('values follow the operator', () => {
  assert.equal(valueFor('gt', '500'), 500);
  assert.equal(valueFor('daysUntilLt', ' 14 '), 14);
  assert.equal(valueFor('contains', '500'), '500');
  assert.equal(valueFor('eq', '500'), 500);
  assert.equal(valueFor('eq', 'true'), true);
  assert.equal(valueFor('eq', 'null'), null);
  assert.equal(valueFor('eq', '"500"'), '500');            // quoted: the string
  assert.equal(valueFor('eq', 'hello'), 'hello');
  assert.equal(valueFor('exists', 'x'), undefined);
});

const ROUND_TRIPS = [
  { path: '$.waiting', op: 'gt', value: 500 },
  { path: '$.error', op: 'exists' },
  { all: [{ path: '$.a', op: 'eq', value: 'x' }, { path: '$.b', op: 'ne', value: '42' }] },
  { any: [{ path: 'from', op: 'matches', value: '@x\\.com$' }, { path: 'subject', op: 'contains', value: 'invoice' }] },
  { count: { items: '$.certificates[*]', where: { path: 'expiration', op: 'daysUntilLt', value: 14 } }, op: 'gte', value: 1 },
  { count: { items: '$.users[*]' }, op: 'gte', value: 1 },
  { new: { items: '$.emails[*]', key: 'uid', where: { any: [{ path: 'subject', op: 'contains', value: 'a' }, { path: 'subject', op: 'contains', value: 'b' }] } } },
  { new: { items: '$.emails[*]' } }
];

test('predicate JSON -> form -> predicate JSON is the identity for what the form can show', () => {
  for (const p of ROUND_TRIPS) assert.deepEqual(buildPredicate(modelOf(p)), p, JSON.stringify(p));
});

test('what the form cannot show stays JSON', () => {
  assert.equal(modelOf({ not: { path: 'a', op: 'exists' } }), null);
  assert.equal(modelOf({ all: [{ any: [{ path: 'a', op: 'exists' }] }] }), null);
  assert.equal(modelOf({ new: { items: '$.x[*]', key: ['a', 'b'] } }), null);
  assert.equal(modelOf({ path: 'a', op: 'bogus', value: 1 }), null);
  assert.equal(modelOf([1]), null);
});

test('the form leaves out empty rows and joins several', () => {
  const m = { mode: 'values', join: 'any', rows: [{ path: '$.a', op: 'gt', value: '1' }, { path: ' ', op: 'gt', value: '' }, { path: '$.b', op: 'exists', value: 'ignored' }] };
  assert.deepEqual(buildPredicate(m), { any: [{ path: '$.a', op: 'gt', value: 1 }, { path: '$.b', op: 'exists' }] });
  assert.equal(buildPredicate({ mode: 'values', rows: [] }), null);
});

test('every example is a predicate the form or the JSON box can carry, and the stub can evaluate', () => {
  for (const ex of EXAMPLES) {
    const m = modelOf(ex.when);
    if (m) assert.deepEqual(buildPredicate(m), ex.when, ex.id);
    assert.equal(evaluatePredicate(ex.when, {}).errors.length, 0, ex.id);
  }
});

test('schema fields skip the injected account', () => {
  const fields = schemaFields({ type: 'object', properties: { account: { type: 'string' }, folderId: { type: 'string' }, take: { type: 'integer' }, flag: { type: 'boolean' }, list: { type: 'array' } }, required: ['folderId', 'account'] });
  assert.deepEqual(fields.map((f) => [f.name, f.type, f.required]), [['folderId', 'string', true], ['take', 'number', false], ['flag', 'boolean', false], ['list', 'json', false]]);
});

test('task card text', () => {
  const now = Date.parse('2026-10-09T12:00:00Z');
  const text = describeTrigger({ everyMinutes: 15, action: 'alert' }, { lastProbeAt: '2026-10-09T11:50:00Z', lastValue: false, probeFailures: 2, firesToday: 1 }, now);
  assert.match(text, /^On condition · every 15 min · emails you an alert · last checked .+ \(false\) · 1 today · 2 failed checks in a row$/);
  assert.match(describeTrigger({ everyMinutes: 5, action: 'run' }, null), /not checked yet/);
});

/* ------------------------------------------------------------ the stub's endpoints */

async function profileSession(stub) {
  const login = await post(stub.base, '/api/auth/login', creds('admin', { readOnly: false }));
  let cookie = cookieOf(login.setCookie);
  await post(stub.base, '/api/accounts', creds('me@example.com', { readOnly: false }), cookie);
  const begin = await post(stub.base, '/api/profile/register/options', {}, cookie);
  const { publicKey } = await crypto.webcrypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, true, ['deriveBits']);
  const spki = Buffer.from(await crypto.webcrypto.subtle.exportKey('spki', publicKey)).toString('base64url');
  const created = await post(stub.base, '/api/profile', {
    ceremonyId: begin.body.ceremonyId, credential: { rawId: 'cred-1' }, wrappedKey: 'AA',
    accountsKey: crypto.randomBytes(32).toString('base64url'), publicKey: spki, encryptedPrivateKey: 'AA'
  }, cookie);
  assert.equal(created.status, 200);
  cookie = cookieOf(created.setCookie);
  const accounts = created.body.accounts;
  for (const a of accounts) await call(stub.base, 'PUT', `/api/profile/accounts/${a.id}/delegation`, { enabled: true }, cookie);
  return { cookie, admin: accounts.find((a) => a.role === 'SysAdmin'), me: accounts.find((a) => a.role === 'User') };
}

test('stub: config, test probe and condition tasks', async () => {
  const stub = await startStub();
  try {
    const config = await call(stub.base, 'GET', '/api/config');
    assert.equal(config.body.tasks.triggers.enabled, true);
    assert.equal(config.body.tasks.triggers.minIntervalMinutes, 5);

    const { cookie, admin, me } = await profileSession(stub);

    const probed = await post(stub.base, '/api/tasks/probe', {
      accountId: admin.id, tool: 'get_ssl_certificates', arguments: {},
      when: { count: { items: '$.certificates[*]', where: { path: 'expiration', op: 'daysUntilLt', value: 14 } }, op: 'gte', value: 1 }
    }, cookie);
    assert.equal(probed.status, 200);
    assert.equal(probed.body.json, true);
    assert.equal(probed.body.evaluation.value, true);
    assert.equal(probed.body.evaluation.matched[0].path, '$.certificates[0]');

    const write = await post(stub.base, '/api/tasks/probe', { accountId: admin.id, tool: 'delete_domain', arguments: { domain: 'x' } }, cookie);
    assert.equal(write.status, 400);
    assert.equal(write.body.code, 'PROBE_INVALID');
    const role = await post(stub.base, '/api/tasks/probe', { accountId: me.id, tool: 'get_domains', arguments: {} }, cookie);
    assert.equal(role.body.code, 'PROBE_INVALID');

    const task = await post(stub.base, '/api/tasks', {
      name: 'Spool', prompt: '', timeZone: 'UTC', accountIds: [admin.id, me.id], model: 'm', emailAccountId: me.id,
      trigger: { probe: { accountId: admin.id, tool: 'get_spool_message_counts', arguments: {} }, everyMinutes: 5, when: { path: '$.waiting', op: 'gt', value: 400 }, action: 'alert' }
    }, cookie);
    assert.equal(task.status, 200, JSON.stringify(task.body));
    assert.equal(task.body.definition.cron, '');
    assert.equal(task.body.nextRunAt, null);
    assert.equal(task.body.trigger.lastProbeAt, null);

    const tooOften = await post(stub.base, '/api/tasks', {
      name: 'x', prompt: 'p', timeZone: 'UTC', accountIds: [admin.id], model: 'm',
      trigger: { probe: { accountId: admin.id, tool: 'get_spool_message_counts', arguments: {} }, everyMinutes: 1, when: { path: '$.waiting', op: 'gt', value: 1 } }
    }, cookie);
    assert.equal(tooOften.status, 400);

    const ran = await post(stub.base, `/api/tasks/${task.body.id}/run`, { dryRun: true }, cookie);
    assert.equal(ran.status, 202);
    const list = await call(stub.base, 'GET', '/api/tasks', undefined, cookie);
    assert.equal(list.body.tasks[0].trigger.lastValue, true);
  } finally {
    await stub.stop();
  }
});
