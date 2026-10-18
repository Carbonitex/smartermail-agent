import test from 'node:test';
import assert from 'node:assert/strict';

import {
  tokenize, rankTools, selectTools, fuseRankings, calledToolNames, retrievalQuery, buildToolIndex,
  parsePythonicCalls, parseJsonCalls, parseModelOutput,
  coerceArguments, validateToolCall, toolsByName, canonicalJson,
  fitToBudget, estimateTokens, toTemplateMessages,
  createOutputSplitter, createTokenStreamer, createLocalStream, loadLocalModel,
  TOOL_CALL_START as S, TOOL_CALL_END as E, TRANSFORMERS_CDN_URL, TRANSFORMERS_VERSION
} from '../../js/local-llm.js';
import { toolsToOpenAI, runTurn } from '../../js/llm.js';

/* -------------------------------------------------------------- fixtures */

const CONTRACT = [
  { name: 'block_senders', description: 'Block email senders to prevent delivery. Accepts email addresses or domains.', category: 'Settings', write: true,
    inputSchema: { type: 'object', properties: { senders: { type: 'string', description: 'Comma-separated addresses' } }, required: ['senders'] } },
  { name: 'create_contact', description: 'Create a new contact in the specified folder', category: 'Contacts', write: true,
    inputSchema: { type: 'object', properties: { firstName: { type: 'string' }, lastName: { type: 'string' }, email: { type: 'string' } }, required: ['email'] } },
  { name: 'delete_note', description: 'Delete a note', category: 'Notes', write: true,
    inputSchema: { type: 'object', properties: { id: { type: 'string' } }, required: ['id'] } },
  { name: 'get_calendar_events', description: 'List calendar events (meetings, appointments) in a date range', category: 'Calendar', write: false,
    inputSchema: { type: 'object', properties: { start: { type: 'string' }, end: { type: 'string' } } } },
  { name: 'get_contacts', description: 'List contacts in the address book', category: 'Contacts', write: false,
    inputSchema: { type: 'object', properties: { search: { type: 'string' } } } },
  { name: 'get_emails', description: 'Get email metadata like subject, from, date, id, read, flagged from a specific folder with optional search and pagination', category: 'Mail', write: false,
    inputSchema: { type: 'object', properties: { folderId: { type: 'string' }, skip: { type: 'integer' }, take: { type: 'integer' }, query: { type: 'string' } }, required: ['folderId'] } },
  { name: 'get_notes', description: 'List notes', category: 'Notes', write: false, inputSchema: { type: 'object', properties: {} } },
  { name: 'get_tasks', description: 'List tasks (to-dos)', category: 'Tasks', write: false, inputSchema: { type: 'object', properties: {} } },
  { name: 'list_folder_info_by_type', description: 'List mail folders with their ids and counts', category: 'Folders', write: false,
    inputSchema: { type: 'object', properties: { type: { type: 'string', enum: ['Mail', 'Calendar', 'Contacts'] } } } },
  { name: 'move_emails', description: 'Move emails to another folder', category: 'Mail', write: true,
    inputSchema: { type: 'object', properties: { uids: { type: 'array', items: { type: 'integer' } }, folderId: { type: 'string' }, destinationFolderId: { type: 'string' } }, required: ['uids', 'folderId', 'destinationFolderId'] } },
  { name: 'send_email', description: 'Send an email message to recipients', category: 'Mail', write: true,
    inputSchema: { type: 'object', properties: { to: { type: 'string' }, subject: { type: 'string' }, body: { type: 'string' }, isHtml: { type: 'boolean' } }, required: ['to', 'subject', 'body'] } },
  { name: 'set_email_properties', description: 'Mark emails as read/unread, flagged/unflagged, or add tags', category: 'Mail', write: true,
    inputSchema: { type: 'object', properties: { uid: { type: 'integer' }, read: { type: 'boolean' } }, required: ['uid'] } },
  { name: 'analyze_result', description: 'Answer a question about a large tool result kept as an artifact', category: 'Other', write: false,
    inputSchema: { type: 'object', properties: { artifact: { type: 'string' }, question: { type: 'string' } }, required: ['artifact', 'question'] } }
];
const TOOLS = toolsToOpenAI(CONTRACT);
const names = (list) => list.map((t) => (t.function ? t.function.name : t.name));
const user = (content) => ({ role: 'user', content });

/* ------------------------------------------------------------- retrieval */

test('tokenize: splits snake_case and camelCase, drops stop words, stems, names addresses', () => {
  assert.deepEqual(tokenize('get_emails folderId'), ['email', 'folder', 'id']);
  assert.deepEqual(tokenize('Show me the meetings'), ['meet']);
  assert.deepEqual(tokenize('mail jane@example.com about baddomain.com'), ['mail', 'address', 'domain']);
});

test('rankTools: finds the tool by name and description; reads beat writes without a write verb', () => {
  const top = rankTools(TOOLS, 'show my unread emails').map((r) => r.name);
  assert.ok(top.indexOf('get_emails') >= 0 && top.indexOf('get_emails') < 3, top.join());
  assert.equal(rankTools(TOOLS, 'what meetings do I have tomorrow')[0].name, 'get_calendar_events');
  assert.equal(rankTools(TOOLS, 'block spam from baddomain.com')[0].name, 'block_senders');
  // asked to change something, the write tool is not penalised
  assert.equal(rankTools(TOOLS, 'mark that email as read')[0].name, 'set_email_properties');
  assert.deepEqual(rankTools(TOOLS, 'zzz qqq'), []);
});

test('rankTools: function tools with categoryOf rank exactly like contract tools', () => {
  const cat = new Map(CONTRACT.map((t) => [t.name, t.category]));
  for (const q of ['send an email', 'my notes', 'calendar this week']) {
    assert.deepEqual(rankTools(TOOLS, q, { categoryOf: (n) => cat.get(n) }), rankTools(CONTRACT, q));
  }
});

