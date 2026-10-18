# Testing the in-browser model in the chat (experimental)

The chat can run a small model on your own machine instead of an OpenRouter model:
LiquidAI LFM2.5 2.6B through Transformers.js on WebGPU (`js/local-llm.js`), wired into the
UI by `js/local-model-ui.js`. No OpenRouter key is needed for it.

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

- **Picker**: "Local · LFM2.5 2.6B (in-browser, experimental)" is the first entry. Switching to an
  OpenRouter model without a key shows a key field under the header; sending without one is refused
  with a notice. Switching back and forth keeps the conversation.
- **Loading**: the bar under the header has **Load model**, or the first message loads it. The first
  load downloads ~1.5–1.9 GB with a progress bar; later loads come from the browser cache in seconds.
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
