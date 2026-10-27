# Testing the in-browser model in the chat (experimental)

The chat can run a small model on your own machine instead of an OpenRouter model:
LiquidAI LFM3 1.7B through Transformers.js (4.3.2 or later: the first with the `lfm3`
architecture) on WebGPU (`js/local-llm.js`), wired into the UI by `js/local-model-ui.js`. No
OpenRouter key is needed for it. LFM2.5 2.6B, the first model tried, still works
(`LOCAL_MODELS`; `node eval.mjs --model LiquidAI/LFM2.5-2.6B-ONNX`).

The two write tool calls differently, and `parseModelOutput` reads both whatever the model: LFM3 one
XML block per call (`<tool_call><function=…><parameter=…>…</parameter></function></tool_call>`,
every value as text, coerced to the schema afterwards), LFM2.5 a Python list between
`<|tool_call_start|>` and `<|tool_call_end|>`. LFM3's ONNX graph also wants a `logits_to_keep`
input that Transformers.js does not fill (it only knows `num_logits_to_keep`); `loadLocalModel`
supplies it (`bridgeLogitsToKeep`).

## What you need

- A browser with WebGPU and a GPU adapter: a recent Chrome or Edge on desktop. Check
  `chrome://gpu` ("WebGPU: Hardware accelerated").
- A **secure context**: `https://…` or `http://localhost` / `http://127.0.0.1`. Over plain HTTP on any
  other address the browser hides `navigator.gpu`, and the chat says there is no WebGPU.
- About 2 GB of free disk for the browser cache and a few GB of free GPU / unified memory.
- Network access to `cdn.jsdelivr.net` (the Transformers.js build and its ONNX Runtime wasm) and
  `huggingface.co` (the model, which redirects to Hugging Face's own CDN hosts).

## Against the stub server

From `src/Agent/`:

```sh
TOOLS=full node wwwroot/dev/stub-server.mjs
# → http://localhost:8787/mail-agent/
```

`TOOLS=full` serves the real tool list (166 mailbox + domain-admin and 58 system-admin tools), so
tool retrieval sees what production would. Sign in with any server and password (e.g.
`mail.example.com`); the email decides the role: `you@example.com` is a mailbox user,
`domainadmin@example.com` a domain admin, `admin` a system admin (see `../README.md`). Leave the key
empty and tick **No key? Try the in-browser model**. Tick **Allow changes** to see write tools.

The stub answers every tool, so whole tool loops run; reads return three fake `example.com` items.

## Against the real Agent

From the repo root, with a SmarterMail server you can sign in to:

```sh
cd src/Agent
ASPNETCORE_URLS=http://127.0.0.1:8199 dotnet run
# add ALLOW_PRIVATE_HOSTS=true only if your mail server is on a private network or plain http
# add BROWSER_ONLY_MODE=true to skip profiles and tasks (no DATA_DIR needed)
```

Open <http://127.0.0.1:8199/>, sign in to `<your-mail-server>` as `<you@your-domain>`, and tick the
in-browser model box (or paste a key and pick the model later from the picker).

## What to look for

- **Picker**: "Local · LFM3 1.7B (in-browser, experimental)" is the first entry. Switching to an
  OpenRouter model without a key shows a key field under the header; sending without one is refused
  with a notice. Switching back and forth keeps the conversation.
- **Loading**: the bar under the header has **Load model**, or the first message loads it. The first
  load downloads ~1.4–1.7 GB with a progress bar; later loads come from the browser cache in seconds.
  "Ready · webgpu · q4f16" (or `q4` on a GPU without `shader-f16`) when done. **Unload** frees the
  GPU memory; the download stays cached.
- **Each round**: a collapsed **Thinking** block above the answer (the model's `<think>` text, never
  in the answer), then a stats line: tokens in / out, tok/s, time to first token, "tools offered:
  N of M", and how many invalid calls were answered locally.
- **Debug** (checkbox in the bar): adds "Round details" under each round: the tools offered, timings,
  context-budget steps (elided / clamped / dropped history), calls rejected locally and why, and the
  raw model output with its special tokens.
- **Invalid tool calls** (wrong argument names, bad types, unknown tools, unreadable call markup) show
  as failed tool cards answered by the browser; they never reach the server. The model should retry.
- **Write gate**: the app, not the model, asks before every change. A write tool call waits on a
  card under its tool card (tool, account, every argument in full, hidden characters marked) with
  **Approve** / **Deny**; destructive tools get a red card. Deny (or **Stop** while the card waits)
  sends nothing to the server and tells the model the user declined and not to retry. In the eval,
  LFM2.5 made writes without asking in 32 of 32 runs, whatever the prompt said; LFM3 in 23 of 32.
  OpenRouter models are not gated this way.
- **Eval** (`eval.mjs`, 37 tasks × 2, k=8, thinking budget 1024, q4f16 on WebGPU, M4 Max): LFM3 1.7B
  85% (reads 94%, writes 72%), LFM2.5 2.6B 89% (reads 91%, writes 88%). LFM3 writes far fewer
  invalid calls (4 vs 25 answered locally) and starts answering sooner (median 0.9 s vs 1.3 s to the
  first token). Several of its write misses are tools whose schemas mark optional fields required
  (`new_or_update_calendar_event`, `new_or_update_task`): LFM3 asks the user for them, LFM2.5
  invents them.
- **Scheduled tasks** never offer the in-browser model: a new task starts from the default OpenRouter
  model, and saving one with a `local:` model is refused.

## Known limitations

- Experimental and far weaker than a hosted model: it misreads requests, picks the wrong tool or
  invents arguments more often. Keep **Allow changes** off unless you are testing writes on a test
  mailbox.
- It sees only the ~8–12 tools retrieved for the conversation (BM25 over names and descriptions),
  not all of them, and a 16K-token context: older turns and long results are shortened first.
- **Large results** are off for it: no `analyze_result` (that needs a second model and an OpenRouter
  key). Results over the context budget are clamped instead.
- Generation is not interruptible mid-download: **Stop** during the first load ends the turn once
  loading finishes; the download carries on and is cached.
- Usage has no cost (nothing is billed); tokens are the local tokenizer's.
- The model choice is per tab (and in a profile's settings); the model itself is cached per browser
  profile, so a private window downloads it again.
