# smartermail-agent (monorepo)

Everything SmarterMail in one repo: the shared API library, three shared tool libraries, the MCP host
library, two MCP servers and the browser chat agent. User-facing docs are in `README.md` and `docs/`;
this file is for working on the code.

```
SmarterMail.slnx              the solution
Directory.Build.props         net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors, default Version
Directory.Packages.props      central package management: every NuGet version lives here
nuget.config                  nuget.org only (clears inherited feeds; same restore everywhere)
scripts/build-images.sh       local image builds: scripts/build-images.sh [user|admin|agent|all]
src/
  Core/                       SmarterMailMcp.Core: auth, UserContext HTTP wrapper, GlobalContext, TokenData
  Tools.Mailbox/              SmarterMail.Tools.Mailbox: 59 mailbox tools + Services/UploadService.cs
  Tools.DomainAdmin/          SmarterMail.Tools.DomainAdmin: 107 domain_* tools
  Tools.SysAdmin/             SmarterMail.Tools.SysAdmin: 58 sysadmin tools
  Mcp.Hosting/                SmarterMailMcp.Hosting: config, sign-in, stdio/HTTP, API key, read-only filter
  McpUser/                    MCP server, one fixed mailbox account (+ Prompts/; domain_* tools gated)
  McpAdmin/                   MCP server, one fixed sysadmin account
  Agent/                      browser chat + multi-account /mcp (detailed below)
tests/
  SmarterMail.Tests/          xunit: Core, the tool libraries, Mcp.Hosting, docs/tools.md (gates all three images)
  Agent.Tests/                xunit for the agent (also gates the agent image)
docs/                         user docs; docs/tools.md is generated (see below)
examples/                     docker-compose.yml, .env.example, MCP client configs
.github/workflows/            ci.yml (build, test, image builds), release.yml (tag v* → GHCR)
```

## Deployables

| Host | Image | Port | Account |
|---|---|---|---|
| `src/McpUser` | `ghcr.io/carbonitex/smartermail-mcp-user` | 8080 (HTTP) or stdio | fixed, env `SMARTERMAIL_URL` / `_USER` / `_PASSWORD`; `API_KEY` for HTTP |
| `src/McpAdmin` | `ghcr.io/carbonitex/smartermail-mcp-admin` | 8080 (HTTP) or stdio | fixed sysadmin, env `SMARTERMAIL_ADMIN_USER` / `_PASSWORD`; `API_KEY` for HTTP |
| `src/Agent` | `ghcr.io/carbonitex/smartermail-agent` | 8080 | whoever signs in; in memory, plus passkey-encrypted profiles under `DATA_DIR` in server mode |

Each host has its own `Dockerfile` under `src/<Host>/`, and every one is built with the **repo root**
as context: `docker buildx build -f src/<Host>/Dockerfile .`. The root `.dockerignore` keeps
`**/bin`, `**/obj`, `**/TestResults`, `**/wwwroot/dev/` and root `*.md` out of the context; `tests/`
and `docs/` are **in** it, because the build stage runs the tests (below) — they never reach the
runtime image. Runtime images run as the aspnet image's non-root `app` user. The `VERSION` build arg
becomes the assembly version (`-p:Version`), shown in MCP `serverInfo` and the MCP servers' `/health`.

Both MCP servers are **read-only by default** (`SMARTERMAIL_READ_ONLY` unset = `true`); see
`src/Mcp.Hosting/CLAUDE.md`.

## Tool libraries

| Library | Namespace | Tools | Used by |
|---|---|---|---|
| `src/Tools.Mailbox` | `SmarterMailMcp.Server.Tools` (`UploadService`: `SmarterMailMcp.Client.Services`) | Mail, Calendar, Contacts, ContactGroups, Tasks, Notes, Folders, User, Settings (59) | McpUser, Agent |
| `src/Tools.DomainAdmin` | `SmarterMailMcp.Server.Tools` | 107 `domain_*` in `DomainAdminTools.cs`, `DomainUserTools.cs`, `DomainRoutingTools.cs`, `DomainSecurityTools.cs`, `DomainMailingListTools.cs` | McpUser (gated), Agent |
| `src/Tools.SysAdmin` | `SmarterMailMcp.SystemAdmin.Tools` | Server, Domain, UserAdmin, Security, Spool, Certificate, Dkim, Monitoring, LogSearch (58) | McpAdmin, Agent |

Tool code lives **only** in these libraries; fix a tool there and every host that uses it gets the
fix. **Every tool declares whether it only reads**: `[McpServerTool(ReadOnly = true)]` on reads,
nothing on writes (an unmarked tool is a write everywhere — fail closed), and `Destructive = true` on
writes that delete, disable or disconnect. MCP clients see these as `readOnlyHint` /
`destructiveHint`; read-only MCP servers drop the writes from `tools/list`; the agent's `ToolPolicy`
derives its read/write split from them. How each host registers them:

- **McpUser**: `.WithToolsFromAssembly(typeof(MailTools).Assembly).WithPromptsFromAssembly(typeof(UserPrompts).Assembly)`
  (prompts in `src/McpUser/Prompts`), plus `.WithToolsFromAssembly(typeof(DomainAdminTools).Assembly)`
  only when `DomainToolsGate` says so (`SMARTERMAIL_DOMAIN_TOOLS`, default `auto` = startup probe of
  `/api/v1/settings/domain/data`; see `src/McpUser/CLAUDE.md`).
- **McpAdmin**: `.WithToolsFromAssembly(typeof(ServerTools).Assembly)`.
- Both then get `.WithReadOnlyFilter(readOnly)` from `src/Mcp.Hosting`.
- **Agent**: `WithAgentTools()` (`src/Agent/Mcp/ToolCatalog.cs`) registers the classes listed in
  `ToolPolicy.Groups` and scans the library assemblies (plus its own); startup **fails** if any
  `[McpServerToolType]` class is not listed.

So a tool added to a shared library ships automatically in McpUser (domain tools: when the gate is
on) / McpAdmin and **forces a scope decision** in the agent (`src/Agent/Mcp/ToolPolicy.cs`).
Adding a tool also means: set its `ReadOnly` / `Destructive` marks, update the counts in
`ToolLibraryTests.ExpectedCounts` and `ToolAnnotationTests`, the pinned read/write lists in
`tests/Agent.Tests/ToolPolicyTests.cs`, and regenerate `docs/tools.md` (below).

Core is in-repo too, so a change to Core and the agent code it affects land in the same commit
instead of surfacing later after a dependency bump. Watch one coupling in particular:
`src/Agent/Auth/UserContextFactory.cs` writes `UserContext`'s **private fields by name**
(`_accessToken`, `_baseUrl`, `_isConnected`, `_httpClient`, `_lastTokenRefresh`). The compiler does
not see that; `tests/Agent.Tests/UserContextFactoryTests.cs` does, so a rename in
`src/Core/Models/UserContext.cs` fails `dotnet test` instead of the agent's first login.

## Build, test, release

```bash
dotnet build SmarterMail.slnx                          # TreatWarningsAsErrors: any warning fails the build
dotnet test SmarterMail.slnx                           # both test projects, offline, a few seconds
node --test 'src/Agent/wwwroot/dev/test/*.test.mjs'   # agent frontend (the glob matters on Node 26)
UPDATE_TOOL_DOCS=1 dotnet test tests/SmarterMail.Tests --filter ToolReferenceTests   # regenerate docs/tools.md
scripts/build-images.sh [user|admin|agent|all]        # local images (REGISTRY, TAG, PLATFORMS, PUSH, VERSION)
```

Two xunit projects:

| Project | Covers | Gates images |
|---|---|---|
| `tests/SmarterMail.Tests` | Core (`StartupSignIn`, `AuthResponseClassifier`, per-sign-in clientIds), tool-library guards (below), `Mcp.Hosting` (settings, read-only filter), `docs/tools.md` freshness | user, admin, agent |
| `tests/Agent.Tests` | agent: policy, schemas, dispatch, roles, session accounts, account logout, `UserContextFactory`, MCP token scoping (in-process host via `WebApplicationFactory<Program>`), remember-me, proxy trust, connect-time SSRF guard, path base, home link; server mode: options, both modes over HTTP, sealer, SQLite store, profile runtime, a full passkey round trip (`SoftAuthenticator`), task definitions, the task gate, the server-side loop, a scheduled run against a fake LLM (`FakeLlm`). Runs serially (see `TestEnvironment.cs`) | agent |

The guards in `tests/SmarterMail.Tests`:

- **Tool counts** — Mailbox 59, DomainAdmin 107, SysAdmin 58. Names come from the SDK itself: each
  assembly goes through `AddMcpServer().WithToolsFromAssembly(asm)` exactly as the hosts register it,
  and the test reads `McpServerTool.ProtocolTool.Name`, so an attribute `Name` and the SDK's default
  snake_case of the method name are both handled without re-implementing either. A plain-reflection
  count of `[McpServerTool]` methods on `[McpServerToolType]` classes must agree, so the SDK skipping
  a method also fails.
- **Annotations** (`ToolAnnotationTests`) — read-only counts per library (Mailbox 23, DomainAdmin 44,
  SysAdmin 29), nothing both read-only and destructive, every `delete_*` destructive.
- **Read-only hosts** (`McpHostingTests`) — a read-only user server lists 23 (67 with domain tools), a
  read-only admin server 29, and all of them are marked read-only.
- **Names unique** across all three libraries.
- **`domain_*` lives only in Tools.DomainAdmin**, and every Tools.DomainAdmin tool is `domain_*`.
- **No process spawning** — Core, the three tool libraries and `Mcp.Hosting` must not reference
  `System.Diagnostics.Process` or `ProcessStartInfo`. Checked on the compiled assembly with
  `System.Reflection.Metadata`: any use needs a TypeReference row, and a
  `Type.GetType("System.Diagnostics.Process…")` dodge leaves the name in the user-string heap. A
  positive control (`ProcessReferenceFixture` in the test assembly) proves the detector finds both.
- **`docs/tools.md` is generated** (`ToolReferenceTests`) from the SDK's view of every tool, grouped by
  class, with the read / write / destructive marks. A stale file fails the build; regenerate it with
  the command above.

**Tests gate the images.** Each Dockerfile's build stage runs its tests **before** `dotnet publish`,
and a failing test fails the image build — nothing is pushed.

| Image | Build stage runs, in order |
|---|---|
| user | `dotnet test tests/SmarterMail.Tests/SmarterMail.Tests.csproj -c Release` → `dotnet publish src/McpUser/…` |
| admin | `dotnet test tests/SmarterMail.Tests/SmarterMail.Tests.csproj -c Release` → `dotnet publish src/McpAdmin/…` |
| agent | `dotnet test` SmarterMail.Tests, then `tests/Agent.Tests/SmarterMailAgent.Tests.csproj`, both `-c Release` → `dotnet publish src/Agent/…` |

