# smartermail-agent

Let AI assistants work with a [SmarterMail](https://www.smartertools.com/smartermail/) server:
your mailbox, your domain if you administer one, or the whole server.

> SmarterMail is a trademark of SmarterTools Inc. This is an independent community project, not
> affiliated with or endorsed by SmarterTools.

This repo contains three things:

- **smartermail-mcp-user**: an [MCP](https://modelcontextprotocol.io) server for one mailbox. It adds
  the domain-admin tools automatically when that account is a domain admin.
- **smartermail-mcp-admin**: an MCP server for one system-admin account.
- **smartermail-agent**: a browser chat. Sign in to one or more SmarterMail accounts, paste your own
  [OpenRouter](https://openrouter.ai/keys) key, and chat. Self-hosted, it can also keep a
  passkey-encrypted **profile** per user (sign in from any browser) and run **scheduled tasks**. A
  hosted instance run by the author is at **https://carbonitex.dev/mail-agent/** (browser-only:
  it stores nothing).

All three use the same tools: 59 for a mailbox, 107 for a domain admin and 58 for a system admin
([full list](docs/tools.md)).

## Which one do I want?

| You want to… | Use | Runs as |
|---|---|---|
| Give Claude, Cursor, VS Code or another MCP client **your mailbox**, plus **your domain** if you're a domain admin | [smartermail-mcp-user](docs/mcp-user.md) | Docker: stdio for one person, or HTTP for a shared server |
| Give an MCP client **server-wide admin** tools | [smartermail-mcp-admin](docs/mcp-admin.md) | Docker: stdio or HTTP |
| Chat in a browser with no install, or use **several accounts** at once | [smartermail-agent](docs/agent.md) | Hosted instance, or self-host with Docker |

Images for `linux/amd64` and `linux/arm64`:

- `ghcr.io/carbonitex/smartermail-mcp-user`
- `ghcr.io/carbonitex/smartermail-mcp-admin`
- `ghcr.io/carbonitex/smartermail-agent`

Available tags are `1.2.3`, `1.2`, `1`, `latest`, and `edge` (the tip of `main`). The examples pin `:1`.

## Quickstart: your mailbox in Claude Desktop (stdio)

The client starts the container itself and talks to it over stdin/stdout, so there is no port and
no API key. Add this to `claude_desktop_config.json` (Settings → Developer → Edit Config):

```json
{
  "mcpServers": {
    "smartermail": {
      "command": "docker",
      "args": ["run", "-i", "--rm",
               "-e", "SMARTERMAIL_URL", "-e", "SMARTERMAIL_USER", "-e", "SMARTERMAIL_PASSWORD",
               "ghcr.io/carbonitex/smartermail-mcp-user:1", "--stdio"],
      "env": {
        "SMARTERMAIL_URL": "https://mail.example.com",
        "SMARTERMAIL_USER": "you@example.com",
        "SMARTERMAIL_PASSWORD": "your-password"
      }
    }
  }
}
```

Restart Claude Desktop and ask "What's in my inbox?". The server starts **read-only**. To let it
send, move and delete, add `"SMARTERMAIL_READ_ONLY": "false"` to `env` and `"-e", "SMARTERMAIL_READ_ONLY"`
to `args`.

This setup keeps your password in the client's config file. If that's not acceptable, run the
server over HTTP instead (next section). Configs for Claude Code, Cursor, VS Code and other clients
are in [docs/clients.md](docs/clients.md).

## Quickstart: a shared MCP server over HTTP

```bash
curl -O https://raw.githubusercontent.com/Carbonitex/smartermail-agent/main/examples/docker-compose.yml
curl -o .env https://raw.githubusercontent.com/Carbonitex/smartermail-agent/main/examples/.env.example
# edit .env: SmarterMail URL and account, and an API key from `openssl rand -hex 32`
docker compose up -d smartermail-mcp-user

claude mcp add --transport http smartermail http://localhost:8102/mcp \
  --header "Authorization: Bearer $(grep '^USER_MCP_API_KEY=' .env | cut -d= -f2-)"
```

The endpoint is `POST /mcp` (Streamable HTTP, stateless). Clients authenticate with
`Authorization: Bearer <API_KEY>` or `X-API-Key`. The compose file binds the port to `127.0.0.1`.
Put a TLS reverse proxy in front of it before exposing it to anything else.

## Quickstart: self-host the chat

```bash
docker run -d --name smartermail-agent -p 8107:8080 \
  -v smartermail-agent-data:/data \
  -e DATA_KEY="$(openssl rand -base64 32)" \
  ghcr.io/carbonitex/smartermail-agent:1
# open http://localhost:8107/
```

The volume holds users' passkey-encrypted profiles and scheduled tasks; `DATA_KEY` turns tasks on.
Generate it once and keep it (a new key breaks every task). Set `BROWSER_ONLY_MODE=true` instead to
store nothing on the server, as the hosted instance does. For production, put it behind a TLS
reverse proxy and set `PUBLIC_ORIGIN` and `TRUSTED_PROXIES`: see [docs/agent.md](docs/agent.md),
which also covers hosting it under a path such as `/mail-agent/`.

## Safety defaults

- **Read-only by default.** Both MCP servers start read-only: they don't list tools that change
  anything (23 of 59 mailbox tools, 29 of 58 admin tools remain). Set `SMARTERMAIL_READ_ONLY=false`
  to allow writes. In the chat, read-only is a per-account checkbox and is ticked by default.
- **Every tool is marked.** Each tool declares whether it only reads, and the ones that delete,
  disable or disconnect are marked destructive. Clients see these as `readOnlyHint` and
  `destructiveHint`.
- **Use a dedicated account**, especially for the admin server, with only the rights you need. The
  MCP servers sign in with a username and password, so the account can't require two-step
  verification (see [troubleshooting](docs/troubleshooting.md#two-step-verification)).
- **Bad credentials are not retried.** Repeated failed logins could get your IP blocked by
  SmarterMail's intrusion detection, so a rejected login exits with code 2 and stays down.
- **The chat stores nothing.** Your OpenRouter key never leaves the browser. SmarterMail tokens live
  in server memory for the session only. No mail, tokens or chat history are written to disk.
- **The chat won't reach private networks.** It refuses to connect to private, loopback and
  link-local addresses. That check is repeated when the connection is made, so DNS rebinding
  can't get around it.

Reporting a vulnerability: see [SECURITY.md](SECURITY.md).

## Documentation

- [docs/mcp-user.md](docs/mcp-user.md): the mailbox/domain MCP server, all settings
- [docs/mcp-admin.md](docs/mcp-admin.md): the system-admin MCP server
- [docs/clients.md](docs/clients.md): Claude Desktop, Claude Code, Cursor, VS Code, generic HTTP
- [docs/agent.md](docs/agent.md): self-hosting the chat, reverse proxies, its own `/mcp` endpoint
- [docs/tools.md](docs/tools.md): every tool, with its read, write and destructive marks (generated)
- [docs/architecture.md](docs/architecture.md): how the repo fits together
- [docs/troubleshooting.md](docs/troubleshooting.md): common problems
- [examples/](examples/): `docker-compose.yml`, `.env.example`, MCP client configs

## Building from source

You need the .NET 10 SDK. Docker is optional.

```bash
dotnet build SmarterMail.slnx
dotnet test SmarterMail.slnx
node --test 'src/Agent/wwwroot/dev/test/*.test.mjs'   # the chat's frontend tests
scripts/build-images.sh all                            # local images: smartermail/smartermail-*:latest
```

Every Dockerfile builds from the repo root and runs the test suites before publishing, so a failing
test fails the image. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Roadmap

- A log-analysis harness for the admin server that runs in-process, built on `search_log_files`
- Packaging as .NET tools (`dnx`) so the MCP servers can run without Docker
- Listing in the MCP registry

## License

[MIT](LICENSE)