test('selectTools: k tools (plus their lookups, within k + 4), in the input order, the obvious one included', () => {
  const sel = selectTools(TOOLS, [user('Send an email to bob@example.com saying hi')], { k: 4 });
  assert.ok(sel.length <= 4 + 4 && sel.length > 0);
  assert.ok(selectTools(TOOLS, [user('Send an email to bob@example.com saying hi')], { k: 4, companions: 0 }).length <= 4);
  assert.ok(names(sel).includes('send_email'));
  const order = names(TOOLS);
  assert.deepEqual(names(sel), [...names(sel)].sort((a, b) => order.indexOf(a) - order.indexOf(b)));
  assert.ok(sel.every((t) => TOOLS.includes(t)), 'returns the same objects');
});

test('selectTools: keeps tools already called, even when the new message never names them', () => {
  const messages = [
    user('list my inbox'),
    { role: 'assistant', content: '', tool_calls: [{ id: 'c1', type: 'function', function: { name: 'get_emails', arguments: '{}' } }] },
    { role: 'tool', tool_call_id: 'c1', content: '[]' },
    { role: 'assistant', content: 'Nothing there.' },
    user('ok, what about my calendar?')
  ];
  assert.deepEqual(calledToolNames(messages), ['get_emails']);
  const sel = names(selectTools(TOOLS, messages, { k: 3 }));
  assert.ok(sel.includes('get_emails'));
  assert.ok(sel.includes('get_calendar_events'));
});

test('selectTools: analyze_result joins once the history holds an artifact stub; `always` pins', () => {
  const stub = [user('search the logs'), { role: 'tool', tool_call_id: 'x', content: '{"artifact":"r1","tool":"search_log_files"}' }, user('how many failures?')];
  assert.ok(names(selectTools(TOOLS, stub, { k: 3 })).includes('analyze_result'));
  assert.ok(!names(selectTools(TOOLS, [user('how many failures?')], { k: 3 })).includes('analyze_result'));
  assert.ok(names(selectTools(TOOLS, [user('hello')], { k: 3, always: ['get_tasks'] })).includes('get_tasks'));
});

test('selectTools: small lists pass through, nothing matched falls back to the first k, maxTools caps', () => {
  assert.equal(selectTools(TOOLS.slice(0, 3), [user('anything')], { k: 8 }).length, 3);
  assert.deepEqual(names(selectTools(TOOLS, [user('zzz')], { k: 2 })), names(TOOLS.slice(0, 2)));
  const many = [user('x'), { role: 'assistant', content: '', tool_calls: names(TOOLS).map((n, i) => ({ id: `c${i}`, function: { name: n, arguments: '{}' } })) }, user('send email')];
  assert.ok(selectTools(TOOLS, many, { k: 2, maxTools: 5 }).length <= 5);
});

test('selectTools: extra rankings (an embedding ranker) are fused with BM25', () => {
  const sel = names(selectTools(TOOLS, [user('zzz')], { k: 2, rankings: [['get_tasks', 'get_notes']] }));
  assert.deepEqual(sel, ['get_notes', 'get_tasks']);
  assert.deepEqual(fuseRankings([['a', 'b'], ['b', 'c']]), ['b', 'a', 'c']);
});

test('selectTools: the latest user message drives retrieval, not an older topic', () => {
  const messages = [user('show my notes'), { role: 'assistant', content: 'You have none.' }, user('send an email to my boss'), { role: 'assistant', content: 'What should it say?' }, user('that I am late')];
  assert.ok(names(selectTools(TOOLS, messages, { k: 3 })).includes('send_email'));
});

test('tokenize: a hyphenated word also counts joined ("to-do" is "todo")', () => {
  assert.ok(tokenize('my to-do list').includes('todo'));
  assert.ok(tokenize('an e-mail').includes('email'));
});

test('retrievalQuery: quoted values are data, not the request', () => {
  const q = retrievalQuery([user('Add a task "Renew domain" due on Friday')]);
  assert.ok(q.has('task'));
  assert.ok(!q.has('domain') && !q.has('renew'));
});

test('retrievalQuery: email used as a verb asks to send', () => {
  assert.equal(retrievalQuery([user('Email me the list of domains')]).get('send'), 1);
  assert.equal(retrievalQuery([user('Find the users and then mail them to bob@example.com')]).get('send'), 1);
  assert.ok(!retrievalQuery([user('Find the emails from billing@example.org')]).has('send'));
  assert.ok(!retrievalQuery([user('Read the email from Alice')]).has('send'));
});

test('retrievalQuery: a reply that names no tool leans on the request before it', () => {
  const index = buildToolIndex(TOOLS);
  const messages = [user('send an email to bob@example.com saying hi'), { role: 'assistant', content: 'Shall I?' }, user('Yes, go ahead.')];
  assert.ok(retrievalQuery(messages, { index }).get('send') > 0.5);
  assert.equal(retrievalQuery(messages).get('send'), 0.5, 'without an index: the usual half weight');
  const topical = [user('send an email to bob@example.com'), { role: 'assistant', content: 'Sent.' }, user('now show my calendar')];
  assert.equal(retrievalQuery(topical, { index }).get('send'), 0.5);
});

test('rankTools: asked for a change, tools that change things rank above readers', () => {
  // "mark … read" matches get_emails' description too; the change wins.
  const top = rankTools(TOOLS, 'mark my emails as read flagged').map((r) => r.name);
  assert.ok(top.indexOf('set_email_properties') < top.indexOf('get_emails'), top.join());
});

