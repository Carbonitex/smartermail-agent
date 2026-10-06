# SmarterMailMcp.Hosting (src/Mcp.Hosting)

The shared host for the fixed-account MCP servers (`src/McpUser`, `src/McpAdmin`). Each server's
`Program.cs` is one `McpHost.RunAsync(args, McpHostDefinition, addPrimitives)` call.

- `McpHostSettings.cs` — parses and validates the environment **before** signing in, and reports every
  problem at once (exit code 1). `SMARTERMAIL_READ_ONLY` defaults to **true**; an unrecognised value is
  an error, never a guess. The HTTP transport refuses to start without `API_KEY`.
  `SMARTERMAIL_LOCAL_FILES` (default: on for stdio, off for HTTP) sets `GlobalContext.LocalFileAccess`,
  which the attachment tools check before touching a path (the agent never sets it).
- `McpHostDefinition.MapHttpEndpoints` adds routes next to `/mcp` behind the same API key (McpUser:
  `POST /attachments`); `Instructions` sets the MCP `instructions` from the settings.
- `McpHost.cs` — startup sign-in, then either transport:
  - **stdio** (`--stdio` or `MCP_TRANSPORT=stdio`): `Host.CreateApplicationBuilder` +
    `WithStdioServerTransport()`. stdout carries JSON-RPC only: every logger writes to stderr
    (`LogToStandardErrorThreshold = Trace`), and Core's `Logging` writes stderr unless
    `Logging.Enabled` is set — keep it that way. No API key: the client launched the process.
  - **HTTP** (default): stateless `/mcp` behind `ApiKeyAuthenticationHandler`, anonymous `/health`.
  - `serverInfo` = the host's name and the release version (`-p:Version`, source-link suffix removed).
- `ReadOnlyTools.cs` — `WithReadOnlyFilter(readOnly)`: a `PostConfigure<McpServerOptions>` that removes
  every tool without `ReadOnlyHint == true` from `ToolCollection` after the SDK has filled it. Fail
  closed: an unannotated tool is a write.
- `ApiKeyAuthenticationHandler.cs` — key from `X-API-Key`, `Authorization: Bearer` or `?apiKey=`
  (first present wins; constant-time compare).

Tests: `tests/SmarterMail.Tests/McpHostingTests.cs` (settings, read-only counts per host).

## MCP protocol dual-stack (HTTP)

`ModelContextProtocol.AspNetCore` 2.1.0 with `WithHttpTransport(options => options.Stateless = true)`:

- **Cursor / Streamable HTTP:** `POST /mcp` `initialize` with `protocolVersion` `2025-11-25` (or
  `2025-06-18`). The SDK echoes the version the client asked. Never answer initialize with
  `2026-07-28` — Cursor rejects that.
- **2026-07-28 (JSON-only, non-streamable):** `server/discover` plus per-request `params._meta`
  (`io.modelcontextprotocol/protocolVersion`) and headers `MCP-Protocol-Version` / `Mcp-Method`. No
  `Mcp-Session-Id`. `initialize` on this version is rejected; use discover instead.
- **GET/DELETE `/mcp`:** 405 is correct. Do not add an SSE GET stream.
- Accept must include both `application/json` and `text/event-stream` or the SDK returns 406.

## Startup and health

- **Configuration errors** exit immediately with code 1: misconfiguration is not transient.
- **Transient sign-in failures retry forever** with capped exponential backoff (2s, 4s, 8s, 16s, 32s,
  then every 60s) via `src/Core/Auth/StartupSignIn.cs`, so a SmarterMail that is still booting does not
  kill the container. Transient = no connection / timeout / DNS, HTTP 5xx, 408, 429, a non-JSON or
  malformed answer (e.g. 200 without a token, 404), an unknown code on a 2xx/400, any exception. One
  stderr line per failure: `Sign-in attempt N failed: <reason>. Retrying in Ns.` (never the password).
- **A credential rejection is fatal: exit code 2, no retry.** Rejection = SmarterMail answered with a
  known refusal code (`USERNAME_OR_PASSWORD_INCORRECT`, `USER_NOT_FOUND`, `TWO_FACTOR_REQUIRED`, password
  change / expired, app password, anything `*DISABLED*` / `*LOCKED*` / `*BLOCKED*` / `*SUSPENDED*`) on
  any status below 500, or a JSON 401/403 that is not a success. Retrying would only feed SmarterMail's
  brute-force detection, which can IP-block the address the server connects from. A restart policy that
  keeps restarting an exit-2 container defeats this — use `restart: on-failure:3` or similar.
  Classifier: `src/Core/Auth/AuthenticationResult.cs`.
- SIGINT / SIGTERM during the wait exits promptly with code 0.
- `GET /health` (HTTP only) → `200 <server name> <version> ok`, anonymous, liveness only — it never
  calls SmarterMail.
- **Trade-off:** sign-in runs before the host is built, so nothing listens (not even `/health`) while
  retrying. A healthcheck needs a generous `start_period`. The aspnet base image has no curl/wget, so
  probe with bash `/dev/tcp` (see `examples/docker-compose.yml`).
