import test from 'node:test';
import assert from 'node:assert/strict';

import {
  LOCAL_MODEL_ID, LOCAL_MODEL_NAME, LOCAL_HUB_MODEL, isLocalModel, taskModelFor, withLocalModel,
  createProgressTracker, formatBytes, progressText, formatRoundStats
} from '../../js/local-model-ui.js';
import { DEFAULT_LOCAL_MODEL } from '../../js/local-llm.js';

test('the picker id names local-llm.js\'s default model and is never an OpenRouter id', () => {
  assert.equal(LOCAL_HUB_MODEL, DEFAULT_LOCAL_MODEL);
  assert.ok(isLocalModel(LOCAL_MODEL_ID));
  for (const id of ['anthropic/claude-haiku-5.5', 'openai/gpt-6-luna', 'meta-llama/llama-4:free', '', null, undefined, 42]) {
    assert.equal(isLocalModel(id), false, String(id));
  }
});

test('a task never starts from the local model', () => {
  assert.equal(taskModelFor(LOCAL_MODEL_ID, 'x/default'), 'x/default');
  assert.equal(taskModelFor('', 'x/default'), 'x/default');
  assert.equal(taskModelFor('openai/gpt-6-luna', 'x/default'), 'openai/gpt-6-luna');
});

test('withLocalModel puts the local option first, exactly once', () => {
  const list = withLocalModel([{ id: 'a/b', name: 'B' }, { id: LOCAL_MODEL_ID, name: 'dup' }, { id: 'c/d', name: 'D' }]);
  assert.deepEqual(list.map((m) => m.id), [LOCAL_MODEL_ID, 'a/b', 'c/d']);
  assert.equal(list[0].name, LOCAL_MODEL_NAME);
  assert.match(LOCAL_MODEL_NAME, /experimental/);
  assert.deepEqual(withLocalModel(null).map((m) => m.id), [LOCAL_MODEL_ID]);
});

test('progress is summed over files, unknown sizes count as zero, done completes a file', () => {
  const t = createProgressTracker();
  assert.equal(t.snapshot().fraction, null);
  t.update({ status: 'initiate', file: 'tokenizer.json' });
  t.update({ status: 'progress', file: 'tokenizer.json', loaded: 50, total: 100 });
  let s = t.update({ status: 'progress', file: 'onnx/model_q4f16.onnx_data', loaded: 100, total: 900 });
  assert.equal(s.loaded, 150);
  assert.equal(s.total, 1000);
  assert.equal(s.fraction, 0.15);
  assert.equal(s.active, 'onnx/model_q4f16.onnx_data');
  s = t.update({ status: 'done', file: 'tokenizer.json' });
  assert.equal(s.loaded, 200);
  // Aggregates and events without a file change nothing.
  assert.deepEqual(t.update({ status: 'progress_total', progress: 99, loaded: 1, total: 1 }), s);
  assert.deepEqual(t.update({ status: 'ready', model: 'x' }), s);
  assert.deepEqual(t.update(null), s);
  // A bogus loaded never exceeds the total.
  s = t.update({ status: 'progress', file: 'onnx/model_q4f16.onnx_data', loaded: 5000, total: 900 });
  assert.equal(s.fraction, 1);
});

test('progress text', () => {
  assert.equal(progressText(null), 'Starting…');
  assert.equal(progressText({ loaded: 0, total: 0, fraction: null }), 'Starting…');
  assert.equal(progressText({ loaded: 2048, total: 0, fraction: null }), 'Downloading 2.0 KB…');
  assert.equal(progressText({ loaded: 640 * 1024 ** 2, total: 1.5 * 1024 ** 3, fraction: 640 / 1536 }), 'Downloading 640 MB of 1.50 GB (41%)');
  assert.match(progressText({ loaded: 10, total: 10, fraction: 1 }), /GPU/);
  assert.equal(formatBytes(512), '512 B');
  assert.equal(formatBytes(50 * 1024 ** 2), '50.0 MB');
  assert.equal(formatBytes(-1), '?');
});

test('round stats from stream.last', () => {
  assert.equal(formatRoundStats(null), '');
  const last = {
    promptTokens: 1234, completionTokens: 210, tokensPerSecond: 18.26, ttftMs: 1400,
    selectedTools: ['a', 'b', 'c'], validations: [{ ok: true }, { ok: false, error: 'x' }], parseErrors: []
  };
  assert.equal(formatRoundStats(last, 59), '1.2k in · 210 out · 18.3 tok/s · first token 1.4 s · tools offered: 3 of 59 · 1 invalid call answered locally');
  assert.equal(formatRoundStats({ promptTokens: 10, completionTokens: 1, tokensPerSecond: null, ttftMs: null, selectedTools: [] }),
    '10 in · 1 out · tools offered: 0');
});