The agent's frontend node tests (`src/Agent/wwwroot/dev/test/*.test.mjs`) are **not** in the image
gate (the .NET SDK image has no node, and `.dockerignore` drops `wwwroot/dev/`); CI runs them. Each
Dockerfile restores from the project files first (a cached layer) and only then copies the sources;
that layer is an optimisation only — the test and publish steps restore again, so a project missing
from its `COPY` list slows the build but cannot break it. The runtime stage copies only `/app`, the
publish output, so no test assembly ships.

**Central package management.** Versions live only in `Directory.Packages.props`
(`ManagePackageVersionsCentrally`); a `PackageReference` never carries `Version=`. Bump a package
there and all three images pick it up. `nuget.config` pins restore to nuget.org (every package is
public), which also keeps CPM's NU1507 "multiple sources" warning away when a user-level config adds
other feeds.

**CI and releases** (`.github/workflows/`): `ci.yml` builds, runs both test projects and the node
tests, and builds all three images without pushing, on every push and pull request. `release.yml`
runs on a `v*` tag: multi-arch (`linux/amd64`, `linux/arm64`) images to GHCR tagged `X.Y.Z`, `X.Y`,
`X` and `latest` (pre-release tags such as `v1.0.0-rc.1` get only their own tag), plus a GitHub
Release whose notes are the tag's `CHANGELOG.md` section. One version for the whole repo.

Per-host notes: `src/Core/CLAUDE.md`, `src/Mcp.Hosting/CLAUDE.md`, `src/McpUser/CLAUDE.md`,
`src/McpAdmin/CLAUDE.md`.

# Agent (src/Agent)

Paths in this part are relative to `src/Agent/` unless they start with `src/` or `tests/`.

Browser chat for SmarterMail (a hosted instance runs at `https://carbonitex.dev/mail-agent/`; anyone
can self-host it, see `docs/agent.md`). Anyone logs into **their own**
SmarterMail server, pastes **their own** OpenRouter key in the browser, and chats with an agent.
One chat can hold **several signed-in accounts at once** (up to `SESSION_MAX_ACCOUNTS`, default 5),
possibly on different servers: mailbox users, domain admins and system admins. Each account gets
the tools its role allows, and the agent picks one per call with an `account` argument.

The .NET service is a **relay**; in server mode it also keeps profiles and runs scheduled tasks:

- SmarterMail sends no CORS headers, so the browser cannot call the user's mail server directly.
  This service does it on the browser's behalf.
- The OpenRouter loop runs **in the browser**. The server never sees an OpenRouter key and never
  pays for inference.
