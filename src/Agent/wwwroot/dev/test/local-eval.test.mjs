/**
 * local-eval.test.mjs — the local-model evaluation's tasks and scorer, offline:
 *
 *  1. every tool a task names exists in the fixture for its role, and every
 *     argument a predicate reads (`argKeys`) exists in that tool's inputSchema;
 *  2. each task's mocks answer, and the default mock flags unknown tools;
 *  3. scoreRun / summarise / neededTools on hand-made good and bad traces.
 *
 * No node_modules needed.
 */

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { TASKS, DEFAULT_MOCK, mockResult } from '../local-model/tasks.mjs';
import { scoreRun, summarise, neededTools, neededGroups } from '../local-model/score.mjs';

const dir = join(dirname(fileURLToPath(import.meta.url)), '..', 'local-model', 'fixtures');
const FIXTURE_FILE = { user: 'tools-user.json', domainadmin: 'tools-domainadmin.json', sysadmin: 'tools-sysadmin.json', multi: 'tools-multi.json' };
const fixtures = Object.fromEntries(Object.entries(FIXTURE_FILE).map(([role, f]) => {
  const list = JSON.parse(readFileSync(join(dir, f), 'utf8'));
  return [role, new Map(list.map(t => [t.name, t]))];
}));
const names = n => (Array.isArray(n) ? n : [n]);
const byId = id => TASKS.find(t => t.id === id);

/* ------------------------------------------------------------ task integrity */

test('task ids are unique and every task has the required shape', () => {
  assert.equal(new Set(TASKS.map(t => t.id)).size, TASKS.length);
  assert.ok(TASKS.length >= 30);
  for (const t of TASKS) {
    assert.ok(fixtures[t.role], `${t.id}: unknown role ${t.role}`);
    assert.equal(typeof t.prompt, 'string', t.id);
    assert.ok(t.prompt.length > 5, t.id);
    assert.ok(Array.isArray(t.tags) && t.tags.length, `${t.id}: tags`);
    assert.equal(typeof t.mocks, 'object', t.id);
    assert.ok(t.expect, t.id);
    const e = t.expect;
    const requires = (e.calls || []).length || (e.anyOf || []).length || e.noTools || e.forbid;
    assert.ok(requires, `${t.id}: expects nothing`);
    for (const c of e.calls || []) {
      assert.ok(c.desc, `${t.id}: a call without desc`);
      if (c.args) assert.ok(Array.isArray(c.argKeys) && c.argKeys.length, `${t.id}: ${c.desc} has an args predicate but no argKeys`);
    }
  }
});

test('every tool named in a task exists in its role fixture', () => {
  for (const t of TASKS) {
    const tools = fixtures[t.role];
    const used = new Set(Object.keys(t.mocks));
    for (const c of t.expect.calls || []) names(c.name).forEach(n => used.add(n));
    for (const g of t.expect.anyOf || []) g.forEach(n => used.add(n));
    if (Array.isArray(t.expect.forbid)) t.expect.forbid.forEach(n => used.add(n));
    for (const n of used) assert.ok(tools.has(n), `${t.id}: tool ${n} is not in tools-${t.role}.json`);
  }
});

test('every argKey exists in the inputSchema of at least one of the call\'s tools', () => {
  for (const t of TASKS) {
    const tools = fixtures[t.role];
    for (const c of t.expect.calls || []) {
      for (const key of c.argKeys || []) {
        const ok = names(c.name).some(n => key in (tools.get(n)?.inputSchema?.properties || {}));
        assert.ok(ok, `${t.id}: argument ${key} is not in the schema of ${names(c.name).join(' | ')}`);
      }
    }
  }
});

test('write tools named in a task are consistent with its tags', () => {
  for (const t of TASKS) {
    const tools = fixtures[t.role];
    const needsWrite = (t.expect.calls || []).some(c => names(c.name).every(n => tools.get(n)?.write));
    if (needsWrite) assert.ok(t.tags.includes('write'), `${t.id}: calls a write tool but is not tagged 'write'`);
  }
  // a delete must be asked for in words
  for (const t of TASKS) {
    const tools = fixtures[t.role];
    const destructive = (t.expect.calls || []).some(c => names(c.name).every(n => tools.get(n)?.destructive));
    if (destructive) assert.match(t.prompt, /delete|remove|get rid|gone/i, t.id);
  }
});

