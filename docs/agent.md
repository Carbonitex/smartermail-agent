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
- **Two modes.** In **server mode** (the default) users may save a **profile**: their accounts and
  settings, encrypted with a passkey, so they can sign in from any browser; with `DATA_KEY` they can
  also run **scheduled tasks**. In **browser-only mode** (`BROWSER_ONLY_MODE=true`) nothing is ever
  written to disk: no token files, no mail, no chat history. Conversations live in the browser tab
  in both modes. The hosted instance runs browser-only.
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
  -v smartermail-agent-data:/data \
  -e DATA_KEY="$(openssl rand -base64 32)" \
  -e PUBLIC_ORIGIN=https://mail-agent.example.com \
  ghcr.io/carbonitex/smartermail-agent:1
```

Open `http://localhost:8107/` (or your `PUBLIC_ORIGIN`). For anything beyond your own machine, serve
it over **HTTPS**: the session cookie is `Secure`, and passkeys only work on HTTPS or `localhost`
(never on a bare IP address).

Generate `DATA_KEY` once and store it somewhere other than the volume: it seals what the server reads
while users are away (scheduled tasks). A new key breaks every task and every account delegated to
tasks; to rotate, move the old key to `DATA_KEY_PREVIOUS`. Without `DATA_KEY` profiles still work and
scheduled tasks are off.

To store nothing at all, run `-e BROWSER_ONLY_MODE=true` and drop the volume. Then set
`RESUME_KEY="$(openssl rand -base64 32)"` to offer "Remember me on this device" instead (the
browser keeps a sealed bundle; a new key signs every remembered device out; to rotate without that,
move the old key to `RESUME_KEY_PREVIOUS` for `RESUME_DAYS`).

## Profiles (server mode)

After signing in, a user can **Create profile**: their browser creates a passkey (iCloud Keychain,
Google Password Manager, 1Password, Windows Hello or a security key; it must support the WebAuthn
**PRF** extension, which current Chrome, Edge, Safari and Firefox do) and the chat's accounts and
settings (OpenRouter key, model, tool groups) are saved to the server, encrypted. On another browser,
**Sign in with passkey** brings them back. A one-time **recovery code** opens the profile if every
passkey is lost; more passkeys can be added from the **Profile** menu.

- The passkey's PRF output never leaves the browser. It unwraps a random profile key, from which the
  browser derives the keys that encrypt the settings (never sent) and the stored accounts (sent at
  unlock and held in server memory while the profile is in use).
- Logging out locks the profile: the server forgets the accounts but keeps their stored tokens.
  Removing an account (×) signs it out on SmarterMail and deletes it from the profile.
- Each stored account lives as long as SmarterMail's refresh token. One that SmarterMail no longer
  accepts is listed as "sign in again"; signing in to it again keeps its place (and its tasks).
- `PROFILE_MAIL_HOSTS` limits which mail servers' accounts may be saved, e.g. your own.
- Profiles nobody opens for `PROFILE_IDLE_DAYS` are deleted.
- Profile → Settings: how long a profile's sessions may sit idle (5 minutes up to
  `PROFILE_MAX_IDLE_MINUTES`; the default is `SESSION_IDLE_MINUTES`), and whether accounts added
  later start with changes allowed. Within the idle time, a new tab or a restarted browser opens the
  profile without the passkey.
- Browsers without PRF fall back to "Remember me on this device" when `RESUME_KEY` is set.

## Scheduled tasks (server mode with `DATA_KEY`)

A task is a prompt the server runs on a schedule, for example *every weekday at 07:00, summarise
unread mail and email me the summary*. **Tasks** in the chat header lists, edits and runs them; each
run's result (report and every tool call) stays under **Results**, encrypted to the profile, and can
also be emailed to the account's own address.

Before a task can run, in the **Profile** menu:

1. tick **"Accounts tasks may use"** for each account it should use. That account's sign-in is then
   sealed with `DATA_KEY`, so the server (and its operator) can use it while you are away;
2. save an **OpenRouter key for tasks**, best one with a spending limit.

Safety rules, enforced by the server rather than the prompt:

- A task only sees the read tools of its accounts, plus the write tools ticked for it (nothing by
  default), at most a set number of changes per run. Writes need an account signed in with
  "Allow changes".
- The model is told to treat mail content as data, never as instructions. **Test run** simulates
  every change so you can see what a task would do.
- Three failed runs in a row pause a task; a rejected sign-in or OpenRouter key pauses it at once.
- `TASKS_ENABLED=false` switches tasks off for the whole instance; each profile can pause its own.

### Invite-only tasks (`TASKS_ACCESS=invite`)