test('selectTools: lookups the chosen tools need come along (folder lister, the category finder)', () => {
  const sel = names(selectTools(TOOLS, [user('move the message with uid 7 to my Archive')], { k: 2 }));
  assert.ok(sel.includes('move_emails'), sel.join());
  assert.ok(sel.includes('list_folder_info_by_type'), `the folder ids: ${sel.join()}`);
  assert.ok(sel.includes('get_emails'), `finds the message: ${sel.join()}`);
  assert.ok(sel.length <= 2 + 4, 'within maxTools');
  const none = names(selectTools(TOOLS, [user('move the message with uid 7 to my Archive')], { k: 2, companions: 0 }));
  assert.ok(none.length <= 2 && none.includes('move_emails'), none.join());
  // a finder needs no lookup of its own
  assert.ok(!names(selectTools(TOOLS, [user('what meetings do I have tomorrow')], { k: 1 })).includes('list_folder_info_by_type'));
});

/* --------------------------------------------------------------- parsing */

test('parsePythonicCalls: keyword arguments, Python literals, several calls', () => {
  const r = parsePythonicCalls(`[get_emails(folderId='a@b.c/Inbox', take=5, unread=True, q=None), send_email(to="x@y.z", tags=['a', "b"], meta={'k': 1, "n": [1.5, -2]})]`);
  assert.equal(r.error, null);
  assert.deepEqual(r.calls, [
    { name: 'get_emails', args: { folderId: 'a@b.c/Inbox', take: 5, unread: true, q: null }, positional: [] },
    { name: 'send_email', args: { to: 'x@y.z', tags: ['a', 'b'], meta: { k: 1, n: [1.5, -2] } }, positional: [] }
  ]);
});

test('parsePythonicCalls: escapes as the template writes them, triple quotes, positional args, bare words', () => {
  const r = parsePythonicCalls(String.raw`[send_email(body='it\'s\nfine \\ ok', subject="""Multi "quoted" line"""), get_note('n1', folder=Inbox)]`);
  assert.equal(r.error, null);
  assert.equal(r.calls[0].args.body, "it's\nfine \\ ok");
  assert.equal(r.calls[0].args.subject, 'Multi "quoted" line');
  assert.deepEqual(r.calls[1], { name: 'get_note', args: { folder: 'Inbox' }, positional: ['n1'] });
});

test('parsePythonicCalls: lenient about a missing [ and a cut-off end, clear about garbage', () => {
  assert.deepEqual(parsePythonicCalls('get_tasks()').calls, [{ name: 'get_tasks', args: {}, positional: [] }]);
  const cut = parsePythonicCalls('[get_emails(folderId="Inbox", take=3');
  assert.equal(cut.error, null);
  assert.deepEqual(cut.calls[0].args, { folderId: 'Inbox', take: 3 });
  assert.match(parsePythonicCalls('[get_emails(folderId="Inbox)]').error, /unterminated string/);
  assert.match(parsePythonicCalls('[not a call]').error, /expected "\("/);
  assert.match(parsePythonicCalls('[f(x=g(1))]').error, /cannot be an argument value/);
});

test('parseJsonCalls: the usual JSON shapes, and an ordinary JSON answer is not a call', () => {
  assert.deepEqual(parseJsonCalls('{"name":"get_tasks","arguments":{}}'), [{ name: 'get_tasks', args: {}, positional: [] }]);
  assert.deepEqual(parseJsonCalls('```json\n[{"name":"a","parameters":{"x":1}},{"function":{"name":"b","arguments":"{\\"y\\":2}"}}]\n```'),
    [{ name: 'a', args: { x: 1 }, positional: [] }, { name: 'b', args: { y: 2 }, positional: [] }]);
  assert.deepEqual(parseJsonCalls('<tool_call>{"name":"a","arguments":{"x":1}}</tool_call>\n<tool_call>{"name":"b","arguments":{}}</tool_call>').map((c) => c.name), ['a', 'b']);
  assert.equal(parseJsonCalls('{"total": 3, "unread": 1}'), null);
  assert.equal(parseJsonCalls('{"name": "Bob", "email": "bob@example.com"}'), null);
  assert.equal(parseJsonCalls('not json'), null);
  assert.match(parseJsonCalls('{"name":"a","arguments":"{broken"}')[0].error, /not valid JSON/);
});

test('parseModelOutput: reasoning, the native call, and the text around it', () => {
  const r = parseModelOutput(`The user wants mail.</think>${S}[get_emails(folderId='me/Inbox', take=3)]${E}Checking your inbox.<|im_end|>`);
  assert.equal(r.reasoning, 'The user wants mail.');
  assert.equal(r.content, 'Checking your inbox.');
  assert.deepEqual(r.calls, [{ name: 'get_emails', args: { folderId: 'me/Inbox', take: 3 }, positional: [] }]);
  assert.deepEqual(r.errors, []);
});

test('parseModelOutput: a plain answer; thinking that never ends is no answer', () => {
  assert.deepEqual(parseModelOutput('ok</think>\n\nYou have **3** messages.'), { content: 'You have **3** messages.', reasoning: 'ok', calls: [], errors: [], truncatedCall: false });
  assert.equal(parseModelOutput('still thinking about it').content, '');
  assert.equal(parseModelOutput('still thinking about it').reasoning, 'still thinking about it');
  assert.equal(parseModelOutput('Plain answer.', { startInThink: false }).content, 'Plain answer.');
  assert.equal(parseModelOutput('<think>x</think>y', { startInThink: false }).content, 'y');
});

test('parseModelOutput: a call straight out of <think>, JSON inside the markers, a truncated call', () => {
  const a = parseModelOutput(`need tasks ${S}[get_tasks()]${E}`);
  assert.equal(a.reasoning, 'need tasks');
  assert.deepEqual(a.calls.map((c) => c.name), ['get_tasks']);
  const b = parseModelOutput(`x</think>${S}{"name":"get_notes","arguments":{}}${E}`);
  assert.deepEqual(b.calls.map((c) => c.name), ['get_notes']);
  const c = parseModelOutput(`x</think>${S}[send_email(to="a@b.c", body="Hel`);
  assert.equal(c.truncatedCall, true);
  assert.equal(c.calls.length, 0);
  assert.match(c.errors[0], /unterminated string/);
});