test('the role mix covers every role and the negative cases', () => {
  const count = r => TASKS.filter(t => t.role === r).length;
  assert.ok(count('user') >= 12);
  assert.ok(count('domainadmin') >= 7);
  assert.ok(count('sysadmin') >= 4);
  assert.ok(count('multi') >= 2);
  assert.ok(TASKS.filter(t => t.tags.includes('negative')).length >= 3);
  assert.ok(TASKS.filter(t => t.tags.includes('multistep')).length >= 4);
});

test('each required call has a mock, and mocks return a result', () => {
  for (const t of TASKS) {
    for (const c of t.expect.calls || []) {
      assert.ok(names(c.name).some(n => t.mocks[n]), `${t.id}: no mock for ${names(c.name).join(' | ')}`);
    }
    for (const [n, fn] of Object.entries(t.mocks)) {
      const r = fn({ folderId: 'x', type: 1, uid: 1, domain: 'example.com', mailingListId: 'ml-101' });
      assert.notEqual(r, undefined, `${t.id}: mock ${n} returned undefined`);
    }
  }
});

test('the default mock names the unexpected tool and flags an error', () => {
  const r = DEFAULT_MOCK('get_domains');
  assert.equal(r.isError, true);
  assert.match(r.content, /get_domains/);
  const t = byId('u-flag');
  assert.equal(mockResult(t, 'set_email_properties', {}).success, true);
  assert.equal(mockResult(t, 'remove_emails', {}).isError, true);
});

/* ------------------------------------------------------------ scoring */

const call = (name, args = {}, isError = false) => ({ name, args, isError });
const trace = (calls, finalText = 'Done.', extra = {}) => ({ calls, finalText, rounds: calls.length + 1, ...extra });

