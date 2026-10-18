/**
 * smoke.mjs — run the experimental local model (js/local-llm.js) for real,
 * under Node, through llm.js runTurn with a few fake mailbox tools.
 *
 *   cd src/Agent/wwwroot/dev/local-model && npm install && node smoke.mjs
 *   node smoke.mjs --device cpu --dtype q4f16        # devices: webgpu (macOS default), cpu, coreml
 *   node smoke.mjs --tools fixtures/tools-user.json  # retrieval over a real tool list
 *   node smoke.mjs --only 2 --max-new 1024
 *
 * The first run downloads the model (~1.9 GB for q4) into Transformers.js's
 * cache under node_modules. Prints, per round: the raw model output, the parsed
 * tool calls, prompt tokens, time to first token and decode speed; per prompt:
 * the final answer.
 */

import fs from 'node:fs';
import * as transformers from '@huggingface/transformers';
import { createLocalStream, loadLocalModel, DEFAULT_LOCAL_MODEL, TRANSFORMERS_VERSION } from '../../js/local-llm.js';
import { runTurn, toolsToOpenAI, buildSystemPrompt } from '../../js/llm.js';

const argv = process.argv.slice(2);
const opt = (name, dflt) => { const i = argv.indexOf(`--${name}`); return i >= 0 ? argv[i + 1] : dflt; };
// WebGPU through onnxruntime-node (Dawn, as in Chrome) is the fastest on macOS; CoreML measured slower than CPU.
const device = opt('device', process.platform === 'darwin' ? 'webgpu' : 'cpu');
const dtype = opt('dtype', 'q4');
const modelId = opt('model', DEFAULT_LOCAL_MODEL);
const k = Number(opt('k', '4'));
const maxNewTokens = Number(opt('max-new', '1536'));
const only = opt('only', null);
const toolsFile = opt('tools', null);

/* ---------------------------------------------------------- fake mailbox */

const ME = 'jane@example.com';
const FAKE_TOOLS = [
  { name: 'get_emails', category: 'Mail', write: false, description: 'Get email metadata (subject, from, date, uid, read, flagged) from a folder, newest first, with optional search and pagination.',
    inputSchema: { type: 'object', properties: {
      folderId: { type: 'string', description: 'Folder id, e.g. "owner@domain/Inbox"' },
      take: { type: 'integer', description: 'How many to return (default 20)' },
      query: { type: 'string', description: 'Optional search text (subject, sender)' } }, required: ['folderId'] } },
  { name: 'read_email', category: 'Mail', write: false, description: 'Read the text body of one email by uid.',
    inputSchema: { type: 'object', properties: { folderId: { type: 'string' }, uid: { type: 'integer' } }, required: ['folderId', 'uid'] } },
  { name: 'send_email', category: 'Mail', write: true, description: 'Send an email message.',
    inputSchema: { type: 'object', properties: { to: { type: 'string', description: 'Comma-separated recipients' }, subject: { type: 'string' }, body: { type: 'string' } }, required: ['to', 'subject', 'body'] } },
  { name: 'list_folders', category: 'Folders', write: false, description: 'List the mailbox folders with their ids, message counts and unread counts.',
    inputSchema: { type: 'object', properties: {} } },
  { name: 'get_calendar_events', category: 'Calendar', write: false, description: 'List calendar events (meetings, appointments) between two dates.',
    inputSchema: { type: 'object', properties: { start: { type: 'string', description: 'ISO date' }, end: { type: 'string', description: 'ISO date' } } } },
  { name: 'get_contacts', category: 'Contacts', write: false, description: 'List or search contacts in the address book.',
    inputSchema: { type: 'object', properties: { search: { type: 'string' } } } }
];

const EMAILS = [
  { uid: 105, from: 'alice@example.com', subject: 'Q4 budget', date: '2026-10-17T09:12:00', read: false },
  { uid: 104, from: 'bob@example.com', subject: 'Lunch Friday?', date: '2026-10-16T17:40:00', read: false },
  { uid: 103, from: 'it@example.com', subject: 'Password expiry notice', date: '2026-10-16T08:00:00', read: true },
  { uid: 102, from: 'carol@example.com', subject: 'Slides for Monday', date: '2026-10-15T14:03:00', read: true }
];
const BODIES = { 105: 'Hi Jane, could you send me the final Q4 numbers for marketing by Wednesday? We need them for the board deck. Thanks, Alice' };

/** The real tool names (with --tools) answer like their fake counterparts. */
const ALIASES = { list_folder_info_by_type: 'list_folders', get_email_message: 'read_email', read_email_part: 'read_email' };

function cannedResult(name, args) {
  switch (ALIASES[name] || name) {
    case 'get_emails': {
      let list = EMAILS;
      if (args.query) list = list.filter((e) => JSON.stringify(e).toLowerCase().includes(String(args.query).toLowerCase()));
      return { isError: false, content: JSON.stringify({ success: true, emails: list.slice(0, args.take || 20) }) };
    }
    case 'read_email':
      return BODIES[args.uid]
        ? { isError: false, content: JSON.stringify({ success: true, uid: args.uid, body: BODIES[args.uid] }) }
        : { isError: true, content: `{"success":false,"error":"No message with uid ${args.uid}"}` };
    case 'list_folders':
      return { isError: false, content: JSON.stringify({ success: true, folders: [
        { id: `${ME}/Inbox`, count: 4, unread: 2 }, { id: `${ME}/Sent Items`, count: 31, unread: 0 },
        { id: `${ME}/Archive`, count: 210, unread: 0 }, { id: `${ME}/Junk E-Mail`, count: 3, unread: 3 }] }) };
    case 'send_email':
      return { isError: false, content: '{"success":true}' };
    case 'get_calendar_events':
      return { isError: false, content: JSON.stringify({ success: true, events: [{ subject: 'Standup', start: '2026-10-18T09:00:00' }] }) };
    default:
      return { isError: false, content: '{"success":true,"items":[]}' };
  }
}

