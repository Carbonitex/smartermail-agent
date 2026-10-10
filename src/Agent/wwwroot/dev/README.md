# Frontend dev harness

Everything in this folder is development-only. It is not needed at runtime and
should be excluded from the container image (`wwwroot/dev/` in `.dockerignore`).

## Run the stub server

```sh
node wwwroot/dev/stub-server.mjs
# → http://localhost:8787/mail-agent/
```

Then open <http://localhost:8787/mail-agent/>.

`stub-server.mjs` serves `wwwroot/` at `/mail-agent/` and implements the full
HTTP contract (the project's `CLAUDE.md`, plus
`plan-multiple-accounts-andmore.md` for multiple accounts) with ten fake tools
across the three scopes and canned data, so the UI can be exercised without the
.NET backend.

| Endpoint | Behaviour in the stub |
|---|---|
| `POST /api/auth/login` | Any hostname/email/password is accepted: a **new** session with one account, and the `sma_session` cookie (any session on the incoming cookie is dropped) — except the fixtures below. Failures carry `{ error, code }`. |
| `POST /api/accounts` | Needs a session. Same body and fixtures as login; adds an account (replacing one with the same server + login) and returns the SessionResponse, or a two-factor challenge **bound to this session**. `409 { code: "ACCOUNT_LIMIT" }` at the cap. |
| `DELETE /api/accounts/{id}` | `204`. Removing the last account ends the session and clears the cookie. Unknown id → `404`. |
| `POST /api/auth/two-factor` | `{ challengeId, code }` → the SessionResponse. A login challenge makes a new session + cookie; an add-account challenge adds to its session and answers `410` from any other (or no) session. |
| `GET /api/auth/session` | `{ expiresAt, maxAccounts, accounts: [{ id, handle, role, username, emailAddress, domain, baseUrl, readOnly }], mcpToken: { active, expiresAt } }` or `401`. |
| `POST /api/auth/logout` | `204`, session destroyed. |
| `POST /api/auth/token` | `{ token: "sma_mcp_…", expiresAt }`, replacing any previous token; `DELETE` → `204` revokes it. The stub has no `/mcp`, and (like the server) takes the session from the cookie only. |
| `GET /api/auth/resume/config` | `{ enabled, days }` (`RESUME=false` turns it off). |
| `PUT` / `GET` / `DELETE /api/auth/resume` | Remember this session / fetch its bundle / stop. Cookie only. The "bundle" is an opaque id; only the newest one per chain works, like SmarterMail's rotating refresh tokens. |
| `POST /api/auth/resume` | `{ bundle }` → the session fields plus `{ bundle, version, rememberedUntil, skipped }` and a new cookie. An older copy → `401 RESUME_EXPIRED`, garbage → `400 RESUME_INVALID`. Logout kills the chain. |
| `POST /api/dev/restart` | Dev only: drops every session, as a restart of the real server does. Use it to exercise resume. |
| `GET /api/tools` | `[{ name, description, inputSchema, category, scope, write }]` for the tools at least one account may run. With more than one account each schema gets an injected `account` enum of the eligible handles, `required` when more than one is eligible. |
| `POST /api/tools/call` | `200 {isError, content, account}`; `arguments.account` picks the account (optional when only one qualifies). Missing/unknown/wrong-role `account` → `200 isError` listing the valid handles. A write on a read-only account → `403` naming it. Unknown tool → `404`. Add `?delay=2000` to slow a tool down. |
| `GET /api/config` | `{ mode, resume, profiles, tasks }`; `MODE=browser` for browser-only, `TASKS=false` hides tasks. |
| `/api/profile/*`, `/api/tasks/*` | Server mode, in `stub-profiles.mjs`. WebAuthn is not verified (any credential is accepted by id), but the browser's crypto is real and fake task runs are sealed to the profile's public key exactly as the server seals them. |
| `GET /health` | `smartermail-agent ok (stub)` |

Environment: `PORT` (default `8787`, `0` picks a free port and logs it),
`PATH_BASE` (default `/mail-agent`), `ALLOW_PRIVATE_HOSTS`,
`TWO_FACTOR_TTL_MS` (default `300000`), `SESSION_MAX_ACCOUNTS` (default `5`),
`RESUME` (default on; `false` hides "Remember me"), `RESUME_DAYS` (default `30`),
`MODE=browser` (browser-only), `TASKS=false`, `TRIGGERS=false` (no condition tasks),
`SEED_PROFILE=1` (below).

