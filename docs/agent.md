# smartermail-agent (the browser chat)

A web page where you sign in to one or more SmarterMail accounts, paste your own
[OpenRouter](https://openrouter.ai/keys) key, and chat with an assistant that can use those accounts.
A hosted instance run by the author is at **https://carbonitex.dev/mail-agent/**. This page is about
running your own.

- Image: `ghcr.io/carbonitex/smartermail-agent` (`linux/amd64`, `linux/arm64`, runs as non-root, port 8080)

## What it does and doesn't do

- **Several accounts in one chat.** Use **+ Add account** to sign in to more mailboxes, a domain
  admin or a system admin, on the same server or different ones (up to five by default). Each
  account gets the tools its role allows, and the assistant chooses which account to use for each
  call.
- **Your AI key stays in your browser.** The chat loop runs in the page and talks to OpenRouter
  directly. The server never sees the key and never pays for inference.
- **Mail credentials are used once.** They are exchanged for a SmarterMail token at sign-in and not
  stored. The token lives in the server's memory for the session: until logout, 30 idle minutes, or
  12 hours. The server then asks SmarterMail to revoke it.
- **Nothing is written to disk.** There are no token files, no mail and no chat history on the
  server. Conversations live in the browser tab.
- **Read-only by default, per account.** Unless you tick "allow changes" when you add an account,
  the assistant isn't even shown the tools that could change anything as that account.
- **Two-step verification works** in the browser sign-in.
- **"Remember me on this device"** is opt-in, and only offered when the server has a `RESUME_KEY`.
  The *browser* keeps a sealed copy of the refresh tokens, and the server still stores nothing. It
  can optionally be locked with a passkey.
- **Why a server at all?** SmarterMail sends no CORS headers, so a browser can't call your mail
  server directly. This service relays those calls, and that's all it does.

## Run it

```bash
docker run -d --name smartermail-agent -p 8107:8080 --restart unless-stopped \
  -e RESUME_KEY="$(openssl rand -base64 32)" \
  ghcr.io/carbonitex/smartermail-agent:1
```

Open `http://localhost:8107/`. For anything beyond your own machine, serve it over **HTTPS**: the
session cookie is `Secure`, so browsers only keep it over HTTPS (or on `localhost`).

Generate `RESUME_KEY` once and store it: it must survive restarts. A new key signs every remembered
device out. To rotate without signing people out, move the old key to `RESUME_KEY_PREVIOUS` and keep
it there for `RESUME_DAYS`.

## Settings

All settings are optional.

| Variable | Default | Description |
|---|---|---|
| `PATH_BASE` | `/` | URL prefix when served under a path, e.g. `/mail-agent` |
| `TRUSTED_PROXIES` | unset | Comma-separated IPs or CIDRs of your reverse proxies. Only these may set `X-Forwarded-For` / `X-Forwarded-Proto`. When unset, forwarded headers are ignored |
| `TRUST_CF_CONNECTING_IP` | `false` | Behind Cloudflare: key rate limits off `CF-Connecting-IP` (only when the request came through a trusted proxy) |
| `RESUME_KEY` | unset | 32 bytes, base64 (`openssl rand -base64 32`). Turns on "Remember me on this device" |
| `RESUME_KEY_PREVIOUS` | unset | The previous key during a rotation (only used to open old bundles) |
| `RESUME_DAYS` | `30` | How long one password sign-in can be remembered (at most 60) |
| `SESSION_IDLE_MINUTES` | `30` | Idle timeout |
| `SESSION_MAX_HOURS` | `12` | Absolute session lifetime |
| `SESSION_MAX_ACCOUNTS` | `5` | Accounts per chat |
| `MCP_TOKEN_HOURS` | unset | Optional shorter lifetime for MCP tokens (below) |
| `HOST_FAILED_LOGIN_LIMIT` | `10` | Failed sign-ins per mail server before sign-ins to it pause |
| `HOST_FAILED_LOGIN_WINDOW_MINUTES` | `60` | Window for the limit above |
| `HOME_LINK_URL` | unset | Optional link under the sign-in form (http(s) or relative) |
| `HOME_LINK_TEXT` | `← Home` | Its text |
| `ALLOW_PRIVATE_HOSTS` | `false` | **Development or LAN only.** Allows `http://` and private or loopback mail servers, and turns the SSRF protection off |
| `CORE_CONSOLE_LOG` | `false` | Debugging: pass the API layer's verbose console output through (it contains mail metadata) |

## Behind a reverse proxy

**Set `TRUSTED_PROXIES` to the address your proxy connects from.** Rate limits are per client IP:
5 sign-ins per minute, 15 two-step codes per minute and 120 API calls per minute. Without the
setting, every visitor looks like the proxy and they all share one limit. What to use:

- proxy in the same Docker network: that network's CIDR, e.g. `172.18.0.0/16` (`docker network inspect <name>`)
- proxy on the host, forwarding to the published port: the Docker bridge gateway, usually `172.17.0.1`
- anything else: the proxy's IP

Only set `TRUST_CF_CONNECTING_IP=true` when Cloudflare really is in front of that proxy.

### At the site root

nginx:

```nginx
location / {
    proxy_pass http://smartermail-agent:8080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

Caddy (it sets the `X-Forwarded-*` headers itself):

```caddy
mail-agent.example.com {
    reverse_proxy smartermail-agent:8080
}
```

### Under a path, e.g. `/mail-agent/`

Set `PATH_BASE=/mail-agent` and pass the **full path, prefix included**, to the container. Don't
strip the prefix; the app removes it itself. The page must be loaded with its trailing slash, so
redirect the bare path to it.

nginx (`proxy_pass` with no URI part keeps the original path):

```nginx
location = /mail-agent { return 301 /mail-agent/; }
location ^~ /mail-agent/ {
    proxy_pass http://smartermail-agent:8080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

Caddy (`handle` keeps the prefix; `handle_path` would strip it, so don't use that):

```caddy
example.com {
    redir /mail-agent /mail-agent/
    handle /mail-agent/* {
        reverse_proxy smartermail-agent:8080
    }
}
```

The session cookie is scoped to `PATH_BASE`, so the agent doesn't see the other site's traffic, and
the other site doesn't see the agent's cookie.

### Health check

`GET /health` (and `<PATH_BASE>/health`) answers `200 smartermail-agent ok`. The image has no
`curl`, so use the bash probe from
[`examples/docker-compose.yml`](../examples/docker-compose.yml).

## Using it as an MCP server (multiple accounts, nothing to install)

The agent is also an MCP server at `<your agent URL>/mcp`. It serves the same tools, for every
account signed in to your chat. This is the easiest way to give Cursor or Claude Code **several
accounts at once**:

1. Sign in in the browser and add the accounts you want.
2. Open the **MCP** menu in the chat header and create a token (`sma_mcp_…`). It is shown once.
3. Configure your client with URL `https://your-host/mcp` (or `https://your-host/mail-agent/mcp`) and
   header `Authorization: Bearer sma_mcp_…` (see [clients.md](clients.md#generic-streamable-http)).

The token only opens `/mcp`. It can't add or remove accounts or create another token. It ends with
the browser session: on logout, after the idle timeout (MCP use counts as activity), or after
`SESSION_MAX_HOURS` at most. Creating a new token replaces the old one. For a server that stays up
indefinitely, run [smartermail-mcp-user](mcp-user.md) instead.

## Security notes

- **SSRF:** the relay refuses mail-server addresses that resolve to loopback, private (RFC 1918),
  link-local (including cloud metadata `169.254.169.254`), CGNAT, IPv6 ULA and similar ranges. It
  checks again when the connection is made, so a DNS answer that changes after the first check
  still can't reach a private address. It doesn't follow redirects. `ALLOW_PRIVATE_HOSTS=true`
  turns all of this off: use it only on a private deployment whose mail server is on your LAN.
- **Protecting your server's IP:** SmarterMail's intrusion detection counts failed logins per source
  IP, and every visitor signs in from the agent's IP. After `HOST_FAILED_LOGIN_LIMIT` failures
  against one mail server within the window, sign-ins to that server pause (`HOST_THROTTLED`). The
  defaults stay below SmarterMail's default brute-force rules.
- **Logs** contain host names, outcomes, tool names and timings. They never contain passwords,
  tokens, email addresses, tool arguments or results.
- More detail on the design: [CLAUDE.md](../CLAUDE.md) (Agent section) and [SECURITY.md](../SECURITY.md).