const PROMPTS = [
  'What are my 3 newest emails?',
  'Which folders do I have, and how many unread messages are in each?',
  'Find the email from Alice about the budget and tell me what she is asking for.'
];

/* -------------------------------------------------------------------- run */

const contract = toolsFile ? JSON.parse(fs.readFileSync(new URL(toolsFile, import.meta.url))) : FAKE_TOOLS;
const tools = toolsToOpenAI(contract);
const categories = new Map(contract.map((t) => [t.name, t.category]));
const system = buildSystemPrompt({ accounts: [{ role: 'User', handle: ME, emailAddress: ME, username: 'jane', baseUrl: 'https://mail.example.com', readOnly: false }] },
  { now: new Date('2026-10-17T10:00:00') });

console.log(`transformers.js ${TRANSFORMERS_VERSION} (installed ${transformers.env?.version ?? '?'}), node ${process.version}, ${process.platform}/${process.arch}`);
console.log(`model ${modelId}  dtype ${dtype}  device ${device}  tools ${tools.length} (k=${k})  max_new_tokens ${maxNewTokens}`);

let lastPct = -10;
const loaded = await loadLocalModel({
  modelId, dtype, device, transformers,
  onProgress: (p) => {
    if (p.status === 'progress' && p.file && /onnx_data/.test(p.file) && p.progress - lastPct >= 10) { lastPct = p.progress; process.stdout.write(`  downloading ${p.file} ${p.progress.toFixed(0)}%\n`); }
  }
});
console.log(`loaded in ${(loaded.loadMs / 1000).toFixed(1)} s\n`);

const stream = createLocalStream({ getModel: async () => loaded, k, maxNewTokens, categoryOf: (n) => categories.get(n) });
const summary = [];

for (const [i, prompt] of PROMPTS.entries()) {
  if (only && String(i + 1) !== only) continue;
  console.log('='.repeat(100));
  console.log(`PROMPT ${i + 1}: ${prompt}`);
  const messages = [{ role: 'system', content: system }, { role: 'user', content: prompt }];
  const rounds = [];
  const guarded = stream.guard({ callTool: async (name, args) => cannedResult(name, args) });
  const t0 = performance.now();
  await runTurn({
    messages, tools, callTool: guarded.callTool, streamImpl: stream, maxToolRounds: 5,
    isWrite: (n) => { const t = contract.find((x) => x.name === n); return t ? t.write === true : undefined; },
    ui: {
      onAssistantEnd: (full, turn) => {
        const d = stream.last;
        rounds.push(d);
        console.log(`\n--- round ${rounds.length}: tools offered [${d.selectedTools.join(', ')}]`);
        console.log(`prompt ${d.promptTokens} tok | completion ${d.completionTokens} tok | TTFT ${fmtMs(d.ttftMs)} | decode ${fmt(d.tokensPerSecond)} tok/s | prefill ${fmt(d.prefillTokensPerSecond)} tok/s | round ${fmtMs(d.totalMs)} | finish ${turn.finishReason}`);
        console.log('RAW >>>');
        console.log(d.raw);
        console.log('<<< RAW');
        if (turn.toolCalls.length) console.log('PARSED tool_calls:', JSON.stringify(turn.toolCalls.map((c) => ({ name: c.function.name, arguments: JSON.parse(c.function.arguments) }))));
        d.validations.filter((v) => !v.ok).forEach((v) => console.log('REJECTED:', v.error.split('\n')[0]));
        d.validations.filter((v) => v.notes.length).forEach((v) => console.log('COERCED:', v.notes.join('; ')));
        if (d.parseErrors.length) console.log('PARSE ERRORS:', d.parseErrors.join(' | '));
      },
      onToolEnd: (call, result) => console.log(`TOOL ${call.name}(${call.argsText}) → ${result.isError ? 'ERROR ' : ''}${String(result.content).slice(0, 160)}`),
      onNotice: (t, kind) => console.log(`NOTICE [${kind}] ${t}`)
    }
  });
  const final = messages[messages.length - 1];
  console.log(`\nFINAL ANSWER (${((performance.now() - t0) / 1000).toFixed(1)} s, ${rounds.length} round${rounds.length === 1 ? '' : 's'}):\n${final.role === 'assistant' ? final.content : '(none)'}`);
  summary.push({ prompt: i + 1, rounds: rounds.length, calls: rounds.flatMap((r) => r.validations.map((v) => `${v.ok ? '' : '!'}${v.name}`)).join(' '), promptTok: rounds.map((r) => r.promptTokens).join('/'), ttft: rounds.map((r) => fmtMs(r.ttftMs)).join('/'), tps: rounds.map((r) => fmt(r.tokensPerSecond)).join('/'), totalS: ((performance.now() - t0) / 1000).toFixed(1) });
}

console.log('\n' + '='.repeat(100));
console.log(`SUMMARY  model ${modelId} ${dtype} on ${device}, load ${(loaded.loadMs / 1000).toFixed(1)} s`);
console.table(summary);
await loaded.dispose();

function fmt(n) { return typeof n === 'number' && Number.isFinite(n) ? n.toFixed(1) : '-'; }
function fmtMs(n) { return typeof n === 'number' && Number.isFinite(n) ? `${(n / 1000).toFixed(2)} s` : '-'; }
