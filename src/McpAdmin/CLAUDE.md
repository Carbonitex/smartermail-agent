# smartermail-mcp-admin (src/McpAdmin)

MCP server for **one** SmarterMail system-admin account: domains, users, certificates, DKIM, security,
spool, monitoring and log search. User docs: `docs/mcp-admin.md`.

## Architecture

- `Program.cs` is only the host definition; configuration, startup sign-in, stdio / HTTP transport,
  API-key auth, the read-only filter and `/health` live in `src/Mcp.Hosting` (`McpHost.RunAsync`).
- Tools come from the shared library `src/Tools.SysAdmin` (namespace `SmarterMailMcp.SystemAdmin.Tools`),
  registered with `.WithToolsFromAssembly(typeof(ServerTools).Assembly)`. This project holds no tool code.
- **No sysadmin tool checks `ReadOnlyMode` itself.** Read-only mode (the default) is enforced only by
  registration: tools without `[McpServerTool(ReadOnly = true)]` are removed from the server, so they
  cannot be listed or called. Mark a new read tool on purpose; an unmarked tool is a write.

## Environment variables

| Variable | Required | Description |
|----------|----------|-------------|
| `SMARTERMAIL_URL` | yes | SmarterMail base URL, e.g. `https://mail.example.com` |
| `SMARTERMAIL_ADMIN_USER` | yes | System admin login |
| `SMARTERMAIL_ADMIN_PASSWORD` | yes | System admin password |
| `API_KEY` | HTTP only | Key clients send as `Authorization: Bearer`, `X-API-Key` or `?apiKey=`. Startup fails without it |
| `MCP_TRANSPORT` | no | `http` (default) or `stdio`; the `--stdio` argument does the same |
| `SMARTERMAIL_READ_ONLY` | no | **`true` by default** (29 read tools). `false` registers all 58 |
| `SMARTERMAIL_TOKEN_FILE` | no | Token file, default `/tmp/smartermail_sysadmin_token.json` |

## Tools (58)

Generated list: `docs/tools.md` — Domains, UserAdmin, Server, Certificates, DKIM, Security, Spool,
Monitoring, LogSearch. `search_log_files` searches SmarterMail's logs through its API; analysis is left
to the client's model. Nothing in this server may start a process
(`tests/SmarterMail.Tests/ProcessSpawnGuardTests.cs` also covers `src/Mcp.Hosting`).

A new tool class in `src/Tools.SysAdmin` ships here automatically and fails the agent's startup until
it is given a scope in `src/Agent/Mcp/ToolPolicy.cs`.

## Startup and health

See `src/Mcp.Hosting/CLAUDE.md`.