A delegated account is a sign-in the server can use on its own, so on a public instance you may want
to choose who can delegate. With `TASKS_ACCESS=invite`, profiles still work for everyone, but
scheduled tasks are closed to a profile until it redeems an **invite code** under **Profile → Scheduled
tasks** (or **Tasks**). Until then it cannot delegate an account, save a task key, create, edit or run
a task, test a probe, or approve a queued change, and the server keeps none of its accounts alive.

You make and revoke codes with the same image, against the same `DATA_DIR`. There is no admin web page.

```bash
docker exec <container> dotnet SmarterMailAgent.dll invites create --note "for Sam"   # prints the code, once
docker exec <container> dotnet SmarterMailAgent.dll invites create --uses 5 --days 14
docker exec <container> dotnet SmarterMailAgent.dll invites list
docker exec <container> dotnet SmarterMailAgent.dll invites revoke <id> [--profiles]
docker exec <container> dotnet SmarterMailAgent.dll access list
docker exec <container> dotnet SmarterMailAgent.dll access grant <profileId>
docker exec <container> dotnet SmarterMailAgent.dll access revoke <profileId>
```

To do the same from the browser, put your own profile ID (shown under **Profile → Settings**) in
`ADMIN_PROFILES`. While that profile is unlocked with its passkey, its Profile menu has an **Invites
(admin)** section: make a code (shown once, with Copy), see and revoke codes, and see and revoke which
profiles may use tasks. To everyone else the admin endpoints don't exist (`404`).

A code is 16 characters (`XXXX-XXXX-XXXX-XXXX`, 80 random bits; case, spaces and dashes don't
matter). It is good for one profile unless you set `--uses`, and never expires unless you set `--days`.
The server stores only a hash of the code. Revoking a profile's access pauses its tasks, deletes its
task key and denies its pending approvals. Its delegated accounts go back under the profile's own key
the next time the owner unlocks it, and they are no longer refreshed in the meantime. Switching an
existing instance to `invite` affects profiles that already have tasks: give them access with
`access grant` first, or their tasks pause on their next run.

Run one replica in server mode: the database is SQLite and the scheduler is in-process.

## Settings

All settings are optional.

| Variable | Default | Description |
|---|---|---|
| `BROWSER_ONLY_MODE` | `false` | `true` = store nothing on the server: no profiles, no tasks, `DATA_DIR` never touched |
| `DATA_DIR` | `/data` | Server mode: where the SQLite database lives. Mount a volume here |
| `DATA_KEY` | unset | 32 bytes, base64. Turns on scheduled tasks (seals delegated accounts, task definitions, the tasks' OpenRouter key) |
| `DATA_KEY_PREVIOUS` | unset | The previous `DATA_KEY` during a rotation |
| `PUBLIC_ORIGIN` | unset | The site's origin, e.g. `https://mail-agent.example.com`. Passkeys are bound to its host. Unset = taken from each request |
| `PROFILE_MAIL_HOSTS` | unset | Comma-separated mail server host names whose accounts may be saved to profiles. Unset = any |
| `PROFILE_IDLE_DAYS` | `180` | Delete profiles nobody has opened for this long |
| `PROFILE_MAX_IDLE_MINUTES` | `480` | Longest session idle timeout a user may choose for their profile (sessions still end at `SESSION_MAX_HOURS`) |
| `MAX_PROFILES` | `1000` | Profiles this instance holds at most |
| `TASKS_ENABLED` | `true` | `false` switches scheduled tasks off |
| `ADMIN_PROFILES` | unset | Comma-separated profile IDs that may manage task invites from the Profile menu |
| `TASKS_ACCESS` | `open` | `invite` = tasks only for profiles that redeemed an invite code (see [Invite-only tasks](#invite-only-tasks-tasks_accessinvite)) |
| `TASK_CONCURRENCY` | `2` | Task runs at once |
| `TASK_TIMEOUT_MINUTES` | `10` | Longest a run may take |
| `TASK_MAX_TOOL_ROUNDS` | `15` | Tool rounds per run |
| `TASK_MIN_INTERVAL_MINUTES` | `15` | Shortest allowed gap between a task's runs |
| `TASKS_PER_PROFILE` | `10` | Tasks per profile |
| `TASK_RUN_RETENTION` | `50` | Results kept per task |
| `LLM_BASE_URL` | OpenRouter | OpenAI-compatible endpoint scheduled tasks call |
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
  tokens, email addresses, tool arguments or results (scheduled runs log task ids and counts only).
- **Server mode at rest:** see the threat model in [SECURITY.md](../SECURITY.md). In short, the
  database alone reveals nothing; with `DATA_KEY` it reveals delegated accounts and task definitions,
  never settings, other accounts or results.
- More detail on the design: [CLAUDE.md](../CLAUDE.md) (Agent section) and [SECURITY.md](../SECURITY.md).
