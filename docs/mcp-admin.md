# smartermail-mcp-admin

An MCP server for **one SmarterMail system-admin account**. It covers domains, users, spool, security
(blocked IPs, IP access rules, SMTP rules, spam servers), certificates, DKIM, monitoring and log
search.

- Image: `ghcr.io/carbonitex/smartermail-mcp-admin` (`linux/amd64`, `linux/arm64`, runs as non-root)
- Tools: 58 ([list with read/write marks](tools.md#system-admin-tools-58-29-read-29-write))

It runs the same way as [smartermail-mcp-user](mcp-user.md): stdio for a local client, or HTTP with
an API key. Transports, health checks and restart behaviour are identical, so this page only covers
what's different.

## Be careful with this one

A system-admin login can delete domains, disable users, stop services and drop connections. The
server has guard rails, but they only go so far:

- **It starts read-only.** Only the 29 tools that read are registered. The other 29 can't be listed
  or called until you set `SMARTERMAIL_READ_ONLY=false`.
- **Tools that delete, disable or disconnect are marked destructive** (`destructiveHint`). Clients
  that respect this ask you before running them. Check that yours does before you turn writes on.
- **Use a dedicated admin account** with only the rights you need, and without two-step
  verification. The server can't complete a two-step prompt; see
  [mcp-user.md](mcp-user.md#accounts-and-two-step-verification).
- **Never expose the HTTP port to the internet.** Bind it to `127.0.0.1` or a private network, put
  TLS in front of it, and treat `API_KEY` like the admin password. Anyone with the key can act as
  that admin.

## Settings

| Variable | Required | Default | Description |
|---|---|---|---|
| `SMARTERMAIL_URL` | yes | | Your SmarterMail address |
| `SMARTERMAIL_ADMIN_USER` | yes | | The system-admin login |
| `SMARTERMAIL_ADMIN_PASSWORD` | yes | | Its password |
| `API_KEY` | HTTP only | | The key clients must send (`Authorization: Bearer`, `X-API-Key`, or `?apiKey=` as a last resort) |
| `MCP_TRANSPORT` | no | `http` | `http` or `stdio`. The `--stdio` argument does the same |
| `SMARTERMAIL_READ_ONLY` | no | `true` | `false` registers all 58 tools |
| `SMARTERMAIL_TOKEN_FILE` | no | `/tmp/smartermail_sysadmin_token.json` | Where the session token is kept inside the container |

## Examples

stdio, launched by a client:

```bash
docker run -i --rm \
  -e SMARTERMAIL_URL=https://mail.example.com \
  -e SMARTERMAIL_ADMIN_USER=admin \
  -e SMARTERMAIL_ADMIN_PASSWORD='…' \
  ghcr.io/carbonitex/smartermail-mcp-admin:1 --stdio
```

HTTP, on localhost only:

```bash
docker run -d --name smartermail-mcp-admin -p 127.0.0.1:8103:8080 \
  -e SMARTERMAIL_URL=https://mail.example.com \
  -e SMARTERMAIL_ADMIN_USER=admin \
  -e SMARTERMAIL_ADMIN_PASSWORD='…' \
  -e API_KEY="$(openssl rand -hex 32)" \
  --restart on-failure:3 \
  ghcr.io/carbonitex/smartermail-mcp-admin:1
```

Client configs are in [clients.md](clients.md). Use the same shapes as for the user server, with the
admin image and the `SMARTERMAIL_ADMIN_*` variables.

## Logs

`search_log_files` searches SmarterMail's own logs through its API (by date range, log type and
search text). Your MCP client's model does the analysis. The server never runs other programs; the
test suite checks this.
