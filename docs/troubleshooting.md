# Troubleshooting

For the MCP servers, check the container's log first (`docker logs <container>`). It prints the
version, the transport and read-only mode at startup, then the sign-in result and whether the
domain tools were enabled. It never logs passwords.

## MCP servers

### The container exits immediately with code 1

The configuration is invalid. The log lists every problem, for example:

```
Configuration error: Missing SMARTERMAIL_PASSWORD.
Configuration error: API_KEY is required for the HTTP transport (generate one with: openssl rand -hex 32). …
```

In HTTP mode `API_KEY` is required. With `--stdio` it isn't needed.

### The container exits with code 2

SmarterMail **rejected the sign-in**: wrong password, unknown user, disabled or locked account,
password expired, or two-step verification required. The server deliberately doesn't retry,
because every failed login counts toward SmarterMail's intrusion detection (IDS), which can block
the IP the server connects from.

- Fix the credentials, then start the container again.
- Don't use `restart: always` or `unless-stopped` for the MCP servers; use `on-failure:3`.
- If the IP is already blocked, every sign-in (including from webmail on that IP) fails until the
  block expires or an admin removes it. In SmarterMail, a system admin can find it under the
  security / IDS blocked-IP list. Check your version's admin UI for the exact location.

### Two-step verification

The MCP servers sign in with a username and password and can't answer a two-step prompt, so an
account that requires it is rejected (exit 2). Use a dedicated account without two-step
verification, or try an app password if your SmarterMail supports them. App passwords should work
but haven't been verified yet, so please report either way. The browser chat supports two-step
verification.

### It hangs at "Authenticating with SmarterMail…"

SmarterMail can't be reached (DNS, firewall, TLS or a wrong URL). The server keeps retrying with
backoff and logs each attempt. Nothing listens until sign-in succeeds, so health checks fail during
that time. Check `SMARTERMAIL_URL` from inside the container's network.

### 401 Unauthorized

The API key is missing or wrong. Send it as `Authorization: Bearer <API_KEY>` or
`X-API-Key: <API_KEY>`. Only the first of those headers present is checked, so don't send a
different bearer token alongside `X-API-Key`.

### 406 Not Acceptable

The request's `Accept` header must include both `application/json` and `text/event-stream`. Real MCP
clients send this automatically; it usually only shows up with hand-written `curl` requests.

### 405 on GET /mcp

This is expected. The server is stateless and has no `GET` event stream. Clients use `POST`.

### The write tools are missing

Both servers are **read-only by default**, and write tools aren't listed at all. Set
`SMARTERMAIL_READ_ONLY=false`. In a stdio config, add it to both `env` and the `-e` list in `args`.

### The domain-admin tools are missing (user server)

Look for the startup line `Domain-admin tools: disabled (probe 403)` or similar. The account
couldn't read its domain's settings, so it isn't a domain admin on that server. If you know it
should work, set `SMARTERMAIL_DOMAIN_TOOLS=true` to register the tools anyway. SmarterMail still
decides whether each call is allowed.

### The stdio client reports JSON or parse errors

In stdio mode, stdout must contain nothing but MCP messages. Make sure the client starts the
container with `-i` and **without** `-t`, since a TTY mangles the stream. If it still happens,
something wrote to stdout. Please open an issue with the client name and the first bad line.

## The browser chat (agent)

### "Too many failed sign-ins to this mail server" (`HOST_THROTTLED`)

Every visitor signs in from the agent's single IP. So that visitors can't get that IP blocked on
your mail server, the agent pauses sign-ins to a server after `HOST_FAILED_LOGIN_LIMIT` failures
(default 10) within `HOST_FAILED_LOGIN_WINDOW_MINUTES` (default 60). Wait for the time shown, then
check the password.

### Everyone gets rate-limited together (429s) behind a reverse proxy

`TRUSTED_PROXIES` isn't set, so every request appears to come from the proxy's IP and all visitors
share one limit. Set it to the address the proxy connects from. See
[agent.md](agent.md#behind-a-reverse-proxy).

### "That host is not allowed" / "Only https:// is allowed."

The agent refuses mail servers on private, loopback, link-local and similar addresses, and plain
`http://`. This protects a public deployment from being used to probe internal networks. If you
self-host the agent on the same LAN as your mail server, and only then, set
`ALLOW_PRIVATE_HOSTS=true`. That turns the protection off.

### Sign-in succeeds but the next page load is signed out

The session cookie is `Secure`, so browsers only keep it over HTTPS or on `localhost`. Serve the
agent over HTTPS. If it's under a path, check that `PATH_BASE` matches the path your proxy uses and
that the proxy passes the prefix through unchanged.

### "Remember me on this device" isn't offered

The server has no `RESUME_KEY`. Set one (`openssl rand -base64 32`) and keep it stable.
