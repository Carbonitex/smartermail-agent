# Security

## Reporting a vulnerability

Please **do not open a public issue** for a security problem. Use GitHub's private reporting instead:
**Security → Report a vulnerability** on this repository. Include what you found, how to reproduce it,
and which part is affected (the agent, `smartermail-mcp-user`, `smartermail-mcp-admin`, or a shared
library). You should get a reply within a week.

This is a community project maintained in spare time, with no bug bounty. Problems in SmarterMail
itself should go to SmarterTools, not here.

## Supported versions

Only the latest release gets fixes. Images are tagged `X.Y.Z`, `X.Y`, `X` and `latest`, so pinning a
major tag (e.g. `:1`) picks up fixes automatically.

## Threat model in brief

**What the software holds**

| Component | Credentials it holds | Where |
|---|---|---|
| `smartermail-mcp-user` / `-admin` | One SmarterMail login (from the environment) and its access/refresh tokens | Memory and a token file (`SMARTERMAIL_TOKEN_FILE`, default under `/tmp` in the container) |
| Agent, browser-only mode | Tokens for whoever signs in | Memory only; nothing is written to disk. "Remember me" keeps an encrypted bundle **in the user's browser**, sealed with `RESUME_KEY` |
| Agent, server mode (default) | The same in memory, plus the refresh tokens of accounts users save to a **profile** | SQLite under `DATA_DIR`, encrypted (below) |
| Agent (browser) | The user's OpenRouter key | The browser's `sessionStorage`; sent to OpenRouter only. In a profile it is also stored encrypted with a key only the user's passkey yields |

**Server-mode profiles** are encrypted under a key the server never sees: a random profile key,
wrapped with each passkey's WebAuthn PRF output (and optionally a recovery code). From it the browser
derives a settings key (never sent), an accounts key (sent at unlock, held in memory while the profile
is unlocked: the server needs the tokens anyway to relay calls) and a key for the private half of a
P-256 key pair, to which the server seals each scheduled run's transcript. Accounts the user lets
**scheduled tasks** use are sealed with the server's `DATA_KEY` instead, as are task definitions and
the tasks' OpenRouter key: the server must read them while nobody is signed in.

| Someone who has | Can read |
|---|---|
| The database file only | Nothing but ids, timestamps, passkey public keys and task schedules |
| The database and `DATA_KEY` | Delegated accounts' refresh tokens, task definitions, the tasks' OpenRouter key. Not other accounts, settings or run transcripts |
| The running server | What it is using, as for any relay |

So **the operator of a server-mode instance can act as any account a user delegated to scheduled
tasks.** Keep `DATA_KEY` out of backups of the volume. A deployment that serves strangers should run
`BROWSER_ONLY_MODE=true`.

**Defaults that limit damage**

- Both MCP servers are **read-only unless `SMARTERMAIL_READ_ONLY=false`**. Write tools are not just
  refused in read-only mode: they are left out of the tool list entirely.
- Every tool is marked read or write (`readOnlyHint`), and writes that delete, disable or disconnect are
  marked `destructiveHint`, so MCP clients that ask before destructive calls can do so.
- The agent signs accounts in read-only by default. Admin tools count as writes unless marked
  read-only in code (fail closed).
- The MCP servers' HTTP transport will not start without `API_KEY`. The key is compared in constant
  time. It can also be sent as a `?apiKey=` query parameter, but query strings end up in access logs,
  so prefer the `Authorization` header.
- Nothing in the shared code can start a process (checked by a test on the compiled assemblies).

**Agent-specific protections**

- **SSRF:** users type the address of their mail server, so the agent refuses private, loopback,
  link-local, CGNAT and similar addresses. The check runs before sign-in and again at connect time
  (so DNS rebinding cannot slip past it), and the agent never follows redirects.
  `ALLOW_PRIVATE_HOSTS=true` turns all of this off; use it only for a private LAN deployment.
- **Brute force:** per-IP rate limits on sign-in, plus a cap on failed sign-ins per target mail server,
  kept below SmarterMail's default IDS thresholds so the agent's own IP does not get blocked.
- **Client IPs:** `X-Forwarded-For` and `CF-Connecting-IP` are ignored unless the direct peer is listed
  in `TRUSTED_PROXIES` (and, for `CF-Connecting-IP`, `TRUST_CF_CONNECTING_IP=true`).
- **Sessions:** HttpOnly, Secure, SameSite=Strict cookie. MCP clients get a separate, revocable token
  that only opens `/mcp`.
- **Logs** never contain passwords, tokens, session ids, email addresses, tool arguments or tool results.

**What it cannot protect against**

- Anyone who can read the MCP server's environment or token file can act as that account. Use a
  dedicated, least-privilege SmarterMail account, and never expose the admin server to the internet.
- A model with write tools can do anything that account can do. Keep servers read-only unless you need
  writes, and use a client that asks before running tools.
- Tool results, including mail contents, go to whichever model provider your MCP client or agent uses.
- Prompt injection: an email can contain text meant to steer the model. Read-only mode is the main
  defence against it. Scheduled tasks run unattended, so they may only make the changes the user ticked
  for that task (enforced by the server, not just the prompt), at most N per run, and a test run
  simulates them first.