test('parseModelOutput: bare JSON or marker-less pythonic answers count as calls (known names only)', () => {
  assert.deepEqual(parseModelOutput('x</think>{"name":"get_tasks","arguments":{}}').calls.map((c) => c.name), ['get_tasks']);
  const known = parseModelOutput('x</think>[get_tasks()]', { tools: TOOLS });
  assert.deepEqual([known.content, known.calls.length], ['', 1]);
  const unknown = parseModelOutput('x</think>[print(1)]', { tools: TOOLS });
  assert.deepEqual([unknown.content, unknown.calls.length], ['[print(1)]', 0]);
});

/* ------------------------------------------------------------ validation */

const SCHEMA = {
  type: 'object',
  properties: {
    folderId: { type: 'string' }, take: { type: 'integer' }, ratio: { type: 'number' }, unread: { type: 'boolean' },
    uids: { type: 'array', items: { type: 'integer' } }, to: { type: 'string' }, sort: { type: 'string', enum: ['Date', 'Subject'] },
    filter: { type: 'object', properties: { max: { type: 'integer' } } }, note: { type: ['string', 'null'] }
  },
  required: ['folderId']
};

test('coerceArguments: safe conversions', () => {
  const r = coerceArguments({ folderId: 7, take: '5', ratio: '0.5', unread: 'yes', uids: '42', to: ['a@b.c', 'd@e.f'], sort: 'subject', filter: '{"max":"3"}', note: null }, SCHEMA);
  assert.deepEqual(r.errors, []);
  assert.deepEqual(r.value, { folderId: '7', take: 5, ratio: 0.5, unread: true, uids: [42], to: 'a@b.c, d@e.f', sort: 'Subject', filter: { max: 3 }, note: null });
  assert.ok(r.notes.length >= 5);
});

test('coerceArguments: argument names matched loosely, optional None dropped, JSON array strings parsed', () => {
  const r = coerceArguments({ folder_id: 'Inbox', Take: 2, sort: null, uids: '[1, 2]' }, SCHEMA);
  assert.deepEqual(r.errors, []);
  assert.deepEqual(r.value, { folderId: 'Inbox', take: 2, uids: [1, 2] });
});

test('coerceArguments: what cannot be fixed is an error', () => {
  const r = coerceArguments({ take: 'five', sort: 'Size', uids: ['x'] }, SCHEMA);
  assert.equal(r.errors.length, 4);
  assert.ok(r.errors.some((e) => /missing required argument "folderId"/.test(e)));
  assert.ok(r.errors.some((e) => /take should be integer/.test(e)));
  assert.ok(r.errors.some((e) => /sort must be one of "Date", "Subject"/.test(e)));
  assert.ok(r.errors.some((e) => /uids\[0\] should be integer/.test(e)));
  assert.equal(coerceArguments({ take: 1.5, folderId: 'x' }, SCHEMA).errors.length, 1);
});

test('coerceArguments: additionalProperties false drops unknown arguments, otherwise they pass', () => {
  assert.deepEqual(coerceArguments({ a: 1, b: 2 }, { type: 'object', properties: { a: { type: 'integer' } }, additionalProperties: false }).value, { a: 1 });
  assert.deepEqual(coerceArguments({ a: 1, b: 2 }, { type: 'object', properties: { a: { type: 'integer' } } }).value, { a: 1, b: 2 });
});

test('validateToolCall: unknown tools, positional arguments, readable errors', () => {
  const byName = toolsByName(TOOLS);
  const unknown = validateToolCall({ name: 'get_mail', args: {} }, byName);
  assert.equal(unknown.ok, false);
  assert.match(unknown.error, /no tool named "get_mail"/);
  assert.match(unknown.error, /Did you mean "get_emails"/);

  const pos = validateToolCall({ name: 'get_emails', args: { take: '2' }, positional: ['me/Inbox'] }, byName);
  assert.deepEqual([pos.ok, pos.args], [true, { take: 2, folderId: 'me/Inbox' }]);

  const bad = validateToolCall({ name: 'send_email', args: { to: 'a@b.c' } }, byName);
  assert.equal(bad.ok, false);
  assert.match(bad.error, /missing required argument "subject"/);
  assert.match(bad.error, /- body \(string, required\)/);

  assert.match(validateToolCall({ name: 'get_tasks', args: {}, positional: [1] }, byName).error, /name every argument/);
  assert.match(validateToolCall({ name: 'get_tasks', args: {}, error: 'arguments were not valid JSON' }, byName).error, /could not be read/);
});

test('canonicalJson: key order does not matter', () => {
  assert.equal(canonicalJson({ b: 1, a: { d: [1, { f: 2, e: 3 }], c: null } }), canonicalJson({ a: { c: null, d: [1, { e: 3, f: 2 }] }, b: 1 }));
});

/* ---------------------------------------------------------------- budget */

const big = (n) => 'x'.repeat(n);
function longConversation() {
  return [
    { role: 'system', content: 'sys' },
    user('first'),
    { role: 'assistant', content: '', tool_calls: [{ id: 'a', type: 'function', function: { name: 'get_emails', arguments: '{}' } }] },
    { role: 'tool', tool_call_id: 'a', content: big(20000) },
    { role: 'assistant', content: 'done' },
    user('second'),
    { role: 'assistant', content: '', tool_calls: [{ id: 'b', type: 'function', function: { name: 'get_notes', arguments: '{}' } }] },
    { role: 'tool', tool_call_id: 'b', content: big(9000) },
    { role: 'assistant', content: 'done' },
    user('third'),
    { role: 'assistant', content: '', tool_calls: [{ id: 'c', type: 'function', function: { name: 'get_tasks', arguments: '{}' } }] },
    { role: 'tool', tool_call_id: 'c', content: big(30000) }
  ];
}