- SmarterMail tokens live **in memory**, for the life of a session. With "Remember me on this device"
  the **browser** keeps a sealed copy of the refresh tokens (see
  [Remember me on this device](#remember-me-on-this-device)).
- **Two modes** (`Server/ServerOptions.cs`). **Server mode**, the default: a user may save a
  passkey-encrypted **profile** (accounts + settings) in SQLite under `DATA_DIR`, and with `DATA_KEY`
  run **scheduled tasks**, the only time the server itself calls an LLM. **`BROWSER_ONLY_MODE=true`**:
  none of that is even registered, `DATA_DIR` is never touched, and the agent stores nothing per user.
  carbonitex.dev runs browser-only. See [Server mode](#server-mode-profiles-and-scheduled-tasks).

## Architecture

```
Program.cs                    composition root, middleware, MCP registration, MCP list/call filters
Server/
  ServerOptions.cs            BROWSER_ONLY_MODE, DATA_DIR, DATA_KEY, PUBLIC_ORIGIN, TASK_* (from IConfiguration)
  ServerModeOnlyAttribute.cs  404 SERVER_MODE_DISABLED before a profile/task controller is built
Storage/
  DataStore.cs                SQLite file, migrations on PRAGMA user_version, ADO helpers
  ProfileStore.cs             profiles, passkeys, stored accounts (rows hold sealed blobs)
  TaskStore.cs                tasks and their runs
Profiles/
  ProfileRuntime.cs           one live owner of a profile's accounts (+ ProfileRegistry: leases)
  ProfileCrypto.cs            accounts-key check, recovery hash, sealing to the profile's public key
  PasskeyService.cs           WebAuthn ceremonies (fido2-net-lib), single-use, 2 minutes
  ProfileMaintenance.cs       ceremony sweep; daily idle-profile pruning and delegated-account keep-alive
Llm/
  OpenRouterClient.cs         non-streaming chat completions for scheduled runs
  AgentLoop.cs                the tool loop (port of llm.js runTurn)
Tasks/
  TaskDefinition.cs           sealed definition, cron (Cronos) + time zone, validation
  TaskPrompt.cs               the unattended system prompt
  TaskRunner.cs               one run: accounts, gate, loop, sealed transcript, optional email
  TaskRunScheduler.cs         30 s tick, claim, concurrency, "Run now"
Auth/
  HostGuard.cs                SSRF guard (scheme + resolved-IP checks)
  Sealer.cs                   AES-256-GCM framing with key rotation, label + row-context AAD (+ Base64Url)
  AccountSet.cs               a set of accounts with unique handles: a session's own, or a profile's shared one
  AccountBuilder.cs           Account from a sign-in or a refreshed stored token; AccountRestorer (resume/unlock/tasks)
  GuardedHttp.cs              handler for every outbound SmarterMail call: HostGuard re-checked at
                              connect time (DNS rebinding), no redirects, no proxy
  PendingLoginStore.cs        in-memory two-factor challenges, optionally bound to a session
  HostLoginThrottle.cs        per-target-server failed-login cap (protects our shared egress IP)
  ResumeSealer.cs             RESUME_KEY: seals / opens the remember-me bundle (AES-256-GCM)
  Account.cs                  one SmarterMail login: TokenData + GlobalContext + UserContext, role,
                              handle, refresh lock. AccountRole = User | DomainAdmin | SysAdmin
  AccountServiceProvider.cs   per-call IServiceProvider that answers UserContext/GlobalContext/Account
  Session.cs                  one browser session: clocks + a locked list of Accounts
  SessionStore.cs             ConcurrentDictionary of sessions + SessionSweeper hosted service
  SessionAuthenticationHandler.cs   two schemes: `Session` = cookie `sma_session` (default, /api/*),
                              `McpToken` = `Authorization: Bearer sma_mcp_…` (/mcp only)
  SessionServiceProvider.cs   per-request IServiceProvider that answers Session only
  SmarterMailAuth.cs          fileless authenticate + two-factor + refresh + logout + role detection
  UserContextFactory.cs       builds Core's UserContext from an in-memory TokenData
Controllers/
  SignInControllerBase.cs     shared password/two-factor → account flow + response records
  AuthController.cs           /api/auth/*
  AccountsController.cs       /api/accounts (add / remove an account in the current session)
  ResumeController.cs         /api/auth/resume (remember me: config, enable / fetch / disable, resume)
  ToolsController.cs          /api/tools, /api/tools/call
  ConfigController.cs         /api/config (mode and what this server offers)
  ProfileController.cs        /api/profile/* (server mode)
  TasksController.cs          /api/tasks/* (server mode with DATA_KEY)
Mcp/
  ToolPolicy.cs               THE policy: registered tool classes → scope + category, role→scope,
                              write classification, eligibility, `account` injection + resolution
  ToolCatalog.cs              explicit tool registration (WithAgentTools) + per-session tool lists
  ToolDispatcher.cs           the one call path for /api/tools/call, /mcp and scheduled runs (IToolContext, ToolGate)
  ToolInvoker.cs              invokes an McpServerTool outside the JSON-RPC pipeline
  NoopTransport.cs            throwaway transport for the above
Logging/CoreConsoleFilter.cs  drops Core's (src/Core) Console chatter (leaks mail metadata)
Web/
  AssetVersioning.cs          content-hash asset URLs (see Build & deploy) + HOME_LINK_* in index.html
  ProxyTrust.cs               TRUSTED_PROXIES / TRUST_CF_CONNECTING_IP: forwarded headers + rate-limit key
  ResumeHeaders.cs            X-Resume-Version on cookie responses; /api responses no-store
wwwroot/                      the browser UI (owned by the frontend; wwwroot/dev/ is not shipped)
  js/vault.js                 profile cryptography (pure WebCrypto; node-tested, incl. a C#-sealed vector)
  js/passkey.js               WebAuthn glue: options in, credentials out WITHOUT clientExtensionResults
  js/webauthn.js              navigator.credentials past a password manager: a refused extension prompt
                              retries once on a fresh iframe's native credentials (passkey.js, resume.js)
  js/profile.js               profile flows (create, passkey / recovery sign-in, unlock, settings sync)
  js/profile-ui.js            passkey panel, "save to a profile" offer, Profile menu, recovery-code dialog
  js/tasks.js                 the Tasks dialog: list, editor (cron presets), runs, transcript viewer
```

Outside `src/Agent/`:

```
src/Tools.Mailbox/*.cs        mailbox tools (namespace SmarterMailMcp.Server.Tools); shared with McpUser
src/Tools.DomainAdmin/*.cs    domain_* tools (same namespace), incl. DomainAdminTools.cs; shared with
                              McpUser, which registers them only for a domain admin
src/Tools.Mailbox/Services/UploadService.cs   used by MailTools (namespace SmarterMailMcp.Client.Services)
src/Tools.SysAdmin/*.cs       sysadmin tools (namespace SmarterMailMcp.SystemAdmin.Tools); shared
                              with McpAdmin
src/Core/                     SmarterMailMcp.Core, by project reference
tests/Agent.Tests/            xunit: policy, schema injection, dispatch resolution, role detection,
                              session accounts, account logout, host login throttle, MCP token
                              scoping, remember-me, proxy trust, GuardedHttp, path base, home link
                              (run in the image's build stage, never shipped)
```

### Tokens never touch disk

In browser-only mode, nothing does. In server mode the one exception is a **profile's stored
accounts**: their refresh tokens (never access tokens), sealed, in `profile_accounts` (see
[Server mode](#server-mode-profiles-and-scheduled-tasks)). Core's token file is still never used.

Core's `AuthenticationService.AuthenticateAsync` writes `TokenData` through
`GlobalContext.WriteTokenFile()` and **fails** if the write fails, and `UserContext`'s refresh path
reads that same file. On a public service those tokens belong to strangers, so:

- `Auth/SmarterMailAuth.cs` re-implements the two endpoints Core uses
  (`/api/v1/auth/authenticate-user`, `/api/v1/auth/refresh-token`) and keeps `TokenData` in memory.
  It also calls `/api/v1/auth/logout-user` when an account goes away (see
  [Sessions and accounts](#sessions-and-accounts)).
- `Auth/UserContextFactory.cs` replicates what `UserContext.InitializeFromFile` does, writing the
  private fields (`_accessToken`, `_baseUrl`, `_isConnected`, `_httpClient`, `_lastTokenRefresh`)
  by reflection. If Core (`src/Core/Models/UserContext.cs`) renames a field the factory throws a
  named error instead of failing quietly, and `UserContextFactoryTests` fails first.
- Each account still constructs a `GlobalContext` (tools and Core take one) pointed at
  `Path.GetTempPath()/sma-never-<guid>.json`, which is never created, read or written.
- `_lastTokenRefresh` is stamped at login and after every refresh, so Core's own 10-minute
  auto-refresh (which would read the missing file) never fires. `SessionSweeper` refreshes every
  account in memory after 8 minutes or 5 minutes before the access token expires, and
  `ToolDispatcher` retries a call once after an in-memory refresh if SmarterMail answers 401.
  A **remembered** session is the exception: the sweeper leaves it alone, and the dispatcher
  refreshes lazily before a call (2 minutes before expiry) and otherwise re-stamps
  `_lastTokenRefresh` (`UserContextFactory.StampRefreshClock`) so Core's file path still never
  runs (see [Remember me on this device](#remember-me-on-this-device)).

### Each account gets its own SmarterMail clientId

SmarterMail keeps one token per `(user, clientId)`. Authenticating twice with the same clientId
invalidates the first token — two browser tabs on the same mailbox would evict each other. Every
login (every account) therefore uses `smartermail-agent-<random>`.

### Tool DI, and why /api/tools/call does not go through /mcp

Tool methods are `static` and take `UserContext userContext` from DI. Two things were needed:

1. **Registration for discovery.** `AIFunctionFactory` decides *at tool-creation time* whether a
   parameter comes from the caller's arguments or from DI, using `IServiceProviderIsService` on the
   root provider. Without a registration the SDK treats `userContext` as a required argument and
   every call fails with *"the arguments dictionary is missing a value for the required parameter
   'userContext'"*. `Program.cs` therefore registers `UserContext` and `GlobalContext` as scoped —
   with factories that **throw**.
2. **Resolution from the chosen account.** `SessionAuthenticationHandler` replaces
   `HttpContext.RequestServices` with a `SessionServiceProvider` that answers `Session` only —
   with several accounts a request has no single `UserContext`, so resolving one through it throws
   with a message pointing at `AccountServiceProvider`. `ToolDispatcher` picks the account for each
   call and runs the tool on an `AccountServiceProvider` that answers `UserContext`,
   `GlobalContext` and `Account` for that account and delegates everything else. Both providers
   wrap any scope created from them, so the MCP SDK's per-request scope keeps the override.
   `AddScoped<UserContext>(sp => account.UserContext)` is not used because the DI container
   disposes IDisposables returned by a scoped factory, and `UserContext` owns the account's
   `HttpClient`: the container would kill a live account after its first request.

### One dispatcher for REST and /mcp

`Mcp/ToolDispatcher.cs` is the only way a tool runs, from `/api/tools/call` and from `/mcp`:

1. resolve the account from `arguments.account` (`ToolPolicy.Resolve`);
2. strip `account`, so the tool never sees it;
3. refuse a wrong role (model-fixable `isError` listing the valid handles) or a write on a
   read-only account;
4. invoke the tool through `ToolInvoker` on an `AccountServiceProvider`;
5. retry once after an in-memory refresh of **that account** if SmarterMail answered 401.

`/api/tools/call` invokes `McpServerTool.InvokeAsync` directly (`Mcp/ToolInvoker.cs`) over a
throwaway `McpServer`, which keeps the UI's contract independent of JSON-RPC framing. On `/mcp` the
SDK's list filter returns the per-session tool list (rewritten schemas) and the call filter hands
the call to the dispatcher instead of `next`. Both paths run the same tool objects.

Tools are registered from the explicit class list in `ToolPolicy.Groups` (`WithAgentTools()`, not
`WithToolsFromAssembly()`), so every tool has a scope and category. Startup **fails** if the agent
assembly or any shared tool library (`src/Tools.Mailbox`, `src/Tools.DomainAdmin`,
`src/Tools.SysAdmin`) contains an
`[McpServerToolType]` class that is not listed, or two tools share a name.

## HTTP contract

All paths are relative to `PATH_BASE` (default `/`). JSON in and out.
Errors are `{ "error": "<message>" }` with a 4xx/5xx.

`HOST_THROTTLED` = `429 { error, code: "HOST_THROTTLED", retryAfterSeconds }` with a `Retry-After`
header (seconds): too many failed sign-ins to **that mail server** from this service (see
[Per-server failed-login cap](#per-server-failed-login-cap)). Refused before SmarterMail is called;
`error` is user-facing ("… Try again in N minutes."). The per-IP limiters' own `429` has no body.

`SessionResponse` = `{ expiresAt, maxAccounts, remembered, accounts: [{ id, handle, role, username, emailAddress, domain, baseUrl, readOnly }], mcpToken: { active, expiresAt }, profile: { id, unlocked, idleMinutes } | null }`,
`role` ∈ `"User" | "DomainAdmin" | "SysAdmin"`; `remembered` = "Remember me on this device" is on;
`mcpToken.expiresAt` is `null` when no token is active.

Auth **session** = the `sma_session` cookie, and nothing else: a bearer header is ignored on
`/api/*` (the request is `401` without the cookie). The only bearer credential is the **MCP token**,
and it opens `/mcp` only (see [MCP tokens](#mcp-tokens)).

`X-Resume-Version`: the browser sends the version of the resume bundle it holds (`0` for none) on
every request. A response to a **cookie**-authenticated request whose session is remembered, and
whose resume version is newer, carries `X-Resume-Version: <newer>`; the browser then fetches
`GET /api/auth/resume`. Never sent to an MCP-token caller. Every `/api` response is `Cache-Control: no-store`.

`BundleResponse` = `{ bundle, version, rememberedUntil }`. The resume endpoints answer
`404 { code: "RESUME_DISABLED" }` when `RESUME_KEY` is not set.

| Method & path | Auth | Body → Response |
|---|---|---|
| `POST /api/auth/login` | none, `login` limiter (5/min/IP) | `{ hostname, email, password, readOnly=true }` → `200 SessionResponse` + `sma_session` cookie. Always opens a **new** session with this as its first account; a session already on the incoming cookie is disposed once the login succeeds. **`200 { twoFactorRequired: true, challengeId, method, emailAddress, expiresAt }` and no cookie** when SmarterMail wants a second factor. `401 { error, code }` bad creds. `403 { error, code }` when something must be finished in webmail first. `400` blocked host / missing fields. `429` too many attempts (per-IP limiter, no body) or `HOST_THROTTLED`. |
| `POST /api/accounts` | session, `login` limiter | Same body as login → `200 SessionResponse` with the account added, or `200` two-factor challenge **bound to this session**. Same server + login as an existing account replaces it. `409 { error, code: "ACCOUNT_LIMIT" }` at `SESSION_MAX_ACCOUNTS` (checked before SmarterMail is called). `400/401/403/429` as login. |
| `DELETE /api/accounts/{id}` | session | `204`. Removing the last account ends the session and clears the cookie. Unknown id → `404`. |
| `POST /api/auth/two-factor` | none, `two-factor` limiter (15/min/IP) | `{ challengeId, code }` → `200 SessionResponse`. A login challenge opens a session and sets the cookie; an add-account challenge adds to its bound session and only completes on a request carrying **that** session — from anywhere else the challenge is discarded and the answer is `410`. `401 { error, code: "INVALID_TWO_FACTOR_CODE", attemptsLeft }`. `410 { error, code: "CHALLENGE_EXPIRED" }` when the challenge is unknown, expired, already used, out of attempts, or presented outside its session. `400` missing challenge id / code not 4–12 characters. `502` the mail server did not answer. `HOST_THROTTLED` for the challenge's server (the challenge is kept and no code attempt is used). |
| `POST /api/auth/logout` | session | `204`, session and all its accounts disposed (tokens revoked on SmarterMail, best effort), cookie cleared |
| `GET /api/auth/session` | session | `200 SessionResponse` or `401` |
| `POST /api/auth/token` | session (cookie only) | `200 { token: "sma_mcp_…", expiresAt }`, `Cache-Control: no-store` — a new MCP token for `Authorization: Bearer` against `/mcp`; any previous token stops working. Shown once: the server keeps only its hash. |
| `DELETE /api/auth/token` | session (cookie only) | `204`, the session's MCP token (if any) stops working |
| `GET /api/auth/resume/config` | none | `200 { enabled, days }` — whether to offer "Remember me on this device" |
| `PUT /api/auth/resume` | session, **cookie only** | `200 BundleResponse`: remember this session (and every account added to it later). The chain starts now for a password sign-in and keeps its original start otherwise. `410 { code: "RESUME_EXPIRED" }` when this sign-in's chain is over. A bearer never gets this far (`/api/*` is cookie-only → `401`); the controller's `403 COOKIE_REQUIRED` stays as a second check. |
| `GET /api/auth/resume` | session, **cookie only** | `200 BundleResponse`, `404 { code: "NOT_REMEMBERED" }` |
| `DELETE /api/auth/resume` | session, **cookie only** | `204`: stop remembering; the session carries on (and the sweeper's refreshes soon kill any copy of the bundle) |
| `POST /api/auth/resume` | none, `login` limiter | `{ bundle }` → `200 { ...SessionResponse, bundle, version, rememberedUntil, skipped: [{ baseUrl, login, role, reason }] }` + `sma_session` cookie; `reason` ∈ `REJECTED EXPIRED UNAVAILABLE BLOCKED_HOST ACCOUNT_LIMIT`. `400 { code: "RESUME_INVALID" }` unreadable / tampered / other key. `401 { code: "RESUME_EXPIRED", skipped }` past `RESUME_DAYS`, or no account could be refreshed. `503 { code: "RESUME_UNAVAILABLE", skipped }` no account came back and a mail server did not answer (keep the bundle). `HOST_THROTTLED` for any account's server, before anything is refreshed. |
| `GET /api/tools` | session | `200 [ { name, description, inputSchema, category, scope, write, destructive } ]` — only tools with at least one eligible account; `inputSchema` carries the injected `account` property (below) |
| `POST /api/tools/call` | session, `api` limiter (120/min/IP) | `{ name, arguments: {…, account?} }` → `200 { isError, content, account }` (`account` = the handle it ran as, `null` if refused before resolving). Tool exceptions **and payloads carrying `success:false`** → `isError: true`. Missing / unknown / wrong-role `account` → `200 isError` listing the valid handles. Write tool on a read-only account → `403 { error }` naming the handle. Unknown tool → `404`. |
| `POST /mcp` | session cookie **or** `Authorization: Bearer <MCP token>` (`401` + `WWW-Authenticate: Bearer` for a bad/expired/revoked token or a raw session id) | Stateless MCP, same per-session tool list and schemas, same dispatcher. Account and read-only refusals are `isError: true`; tool results pass through unchanged. |
| `GET /api/config` | none | `{ mode: "server"\|"browser", resume: { enabled, days }, profiles: { enabled }, tasks: { enabled, minIntervalMinutes, maxPerProfile, maxToolRounds } }` |
| `/api/profile/*`, `/api/tasks/*` | see [Server mode](#server-mode-profiles-and-scheduled-tasks) | `404 SERVER_MODE_DISABLED` in browser-only mode |
| `GET /health` | none | `smartermail-agent ok` |

Tool names are MCP snake_case (`get_emails`); argument names are camelCase (`folderId`, `take`) as
the SDK generates them. `inputSchema` is the JSON Schema the MCP server reports, passed straight
through to OpenRouter as a function definition.

## Two-factor logins

SmarterMail does not describe a login with HTTP status alone, and the old code got both halves
wrong: it read any non-2xx as "invalid username or password" and any `200` carrying an
`accessToken` as a completed login. On a 2FA account the second one is actively harmful —
`authenticate-user` answers **`200`**, `success: true`, `message: "TWO_FACTOR_REQUIRED|rfc6238|user@example.com"`
with an `accessToken` that is a **10-minute AuthStep JWT** (claims `IsAuthStep: "True"`,
`AuthStepPurpose: "TwoFactorVerification"`) and an empty refresh token. That token gets `403` on
every real endpoint and `401` on `refresh-token`, so a session built from it is dead on arrival.

`SmarterMailAuth.AuthenticateAsync` therefore parses the JSON body **regardless of status** (a
`401` body still carries `USERNAME_OR_PASSWORD_INCORRECT`, `USER_NOT_FOUND`, or — on builds older
than April 2026 — `TWO_FACTOR_REQUIRED`) and returns an `AuthOutcome`:

| Outcome | Raised when |
|---|---|
| `Success(TokenData)` | `success: true` with a usable access token and no 2FA / password flag |
| `TwoFactorRequired(method, emailAddress, stepToken?)` | `message` starts with `TWO_FACTOR_REQUIRED`; `method` is `rfc6238` or `email`; `stepToken` is null on legacy builds |
| `ActionRequired(kind, message)` | `CHANGE_PASSWORD_NEEDED`, `PASSWORD_EXPIRED`, `TWO_FACTOR_SETUP_REQUIRED` (`…|DOMAIN_FORCED_NOT_SETUP|…` / `…|SYSADMIN_NOT_SETUP|…`), `APP_PASSWORD_REQUIRED` (`…|APP_PASSWORD`) |
| `Failed(code, friendlyMessage)` | anything else; `code` is SmarterMail's own message code |

`changePasswordNeeded: true` and `passwordExpired: true` also arrive with `success: true` and a
*PasswordReset* step token, so they are checked before the success branch. All four `ActionRequired`
kinds mean the same thing to a user: finish it in SmarterMail webmail, then sign in here.

`CompleteTwoFactorAsync` has two paths:

- **Step token** (current builds): `POST /api/v1/auth/authenticate-two-factor-code` with
  `Authorization: Bearer <stepToken>` and `{ twoFactorCode, clientId }` — the same `clientId` as the
  first call. The step token is **single-use**: replaying it after a success yields
  `INVALID_TWO_FACTOR_CODE`. A *wrong* code does not burn it, so the user can retype.
- **Legacy inline** (no step token): re-POST `authenticate-user` with
  `{ username, password, clientId, twoFactorCode }`. Older builds signal 2FA with a `401` and no
  token; current builds still accept this form.

### The pending-challenge store

`Auth/PendingLoginStore.cs` holds the state between a correct password and a correct code:
`baseUrl`, `username`, `readOnly`, `clientId`, `method`, `emailAddress`, `stepToken`, `createdAt`,
attempt count — keyed by a 32-byte base64url id, the same shape as a session id. TTL **5 minutes**,
**5** code attempts, single-use: the challenge is removed on success, on expiry, and when attempts
run out, so a replayed or exhausted `challengeId` is `410 CHALLENGE_EXPIRED`. `SessionSweeper`
calls `Sweep()` every minute alongside the session pass. Nothing is written to disk.

**The password is held only on the legacy path.** When SmarterMail gave a step token there is
nothing to replay, so no password is stored at all. When it did not, the password is the only way
to finish the login, and it is kept as a `char[]` that `Erase()` overwrites in place on success,
expiry, or exhaustion, rather than an immutable `string` left to the GC. (The transient `string`
copy handed to `CompleteTwoFactorAsync` is still the GC's problem — this is best effort, not a
guarantee.) A challenge never outlives five minutes either way.

### Logging

Every failed or intermediate login logs the SmarterMail HTTP status and the message **code**:
`SmarterMail auth for host http://mail.example.com: 200 TWO_FACTOR_REQUIRED|rfc6238`. The message
can carry the account's email address in its third pipe-separated segment, so only the first two
segments are ever logged. Never logged: the email, the password, the code, the step token, the
access token.

## Per-server failed-login cap

Every visitor signs in from this service's **one egress IP**, and SmarterMail's intrusion detection
counts failed logins per **source IP**. A few visitors mistyping passwords against the same server
could get our IP blocked on that server for everyone. `Auth/HostLoginThrottle.cs` caps it.

SmarterMail's default "Brute Force by IP" IDS rules (as a fresh install ships them; an admin sees and
edits them under Settings → Security → IDS Rules):

| Rule | Failures | Window | Action |
|---|---|---|---|
| Default Brute Force by IP rule - Delay | 15 | 10 min | 2.5 s delay per attempt |
| Default Brute Force by IP rule | 25 | 10 min | block 30 min |
| Default Brute Force by IP rule - Long Block | 35 | 50 min | block 4 h |

(`success_score = 2`: each success, at most one per login per 5 min, takes 2 off the score; servers
upgraded from older builds have `0`, which counts only failures since the last success.) Failed
password logins, wrong 2FA codes and invalid refresh tokens all tick it.

The cap defaults to **10 failures per sliding 60 minutes per server**, so no 10-minute slice (15 /
25) and no 50-minute slice (35) can reach a default rule, with headroom for admins who tightened
them a little and for other failures SmarterMail sees from our IP.

- **Key**: `scheme://host:port` of the base URL, lower-case (`https://Mail.x.com/` =
  `https://mail.x.com:443`). Different hostnames on one SmarterMail install are counted separately.
- **Counts**: `USERNAME_OR_PASSWORD_INCORRECT`, `USER_NOT_FOUND`, `INVALID_TWO_FACTOR_CODE`, and on
  the two-factor step the legacy path's `TWO_FACTOR_REQUIRED` answer to a wrong code
  (`HostLoginThrottle.CountsAsFailure`). **Not**: connection failures, timeouts, unreadable
  replies, blocked hosts, our own 429s, action-required answers, successes.
- A success resets nothing; failures just age out of the window.
- An attempt holds a slot while SmarterMail is answering (`TryBegin` → `Attempt`), so concurrent
  sign-ins cannot overshoot the cap. A server held only by attempts in flight answers
  `retryAfterSeconds: 5`.
- Over the cap, `POST /api/auth/login`, `POST /api/accounts` and `POST /api/auth/two-factor` for that
  server answer `HOST_THROTTLED` without calling SmarterMail.
- In memory, one lock, at most 10,000 servers tracked (at the cap: expired entries go first, then
  the least recently active); `SessionSweeper` calls `Sweep()` every minute. Logged: the server and
  the count, never the login.

The trade-off is deliberate: anyone can pause sign-ins to a server through this service for up to
an hour by failing 10 logins against it, but they cannot get our IP blocked there.

## In-band tool failures

The SmarterMail tools swallow API exceptions and return them as ordinary text, e.g.
`{"success":false,"error":"API call failed: InternalServerError","statusCode":500,…}`, leaving the
MCP error flag unset. `ToolInvoker.PayloadIndicatesFailure` therefore treats an explicit
`success:false` as an error. Two reasons:

- the browser LLM loop would otherwise be told a hard upstream failure succeeded, and would answer
  as if the call had worked;
- the stale-token retry in `ToolDispatcher` keys off the failure flag, so before this a 401 from
  SmarterMail never triggered the in-memory refresh. Proactive refresh by the sweeper hid it.

`/mcp` reports the flag the tool itself set (the dispatcher passes the `CallToolResult` through
unchanged); only its 401 retry uses the `success:false` rule. The
upstream error text is passed through unchanged — it is the user's own server's message, and it is
what makes a failure diagnosable.

## Sessions and accounts

- A **session** is the browser: id, clocks, and a lock-guarded list of **accounts**. An account is
  one SmarterMail login (token, `UserContext`, role, handle, read-only flag, refresh lock).
- Id: 32 random bytes, base64url. Cookie `sma_session`: HttpOnly, Secure, SameSite=Strict,
  `Path=<PATH_BASE>/`, MaxAge 12 h. (`Secure` is relaxed only when `ALLOW_PRIVATE_HOSTS=true` on a
  plain-http request, for local development.)
- Idle timeout 30 min (a profile may choose its own, below), absolute 12 h, **per session**. `SessionSweeper` runs every minute: it
  disposes expired sessions (and every account in them), refreshes each account's token in memory,
  and drops **only the account** whose refresh fails. A session left with no accounts is removed.
  Remembered sessions are only expired by the sweeper, never refreshed (see
  [Remember me on this device](#remember-me-on-this-device)).
- At most `SESSION_MAX_ACCOUNTS` (default 5) accounts per session. Adding the same
  `(baseUrl, login)` again replaces the old account (which is disposed).
- **Disposing an account revokes its tokens on SmarterMail.** Every removal path — logout, `DELETE
  /api/accounts/{id}`, replacement, session expiry, a failed refresh in the sweeper — ends in
  `Account.DisposeAsync`, except the expiry of a **remembered** session, which ends in
  `Account.ForgetAsync` (same lock and cleanup, no `logout-user`) so the browser's bundle stays
  usable. `DisposeAsync` posts `/api/v1/auth/logout-user` with the account's bearer token
  (`SmarterMailAuth.LogoutAsync`). SmarterMail then revokes every token for that token's
  `(user, clientId)`; each sign-in has its own clientId, so no other account or session is touched.
  Best effort and **awaited**, bounded by `SmarterMailAuth.DefaultLogoutTimeout` (3 s); failures,
  timeouts and 401s (an expired access token, or the sweeper's already-dead account) are swallowed
  and logged by host + status only, and the tokens are nulled in memory regardless. Disposal first
  waits (up to 3 s) for the account's refresh lock, so it revokes the newest pair; a refresh that
  lands after disposal revokes the pair it minted itself. `Session.DisposeAsync` disposes its
  accounts in parallel, so closing a session costs one timeout at worst.
- Why not a `__Host-` cookie: the prefix demands `Path=/` and no `Domain`, but the agent can live
  under a `PATH_BASE` (e.g. `/mail-agent`) on a host shared with another site. `Path=/` would send
  the session id with every request to the whole site. `Path=<PATH_BASE>/` stays; the cookie keeps
  its name.
- Sessions are **not** disposed on process shutdown (`SessionStore` is not disposable), so a
  restart or redeploy leaves live accounts' tokens valid on SmarterMail until they expire — which
  is also what lets a remembered browser resume after a redeploy.
- Conversation history is never stored server-side, in either mode. In browser-only mode nothing about
  the user is persisted anywhere; in server mode, only what a user saves to a profile.
- **Profile sessions** (`Session(ProfileRuntime)`) borrow the profile's `AccountSet` instead of owning
  one. Closing one (logout, idle, expiry) releases a lease on the profile and touches no account;
  they refresh lazily (`RefreshesLazily`), may hold zero accounts, and removing an account deletes it
  from the profile. A browser bundle (remember-me) is refused for them (`409 PROFILE_SESSION`).

### MCP tokens

The session id is the cookie's value and never leaves it: page script (XSS, an extension) cannot
read it, and it is not accepted as a bearer anywhere. MCP clients (Cursor, Claude Code) get a
separate credential instead, from the **MCP** menu in the chat header or `POST /api/auth/token`.

- Format `sma_mcp_` + 32 random bytes base64url. The session keeps only its SHA-256 (compared with
  `CryptographicOperations.FixedTimeEquals`); `SessionStore` indexes hash → session id so lookup is
  not a scan. Nothing is persisted.
- **One per session.** Minting again replaces it; `DELETE /api/auth/token` revokes it. Both need
  the cookie, so a stolen MCP token cannot mint its successor or revoke the user's.
- **Lifetime**: it dies with its session (logout, idle or absolute expiry, last account removed, a
  new login on that cookie), and never outlives the session's absolute expiry. `MCP_TOKEN_HOURS`
  optionally caps it shorter; unset means just the session lifetime.
- **Activity**: an MCP-token request touches the session like a cookie request, so a Cursor-only
  user does not idle out after 30 minutes. (A stolen token can therefore keep its session alive, but
  never past `SESSION_MAX_HOURS`, and only until revoked.)
- **Scope** is enforced by authorization, not paths. `Program.cs` registers two schemes on the same
  store: `Session` (cookie, the default scheme) and `McpToken` (bearer). Policy `SessionAccess`
  (every `/api/*` endpoint) names only `Session`. Policy `McpAccess` (`MapMcp`, every method on
  `/mcp`) names the policy scheme `SessionOrMcpToken`, which forwards to `McpToken` when the request
  carries a bearer header and to `Session` otherwise. Because the default scheme is the cookie,
  anonymous endpoints (`login`, `two-factor`) never see a session from a bearer either.

### Remember me on this device

Opt-in, per session, and the server still stores **nothing** per user: no database, no files, no
records. `RESUME_KEY` unset or invalid switches it off entirely (the UI hides the option; every
resume endpoint answers `404 RESUME_DISABLED`) and sessions behave exactly as before.

**The bundle** (`Auth/ResumeSealer.cs`). AES-256-GCM (`System.Security.Cryptography.AesGcm`) over
JSON `{ version: 1, rememberedSince, issuedAt, accounts: [{ baseUrl, login, role, readOnly,
clientId, refreshToken, refreshExpiration, userType }] }` — what `SignInControllerBase.BuildAccount`
needs to rebuild each account exactly as a fresh sign-in would, minus the access token. Wire
format, base64url: `[0x01][key id][nonce 12][ciphertext][tag 16]`; key id = first byte of
SHA-256(key); AAD = `"sma-resume-v1"` + the two header bytes. `RESUME_KEY_PREVIOUS` opens (never
seals) bundles from before a rotation; keep it for `RESUME_DAYS` and a rotation signs nobody out.
Refused before any network call: malformed, unknown key, failed tag, bad payload, or
`rememberedSince` more than `RESUME_DAYS` ago (default 30, capped at SmarterMail's 60-day refresh
lifetime) or in the future. `rememberedSince` is the **password** sign-in and carries across every
resume; `DELETE` then `PUT` keeps it too, so the chain cannot be stretched without a password.

**Opt-in** is a session toggle, `PUT /api/auth/resume`, which the UI calls right after a sign-in
when the box was ticked (after the two-factor step, if any). Every account in the session goes in
the bundle, including ones added later. The login, add-account and two-factor contracts are
unchanged.

**Keeping the browser's copy current is the crux.** SmarterMail rotates the refresh token on every
`refresh-token` call (one token per user and clientId), so each refresh kills the copy the browser
holds. Hence:

- **Lazy refresh.** The sweeper skips remembered sessions. `ToolDispatcher` calls
  `Account.EnsureFreshAsync` before each call: refresh only within `Account.LazyRefreshMargin`
  (2 min) of the access token's expiry (tokens live 15 min), re-checked under the refresh lock so
  concurrent calls rotate once; otherwise just re-stamp Core's refresh clock
  (`UserContextFactory.StampRefreshClock`) so Core's file path never runs. No expiry to go by →
  the 8-minute clock. The 401 retry still applies. A refresh SmarterMail **refuses** (4xx) drops
  the account, as the sweeper would, and the call gets a model-readable `isError`; an unreachable
  server (`RefreshResult.Unavailable`) is not fatal and the call goes ahead on the token it has.
  An idle remembered session rotates nothing, so the browser's copy stays valid while it is away.
- **Resume version.** `Session.ResumeVersion` is a Unix-ms clock forced strictly upward, bumped by
  every rotation (`Account.Rotated`, wired by `Session.Add`) and every account add, replace or
  remove. `Web/ResumeHeaders.cs` compares it with the request's `X-Resume-Version` as the response
  starts (so a refresh made by that very request is announced by it) and adds the newer number to
  **cookie** responses only. The bundle is never put in a header: at ~0.5–1.5 KB per account, five
  could overflow nginx's default 4–8 KB proxy header buffer and 502 an ordinary tool call. The
  browser fetches `GET /api/auth/resume` instead. Time-based, so a new session's versions are
  already above anything an older session gave the same browser.
- Refreshes caused by a **bearer** caller (MCP) bump the version too; the browser picks the new
  bundle up on its next request. If the session expires before the browser comes back, its copy is
  stale and resume fails: sign in again. Same for a tab closed between a refresh and the fetch.

**Forget vs revoke.** `SessionStore.ExpireAsync` (idle or max age, from the sweeper or a lookup)
forgets a remembered session (`Session.ForgetAsync` → `Account.ForgetAsync`: no `logout-user`) and
revokes anything else. Logout, `DELETE /api/accounts/{id}`, replacement and a failed refresh still
revoke; the UI drops its copy on logout and on removing the last account (other removals just bump
the version). A remembered session past its chain end counts as ordinary again.

**Resume** (`POST /api/auth/resume`, `login` limiter): unseal → skip an account whose
`refreshExpiration` has passed or whose `baseUrl` no longer passes `HostGuard` (re-validated; must
normalise to the same URL) → if **any** remaining server is over the `HostLoginThrottle` cap,
answer `HOST_THROTTLED` before refreshing anything (nothing rotated; the browser keeps a working
copy) → refresh each in parallel, each as a throttle attempt (SmarterMail's IDS counts an invalid
refresh token as a failed login, so `Rejected` counts) → rebuild with `BuildAccount`, open the
session with the chain's original `rememberedSince`, set the cookie, return the new bundle. A live
session on the incoming cookie is closed, but its accounts whose clientId is in the bundle are
**forgotten**, not revoked: `logout-user` revokes per clientId and would kill the pair the resume
just minted. Nothing restored → `401 RESUME_EXPIRED` (discard) or, if a server was unreachable,
`503 RESUME_UNAVAILABLE` (keep). Logged: the failure class, each host + `RefreshResult`, the
restored count; never the bundle.

**Replay.** Each resume rotates, so the copy presented is dead afterwards; a stolen older copy dies
once the legitimate browser resumes (or makes a call that refreshes), and SmarterMail simply
refuses it. No server state needed.

**Threat model.** An unlocked bundle is a bearer credential for its accounts: whoever copies it out
of the browser profile can resume until it is rotated past, reaches `RESUME_DAYS` or the 60-day
refresh expiry, or the user logs out (which revokes). Two-factor is **not** asked again on resume,
the same as SmarterMail's own refresh. It is useless without `RESUME_KEY` (it cannot be opened or
forged elsewhere), and rotating the key without `RESUME_KEY_PREVIOUS` signs out every remembered
device. The optional passkey lock means a copied profile alone is not enough.

**Browser side** (`wwwroot/js/resume.js`, `api.js`, `chat.js`). The record lives in
`localStorage["sma.resume"]`: `{ v: 1, locked: false, bundle, version }`. `api.js` sends
`X-Resume-Version` on every request and hands a newer announced version to `chat.js`, which fetches
and saves it (an older version never overwrites a newer one). On load a live cookie session wins;
otherwise an unlocked bundle resumes automatically, serialised across tabs with the Web Locks API
and re-checking the session inside the lock, so two tabs never rotate the same bundle twice and a
loser's failure never discards the winner's copy. A session that dies mid-chat (a redeploy)
resumes in place and keeps the conversation. Without the OpenRouter key in this tab, the existing
key-only continuation takes over. A dead bundle (`RESUME_EXPIRED` / `INVALID` / `DISABLED`) is
discarded only if it is still the one that was presented.

**Passkey lock** (client-side only; the server never sees any of it). Offered when
`PublicKeyCredential.getClientCapabilities()` reports `extension:prf`. `navigator.credentials.create`
with `rp.id` = the page's hostname, `residentKey: "preferred"`, `userVerification: "required"`, PRF
`eval.first` = the fixed 32-byte salt `"smartermail-agent resume lock v1"`, and a random,
never-verified challenge (the credential only derives a local key). PRF output (from `create`, or
an immediate `get` for authenticators that only answer on assertion) → HKDF-SHA-256 (info
`sma-resume-bundle-v1`) → non-extractable AES-256-GCM key, which encrypts the sealed bundle again:
`{ v: 1, locked: true, credentialId, iv, ciphertext, version }`. The passkey is created at the login
click, before the login request, because WebAuthn needs the user gesture. A locked bundle waits on
the login view for "Unlock with passkey" (a `get` with `allowCredentials: [credentialId]`);
cancelling keeps it. The key stays in memory for the page, so newer bundles re-encrypt silently; a
page that never unlocked cannot, and keeps the old copy rather than store one in the clear (the
"This device" menu then offers to unlock). That menu turns remembering and the lock on and off;
adding or removing the lock re-fetches the bundle from the live session, so no unlock is needed.

### Handles

The **handle** is what the LLM passes as `account`: the email address for mailbox and domain-admin
accounts, `sysadmin:<username>@<host>` for sysadmins (`host` = the base URL's authority, with a
non-default port). Unique within a session: on a clash `#<host>` is appended, then `-2`, `-3`….
Handles contain email addresses, so they are **never logged**.

### Role detection

`SmarterMailAuth` decides the role on every successful login (password or two-factor), strongest
signal first:

1. **SysAdmin** — JWT `role` claim `SysAdmin`/`PrimarySysAdmin` (payload decoded base64url, no
   signature check), or body `isAdmin: true`, or a login with no `@`. Also sets
   `TokenData.UserType = "admin"` (Core only reads that on its file-based re-login path, which never
   runs here, so it is cosmetic).
2. **DomainAdmin** — body `isDomainAdmin: true` (documented on `authenticate-user` and
   `authenticate-two-factor-code`), or a JWT `DomainAdmin` role.
3. **User** — body `isDomainAdmin: false`.
4. No signal at all → one probe `GET /api/v1/settings/domain/data`
   (`SmarterMailAuth.DomainAdminProbePath`); `2xx` means DomainAdmin, anything else User.

The role **only decides which tools are visible**. SmarterMail still authorizes every call, so a
wrong role gives a wrong tool list, never extra privilege.

## Server mode: profiles and scheduled tasks

Off entirely with `BROWSER_ONLY_MODE=true`: `Program.cs` registers no store, runtime, passkey service,
maintenance or scheduler, a middleware answers `/api/profile*` and `/api/tasks*` with
`404 SERVER_MODE_DISABLED` ahead of authentication, and `ServerModeOnlyAttribute` repeats the check.
`ServerModeHttpTests` asserts that `DATA_DIR` is never created then.

### Keys

Made in the browser (`js/vault.js`), HKDF-SHA-256 with a zero salt:

| Key | From | Where it goes |
|---|---|---|
| profile key PK | 32 random bytes | wrapped (AES-GCM) under each passkey's PRF output (`sma-profile-wrap-v1`, PRF salt `smartermail-agent profile v1`) and optionally a recovery code (`sma-recovery-wrap-v1`); the wraps are stored, PK never leaves the browser |
| settings key | `HKDF(PK, sma-settings-v1)` | never leaves the browser; encrypts `{ openRouterKey, model, toolsOff, allowChanges }`. Until the profile's idle timeout passes without activity (any successful request from a page holding the keys, like the server's idle clock) after the passkey or recovery code was used, it and the inbox key are kept in IndexedDB (`sma-profile`) as non-extractable `CryptoKey`s, so a reload, new tab or restarted browser on the still-unlocked profile session reopens the settings without the key prompt (`profile.reopenWarm`); cleared on logout and profile deletion |
| accounts key | `HKDF(PK, sma-accounts-v1)`, raw | sent to `unlock`; `ProfileRuntime` keeps it (as a `Sealer`) while a session of the profile lives; the server stores only `HMAC(key, "sma-accounts-check-v1")` |
| inbox key | `HKDF(PK, sma-inbox-v1)` | encrypts the PKCS#8 private half of a P-256 key pair; the public half is stored, and `ProfileCrypto.SealToPublicKey` seals every run transcript to it |
| recovery auth | `HKDF(code secret, sma-recovery-auth-v1)` | sent to `recover`; the server stores its SHA-256 |
| `DATA_KEY` | the operator | seals delegated accounts (`sma-server-account-v1`), task definitions, the tasks' OpenRouter key |

Every server-side seal is `Auth/Sealer.cs` with a label per use and the row as context
(`profileId|rowId`), so a blob moved to another row or use does not open. **The PRF output must never
reach the server**: `js/passkey.js` serialises credentials by hand with empty
`clientExtensionResults` (`toJSON()` would include it), and `PasskeyService` discards that field anyway.

### One owner per stored account

SmarterMail keeps one token per `(user, clientId)`, so two live copies of a stored account (two
browsers, or a browser and a task) would rotate each other's refresh token dead. `ProfileRegistry`
hands out one `ProfileRuntime` per profile, counted by session and task **leases**; every session of
the profile and every run share its `AccountSet`, i.e. the same `Account` objects and refresh locks.
Each rotation is saved by `Account.Persist`, awaited inside the refresh lock; a refresh that lands
after a *forget* saves the new pair instead of revoking it. When the last session lease goes, the
accounts sealed with the accounts key are forgotten and the key dropped; delegated ones may stay for a
running task. When no lease is left the runtime leaves the registry.

Stored rows (`profile_accounts`): `seal` = `profile` (accounts key) or `server` (`DATA_KEY`,
"delegated"), exactly one copy, re-sealed when delegation changes. `state` = `ok` or `rejected`
(SmarterMail refused the token): the row stays so the UI can ask for a sign-in, and a new sign-in to the
same login reuses the row id (`RowForLogin`), which tasks refer to.

### Flows

- **Create** (`POST /api/profile/register/options`, then `POST /api/profile`, cookie session with an
  account): verifies the passkey, writes the profile, its passkey and every account sealed with the new
  accounts key, moves the session's live accounts into the runtime, and swaps the cookie for a profile
  session. `PROFILE_MAIL_HOSTS` and `MAX_PROFILES` apply.
- **Sign in** (`POST /api/profile/login/options` with the two-factor limiter, because the page
  prefetches them; `POST /api/profile/login`): any discoverable credential; opens a **locked** profile
  session and returns that passkey's wrapped PK. **Recover** (`POST /api/profile/recover`) does the same
  with the recovery auth key.
- **Unlock** (`POST /api/profile/unlock { accountsKey }`): checked against the stored HMAC, then
  `RestoreAsync` refreshes every row it can open (`AccountRestorer`: SSRF guard, per-server throttle,
  a throttled server is skipped as `THROTTLED`), saves the rotated tokens, and answers
  `{ session, skipped }` with resume's reasons.
- `GET /api/profile` (passkeys, accounts live / stored / delegated, task settings, `idle: { minutes,
  defaultMinutes, minMinutes, maxMinutes }`), `PUT /api/profile/idle { minutes | null }` (the profile's
  session idle timeout, 5 to `PROFILE_MAX_IDLE_MINUTES`, `400 IDLE_OUT_OF_RANGE`; stored in
  `profiles.idle_minutes`, held on `ProfileRuntime.IdleTimeout`, applied at once to every session of the
  profile and reported as `SessionResponse.profile.idleMinutes`, which the browser also uses for how long
  it keeps the keys),
  `GET|PUT /api/profile/settings` (opaque blob, optimistic `version`, `409 SETTINGS_STALE`),
  `POST /api/profile/passkeys/options` + `POST /api/profile/passkeys`, `DELETE /api/profile/passkeys/{id}`
  (`409 LAST_PASSKEY`), `PUT /api/profile/recovery`, `PUT /api/profile/accounts/{id}/delegation`,
  `PUT /api/profile/task-key`, `PUT /api/profile/tasks-paused`, `DELETE /api/profile` (revokes every
  account, ends every session of it). All cookie-only (`403 COOKIE_REQUIRED` for an MCP token).

### Scheduled tasks

`TasksController` (`/api/tasks`, `/api/tasks/{id}`, `/api/tasks/{id}/run { dryRun }`,
`/api/tasks/runs[?taskId]`, `/api/tasks/runs/{id}`, `/api/tasks/runs/{id}/read`), only with `DATA_KEY`.

- **Definition** (`TaskDefinition`, sealed with `DATA_KEY`): name, prompt, five-field cron + IANA time
  zone, delegated account ids, `allowedWrites` (tool names), `maxWrites`, model, optional
  `emailAccountId`. Validation: shortest gap ≥ `TASK_MIN_INTERVAL_MINUTES` over 200 occurrences,
  accounts delegated, every allowed write a real write tool some task account's role can run, writes
  and email need a read-write account.
- **Scheduler** (`TaskRunScheduler`): every 30 s claims due tasks (`next_run_at` moved on first, so a
  run never starts twice; downtime collapses into one catch-up run), at most `TASK_CONCURRENCY`, never
  two runs of one task. Runs left `running` by a crash become `INTERRUPTED` at start.
- **Run** (`TaskRunner`): task key first (no key, no token rotation), then the accounts from the
  runtime (restored from their server-sealed rows if nobody is signed in), then `AgentLoop` with
  `TaskPrompt` and a `TaskToolContext` whose **`ToolGate`** the dispatcher enforces: writes not on the
  allowlist are `NotAllowed`, the budget is per run, a dry run answers writes itself. The model is only
  shown reads plus allowlisted writes. The transcript (report + every tool call, clamped to fit
  256 KB) is sealed to the profile's public key with context `task-run|profileId|runId`. Email
  delivery is `send_email` from the delivery account to its own address, issued by the server.
- **Failures**: codes in `TaskRunner.Explain`; hard ones (`TaskStore.HardFailures`: sign-in rejected,
  no or rejected key, account removed / no longer delegated / unreadable) pause the task at once,
  others after three in a row. Logs carry the task id, outcome and counts, never prompts or results.
- `ProfileMaintenance` refreshes delegated accounts untouched for 20 hours once a day, so a weekly
  task still finds a live token, and deletes profiles idle for `PROFILE_IDLE_DAYS`.

## Tool scopes and read-only

Every tool class is listed in `ToolPolicy.Groups` with a **scope** and a UI **category**:

| Scope | Tools | Open to | Categories |
|---|---|---|---|
| `Mailbox` | the 59 `src/Tools.Mailbox` tools | `User`, `DomainAdmin` | Mail, Calendar, Contacts, Tasks, Notes, Folders, Settings |
| `DomainAdmin` | the 107 `domain_*` tools (`src/Tools.DomainAdmin/Domain*Tools.cs`) | `DomainAdmin` | Domain, Domain users, Domain routing, Domain security, Mailing lists |
| `SysAdmin` | the 58 `src/Tools.SysAdmin` tools | `SysAdmin` (no mailbox tools) | Server, Domains, Users, Security, Spool, Certificates, DKIM, Monitoring |

A tool is **eligible** for an account when the account's role allows its scope and the account is
read-write or the tool is a read. A tool with no eligible account is hidden from `GET /api/tools`
and MCP `tools/list`.

### The `account` argument

`ToolPolicy.InjectAccount` adds `account: { type: "string", enum: [eligible handles], description }`
to a clone of each tool's `inputSchema`:

- **several eligible** → `account` is listed in `required`;
- **exactly one eligible** → present but optional; omitted means that account;
- **a single-account session** → no `account` property at all, so it behaves exactly as the
  one-mailbox service always did.

### Read-only

`readOnly` is chosen per account at sign-in and fixed for that account. Three layers:

1. Tool lists only offer a write tool when some read-write account can run it, and its `account`
   enum lists only the read-write accounts.
2. The dispatcher refuses a write on a read-only account: REST `403`, MCP `isError: true`.
3. `userContext.ReadOnlyMode` is set from the account; most mailbox tools and all
   `domain_*` write tools check it themselves. **No sysadmin tool checks it**, which is why every
   scope fails closed (below).

Reads are declared on the tool itself, `[McpServerTool(ReadOnly = true)]`; `ToolPolicy.IsMarkedReadOnly`
reads the SDK's `ProtocolTool.Annotations.ReadOnlyHint`. A tool without the mark is a write in every
scope. The full generated list, with the marks, is `docs/tools.md`.

**Mailbox scope** — a tool counts as a write tool if it lacks the read-only mark, **or** (belt and
braces) its name starts with one of

`send_ delete_ create_ update_ move_ upload_ add_ remove_ set_ mark_ respond_ reply_ forward_`

**or** is in `ToolPolicy.MailboxWriteNames`. That extra list exists because five tools guard themselves
with `if (userContext.ReadOnlyMode)` but do not match any prefix:

`new_or_update_calendar_event`, `new_or_update_task`, `block_senders`, `unblock_senders`,
`run_content_filters`.

Mailbox tools excluded from a read-only account (36 of 59):

```
block_senders                 create_contact                create_contact_group
create_content_filter         create_content_filter_simple  create_draft
create_folder                 create_note                   delete_calendar_event
delete_contact_group          delete_contacts               delete_content_filters
delete_note                   delete_task                   forward_email
move_emails                   new_or_update_calendar_event  new_or_update_task
remove_emails                 remove_folder                 remove_items
reply_to_email                respond_to_meeting            run_content_filters
send_draft                    send_email                    send_email_with_attachments
set_email_properties          set_trusted_senders           unblock_senders
update_contact                update_contact_group          update_content_filter
update_folder                 update_note                   upload_attachment
```

Note `create_content_filter` and `create_content_filter_simple` do **not** check
`userContext.ReadOnlyMode` themselves; the name filter is what stops them.

The 23 a read-only mailbox account sees:

```
check_sender_blocked  check_sender_trusted  download_email_attachment  expand_contact_group
get_calendar_event    get_calendar_events   get_contact                get_contact_group
get_contact_groups    get_contacts          get_content_filters        get_email_attachments
get_email_message     get_emails            get_global_address_book    get_note
get_notes             get_task_details      get_tasks                  get_user_data
list_folder_info_by_type  read_email_part   search_items
```

**Admin scopes go by the mark alone.** For `SysAdmin` and `DomainAdmin` a tool is a read only if it
is marked `ReadOnly = true`; everything else in those scopes is a write. A name rule would not be
safe there: `enable_dkim`, `stop_services`, `kill_user_sessions`, `reset_all_spool_messages`,
`disable_users`… match no write prefix. An admin tool newly added to a shared library is therefore a
write until someone marks it on purpose. `tests/Agent.Tests/ToolPolicyTests.cs` pins the exact read
set, so a mark added or dropped by accident fails the build.

SysAdmin reads (29 of 58):

```
get_acme_certificates      get_blocked_ips            get_connections            get_connections_count
get_dashboard_stats        get_dkim_settings          get_domain_info            get_domain_settings
get_domains                get_inactive_users         get_ip_access_rules        get_rspamd_servers
get_server_version         get_services               get_smtp_auth_bypass       get_smtp_block_rules
get_spam_assassin_servers  get_spool_message_count    get_spool_message_counts   get_spool_messages
get_ssl_certificate_counts get_ssl_certificates       get_throttled_counts       get_throttled_domains
get_throttled_users        get_troubleshooting_counts list_users                 search_log_files
search_users
```

SysAdmin writes (29): everything else, including `refresh_acme_certificates` and `reload_domain`
(POSTs that make the server do work).

**The agent owns no tool code.** Tools live in `src/Tools.Mailbox`, `src/Tools.DomainAdmin` and
`src/Tools.SysAdmin`; fix them there and McpUser / McpAdmin get the same fix. A new tool class in
any of them fails the agent's startup until it is given a scope in `ToolPolicy.Groups`.

### Domain-admin tools

`src/Tools.DomainAdmin/DomainAdminTools.cs`, all under
`/api/v1/settings/domain/*`, acting on the token's own domain (no `domain` parameter). Paths come from the public SmarterMail API reference
(`https://mail.smartertools.com/Documentation/api`, DomainSettingsController).

| Tool | Call |
|---|---|
| `domain_get_info` | GET `data` |
| `domain_get_settings` | GET `domain` — strings under password/secret-like keys (e.g. `ldapPassword`) are redacted, because tool results go on to the user's LLM provider |
| `domain_list_users` | GET `list-users`, trimmed to key fields, optional `search` filter |
| `domain_get_user` | GET `user/{email}` (a bare local part gets `@<token domain>`) |
| `domain_list_aliases` / `domain_get_alias` | GET `aliases[/{search}]` / `alias/{name}` |
| `domain_create_user` | POST `user-put` |
| `domain_update_user` | GET `user/{email}`, merge the given fields, POST `post-user` |
| `domain_delete_users` / `domain_disable_users` | POST `users-delete` / `users-disable/{disable}/{allowMail}` with `{ input: [...] }` |
| `domain_create_alias` / `domain_update_alias` / `domain_delete_alias` | POST `alias-put` / GET+merge then POST `alias` / POST `alias-delete/{name}` |

That is `DomainAdminTools.cs` (category Domain, 6 reads / 7 writes). The rest, all in
`src/Tools.DomainAdmin/` with each read marked `ReadOnly = true` (pinned in `ToolPolicyTests`):

| File | Category | Tools | Covers |
|---|---|---|---|
| `DomainUserTools.cs` | Domain users | 30 (14 read) | user groups, account search/counts, statuses, last logins, connections + disconnect, ActiveSync/MAPI/EWS access, password policy + compliance lists + expiry, disable 2FA, rename, per-user mail settings, forwarding, user defaults, reindex/resync/recalculate |
| `DomainRoutingTools.cs` | Domain routing | 25 (10 read) | domain aliases, address availability, catch-all, forwarding blacklist, signatures + mappings, shared resources, event hooks, permissions, `domain_update_settings` (merge, unknown keys refused) |
| `DomainSecurityTools.cs` | Domain security | 19 (7 read) | DKIM (enable/disable/settings/rollover/verify, DNS test), spam settings + checks, security settings, trusted senders, user login IPs, content filters |
| `DomainMailingListTools.cs` | Mailing lists | 20 (7 read) | lists, settings, subscribers (incl. digest), bounces, posters / banned users, system messages, subscriber fields, send digest |

Deliberately **not** wrapped: `impersonate-user` (no impersonation), `show-password`,
`reset-app-password` (returns a secret), every export / download / import endpoint, message-archive
and chat-history search (every user's mail and chats), `generate-key` and the key-resetting
`dkim-settings`, `subscriber-remove-all`, `propagate-settings`, `user-detach`, `webrtc-config`
(TURN credentials), LDAP / auth-provider endpoints. Every `domain_*` result has password, secret,
token, credential and private-key string fields redacted.

## SSRF guard

`Auth/HostGuard.cs` normalises whatever the user typed, forces `https://`, and resolves the host,
rejecting loopback, `0.0.0.0/8`, RFC1918, link-local (`169.254.0.0/16`, `fe80::/10`), CGNAT
(`100.64.0.0/10`), IPv6 ULA (`fc00::/7`), 6to4/Teredo, multicast, the TEST-NET ranges, and the
`localhost` / `*.local` / `*.internal` names. That check runs before a sign-in or resume; the
address actually connected to is checked again by `Auth/GuardedHttp.cs`, the handler behind
`SmarterMailAuth.SharedHttp` and every account's tool `HttpClient`: its `ConnectCallback` resolves
the host, refuses if **any** address is one `HostGuard` rejects (so a DNS answer that changes
between check and connect — rebinding — still cannot reach a private address), and connects only
to vetted addresses. Redirects and system proxies are off on both clients.
`ALLOW_PRIVATE_HOSTS=true` disables all of it *and* permits `http://` — development and LAN testing
only, never in production.

## Logging

Never logged: passwords, tokens, session ids, resume bundles or keys, tool arguments, tool
results, email addresses.
Logged: login success/failure **by hostname**, token revocation by hostname + HTTP status, session
open/close with the active count, MCP token issued/revoked (never the value), resume outcomes
(failure class, host + refresh result, restored count), and `tool name + duration + isError`.
`CoreConsoleFilter` drops Core's (`src/Core`) own
`[INFO]`/`[ERROR]`/`[DB]` Console writes, which contain folder ids, owner addresses and raw
SmarterMail error bodies. Set `CORE_CONSOLE_LOG=true` to see them while debugging.

## Environment

| Var | Default | Purpose |
|---|---|---|
| `BROWSER_ONLY_MODE` | `false` | `true` = no profiles, no tasks, nothing on disk |
| `DATA_DIR` | `./data` (`/data` in the image) | server mode: the SQLite file; startup fails if not writable |
| `DATA_KEY` / `DATA_KEY_PREVIOUS` | unset | server key (32 bytes, base64); unset = no delegation, no tasks. Malformed fails startup |
| `PUBLIC_ORIGIN` | unset | passkey origin / RP id; unset = from the request (passkeys need a host name, not an IP) |
| `PROFILE_MAIL_HOSTS`, `PROFILE_IDLE_DAYS` (180), `MAX_PROFILES` (1000), `PROFILE_MAX_IDLE_MINUTES` (480) | | profile limits; the last is the longest session idle timeout a profile may choose |
| `TASKS_ENABLED` (true), `TASK_CONCURRENCY` (2), `TASK_TIMEOUT_MINUTES` (10), `TASK_MAX_TOOL_ROUNDS` (15), `TASK_MIN_INTERVAL_MINUTES` (15), `TASKS_PER_PROFILE` (10), `TASK_RUN_RETENTION` (50), `LLM_BASE_URL` | | scheduled tasks |
| `PATH_BASE` | `/` | path prefix, e.g. `/mail-agent` when a reverse proxy serves it under one |
| `TRUSTED_PROXIES` | unset | comma-separated IPs/CIDRs of reverse proxies whose `X-Forwarded-For` / `-Proto` are believed. Unset = forwarded headers ignored; limits key off the TCP peer. A malformed entry fails startup |
| `TRUST_CF_CONNECTING_IP` | `false` | key rate limits off `CF-Connecting-IP`, only when the TCP peer is a trusted proxy |
| `HOME_LINK_URL` | unset | optional back link under the login form (http(s) or relative only); unset = no link |
| `HOME_LINK_TEXT` | `← Home` | text for that link |
| `ALLOW_PRIVATE_HOSTS` | `false` | dev only: permit `http://` and private IPs |
| `SESSION_IDLE_MINUTES` | `30` | idle timeout |
| `SESSION_MAX_HOURS` | `12` | absolute session lifetime |
| `SESSION_MAX_ACCOUNTS` | `5` | accounts per session |
| `MCP_TOKEN_HOURS` | unset | optional cap on an MCP token's lifetime; unset = until its session ends (at most `SESSION_MAX_HOURS`) |
| `ASPNETCORE_URLS` | `http://+:8080` | |
| `CORE_CONSOLE_LOG` | `false` | pass Core's Console output through |
| `HOST_FAILED_LOGIN_LIMIT` | `10` | failed sign-ins per mail server (from this service) before `HOST_THROTTLED` |
| `HOST_FAILED_LOGIN_WINDOW_MINUTES` | `60` | sliding window for the above; keep limit < 15 and window ≥ 50 to stay under SmarterMail's defaults |
| `RESUME_KEY` | unset | base64/base64url, 32 bytes (`openssl rand -base64 32`). Turns on "Remember me on this device"; unset or invalid = off. Keep it stable: changing it signs every remembered device out |
| `RESUME_KEY_PREVIOUS` | unset | the key before a rotation; only opens bundles (invalid = ignored) |
| `RESUME_DAYS` | `30` | how long one password sign-in can be remembered (capped at 60, SmarterMail's refresh-token lifetime) |

Server-mode settings are read from `IConfiguration` (`ServerOptions`), so tests set them with
`UseSetting`; a malformed value fails startup with its name.

`RESUME_KEY` is the service's one secret in browser-only mode (`DATA_KEY` is the other in server mode) (it seals other people's refresh tokens): keep it in your
secret store and pass it in through the environment, never in a file in this repo. Without it the service
has no credentials of its own.

## Build & deploy

From the repo root:

```bash
dotnet build SmarterMail.slnx
dotnet test SmarterMail.slnx                           # SmarterMail.Tests + Agent.Tests (both gate this image)
node --test 'src/Agent/wwwroot/dev/test/*.test.mjs'    # frontend (the glob matters on Node 26)
scripts/build-images.sh agent                          # local image
```

Self-hosting (reverse proxy, env, secrets): `docs/agent.md`. Behind a reverse proxy set
`TRUSTED_PROXIES` to the proxy's address, or every visitor shares the proxy's IP as one rate-limit
bucket.

A healthcheck is a bash `/dev/tcp` probe of the **unprefixed** `/health`, not curl: the
`mcr.microsoft.com/dotnet/aspnet:10.0` base image ships neither curl nor wget, and its `/bin/sh` is
dash. `UsePathBase` only strips a prefix, it never requires one, so `/health` and
`<PATH_BASE>/health` both answer `200`.

Assets are **content-versioned** (`Web/AssetVersioning.cs`, wired in `Program.cs`). At startup the
agent hashes `wwwroot` (minus `dev/`) into a 12-hex version and serves `index.html` with its
`./css/…` and `./js/…` references rewritten to `./v/{version}/css/…` and `./v/{version}/js/…`.
The ES modules import each other by relative path (`./api.js`), so every import resolves under the
same versioned directory without touching the JS. `/v/{any}/rest` is served from `wwwroot/rest`
with `Cache-Control: public, max-age=31536000, immutable`; `index.html` gets `no-cache` plus an
ETag of the version (cheap `304`); unversioned asset paths stay `no-cache`. Plain `no-cache` alone
was not enough: a CDN such as Cloudflare can override it with a zone Browser Cache TTL
(`max-age=86400`) on `.js`/`.css` and cache them at the edge.
A deploy that changes any shipped file changes every asset URL; unchanged content keeps its URLs
across restarts. Keep `index.html`'s asset references in the `"./css/…"` / `"./js/…"` form.

## Verification

Run locally against a SmarterMail server you can sign in to. Keep credentials in the environment,
never in a file:

```bash
export SM_URL=https://mail.example.com SM_USER=you@example.com SM_PASS='…'

cd src/Agent
ALLOW_PRIVATE_HOSTS=true ASPNETCORE_URLS=http://127.0.0.1:8199 dotnet run &   # ALLOW_PRIVATE_HOSTS only for a LAN / http server
B=http://127.0.0.1:8199

curl -s $B/health                                  # smartermail-agent ok

python3 -c "import json,os;print(json.dumps({'hostname':os.environ['SM_URL'],'email':os.environ['SM_USER'],'password':os.environ['SM_PASS'],'readOnly':True}))" \
  | curl -s -X POST $B/api/auth/login -H 'Content-Type: application/json' --data-binary @- -c /tmp/ck.txt

curl -s -b /tmp/ck.txt $B/api/tools | python3 -c 'import sys,json;print(len(json.load(sys.stdin)))'   # 23 read-only / 59 read-write

curl -s -b /tmp/ck.txt -X POST $B/api/tools/call -H 'Content-Type: application/json' \
  -d '{"name":"get_emails","arguments":{"folderId":"jdoe/Inbox","take":3}}'    # <username>/<folder>

curl -s -b /tmp/ck.txt -X POST $B/api/tools/call -H 'Content-Type: application/json' \
  -d '{"name":"send_email","arguments":{}}' -w ' %{http_code}\n'                      # 403

TOK=$(curl -s -b /tmp/ck.txt -X POST $B/api/auth/token | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')
curl -s -X POST $B/mcp -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $TOK" $B/api/auth/session   # 401: /mcp only
curl -s -o /dev/null -w '%{http_code}\n' -b /tmp/ck.txt -X DELETE $B/api/auth/token          # 204: revoked

ls /tmp | grep sma-never          # must print nothing
rm -f /tmp/ck.txt
```

A second account (e.g. a sysadmin, read-only) goes through `POST /api/accounts` with the same body;
`GET /api/tools` then shows both scopes, a sysadmin tool runs as the sysadmin, passing the mailbox
handle as `account` is an `isError` (wrong role), and `delete_domain` on a read-only account is `403`.

Private-host rejection must be checked **without** `ALLOW_PRIVATE_HOSTS`:

```bash
cd src/Agent
ASPNETCORE_URLS=http://127.0.0.1:8198 dotnet run &
for h in https://169.254.169.254 localhost https://192.168.1.5 'https://[::1]' https://100.64.0.1; do
  curl -s -X POST http://127.0.0.1:8198/api/auth/login -H 'Content-Type: application/json' \
    -d "{\"hostname\":\"$h\",\"email\":\"a@b.c\",\"password\":\"x\"}" -w " $h %{http_code}\n"
done   # all 400 (the sixth attempt in a minute is 429 — the login limiter)
```

MCP servers: `npx @modelcontextprotocol/inspector` with `docker run -i --rm -e SMARTERMAIL_URL -e
SMARTERMAIL_USER -e SMARTERMAIL_PASSWORD smartermail/smartermail-mcp-user --stdio` (after
`scripts/build-images.sh user`): `tools/list` shows 23 tools read-only, and nothing but JSON-RPC
reaches stdout.

## Known limitations

- SmarterMail's own `/api/v1/auth/*` responses are the only source of truth for credentials. The
  body is parsed on every status and reported by its own message code; an unrecognised code
  surfaces verbatim as "SmarterMail refused the login (CODE)."
- A pending two-factor challenge is process-local. Two replicas behind a load balancer would need
  sticky sessions, or the challenge store moved somewhere shared.
- An account survives exactly as long as SmarterMail's refresh token does. If refresh fails the
  sweeper drops that account; if it was the last one the session goes too and the UI falls back to
  the login view on the next `401`.
- **Not yet checked against a live server**:
  the real `isAdmin`/`isDomainAdmin` login flags and JWT claims; that the domain probe answers
  `2xx` only to domain admins; and, for the `domain_*` tools, whether `users-delete` /
  `users-disable` want local parts or full addresses, whether `post-user` / `alias` accept the
  whole object read back from GET, and that `maxMailboxSize` is bytes. The same read-modify-write
  question applies to most `domain_update_*` tools (settings, signatures, event hooks, mailing
  lists, password policy), plus whether enums round-trip as numbers, local part vs full address
  on group/protocol endpoints, and whether `whitelist` POST replaces the whole list.
- There is no approval step for admin writes beyond the per-account read-only flag and a
  system-prompt instruction to state the change and get confirmation first.
- Remember-me: a browser copy goes stale if a refresh happens and no browser request follows
  before the session expires (an MCP bearer caller refreshing, or a tab closed right after a
  refresh); resume then fails and the user signs in again. On resume, accounts that fail are left
  out of the new bundle even when the failure was transient (only a resume where nothing came back
  keeps the old copy). Not yet checked against a live SmarterMail; exercised against a fake one.
- Server mode is **single-replica**: SQLite, in-memory runtimes and an in-process scheduler.
- Profiles need a passkey with the WebAuthn **PRF** extension; without it the browser falls back to
  remember-me (when `RESUME_KEY` is set). Passkeys need HTTPS or `localhost`, never a bare IP.
- Checked against a live server (October 2026): `refresh-token` **slides** `refreshTokenExpiration`
  (60 days from each refresh), rotates the refresh token, and refuses a replayed one, so the daily
  keep-alive keeps delegated accounts alive indefinitely. Not yet checked: the passkey flows in real
  browsers with synced passkeys (the server side is tested with a software authenticator, the
  browser crypto against a C#-sealed vector, the UI against the stub with a recovery code).
- `wwwroot/dev/` (the frontend's stub server) is excluded from the published image by both the root
  `.dockerignore` and a `Content Remove` in the csproj, but still serves from a local `dotnet run`.
