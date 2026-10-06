# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/). One version covers all three images.

## [Unreleased]

### Added

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

- `upload_attachment`'s `contentId` is replaced by `inline`. SmarterMail rejects every content ID
  but its own, so custom IDs never worked.

### Fixed

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
