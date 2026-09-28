/**
 * Recorded-shape OpenRouter SSE fixtures.
 *
 * These reproduce the wire formats seen from different providers: a plain text
 * stream, a tool-call stream whose arguments arrive as fragments, a stream with
 * two parallel tool calls, a provider that repeats the full function name on
 * every fragment and omits `index`, a truncated (finish_reason: "length")
 * stream, and a mid-stream error frame.
 */

const enc = (obj) => 'data: ' + JSON.stringify(obj) + '\n\n';
const chunk = (delta, finish = null) => enc({
  id: 'gen-1', object: 'chat.completion.chunk', model: 'test/model',
  choices: [{ index: 0, delta, finish_reason: finish }]
});

/** Plain text answer, split mid-word, with a keepalive comment and CRLF. */
export const textStream = [
  ': OPENROUTER PROCESSING\n\n',
  chunk({ role: 'assistant', content: '' }),
  chunk({ content: 'You have ' }),
  chunk({ content: '3 unread' }),
  chunk({ content: ' messages.' }),
  chunk({}, 'stop').replace(/\n\n$/, '\r\n\r\n'),
  enc({ id: 'gen-1', usage: { prompt_tokens: 100, completion_tokens: 8, total_tokens: 108 }, choices: [] }),
  'data: [DONE]\n\n'
];

/** One tool call; arguments arrive in five fragments; content is null throughout. */
export const toolCallStream = [
  chunk({ role: 'assistant', content: null, tool_calls: [{ index: 0, id: 'call_abc123', type: 'function', function: { name: 'get_emails', arguments: '' } }] }),
  chunk({ tool_calls: [{ index: 0, function: { arguments: '{"fold' } }] }),
  chunk({ tool_calls: [{ index: 0, function: { arguments: 'erId":"me@' } }] }),
  chunk({ tool_calls: [{ index: 0, function: { arguments: 'example.com/Inbox"' } }] }),
  chunk({ tool_calls: [{ index: 0, function: { arguments: ',"take":3}' } }] }),
  chunk({}, 'tool_calls'),
  'data: [DONE]\n\n'
];

/** Two parallel tool calls interleaved by index, plus a preamble sentence. */
export const parallelToolCallStream = [
  chunk({ role: 'assistant', content: 'Let me check.' }),
  chunk({ tool_calls: [{ index: 0, id: 'call_1', type: 'function', function: { name: 'list_folder_info_by_type', arguments: '' } }] }),
  chunk({ tool_calls: [{ index: 1, id: 'call_2', type: 'function', function: { name: 'get_emails', arguments: '' } }] }),
  chunk({ tool_calls: [{ index: 0, function: { arguments: '{"folderType"' } }] }),
  chunk({ tool_calls: [{ index: 1, function: { arguments: '{"folderId"' } }] }),
  chunk({ tool_calls: [{ index: 0, function: { arguments: ':"mail"}' } }] }),
  chunk({ tool_calls: [{ index: 1, function: { arguments: ':"me@example.com/Inbox"}' } }] }),
  chunk({}, 'tool_calls'),
  'data: [DONE]\n\n'
];

/** A provider that omits `index`, repeats the whole name, and never sets finish_reason. */
export const noIndexToolCallStream = [
  chunk({ tool_calls: [{ id: 'call_x', type: 'function', function: { name: 'get_emails', arguments: '{"folderId"' } }] }),
  chunk({ tool_calls: [{ id: 'call_x', function: { name: 'get_emails', arguments: ':"me@example.com/Inbox"}' } }] }),
  'data: [DONE]\n\n'
];

/** Output cut off by max_tokens. */
export const truncatedStream = [
  chunk({ content: 'Here is the very long list of' }),
  chunk({}, 'length'),
  'data: [DONE]\n\n'
];

/** Provider failure delivered inside the stream rather than as an HTTP status. */
export const midStreamErrorStream = [
  chunk({ content: 'Thinking' }),
  enc({ error: { code: 429, message: 'Provider rate limit' } }),
  'data: [DONE]\n\n'
];

/** Malformed JSON frames must not kill the stream. */
export const malformedStream = [
  'data: {"choices":[{"delta":{"content":"ok"}}\n\n',  // truncated JSON, dropped
  chunk({ content: ' fine' }),
  'data: [DONE]\n\n'
];

/** Build a fake fetch that replays `parts` as a byte stream, one chunk per read. */
export function fakeFetch(parts, { status = 200, byteChunks = null } = {}) {
  return async () => {
    if (status !== 200) {
      return {
        ok: false,
        status,
        text: async () => JSON.stringify({ error: { message: 'nope', code: status } })
      };
    }
    const encoder = new TextEncoder();
    const queue = (byteChunks || parts).map((p) => encoder.encode(p));
    let i = 0;
    return {
      ok: true,
      status: 200,
      body: {
        getReader() {
          return {
            read: async () => (i < queue.length ? { done: false, value: queue[i++] } : { done: true, value: undefined }),
            releaseLock() {}
          };
        }
      }
    };
  };
}

/** Split a whole stream into arbitrary byte-sized pieces to test the buffering. */
export function shred(parts, size = 7) {
  const whole = parts.join('');
  const out = [];
  for (let i = 0; i < whole.length; i += size) out.push(whole.slice(i, i + size));
  return out;
}