In a remembered session every tool call and account change bumps the resume
version (as a real refresh would rotate a token), and a cookie request that
sent an older `X-Resume-Version` gets the newer number back.

### Tools

| Scope | Tool | Category | Write |
|---|---|---|---|
| Mailbox (User, DomainAdmin) | `list_folder_info_by_type` | Folders | |
| | `get_emails` | Mail | |
| | `send_email` | Mail | ✓ |
| DomainAdmin | `domain_list_users`, `domain_list_aliases` | Domain | |
| | `domain_create_alias` | Domain | ✓ |
| SysAdmin | `get_domains` | Domains | |
| | `get_spool_messages` | Spool | |
| | `delete_domain` | Domains | ✓ |
| | `enable_dkim` | DKIM | ✓ |

## Login fixtures

The email address decides what the login does. Matching is on the local part,
case-insensitively. The same fixtures apply to `POST /api/accounts`.

### Roles

The role comes from what was typed, ignoring a leading `2fa` so an admin can
also need a code:

| Type this | Role | Handle |
|---|---|---|
| no `@` at all (`admin`), or a local part starting `admin` / `sysadmin` | `SysAdmin` | `sysadmin:<username>@<host>` |
| a local part starting `domainadmin` (`domainadmin@contoso.com`) | `DomainAdmin` | the email |
| anything else | `User` | the email |
| `2faadmin` | `SysAdmin`, after the code `123456` | |

A handle already in the session gets `#<host>` appended (the same email on a
second server).

| Type this | You get |
|---|---|
| anything else | `200` SessionResponse + `sma_session` cookie |
| password `bad` | `401 { error, code: "USERNAME_OR_PASSWORD_INCORRECT" }` |
| `2fa@example.com` | `200 { twoFactorRequired: true, …, method: "rfc6238" }`, **no cookie** |
| `2famail@example.com` | the same, but `method: "email"` (any local part containing `mail`) |
| `expired@…` | `403 { code: "CHANGE_PASSWORD_NEEDED" }` |
| `stale@…` | `403 { code: "PASSWORD_EXPIRED" }` |
| `setup2fa@…` | `403 { code: "TWO_FACTOR_SETUP_REQUIRED" }` |
| `apppass@…` | `403 { code: "APP_PASSWORD_REQUIRED" }` |
| a private/loopback hostname | `400` (set `ALLOW_PRIVATE_HOSTS=true` to allow it) |
| missing fields | `400` |

The four `403`s carry an `error` telling the user to finish something in
SmarterMail webmail; the login view prints that text verbatim and adds an
"Open your SmarterMail webmail" link to the hostname that was typed.

### The two-factor challenge

A challenge lives for five minutes (`TWO_FACTOR_TTL_MS`) and is answered by
`POST /api/auth/two-factor`:

- the accepted code is **`123456`** (spaces are ignored);
- a wrong code returns `401 { code: "INVALID_TWO_FACTOR_CODE", attemptsLeft }`,
  counting down from 5;
- the fifth wrong code, a lapsed challenge, an unknown id, and any reuse after
  a success all return `410 { code: "CHALLENGE_EXPIRED" }` — the UI drops back
  to the login view with that message;
- the OpenRouter key typed on the login view is already in `sessionStorage`
  when the two-factor view appears, so verifying goes straight to the chat.

## Testing the LLM loop without an OpenRouter key

The stub also exposes a scripted, fake OpenRouter-compatible streaming endpoint
at `POST /api/dev/completions`. Point the UI at it by loading:

```
http://localhost:8787/mail-agent/?llmUrl=./api/dev/completions
```

The `llmUrl` override is **only honoured when the page is served from
localhost** (see `completionsUrl()` in `js/chat.js`) — in production a crafted
link could otherwise redirect a user's OpenRouter key to a third party.

The scripted model drives a realistic two-round tool conversation:

1. first user message → `list_folder_info_by_type` (arguments streamed in fragments)
2. after that result → `get_emails` (arguments streamed in fragments)
3. after that result → a markdown answer, streamed word by word
4. a user message containing "send" → a `send_email` call (a read-only session
   then returns `403`, which the UI renders as a failed tool card)
5. a user message containing "slow", "long" or "stop" → the answer streams at
   300 ms per word, which is the easy way to test the **Stop** button and the
   message queue
6. a user message containing "spool" or "stuck", with a system admin signed in
   → `get_spool_messages` as that sysadmin, then a short answer. With more than
   one account every scripted call passes `account`, so tool cards show it

Everything the loop needs is exercised: fragmented tool arguments, `content:
null` tool-call deltas, keepalive comments, `[DONE]`, usage frames, and a
session that expires mid-conversation (log out in another tab, or run
`fetch('./api/auth/logout',{method:'POST'})` in the console, then send a
message: the tool call 401s and the UI drops back to the login view).

## Unit tests

```sh
node --test 'wwwroot/dev/test/*.test.mjs'
```

- `test/fixtures.mjs` — recorded-shape OpenRouter SSE chunk sequences: plain
  text, fragmented tool arguments, two parallel tool calls, a provider that
  omits `index` and repeats the function name, `finish_reason: "length"`, a
  mid-stream error frame, and malformed JSON frames.
- `test/llm.test.mjs` — SSE parser, tool-call accumulator, argument parsing,
  tool-definition conversion, system prompt, `streamCompletion` (including
  401/402/429 handling), and the `runTurn` agent loop (multi-round tool calls,
  round cap, stop, protocol correctness: every `tool_call` id gets a matching
  `role:"tool"` message), and the multi-account system prompt and Tools-menu
  grouping/filtering.
- `test/accounts.test.mjs` — several accounts: `addAccount` / `removeAccount`,
  `callTool`'s `account` echo and the read-only refusal naming the account, the
  coded-vs-uncoded 401 split; and against the stub, roles and handles, the
  injected `account` enum, account resolution and its error texts, the cap,
  removal (last one ends the session), login replacing the session, and the
  add-account two-factor challenge bound to its session.
- `test/stub.mjs` — shared harness (spawn the stub on a free port, requests,
  cookies). Not a test file.
- `test/twofactor.test.mjs` — the two-factor step from both ends: `js/api.js`
  parsing of the three login outcomes (session, challenge, coded failure) and
  of the three `/auth/two-factor` outcomes against a fake `fetch`, plus the
  stub's challenge state machine against a real stub spawned on a random port
  (`PORT=0`).
- `test/resume.test.mjs` — "Remember me on this device": `js/resume.js`'s
  saved record and its passkey lock against a fake WebAuthn authenticator (PRF
  as an HMAC, the real WebCrypto), `js/api.js`'s `X-Resume-Version` handling,
  and the stub's enable → restart → resume → rotation contract.
- `test/markdown.test.mjs` — the escape-first renderer, including XSS attempts
  (`<script>`, `javascript:` and `data:` URLs, attribute break-out in a link
  label) and streaming edge cases such as an unterminated code fence.

No dependencies, no build step, no `package.json`: Node's ESM syntax detection
loads `js/*.js` directly.

## Profiles and tasks without a passkey

```sh
SEED_PROFILE=1 node wwwroot/dev/stub-server.mjs
```

prints a recovery code for a seeded profile (two accounts, one delegated, a task key, one task with
a result). On the login view choose **Use a recovery code** and paste it: you land in a profile
session with the Profile menu and the Tasks dialog working, and the result opens with the real
browser crypto. A real passkey works against the stub too (it just is not verified).

The seeded profile also has two pending approvals under **Tasks → To approve** (`stub-approvals.mjs`;
one of them fails when approved). Condition tasks can be built under "When something happens" in the
task editor: the stub's sysadmin tools `get_spool_message_counts` (its `waiting` count grows on every
call, so a `> N` condition turns true), `get_ssl_certificates` and `get_throttled_users` answer
**Test probe**, and `POST /api/tasks/probe` evaluates predicates with `stub-predicate.mjs`.
`search_log_files` returns ~300 KB of SMTP log, so its result becomes an artifact and
`analyze_result` runs (against a scripted sub-agent, no OpenRouter call).