test('every task\'s own ideal run scores as a pass (smoke: predicates accept what they should)', () => {
  const good = {
    'u-unread-summary': trace([call('get_emails', { folderId: 'user@example.com/Inbox', query: 'is:unread' })], 'You have unread mail: Q3 budget from Alice.'),
    'u-search-sender': trace([call('get_emails', { folderId: 'user@example.com/Inbox', query: 'from:billing@example.org' })], 'Invoice 1042 for October.'),
    'u-read-latest': trace([call('get_emails', { folderId: 'user@example.com/Inbox', query: 'Alice' }), call('get_email_message', { uid: 4412, folderId: 'user@example.com/Inbox' })], 'She wants the Q3 budget.'),
    'u-attachments': trace([call('get_email_attachments', { uid: 3021, folderId: 'user@example.com/Inbox' })], 'Yes: invoice-1042.pdf.'),
    'u-flag': trace([call('set_email_properties', { uid: '3021', folderId: 'user@example.com/Inbox', flagged: true })]),
    'u-move-to-folder': trace([call('list_folder_info_by_type', { type: 1 }), call('move_emails', { uid: '3021', srcFolderId: 'user@example.com/Inbox', destFolderId: 'user@example.com/Receipts' })]),
    'u-delete-explicit': trace([call('remove_emails', { uid: '3055', folderId: 'user@example.com/Inbox' })]),
    'u-send-email': trace([call('send_email', { to: 'carol@example.org', subject: 'Lunch Friday', body: 'Are you free for lunch on Friday at noon?' })]),
    'u-reply-latest': trace([call('get_emails', { folderId: 'Inbox', query: 'dave' }), call('reply_to_email', { uid: 5120, folderId: 'user@example.com/Inbox', body: 'I will be there at 10.', replyAll: false })]),
    'u-calendar-event': trace([call('list_folder_info_by_type', { type: 2 }), call('new_or_update_calendar_event', { folderId: 'cal-1001', subject: 'Dentist', startTime: '2026-10-22T15:00:00', endTime: '2026-10-22T16:00:00' })]),
    'u-add-contact': trace([call('list_folder_info_by_type', { type: 3 }), call('create_contact', { folderId: 'contacts-2001', firstName: 'Erin', lastName: 'Example', email: 'erin@example.org' })]),
    'u-contact-then-email': trace([call('get_contacts', { folderId: 'contacts-2001', searchQuery: 'Frank' }), call('send_email', { to: 'frank@example.org', subject: 'Meeting', body: 'The meeting moved to 2pm.' })]),
    'u-forward-rule': trace([call('create_content_filter_simple', { name: 'Boss to PA', when: 'from equals boss@example.org', then: 'forward to pa@example.org' })]),
    'u-add-task': trace([call('new_or_update_task', { subject: 'Renew domain', due: '2026-11-01 09:00' })]),
    'u-notes-search': trace([call('search_items', { section: 'Notes', searchQuery: 'wifi' })], 'Note says SSID ExampleGuest.'),
    'u-list-folders': trace([call('list_folder_info_by_type', { type: 1 })], 'Inbox, Receipts, Drafts.'),
    'd-list-users': trace([call('domain_list_users', {})], 'alice, bob, domainadmin.'),
    'd-create-user': trace([call('domain_create_user', { username: 'jamie', password: 'Tr0ub4dor&3x!', fullName: 'Jamie Example' })]),
    'd-create-alias': trace([call('domain_create_alias', { name: 'sales', targets: 'alice@example.com,bob@example.com' })]),
    'd-reset-password': trace([call('domain_update_user', { username: 'alice', password: 'N3w-Passw0rd#2026' })]),
    'd-spam-settings': trace([call('domain_get_spam_settings', {})], 'High spam is deleted at 15.'),
    'd-dkim-check': trace([call('domain_get_dkim', {})], 'Yes, DKIM is enabled and verified.'),
    'd-mailing-lists': trace([call('domain_list_mailing_lists', {}), call('domain_get_mailing_list', { mailingListId: 'ml-101' })], 'team@example.com has 12 subscribers.'),
    'd-set-forwarding': trace([call('domain_set_mailbox_forwarding', { username: 'alice', forwardTo: 'alice.personal@example.org', deleteOnForward: false })]),
    'd-mailbox-unread': trace([call('get_emails', { folderId: 'Inbox', query: 'is:unread' })], 'Q3 budget is unread.'),
    'd-mailbox-send': trace([call('send_email', { to: 'alice@example.com', subject: 'Mailbox ready', body: 'Your mailbox is ready.' })]),
    's-list-domains': trace([call('get_domains', {})], 'example.com and example.org.'),
    's-spool-count': trace([call('get_spool_message_counts', {})], '14 messages.'),
    's-dkim-settings': trace([call('get_dkim_settings', { domain: 'example.com' })], 'Enabled, relaxed canonicalization.'),
    's-server-version': trace([call('get_server_version', {})], 'Version 100.0.9001.'),
    's-users-first-domain': trace([call('get_domains', {}), call('list_users', { domain: 'example.com' })], 'alice and bob.'),
    'm-inbox-user': trace([call('get_emails', { folderId: 'Inbox', account: 'user@example.com' })], 'Q3 budget is unread.'),
    'm-spool-and-inbox': trace([call('get_emails', { folderId: 'Inbox' }), call('get_spool_message_counts', { account: 'sysadmin:admin@mail.example.com' })], 'Spool has 14.'),
    'm-email-domains': trace([call('get_domains', {}), call('send_email', { to: 'user@example.com', subject: 'Domains', body: 'example.com, example.org' })]),
    'n-no-tools': trace([], 'DomainKeys Identified Mail.'),
    'n-ambiguous-delete': trace([], 'Which email do you mean? Tell me the sender or subject.'),
    'n-impossible': trace([], 'I can\'t set an out-of-office auto-reply with the tools I have.')
  };
  for (const t of TASKS) {
    assert.ok(good[t.id], `no ideal trace for ${t.id}`);
    const s = scoreRun(t, good[t.id]);
    assert.ok(s.pass, `${t.id}: ${JSON.stringify(s.checks.filter(c => !c.ok))}`);
    assert.equal(s.firstToolCorrect, true, t.id);
  }
});

