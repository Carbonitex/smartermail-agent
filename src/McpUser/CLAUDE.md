# smartermail-mcp-user (src/McpUser)

MCP server for **one** SmarterMail account: email, calendar, contacts, tasks, notes, folders and
settings, plus the domain-admin tools (`domain_*`) when the account is a domain admin (see
`SMARTERMAIL_DOMAIN_TOOLS`). User docs: `docs/mcp-user.md`.

## Architecture

- `Program.cs` is only the host definition: everything shared with the admin server — configuration,
  startup sign-in, stdio / HTTP transport, API-key auth, the read-only filter, `/health` — lives in
  `src/Mcp.Hosting` (`McpHost.RunAsync`).
- Tools come from the shared libraries `src/Tools.Mailbox` (always) and `src/Tools.DomainAdmin` (only
  when `DomainToolsGate.cs` allows); this project holds no tool code.
- Prompts from `Prompts/` (namespace `SmarterMailMcp.Server.Prompts`), registered with
  `.WithPromptsFromAssembly(typeof(UserPrompts).Assembly)` — the assembly must be explicit, because the
  call is made from inside `src/Mcp.Hosting`.

## Environment variables

| Variable | Required | Description |
|----------|----------|-------------|
| `SMARTERMAIL_URL` | yes | SmarterMail base URL, e.g. `https://mail.example.com` |
| `SMARTERMAIL_USER` | yes | Login (email address) |
| `SMARTERMAIL_PASSWORD` | yes | Password (an app password works when the account has one) |
| `API_KEY` | HTTP only | Key clients send as `Authorization: Bearer`, `X-API-Key` or `?apiKey=`. Startup fails without it |
| `MCP_TRANSPORT` | no | `http` (default) or `stdio`; the `--stdio` argument does the same |
| `SMARTERMAIL_READ_ONLY` | no | **`true` by default.** `false` also registers write tools |
| `SMARTERMAIL_DOMAIN_TOOLS` | no | `auto` (default): after sign-in, GET `/api/v1/settings/domain/data` as the account (10 s timeout); 2xx registers the 107 `domain_*` tools, anything else leaves them out. `true` / `false` force it. One stderr line logs the decision |
| `SMARTERMAIL_TOKEN_FILE` | no | Token file, default `/tmp/smartermail_token.json` |

## Tools (59, or 166 for a domain admin)

Generated list: `docs/tools.md`. Read-only mode lists only tools marked `[McpServerTool(ReadOnly = true)]`
(23 mailbox + 44 domain reads); write tools are removed from `tools/list`, and the write tools also
check `ReadOnlyMode` themselves.

The `domain_*` tools call `/api/v1/settings/domain/*` (mailing lists: `.../mailing-lists/*`) and act on
the signed-in account's own domain. For a plain user every call returns 403 from SmarterMail, which is
why `auto` leaves them unregistered for one. Paths come from the public API reference and are not yet
exercised against a live domain-admin login: unconfirmed are whether `users-delete` / `users-disable`
want local parts or full addresses, whether `post-user` / `alias` accept the whole object read back
from GET, and that `maxMailboxSize` is bytes. `domain_get_settings` redacts password/secret-like fields.

The agent (`src/Agent`) uses the same libraries. A new tool class there ships here automatically
(domain ones when the gate is on) and fails the agent's startup until it is given a scope in
`src/Agent/Mcp/ToolPolicy.cs`.

### Mail: large bodies

`get_email_message` returns a real preview when HTML or plaintext exceeds 10k characters, plus
`htmlLength`, `plainLength`, `truncated`, and a hint. It does **not** replace the body with
`"<N characters>"`.

Use **`read_email_part`** to pull the rest:

| Mode | Args | Result |
|------|------|--------|
| Inventory | `uid`, `folderId` (omit `part`) | Every part with size and `readable` |
| Window | `part` + `offset`/`limit` | `content`, `totalChars`, `nextOffset`, `hasMore` |
| Grep | `part` + `pattern` | `matchCount` and per-match `offset`/`line`/`snippet` |

- `part`: `html`, `plain`, `text` (HTML converted to plaintext), `headers`, or `attachment:<filename>`
- Default `limit` is 8000 characters; `0` means to the end, still capped at ~200k per call
- Grep is literal by default (`ignoreCase` true). `isRegex=true` uses NonBacktracking with a 2s timeout,
  falling back to interpreted-with-timeout for lookarounds/backreferences
- Binary attachments are not decoded; inventory marks them `readable: false` and a part read tells you
  to use `download_email_attachment`
- Messages are cached in-process for ~60s so pagination does not refetch the full message each time

## Startup and health

See `src/Mcp.Hosting/CLAUDE.md`.
