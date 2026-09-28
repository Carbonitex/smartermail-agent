# smartermail-mcp-user

An MCP server for **one SmarterMail account**. It covers email, calendar, contacts, tasks, notes,
folders and settings. When the account is a domain admin, it also gets the domain-admin tools for
that domain.

- Image: `ghcr.io/carbonitex/smartermail-mcp-user` (`linux/amd64`, `linux/arm64`, runs as non-root)
- Tools: 59 mailbox tools, plus 107 `domain_*` tools for a domain admin
  ([list with read/write marks](tools.md))
- Prompts: `daily_briefing`, `search_all`, `follow_up_email`, `send_email_with_attachment`,
  `send_email_with_inline_image`

## Two ways to run it

| | stdio | HTTP |
|---|---|---|
| Start with | `docker run -i --rm … --stdio` launched **by your MCP client** | a long-running container (`docker compose up`) |
| Who can use it | the one client that started it | any client that has the API key |
| API key | not needed | `API_KEY` required |
| Where the SmarterMail password lives | in the client's config file | only on the server |
| Good for | one person on their own machine | a team, a remote host, several clients |

### stdio

```bash
docker run -i --rm \
  -e SMARTERMAIL_URL=https://mail.example.com \
  -e SMARTERMAIL_USER=you@example.com \
  -e SMARTERMAIL_PASSWORD='…' \
  ghcr.io/carbonitex/smartermail-mcp-user:1 --stdio
```

You would not normally run this yourself. Your MCP client runs it; see [clients.md](clients.md).
stdout carries only MCP messages, and all logging goes to stderr. You can use
`MCP_TRANSPORT=stdio` instead of the `--stdio` argument.

### HTTP

```bash
docker run -d --name smartermail-mcp-user -p 127.0.0.1:8102:8080 \
  -e SMARTERMAIL_URL=https://mail.example.com \
  -e SMARTERMAIL_USER=you@example.com \
  -e SMARTERMAIL_PASSWORD='…' \
  -e API_KEY="$(openssl rand -hex 32)" \
  --restart on-failure:3 \
  ghcr.io/carbonitex/smartermail-mcp-user:1
```

Or use [`examples/docker-compose.yml`](../examples/docker-compose.yml).

- **Endpoint:** `POST http://host:8102/mcp`. It is stateless Streamable HTTP, so there's no session
  id and no `GET` stream.
- **Accept header:** requests must accept both `application/json` and `text/event-stream`. MCP
  clients do this for you.
- **Authentication:** send the key in `Authorization: Bearer <API_KEY>` (preferred) or
  `X-API-Key: <API_KEY>`. The `?apiKey=` query parameter also works for clients that can't set
  headers, but query strings end up in proxy logs, so avoid it if you can.
- **Health:** `GET /health` answers `200 smartermail-mcp-user <version> ok` without authentication.
  It only shows that the process is up; it doesn't contact SmarterMail.
- **Exposure:** the server doesn't do TLS. If anything other than localhost will reach it, put it
  behind a reverse proxy with TLS.

## Settings

| Variable | Required | Default | Description |
|---|---|---|---|
| `SMARTERMAIL_URL` | yes | | Your SmarterMail address, e.g. `https://mail.example.com` |
| `SMARTERMAIL_USER` | yes | | The login, usually the email address |
| `SMARTERMAIL_PASSWORD` | yes | | The password (see [two-step verification](#accounts-and-two-step-verification)) |
| `API_KEY` | HTTP only | | The key clients must send. The server won't start in HTTP mode without one |
| `MCP_TRANSPORT` | no | `http` | `http` or `stdio`. The `--stdio` argument does the same |
| `SMARTERMAIL_READ_ONLY` | no | `true` | `false` also lists tools that send, change or delete |
| `SMARTERMAIL_DOMAIN_TOOLS` | no | `auto` | `auto`, `true` or `false`. See below |
| `SMARTERMAIL_TOKEN_FILE` | no | `/tmp/smartermail_token.json` | Where the session token is kept inside the container |

Any value it doesn't recognise is treated as an error, not guessed. The server lists every problem
it finds and exits with code 1.

## Read-only mode

Read-only is **on by default**. In read-only mode, write tools aren't hidden behind an error; they
aren't registered at all. The client sees only tools that read:

| Account | Read-only | Read-write |
|---|---|---|
| Mailbox | 23 tools | 59 tools |
| Domain admin | 67 tools (23 + 44 domain) | 166 tools |

Set `SMARTERMAIL_READ_ONLY=false` to allow sending, moving, deleting and settings changes. Every
tool carries MCP annotations. `readOnlyHint` marks tools that only read, and `destructiveHint` marks
writes that delete, disable or disconnect. Clients that support these use them to decide when to
ask you before running a tool.

## Domain-admin tools

With `SMARTERMAIL_DOMAIN_TOOLS=auto` (the default), the server checks once at startup whether the
account can read its domain's settings. It logs one line with the result:

```
Domain-admin tools: enabled (probe 2xx)
Domain-admin tools: disabled (probe 403)
```

If the check succeeds, the 107 `domain_*` tools are registered. They act only on the signed-in
account's own domain. Use `true` or `false` to force the choice either way. For a plain mailbox user
the tools would fail with 403 anyway.

## Accounts and two-step verification

The server signs in with a username and password. It can't answer a two-step verification prompt,
so an account that requires it is **rejected**: the server exits with code 2. You have two options:

- Use a **dedicated account** without two-step verification that has only the access you want the
  assistant to have. This is recommended.
- If your SmarterMail version supports **app passwords**, use one as `SMARTERMAIL_PASSWORD`. This
  should work but hasn't been verified yet. Please
  [report](https://github.com/Carbonitex/smartermail-agent/issues) whether it does.

## Startup, restarts and health checks

- **Configuration errors** (missing or invalid settings) exit with code 1 before contacting SmarterMail.
- **Temporary failures** (server unreachable, timeouts, 5xx) are retried forever with backoff of 2s,
  4s, 8s and so on, up to 60s. Nothing listens during that time, not even `/health`, so give health
  checks a generous `start_period`.
- **Rejected credentials** (wrong password, unknown user, two-step required, disabled account) exit
  with **code 2 and are not retried**. Repeated failed logins can get the server's IP blocked by
  SmarterMail's intrusion detection. Use `restart: on-failure:3` (or `--restart on-failure:3`), not
  `always` or `unless-stopped`, which would keep retrying a bad password.
- The image has no `curl`. Health-check it with bash:

  ```yaml
  healthcheck:
    test: ["CMD", "bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080; printf 'GET /health HTTP/1.0\\r\\n\\r\\n' >&3; head -1 <&3 | grep -q 200"]
    interval: 30s
    timeout: 10s
    retries: 3
    start_period: 120s
  ```

## Reading large emails

`get_email_message` returns a preview when a body is over 10,000 characters. `read_email_part` gets
the rest. It has three modes: list the parts, read a window (`offset`/`limit`), or search (`pattern`).
The model uses these on its own; you only need to know they exist if you're writing prompts.