test('fitToBudget: nothing to do under budget, and the caller\'s messages are never touched', () => {
  const msgs = longConversation();
  const before = JSON.stringify(msgs);
  const r = fitToBudget(msgs, { budget: 100000 });
  assert.equal(r.steps.length, 0);
  assert.equal(r.overBudget, false);
  fitToBudget(msgs, { budget: 500 });
  assert.equal(JSON.stringify(msgs), before);
});

test('fitToBudget: old results first, then the current turn clamped, then old turns dropped', () => {
  const r1 = fitToBudget(longConversation(), { budget: 12000 });
  assert.equal(r1.overBudget, false);
  assert.match(r1.steps[0], /elided 2 old tool results/);
  assert.match(r1.messages[3].content, /^\[earlier result of get_emails/);
  assert.equal(r1.messages[11].content.length, 30000, 'the current turn kept whole while it fits');

  const r2 = fitToBudget(longConversation(), { budget: 3000 });
  assert.ok(r2.steps.some((s) => /clamped 1 result/.test(s)));
  assert.ok(r2.messages[r2.messages.length - 1].content.length < 30000);

  const r3 = fitToBudget(longConversation(), { budget: 600, extraTokens: 50 });
  assert.ok(r3.steps.includes('dropped the oldest turn'));
  assert.equal(r3.messages[0].role, 'system');
  assert.equal(r3.messages[1].content, 'third');
  // every remaining tool message still answers a call in the remaining history
  const ids = new Set(r3.messages.flatMap((m) => (m.tool_calls || []).map((c) => c.id)));
  assert.ok(r3.messages.filter((m) => m.role === 'tool').every((m) => ids.has(m.tool_call_id)));
});

test('fitToBudget: write results survive elision, and a budget it cannot meet says so', () => {
  const msgs = longConversation();
  msgs[2].tool_calls[0].function.name = 'send_email';
  const r = fitToBudget(msgs, { budget: 12000, isWrite: (n) => n === 'send_email' });
  assert.equal(r.messages[3].content.length, 20000);
  assert.equal(fitToBudget([{ role: 'system', content: big(10000) }, user('hi')], { budget: 100 }).overBudget, true);
  assert.ok(estimateTokens([user(big(320))]) >= 100);
});

/* -------------------------------------------------------------- template */

test('toTemplateMessages: arguments as objects, broken JSON as {}, empty assistant messages dropped', () => {
  const out = toTemplateMessages([
    { role: 'system', content: 's' },
    { role: 'assistant', content: null, tool_calls: [{ id: '1', type: 'function', function: { name: 'a', arguments: '{"x":1}' } }, { id: '2', type: 'function', function: { name: 'b', arguments: '{oops' } }] },
    { role: 'tool', tool_call_id: '1', content: { ok: true } },
    { role: 'assistant', content: '' },
    { role: 'user', content: [{ type: 'text', text: 'hi' }] }
  ]);
  assert.deepEqual(out, [
    { role: 'system', content: 's' },
    { role: 'assistant', content: '', tool_calls: [{ id: '1', type: 'function', function: { name: 'a', arguments: { x: 1 } } }, { id: '2', type: 'function', function: { name: 'b', arguments: {} } }] },
    { role: 'tool', content: '{"ok":true}' },
    { role: 'user', content: 'hi' }
  ]);
});

test('toTemplateMessages: top-level null arguments are left out (the template cannot print them)', () => {
  const out = toTemplateMessages([{ role: 'assistant', content: '', tool_calls: [{ id: '1', type: 'function', function: { name: 'a', arguments: '{"kind":null,"n":0,"o":{"x":null}}' } }] }]);
  assert.deepEqual(out[0].tool_calls[0].function.arguments, { n: 0, o: { x: null } });
});

/* ------------------------------------------------------------- streaming */

function feed(splitter, text, size = 1) {
  for (let i = 0; i < text.length; i += size) splitter.push(text.slice(i, i + size));
  return splitter.end();
}

test('output splitter: reasoning and content apart, markup never shown, one character at a time', () => {
  const content = [];
  const reasoning = [];
  const s = createOutputSplitter({ onContent: (c) => content.push(c), onReasoning: (r) => reasoning.push(r) });
  const r = feed(s, `Let me look.</think>\n\nSure, checking.${S}[get_emails(folderId='x')]${E} Done <b>.`);
  assert.equal(reasoning.join(''), 'Let me look.');
  assert.equal(content.join(''), 'Sure, checking. Done <b>.');
  assert.equal(r.content, 'Sure, checking. Done <b>.');
  assert.ok(!content.join('').includes('tool_call'));
});

test('output splitter: an answer that opens like JSON is held for the parser', () => {
  const content = [];
  const s = createOutputSplitter({ onContent: (c) => content.push(c) });
  const r = feed(s, 'hm</think>{"name":"get_tasks","arguments":{}}', 3);
  assert.deepEqual(content, []);
  assert.equal(r.deferred, true);
});

test('token streamer: skips the prompt, holds partial UTF-8, flushes at the end', () => {
  const vocab = { 1: 'He', 2: 'llo', 3: ' wor', 4: '�', 5: 'ld', 9: '<eos>' };
  const tokenizer = {
    decode: (ids, { skip_special_tokens }) => {
      const s = ids.map((i) => vocab[Number(i)]).filter((t) => !(skip_special_tokens && t === '<eos>')).join('');
      return s.replace('�ld', 'ł'); // two tokens that only make sense together
    }
  };
  const out = [];
  const st = createTokenStreamer(tokenizer, (p) => out.push(p));
  st.put([[7n, 7n, 7n]]);
  for (const t of [1n, 2n, 3n, 4n, 5n, 9n]) st.put([[t]]);
  st.end();
  assert.equal(out.join(''), 'Hello worł');
  assert.ok(!out.includes('�'));
  assert.equal(st.ids.length, 6);
});

/* ------------------------------------------------------ fake local model */

/**
 * A tokenizer + model pair that "generates" scripted outputs, one per call, in
 * small pieces, honouring max_new_tokens and the stopping criteria the way
 * Transformers.js does. Every rendered prompt is recorded.
 */
function fakeRuntime(outputs, { piece = 4, onStep = null } = {}) {
  const vocab = ['<|im_end|>'];
  const idOf = (s) => { let i = vocab.indexOf(s); if (i < 0) { vocab.push(s); i = vocab.length - 1; } return BigInt(i); };
  const rendered = [];
  const encoded = (text) => { const len = Math.ceil(String(text).length / 4); return { input_ids: { dims: [1, len] }, attention_mask: { dims: [1, len] } }; };
  const tokenizer = Object.assign((text) => encoded(text), {
    eos_token_id: 0,
    decode(ids, { skip_special_tokens = false } = {}) {
      return ids.map((i) => vocab[Number(i)]).filter((t) => !(skip_special_tokens && t === '<|im_end|>')).join('');
    },
    encode: (text) => [idOf(text)],
    apply_chat_template(messages, opts) {
      if (opts.tokenize === false) return `PROMPT ${JSON.stringify(messages)}`;
      rendered.push({ messages, tools: opts.tools });
      return encoded(JSON.stringify({ messages, tools: opts.tools }));
    }
  });
  let n = 0;
  const calls = [];
  const model = {
    generation_config: { eos_token_id: 0 },
    async generate(opts) {
      calls.push(opts);
      const text = outputs[Math.min(n++, outputs.length - 1)];
      const pieces = [];
      for (let i = 0; i < text.length; i += piece) pieces.push(text.slice(i, i + piece));
      const ids = [[1n, 2n, 3n]];
      opts.streamer.put(ids);
      let produced = 0;
      for (const p of [...pieces.map(idOf), 0n]) {
        await Promise.resolve();
        if (onStep) onStep(produced);
        ids[0].push(p);
        produced++;
        opts.streamer.put([[p]]);
        if (p === 0n || produced >= opts.max_new_tokens) break;
        if (opts.stopping_criteria.some((c) => c(ids)[0])) break;
      }
      opts.streamer.end();
      return ids;
    }
  };
  return { tokenizer, model, rendered, calls };
}

test('createLocalStream: a plain answer streams like streamCompletion', async () => {
  const rt = fakeRuntime(['The user greets.</think>Hello! How can I help?']);
  const stream = createLocalStream({ getModel: async () => rt, maxNewTokens: 100 });
  const chunks = [];
  const reasoning = [];
  const turn = await stream({ messages: [{ role: 'system', content: 'sys' }, user('hi')], tools: TOOLS, onContent: (c, full) => chunks.push([c, full]), onReasoning: (r) => reasoning.push(r) });
  assert.equal(turn.content, 'Hello! How can I help?');
  assert.equal(turn.reasoning, 'The user greets.');
  assert.equal(turn.finishReason, 'stop');
  assert.deepEqual(turn.toolCalls, []);
  assert.equal(turn.aborted, false);
  assert.equal(chunks.map((c) => c[0]).join(''), 'Hello! How can I help?');
  assert.equal(chunks[chunks.length - 1][1], 'Hello! How can I help?');
  assert.equal(reasoning.join(''), 'The user greets.');
  assert.ok(turn.usage.prompt_tokens > 0);
  assert.equal(turn.usage.completion_tokens, Math.ceil('The user greets.</think>Hello! How can I help?'.length / 4) + 1);
  assert.equal(turn.usageSummary.promptTokens, turn.usage.prompt_tokens);
  // sampling and the generation limit reach generate(); the tool note reaches the system prompt
  assert.equal(rt.calls[0].temperature, 0.1);
  assert.equal(rt.calls[0].max_new_tokens, 100);
  assert.match(rt.rendered[0].messages[0].content, /^sys\n\nOnly the tools in the list below/);
  assert.ok(rt.rendered[0].tools.length <= 8 + 4);
  assert.deepEqual(Object.keys(rt.rendered[0].tools[0]), ['name', 'description', 'parameters']);
  assert.equal(typeof stream.last.ttftMs, 'number');
});

test('createLocalStream: a native tool call becomes OpenAI tool_calls, coerced', async () => {
  const rt = fakeRuntime([`Need mail.</think>${S}[get_emails(folderId='me/Inbox', take='3')]${E}`]);
  const stream = createLocalStream({ getModel: async () => rt });
  const shown = [];
  const turn = await stream({ messages: [user('show my inbox')], tools: TOOLS, onContent: (c) => shown.push(c) });
  assert.equal(turn.finishReason, 'tool_calls');
  assert.equal(turn.toolCalls.length, 1);
  assert.equal(turn.toolCalls[0].type, 'function');
  assert.match(turn.toolCalls[0].id, /^call_local_/);
  assert.equal(turn.toolCalls[0].function.name, 'get_emails');
  assert.deepEqual(JSON.parse(turn.toolCalls[0].function.arguments), { folderId: 'me/Inbox', take: 3 });
  assert.equal(turn.content, '');
  assert.deepEqual(shown, []);
  assert.ok(stream.last.selectedTools.includes('get_emails'));
  assert.equal(stream.last.validations[0].ok, true);
});

test('createLocalStream + runTurn: tool round, result, final answer; reasoning re-sent within the turn', async () => {
  const rt = fakeRuntime([
    `Need mail.</think>${S}[get_emails(folderId="me/Inbox", take=2)]${E}`,
    'Got two.</think>You have 2 emails: **Hi** and **Lunch**.'
  ]);
  const stream = createLocalStream({ getModel: async () => rt });
  const seen = [];
  const messages = [{ role: 'system', content: 'sys' }, user('what is in my inbox?')];
  const guarded = stream.guard({ callTool: async (name, args) => { seen.push([name, args]); return { isError: false, content: '[{"subject":"Hi"},{"subject":"Lunch"}]' }; } });
  const res = await runTurn({ messages, tools: TOOLS, apiKey: null, model: 'local', callTool: guarded.callTool, streamImpl: stream });
  assert.equal(res.rounds, 1);
  assert.deepEqual(seen, [['get_emails', { folderId: 'me/Inbox', take: 2 }]]);
  assert.deepEqual(messages.map((m) => m.role), ['system', 'user', 'assistant', 'tool', 'assistant']);
  assert.equal(messages[4].content, 'You have 2 emails: **Hi** and **Lunch**.');
  assert.equal(res.usage.requests, 2);
  // second render: the call with object arguments, its reasoning, and the result
  const second = rt.rendered[1].messages;
  const asst = second.find((m) => m.role === 'assistant');
  assert.deepEqual(asst.tool_calls[0].function.arguments, { folderId: 'me/Inbox', take: 2 });
  assert.equal(asst.reasoning, 'Need mail.');
  assert.equal(second[second.length - 1].role, 'tool');
  // the history runTurn keeps is plain OpenAI: string arguments, no reasoning field
  assert.equal(typeof messages[2].tool_calls[0].function.arguments, 'string');
  assert.ok(!('reasoning' in messages[2]));
});

test('createLocalStream + runTurn: an invalid call is answered locally and the model retries', async () => {
  const rt = fakeRuntime([
    `x</think>${S}[get_mail(folder="Inbox")]${E}`,
    `x</think>${S}[get_emails(take=2)]${E}`,
    `x</think>${S}[get_emails(folderId="me/Inbox", take=2)]${E}`,
    'x</think>Two emails.'
  ]);
  const stream = createLocalStream({ getModel: async () => rt });
  const server = [];
  const tools = [];
  const guarded = stream.guard({ callTool: async (name, args) => { server.push(name); return { isError: false, content: '[]' }; } });
  const messages = [user('inbox?')];
  await runTurn({
    messages, tools: TOOLS, callTool: guarded.callTool, streamImpl: stream,
    ui: { onToolEnd: (call, result) => tools.push([call.name, result.isError, result.content]) }
  });
  assert.deepEqual(server, ['get_emails'], 'only the valid call reached the server');
  assert.equal(tools.length, 3);
  assert.equal(tools[0][1], true);
  assert.match(tools[0][2], /no tool named "get_mail".*get_emails/s);
  assert.equal(tools[1][1], true);
  assert.match(tools[1][2], /missing required argument "folderId"/);
  assert.equal(tools[2][1], false);
  assert.equal(messages[messages.length - 1].content, 'Two emails.');
});

test('createLocalStream: unreadable call markup becomes an error round, not a crash', async () => {
  const rt = fakeRuntime([`x</think>${S}[get_emails(folderId="me/Inbox)]${E}`, 'x</think>Sorry.']);
  const stream = createLocalStream({ getModel: async () => rt });
  const results = [];
  const guarded = stream.guard({ callTool: async () => { throw new Error('must not be called'); } });
  await runTurn({ messages: [user('inbox')], tools: TOOLS, callTool: guarded.callTool, streamImpl: stream, ui: { onToolEnd: (c, r) => results.push(r) } });
  assert.equal(results.length, 1);
  assert.equal(results[0].isError, true);
  assert.match(results[0].content, /Could not read the tool call/);
});

test('createLocalStream: guard covers localTools (analyze_result) too', async () => {
  const rt = fakeRuntime([`x</think>${S}[analyze_result(artifact="r1")]${E}`]);
  const stream = createLocalStream({ getModel: async () => rt });
  await stream({ messages: [user('q')], tools: TOOLS });
  let ran = false;
  const g = stream.guard({ callTool: async () => ({}), localTools: { analyze_result: async () => { ran = true; return {}; } } });
  const r = await g.localTools.analyze_result({ artifact: 'r1' }, { id: 'x' });
  assert.equal(ran, false);
  assert.match(r.content, /missing required argument "question"/);
  assert.equal(stream.rejection('analyze_result', { artifact: 'r1' }), r.content);
});

test('createLocalStream: an unchanged repeat of a failed call is answered locally, until the next user turn', async () => {
  const call = `x</think>${S}[get_tasks()]${E}`;
  const rt = fakeRuntime([call, call, call]);
  const stream = createLocalStream({ getModel: async () => rt });
  const messages = [user('my tasks?')];
  let calls = 0;
  const g = stream.guard({ callTool: async () => { calls++; return { isError: true, content: 'Folder not found\nmore detail' }; } });
  await stream({ messages, tools: TOOLS });
  assert.equal((await g.callTool('get_tasks', {})).content, 'Folder not found\nmore detail');
  await stream({ messages, tools: TOOLS });
  const again = await g.callTool('get_tasks', {});
  assert.equal(calls, 1, 'the repeat never reached the server');
  assert.equal(again.isError, true);
  assert.match(again.content, /already failed in this turn: Folder not found Do not repeat it/);
  assert.equal(stream.repeatRejection('get_tasks', {}), again.content);
  assert.equal((await g.callTool('get_tasks', { folderId: 'x' })).content, 'Folder not found\nmore detail', 'other arguments still run');
  messages.push({ role: 'assistant', content: 'It failed.' }, user('try again please'));
  await stream({ messages, tools: TOOLS });
  await g.callTool('get_tasks', {});
  assert.equal(calls, 3, 'a new user turn may retry');
});

test('createLocalStream: reasoning past thinkBudget is closed and the answer generated after it', async () => {
  // One token per character: the first generation would think for 200 tokens.
  const rt = fakeRuntime(['x'.repeat(200) + '</think>late', 'The answer.'], { piece: 1 });
  const stream = createLocalStream({ getModel: async () => rt, thinkBudget: 50, maxNewTokens: 400 });
  const reasoning = [];
  const r = await stream({ messages: [user('q')], tools: TOOLS, onReasoning: (s) => reasoning.push(s) });
  assert.equal(rt.calls.length, 2, 'a second generation after the forced </think>');
  assert.equal(r.content, 'The answer.');
  assert.equal(r.reasoning, 'x'.repeat(50));
  assert.equal(stream.last.thinkingCut, true);
  assert.ok(rt.calls[1].max_new_tokens <= 400 - 50);
  assert.equal(r.finishReason, 'stop');
  // under the budget nothing changes
  const rt2 = fakeRuntime(['short</think>Fine.'], { piece: 1 });
  const s2 = createLocalStream({ getModel: async () => rt2, thinkBudget: 50 });
  assert.equal((await s2({ messages: [user('q')], tools: TOOLS })).content, 'Fine.');
  assert.equal(rt2.calls.length, 1);
  assert.equal(s2.last.thinkingCut, false);
});

test('createLocalStream: a bare JSON call is held back from the visible stream', async () => {
  const rt = fakeRuntime(['x</think>{"name": "get_tasks", "arguments": {}}']);
  const stream = createLocalStream({ getModel: async () => rt });
  const shown = [];
  const turn = await stream({ messages: [user('my todos')], tools: TOOLS, onContent: (c) => shown.push(c) });
  assert.deepEqual(shown, []);
  assert.equal(turn.toolCalls[0].function.name, 'get_tasks');
});

test('createLocalStream: a JSON-looking answer that is not a call is shown at the end', async () => {
  const rt = fakeRuntime(['x</think>[1, 2, 3] are the ids.']);
  const stream = createLocalStream({ getModel: async () => rt });
  const shown = [];
  const turn = await stream({ messages: [user('ids?')], tools: TOOLS, onContent: (c, full) => shown.push(full) });
  assert.equal(turn.content, '[1, 2, 3] are the ids.');
  assert.equal(shown[shown.length - 1], '[1, 2, 3] are the ids.');
});

test('createLocalStream: abort mid-generation stops and reports aborted; runTurn stops', async () => {
  const controller = new AbortController();
  const rt = fakeRuntime(['thinking '.repeat(50) + '</think>never'], { onStep: (n) => { if (n === 5) controller.abort(); } });
  const stream = createLocalStream({ getModel: async () => rt });
  const messages = [user('hi')];
  const notices = [];
  const res = await runTurn({ messages, tools: TOOLS, signal: controller.signal, callTool: async () => ({}), streamImpl: stream, ui: { onNotice: (t) => notices.push(t) } });
  assert.equal(res.stopped, true);
  assert.deepEqual(notices, ['Stopped.']);
  assert.ok(stream.last.completionTokens < 10);
  // already aborted: no model work at all
  const turn = await stream({ messages, tools: TOOLS, signal: controller.signal });
  assert.equal(turn.aborted, true);
  assert.equal(rt.calls.length, 1);
});

test('createLocalStream: hitting max_new_tokens is finishReason length, and a half-written call is dropped', async () => {
  const rt = fakeRuntime([`x</think>${S}[send_email(to="a@b.c", subject="Hi", body="${'long '.repeat(40)}")]${E}`]);
  const stream = createLocalStream({ getModel: async () => rt, maxNewTokens: 12 });
  const turn = await stream({ messages: [user('mail bob')], tools: TOOLS });
  assert.equal(turn.finishReason, 'length');
  assert.deepEqual(turn.toolCalls, []);
  assert.equal(turn.usage.completion_tokens, 12);
});

test('createLocalStream: an oversized history is trimmed to the context budget before rendering', async () => {
  const rt = fakeRuntime(['x</think>ok']);
  const stream = createLocalStream({ getModel: async () => rt, contextTokens: 4096, maxNewTokens: 1024 });
  await stream({ messages: longConversation(), tools: TOOLS });
  assert.ok(stream.last.budgetSteps.length > 0);
  assert.ok(stream.last.promptTokens <= 3072, `prompt ${stream.last.promptTokens}`);
});

test('createLocalStream: a failing model is a LocalModelError, not a hang', async () => {
  const stream = createLocalStream({ getModel: async () => ({ tokenizer: fakeRuntime([]).tokenizer, model: { generate: async () => { throw new Error('OOM'); } } }) });
  await assert.rejects(stream({ messages: [user('hi')], tools: TOOLS }), { name: 'LocalModelError', message: /OOM/ });
  assert.throws(() => createLocalStream({}), TypeError);
});

/* ---------------------------------------------------------------- loading */

test('loadLocalModel: uses the injected runtime; the CDN URL is pinned to an exact version', async () => {
  const seen = [];
  const tf = {
    AutoTokenizer: { from_pretrained: async (id) => { seen.push(['tok', id]); return { t: 1 }; } },
    AutoModelForCausalLM: { from_pretrained: async (id, o) => { seen.push(['model', id, o.dtype, o.device]); return { dispose: () => seen.push(['dispose']) }; } }
  };
  const m = await loadLocalModel({ transformers: tf });
  assert.deepEqual(seen, [['tok', 'LiquidAI/LFM2.5-2.6B-ONNX'], ['model', 'LiquidAI/LFM2.5-2.6B-ONNX', 'q4', 'cpu']]);
  assert.equal(m.device, 'cpu');
  m.dispose();
  assert.match(TRANSFORMERS_VERSION, /^\d+\.\d+\.\d+$/);
  assert.ok(TRANSFORMERS_CDN_URL.includes(`@${TRANSFORMERS_VERSION}/`));
});

test('loadLocalModel: in a browser without WebGPU it says so', async () => {
  const saved = { window: globalThis.window, document: globalThis.document };
  globalThis.window = {};
  globalThis.document = {};
  try {
    await assert.rejects(loadLocalModel({ transformers: {} }), { name: 'LocalModelError', code: 'no_webgpu' });
  } finally {
    if (saved.window === undefined) delete globalThis.window; else globalThis.window = saved.window;
    if (saved.document === undefined) delete globalThis.document; else globalThis.document = saved.document;
  }
});
