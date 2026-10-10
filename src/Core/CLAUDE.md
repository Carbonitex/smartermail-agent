# SmarterMailMcp.Core (src/Core)

Shared .NET library for everything in this repo. Provides authentication, HTTP client, models, and token management used by McpUser, McpAdmin, the Agent and all three tool libraries (`src/Tools.Mailbox`, `src/Tools.DomainAdmin`, `src/Tools.SysAdmin`). Formerly the separate `smartermail-mcp-core` repo.

## Structure

- `Auth/AuthenticationService.cs` — SmarterMail API authentication (username/password → token). SmarterMail keeps one token per `(user, clientId)`, so every sign-in (`AuthenticateAsync`, `AuthenticateWithResultAsync`, and through them `StartupSignIn`, `UserContext`'s re-auth fallback and `LoginServer`) sends a fresh `NewClientId()` (`smartermail-mcp-<16 hex>`) and stores it in `TokenData.ClientId`; two instances on one account no longer evict each other. Refresh passes the stored value through as-is, but SmarterMail reads the clientId from the refresh token's claim, so token files still carrying the old fixed `smartermail-mcp` keep refreshing. Tests: `tests/SmarterMail.Tests/ClientIdTests.cs`
- `Auth/AuthenticationResult.cs` — `AuthenticateWithResultAsync`'s result (`AuthResultKind` Success / Rejected / Transient, safe-to-log message, code, HTTP status) and `AuthResponseClassifier`, which reads the authenticate-user JSON body on every status the same way `src/Agent/Auth/SmarterMailAuth.cs` does. Unlike the legacy tuple `AuthenticateAsync` (unchanged, still used by `UserContext`'s re-auth fallback and `LoginServer`), it refuses a two-factor / password-change AuthStep token instead of saving it as a login
- `Auth/StartupSignIn.cs` — startup sign-in for McpUser / McpAdmin (called from `src/Mcp.Hosting`): transient failures retry forever with capped backoff (2s … 60s), a credential rejection returns exit code 2 without retrying (repeated failed logins can IP-block the shared Docker bridge address). `StartupShutdownSignal` cancels the wait on SIGINT/SIGTERM. Tests: `tests/SmarterMail.Tests/StartupSignInTests.cs`
- `Auth/LoginServer.cs` — Interactive browser-based login (CLI usage only, not used in server mode)
- `Models/GlobalContext.cs` — Token file management, read-only mode enforcement
- `Models/UserContext.cs` — HTTP client wrapper (see the agent coupling below). Concurrent callers share one in-flight token refresh (they wait; they do not skip). Every JSON/bytes GET/POST/PUT/DELETE retries once on 401 after a force refresh. Multipart uploads wait for a fresh token but cannot retry (content is consumed). `GetUserInfoAsync` no longer swallows 401s.
- `Models/TokenData.cs` — Token data model (access token, refresh token, base URL, etc.)
- `Models/SmarterMailApiException.cs` — API error exception with status code and response body
- `Logging.cs` — Debug logging utilities
- `SecretRedactor.cs` — blanks non-empty strings under secret-like keys (password, secret, apikey, privatekey, token, credential, activationkey) at any depth, optionally `{key,value}` pairs; `privatekey` does not match `publicKey`. Used by every Tools.DomainAdmin and Tools.SysAdmin result that passes an API response through. Tests: `tests/SmarterMail.Tests/RedactionTests.cs`
- `TextWindow.cs` — `Slice` / `FilterLines` / `ClampMaxChars`: line-boundary windows over large text (forward or from the end), with `totalChars` / `hasMore` / `nextOffset`; used by `search_log_files`

## Usage

Referenced by project reference (`../Core/SmarterMailMcp.Core.csproj`) from `src/McpUser`, `src/McpAdmin`, `src/Agent`, `src/Tools.Mailbox`, `src/Tools.DomainAdmin` and `src/Tools.SysAdmin`. A change here goes into all three images.

### The agent depends on private field names

`src/Agent/Auth/UserContextFactory.cs` never lets Core touch a token file: it builds a `UserContext` and writes its private fields by reflection — `_accessToken`, `_baseUrl`, `_isConnected`, `_httpClient`, `_lastTokenRefresh`. Renaming or removing any of them still compiles, but fails `tests/Agent.Tests/UserContextFactoryTests.cs` (and would throw a named error on the agent's first login). Change the factory in the same commit. The agent also relies on `_lastTokenRefresh` being stamped so Core's own 10-minute file-based auto-refresh never fires; see the root `CLAUDE.md`, "Tokens never touch disk".

Core's `[INFO]` / `[ERROR]` / `[DB]` Console writes carry mail metadata; the agent drops them by prefix in `src/Agent/Logging/CoreConsoleFilter.cs`. Changing those prefixes means updating the filter.

### Server-side initialization (HTTP transport mode)

```csharp
// Use the server-side constructor (no CLI args needed)
var globalContext = new GlobalContext("/tmp/smartermail_token.json", readOnlyMode: false);

// Authenticate using env vars
var authService = new AuthenticationService(globalContext);
var (success, message, tokenData) = await authService.AuthenticateAsync(baseUrl, username, password, readOnlyMode: false);

// Initialize UserContext from the token file
var userContext = new UserContext(globalContext);
userContext.InitializeFromFile(globalContext);
await userContext.RefreshTokenAsync();
```

## Dependencies

- .NET 10.0, no external NuGet packages
