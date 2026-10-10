# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/). One version covers all three images.

## [Unreleased]

### Added

- Agent (server mode): a Settings section in the Profile menu. A profile chooses its own session
  idle timeout (5 minutes up to the new `PROFILE_MAX_IDLE_MINUTES`, default 480), applied at once to
  every session of it, and whether accounts added to it start with changes allowed.
- Agent: prompt caching on OpenRouter. Claude models cache the tool list, system prompt and
  conversation; other providers' automatic caching now gets a stable prefix. Chat and scheduled tasks.
- Agent: the chat renders GitHub-style markdown tables (alignment, inline formatting in cells,
  horizontal scroll for wide tables).
- Agent: scheduled runs are told when the previous successful run started (in the task's time zone)
  and whether the latest attempt failed; task results show prompt / completion token counts.
- Agent: token usage, cached tokens and cost are tracked per chat turn and per scheduled run.
- Agent: tool results over 20,000 characters become artifacts; the model asks `analyze_result`, which
  runs a cheaper analysis model (default `openai/gpt-6-luna`) over deterministic search, count and
  extract operators. Browser: in tab memory, with your key; Tools menu switch and model choice. Tool
  cards show the analysis calls and cost, plus a download of the full result. Scheduled runs do the
  same with `TASK_ANALYSIS_MODEL`, billed to the task key. New env `ANALYSIS_MODEL`.
- Agent: scheduled tasks can ask before changing anything. Mark an allowed change "asks me first"
  and the run only proposes the exact call; review it under Tasks → To approve (arguments shown
  verbatim), then approve to run it once, or deny. Proposals expire (per task, 72 h by default, up to
  7 days). Approving a destructive or admin change asks for your passkey, bound to that exact change.
  The emailed report says how many changes are waiting, never what they are. New env
  `APPROVAL_MAX_PENDING` (100), `TASK_MAX_PROPOSALS` (50).
- Agent: condition-triggered tasks. A task can run when a condition over a read tool's result holds
  (checked every few minutes, no AI involved in the check) instead of on a schedule: run the prompt
  with what matched, or just email an alert. "When something happens" in the task editor, with "Test
  probe" to see the real result and pick fields by clicking. New items, counts, thresholds, text /
  regex / date checks; edge or level firing, hold-for, cooldown, active hours and a daily cap.
  Matched data reaches the model only as a tool result. `POST /api/tasks/probe`; env
  `TRIGGERS_ENABLED`, `TRIGGER_MIN_INTERVAL_MINUTES`, `TRIGGERS_PER_PROFILE`,
  `TRIGGER_MAX_RUNS_PER_DAY`, `TRIGGER_CONCURRENCY`, `PROBES_PER_HOST_PER_MINUTE`.

- `upload_attachment` takes the file as `base64Content` or `text` (with `fileName`), so remote
  clients can attach files they have. New `inline=true` embeds an image: SmarterMail assigns the
  content ID and the tool returns it as `htmlReference` (`cid:…`).
- smartermail-mcp-user: `POST /attachments` (HTTP, same API key): upload files with
  `curl -F file=@…` instead of passing base64 through the model. HTTP servers describe it in their
  MCP `instructions`.
- `download_email_attachment` returns text attachments' contents, and binary ones as base64 with
  `includeBase64=true`; `savePath` is now optional.
- `SMARTERMAIL_LOCAL_FILES` (MCP servers): whether attachment tools may use paths on the server's
  filesystem. Default: on for stdio, off for HTTP.

### Changed

- Agent: large tool results from older chat turns are no longer re-sent to the model (the tool cards
  keep them); the model calls the tool again if it needs one.
- `search_log_files` returns a window (`maxChars` default 16,000, `offset`, `tail`, optional `contains`) with
  `totalChars`, `hasMore` and `nextOffset`, instead of a whole day's log in one result.
- Agent: the default model is Claude Haiku 5.5 (`anthropic/claude-haiku-5.5` on OpenRouter).
- `upload_attachment`'s `contentId` is replaced by `inline`. SmarterMail rejects every content ID
  but its own, so custom IDs never worked.

### Fixed

- Agent: old tool results from write tools and failed calls are never elided from the chat history,
  so the model is not nudged into repeating a change; an elided result of unknown kind says a change
  it made was already made.
- Agent: the approval review shows invisible and direction-changing characters as `⟦U+XXXX⟧` markers
  with a warning and flags non-ASCII characters in addresses (display only; the hash is unchanged).
- Agent: approving a change that sends mail (`send_*`, `forward_*`, `reply_*`, meeting responses,
  calendar invitations, content filters that can forward) needs a passkey too.
- Agent: a mail server that is slow while an approved change's account is restored leaves the proposal
  pending (`503 ACCOUNT_UNAVAILABLE`) instead of `unknown`; decided proposals are deleted after 30 days
  (newest 200 per profile kept); a repeated proposal moves to the newer run and only tightens its
  passkey requirement; a failed run still records its proposals.
- Agent: Test probe and Run now have their own per-server budget and a per-profile cap
  (`PROBES_PER_PROFILE_PER_MINUTE`, default 6), so they cannot starve scheduled probes;
  `POST /api/tasks/{id}/run` is rate-limited; a condition may contain at most one `new` check.
- Agent: analyze_result's operator timeout starts after the artifact has loaded into the worker (30 s
  load limit of its own); browsers without module workers get a refusal instead of running patterns
  on the page thread. The task editor takes the artifact threshold from `/api/config`.
- Agent (server mode with tasks off, or no `DATA_KEY`): task, approval and probe endpoints answer
  `404 TASKS_DISABLED` / `TRIGGERS_DISABLED` instead of 500.