test('scoreRun: missing and wrong-argument calls fail with a useful reason', () => {
  const t = byId('u-send-email');
  const none = scoreRun(t, trace([]));
  assert.equal(none.pass, false);
  assert.equal(none.allRequiredCalled, false);
  assert.equal(none.firstToolCorrect, false);

  const wrong = scoreRun(t, trace([call('send_email', { to: 'someone@example.org', subject: 'Lunch Friday', body: 'lunch?' })]));
  assert.equal(wrong.pass, false);
  assert.equal(wrong.allRequiredCalled, true);
  assert.equal(wrong.argsValid, false);
  assert.equal(wrong.firstToolCorrect, true);
  assert.match(wrong.checks.find(c => !c.ok).detail, /arguments did not match/);

  const otherTool = scoreRun(t, trace([call('create_draft', { to: 'carol@example.org', subject: 'Lunch Friday', body: 'lunch' })]));
  assert.equal(otherTool.firstToolCorrect, false);
});

test('scoreRun: alternatives in name arrays and a throwing predicate', () => {
  const t = byId('u-search-sender');
  assert.ok(scoreRun(t, trace([call('search_items', { section: 'Email', searchQuery: 'BILLING@example.org' })], 'Invoice 1042.')).pass);
  const throwing = { ...t, expect: { ...t.expect, calls: [{ name: 'get_emails', args: () => { throw new Error('x'); }, desc: 'boom' }] } };
  assert.equal(scoreRun(throwing, trace([call('get_emails', {})])).argsValid, false);
});

test('scoreRun: ordered tasks reject the reverse order, unordered ones accept it', () => {
  const ordered = byId('u-move-to-folder');
  const reversed = trace([
    call('move_emails', { uid: '3021', srcFolderId: 'Inbox', destFolderId: 'Receipts' }),
    call('list_folder_info_by_type', { type: 1 })
  ]);
  const s = scoreRun(ordered, reversed);
  assert.equal(s.pass, false);
  assert.equal(s.allRequiredCalled, true);
  assert.ok(s.checks.some(c => c.name === 'order' && !c.ok));

  const unordered = byId('m-spool-and-inbox');
  const swapped = trace([call('get_spool_message_counts', {}), call('get_emails', { folderId: 'Inbox' })], '14 in the spool.');
  assert.ok(scoreRun(unordered, swapped).pass);
});

test('scoreRun: forbidden calls, destructive calls on the wrong uid, maxCalls', () => {
  const del = byId('u-delete-explicit');
  const wrongUid = scoreRun(del, trace([call('remove_emails', { uid: '3055', folderId: 'Inbox' }), call('remove_emails', { uid: '9999', folderId: 'Inbox' })]));
  assert.equal(wrongUid.forbiddenCalled, true);
  assert.equal(wrongUid.pass, false);

  const flag = byId('u-flag');
  const deleted = scoreRun(flag, trace([call('set_email_properties', { uid: '3021', folderId: 'Inbox', flagged: true }), call('remove_emails', { uid: '3021', folderId: 'Inbox' })]));
  assert.equal(deleted.forbiddenCalled, true);
  assert.equal(deleted.pass, false);

  const loopy = scoreRun(flag, trace(Array.from({ length: 5 }, () => call('set_email_properties', { uid: '3021', folderId: 'Inbox', flagged: true }))));
  assert.equal(loopy.pass, false);
  assert.ok(loopy.checks.some(c => /at most/.test(c.name) && !c.ok));
});

test('scoreRun: negative scenarios', () => {
  const none = byId('n-no-tools');
  assert.equal(scoreRun(none, trace([call('get_user_data', {})], 'DomainKeys Identified Mail.')).pass, false);

  const amb = byId('n-ambiguous-delete');
  assert.equal(scoreRun(amb, trace([call('remove_emails', { uid: '1', folderId: 'Inbox' })], 'Deleted.')).pass, false);
  assert.ok(scoreRun(amb, trace([call('get_emails', { folderId: 'Inbox' })], 'Which of these emails should I delete?')).pass);
  assert.equal(scoreRun(amb, trace([], 'Done.')).pass, false);

  const imp = byId('n-impossible');
  assert.equal(scoreRun(imp, trace([call('create_content_filter_simple', { name: 'x', when: 'x', then: 'x' })], 'I cannot do that.')).pass, false);
  assert.equal(scoreRun(imp, trace([], 'Sure, it is on!')).pass, false);
});

test('scoreRun: errors, empty answers and answer predicates', () => {
  const t = byId('s-list-domains');
  const base = [call('get_domains', {})];
  assert.equal(scoreRun(t, trace(base, 'example.com and example.org', { error: 'boom' })).pass, false);
  assert.equal(scoreRun(t, trace(base, '')).pass, false);
  assert.equal(scoreRun(t, trace(base, 'just example.com')).pass, false);
  assert.ok(scoreRun(t, trace(base, 'Both: Example.com, example.org')).pass);
  const withError = scoreRun(t, trace([call('get_domains', {}), call('get_dashboard_stats', {}, true)], 'example.com, example.org'));
  assert.equal(withError.errorCalls, 1);
  assert.deepEqual(withError.strayCalls, ['get_dashboard_stats']);
});

test('multi-account: a wrong account value fails, an omitted one passes', () => {
  const t = byId('m-spool-and-inbox');
  const ok = trace([call('get_emails', { folderId: 'Inbox' }), call('get_spool_message_counts', {})], '14');
  assert.ok(scoreRun(t, ok).pass);
  const bad = trace([call('get_emails', { folderId: 'Inbox' }), call('get_spool_message_counts', { account: 'user@example.com' })], '14');
  assert.equal(scoreRun(t, bad).argsValid, false);
});

/* ------------------------------------------------------------ neededTools, summarise */

test('neededTools and neededGroups', () => {
  assert.deepEqual([...neededTools(byId('u-move-to-folder'))].sort(), ['list_folder_info_by_type', 'move_emails']);
  assert.deepEqual([...neededTools(byId('u-search-sender'))].sort(), ['get_emails', 'search_items']);
  assert.equal(neededTools(byId('n-no-tools')).size, 0);
  assert.deepEqual(neededGroups(byId('u-read-latest')), [['get_emails', 'search_items'], ['get_email_message']]);
  const withAnyOf = { expect: { calls: [{ name: 'a' }], anyOf: [['b', 'c']] } };
  assert.deepEqual([...neededTools(withAnyOf)].sort(), ['a', 'b', 'c']);
  assert.deepEqual(neededGroups(withAnyOf), [['a'], ['b', 'c']]);
  assert.ok(TASKS.filter(t => !t.expect.noTools && t.expect.calls).every(t => neededTools(t).size > 0));
});

test('summarise aggregates by role and tag', () => {
  const pass = byId('s-list-domains'), fail = byId('u-flag'), read = byId('u-list-folders');
  const out = summarise([
    { task: pass, trace: trace([call('get_domains', {})], 'example.com example.org') },
    { task: fail, trace: trace([], 'nothing') },
    { task: read, trace: trace([call('list_folder_info_by_type', { type: 1 })], 'Receipts and Inbox'), }
  ]);
  assert.equal(out.total, 3);
  assert.equal(out.passed, 2);
  assert.ok(Math.abs(out.passRate - 2 / 3) < 1e-9);
  assert.equal(out.byRole.sysadmin.passRate, 1);
  assert.equal(out.byRole.user.n, 2);
  assert.equal(out.byRole.user.passed, 1);
  assert.equal(out.byTag.read.n, 2);
  assert.equal(out.byTag.write.passRate, 0);
  assert.equal(out.failures.length, 1);
  assert.equal(out.failures[0].id, 'u-flag');
  assert.ok(out.avgCalls > 0);
});