- Every `domain_*` result now also hides token and credential fields, as documented.
- **Security:** `get_dkim_settings` (sysadmin) returned the whole domain-settings response when it
  could not find its DKIM section, including authentication-provider secrets such as an LDAP
  password, which then went to the user's LLM provider. It now returns only the DKIM fields, or an
  error. Sysadmin tool results now redact secret-like fields (password, secret, token, API key,
  private key, credential), with the redactor shared with the domain-admin tools.
- Agent: after a passkey sign-in, until the profile's idle timeout passes without activity, a new tab, a reload or a restarted browser on the
  same unlocked profile goes straight back into the chat. Before, it asked for the OpenRouter key
  again, which the profile already stores.

- Agent: tool cards no longer collapse into thin lines once a chat is taller than the window.
- Agent: dismissing a password manager's passkey prompt (e.g. LastPass) no longer fails the
  ceremony; it falls through to the browser's own passkey sheet (iCloud Keychain, Windows Hello…).
- **Security:** `upload_attachment` (`filePath`) and `download_email_attachment` (`savePath`) read and
  wrote any path on the *server's* filesystem. In smartermail-agent and HTTP MCP servers that is
  the server's own files, reachable by any signed-in user, or by an email crafted to steer the model.
  `download_email_attachment` is a read tool, so this even worked on read-only accounts. Paths
  now work only where `SMARTERMAIL_LOCAL_FILES` is on, and never in smartermail-agent.
- `send_email` and `send_email_with_attachments` sent all recipients as one entry under the sender's
  name (`"user" <a, b>`); each To / CC / BCC recipient is now its own entry.
- Inline image uploads returned `cid:cidgenerate` instead of the content ID SmarterMail assigned, so
  the image did not show.
- Attachments larger than one 2 MB upload chunk failed ("The input does not contain any JSON
  tokens"): SmarterMail answers intermediate chunks with an empty body.
- Failed uploads report SmarterMail's message (e.g. "Invalid content ID") instead of only the
  status code.
- `get_email_attachments` reported every attachment as not inline, with no content type, index 0.
  It now gives the content type (from the file name), SmarterMail's part ID and approximate size,
  and the message's inline content IDs. `read_email_part` now reads `.txt`/`.ics`/`.json`/… attachments
  that it wrongly reported as binary.

## [1.1.0]

### Added

- **smartermail-agent server mode**, now the default. A user can save the chat to a **profile** on
  the server: its accounts and settings (OpenRouter key, model, tool groups), encrypted with a
  passkey (WebAuthn PRF) the server never sees the secret of. **Sign in with passkey** brings them
  back in any browser; a one-time recovery code is the way back if every passkey is lost.
- **Scheduled tasks** (server mode with `DATA_KEY`): prompts the server runs on a cron schedule with
  accounts the user explicitly lets tasks use. They only get read tools plus the write tools ticked
  for the task, at most N changes per run, enforced by the server; **Test run** simulates changes.
  Results are kept encrypted to the profile and can be emailed to the account's own address.
- `GET /api/config` (the server's mode and features); `destructive` in `GET /api/tools`.
- New settings: `BROWSER_ONLY_MODE`, `DATA_DIR`, `DATA_KEY`, `DATA_KEY_PREVIOUS`, `PUBLIC_ORIGIN`,
  `PROFILE_MAIL_HOSTS`, `PROFILE_IDLE_DAYS`, `MAX_PROFILES`, `TASKS_ENABLED`, `TASK_*`, `LLM_BASE_URL`
  (see `docs/agent.md`).

### Changed

- **Upgrading the agent:** server mode writes a SQLite database to `/data`. Mount a volume there to
  keep profiles across container re-creation, or set `BROWSER_ONLY_MODE=true` to keep the old
  behaviour (nothing stored). Existing sign-ins, remember-me and `/mcp` work as before in both modes;
  where profiles are available they replace "Remember me on this device" in the UI.

### Fixed

- `examples/`: `docker compose up <one service>` no longer fails because another service's settings
  are empty, and `.env.example` quotes passwords so a `$` in one survives Compose's interpolation.

## [1.0.0]

First public release.

### Added

- **smartermail-agent**: browser chat for SmarterMail. You bring your own OpenRouter key, and one chat
  can hold several accounts (mailbox, domain admin, system admin). It includes two-factor sign-in,
  "remember me" with an optional passkey lock, and its own `/mcp` endpoint with revocable tokens.
- **smartermail-mcp-user**: MCP server for one mailbox, with 59 tools and 5 prompts. It adds 107
  domain-admin tools when the account is a domain admin.
- **smartermail-mcp-admin**: MCP server for one system-admin account, with 58 tools.
- Both MCP servers run over **stdio** (`--stdio`) or **Streamable HTTP**. They are **read-only by
  default**, and read-only mode leaves write tools out of `tools/list` entirely.
- Every tool carries `readOnlyHint`, and destructive ones carry `destructiveHint`. The tool reference
  in `docs/tools.md` is generated from the code.
- Multi-arch images (`linux/amd64`, `linux/arm64`) on GHCR. They run as a non-root user.

### Security

- The agent checks mail-server addresses against private ranges again at connect time, which stops
  DNS rebinding, and it never follows redirects.
- The agent trusts forwarded client-IP headers only from `TRUSTED_PROXIES`.

### Roadmap

- A log-analysis harness for the admin server that runs in-process.
- NuGet tool packages (`dnx`) and an MCP Registry listing.

[Unreleased]: https://github.com/Carbonitex/smartermail-agent/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/Carbonitex/smartermail-agent/releases/tag/v1.1.0
[1.0.0]: https://github.com/Carbonitex/smartermail-agent/releases/tag/v1.0.0
