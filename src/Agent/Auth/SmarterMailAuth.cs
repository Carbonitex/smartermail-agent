using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Auth;

/// <summary>
/// The result of an authentication step against SmarterMail.
///
/// SmarterMail does not use HTTP status alone to describe a login: a two-factor account answers
/// <c>200 { success: true, accessToken: &lt;10-minute AuthStep JWT&gt;, message: "TWO_FACTOR_REQUIRED|..." }</c>
/// with an empty refresh token. Treating that as a success hands the session a token that gets 403
/// on every real endpoint, so the outcome is modelled explicitly instead.
/// </summary>
public abstract record AuthOutcome
{
    /// <summary>
    /// Fully authenticated: <paramref name="TokenData"/> has a usable access token.
    /// <paramref name="Role"/> only decides which tools the account sees.
    /// </summary>
    public sealed record Success(TokenData TokenData, AccountRole Role = AccountRole.User) : AuthOutcome;

    /// <summary>
    /// Credentials were correct but a second factor is needed.
    /// <paramref name="StepToken"/> is the short-lived AuthStep JWT for
    /// <c>authenticate-two-factor-code</c>; it is null on older builds that signal 2FA with a 401,
    /// in which case the login must be replayed with username + password + twoFactorCode.
    /// </summary>
    public sealed record TwoFactorRequired(string Method, string EmailAddress, string? StepToken) : AuthOutcome;

    /// <summary>
    /// Something must be finished in SmarterMail webmail first. <paramref name="Kind"/> is one of
    /// <c>CHANGE_PASSWORD_NEEDED</c>, <c>PASSWORD_EXPIRED</c>, <c>TWO_FACTOR_SETUP_REQUIRED</c>,
    /// <c>APP_PASSWORD_REQUIRED</c>.
    /// </summary>
    public sealed record ActionRequired(string Kind, string Message) : AuthOutcome;

    /// <summary><paramref name="Code"/> is SmarterMail's own message code, e.g. USERNAME_OR_PASSWORD_INCORRECT.</summary>
    public sealed record Failed(string Code, string FriendlyMessage) : AuthOutcome;
}

/// <summary>How a refresh-token call went.</summary>
public enum RefreshResult
{
    /// <summary>New access and refresh tokens are in the TokenData; the old refresh token is dead.</summary>
    Refreshed,

    /// <summary>SmarterMail refused the refresh token (4xx), or there was none: sign in again.</summary>
    Rejected,

    /// <summary>No usable answer (connection failure, timeout, 5xx): the token may still be good.</summary>
    Unavailable,
}

/// <summary>
/// Fileless replacement for Core's <c>AuthenticationService</c>.
///
/// Core's AuthenticationService.AuthenticateAsync() and UserContext's refresh path both persist
/// TokenData through GlobalContext.WriteTokenFile() and *fail* if the write fails, which would put
/// SmarterMail access tokens for arbitrary members of the public on this container's disk.
/// This class speaks the same endpoints and keeps TokenData in memory only.
/// </summary>
public sealed class SmarterMailAuth(ILogger<SmarterMailAuth> logger)
{
    /// <summary>
    /// SmarterMail stores one token per (user, clientId) pair: authenticating again with the same
    /// clientId invalidates the previous session's token. Every session therefore gets its own
    /// clientId so two browsers (or a browser and Cursor) on the same mailbox do not evict
    /// each other. The same clientId must be reused for the two-factor completion call.
    /// </summary>
    public static string NewClientId() =>
        "smartermail-agent-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    // Connect-time SSRF check, no redirects, no proxy (see GuardedHttp).
    private static readonly HttpClient SharedHttp = new(GuardedHttp.CreateHandler())
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// How long <see cref="LogoutAsync"/> waits for the mail server. Logout is awaited on the
    /// request path, so a dead server may delay it by this much and no more.
    /// </summary>
    public static readonly TimeSpan DefaultLogoutTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http = SharedHttp;
    private readonly TimeSpan _logoutTimeout = DefaultLogoutTimeout;

    /// <summary>Test seam: a client over a stub handler. DI only sees the public constructor.</summary>
    internal SmarterMailAuth(ILogger<SmarterMailAuth> logger, HttpClient http, TimeSpan? logoutTimeout = null)
        : this(logger)
    {
        _http = http;
        _logoutTimeout = logoutTimeout ?? DefaultLogoutTimeout;
    }

    /// <summary>Step one: username + password.</summary>
    public async Task<AuthOutcome> AuthenticateAsync(
        string baseUrl, string username, string password, bool readOnlyMode,
        string clientId, CancellationToken ct = default)
    {
        var url = $"{baseUrl.TrimEnd('/')}/api/v1/auth/authenticate-user";
        return await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { username, password, clientId }),
            },
            baseUrl, username, readOnlyMode, clientId, ct);
    }

    /// <summary>
    /// Step two. With a step token this posts <c>authenticate-two-factor-code</c> carrying the
    /// AuthStep JWT as a bearer (the step token is single-use: SmarterMail answers
    /// INVALID_TWO_FACTOR_CODE if it is replayed). Without one — older builds that signalled 2FA
    /// with a 401 and no token — it replays <c>authenticate-user</c> with an inline
    /// <c>twoFactorCode</c>, which those builds and current ones both accept.
    /// </summary>
    public async Task<AuthOutcome> CompleteTwoFactorAsync(
        string baseUrl, string username, string? password, string? stepToken, string code,
        bool readOnlyMode, string clientId, CancellationToken ct = default)
    {
        var root = baseUrl.TrimEnd('/');

        if (!string.IsNullOrEmpty(stepToken))
        {
            return await SendAsync(
                () =>
                {
                    var request = new HttpRequestMessage(
                        HttpMethod.Post, $"{root}/api/v1/auth/authenticate-two-factor-code")
                    {
                        Content = JsonContent.Create(new { twoFactorCode = code, clientId }),
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stepToken);
                    return request;
                },
                baseUrl, username, readOnlyMode, clientId, ct);
        }

        if (string.IsNullOrEmpty(password))
            return new AuthOutcome.Failed("CHALLENGE_EXPIRED", "That sign-in attempt expired. Please log in again.");

        return await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{root}/api/v1/auth/authenticate-user")
            {
                Content = JsonContent.Create(new { username, password, clientId, twoFactorCode = code }),
            },
            baseUrl, username, readOnlyMode, clientId, ct);
    }

    private async Task<AuthOutcome> SendAsync(
        Func<HttpRequestMessage> factory, string baseUrl, string username, bool readOnlyMode,
        string clientId, CancellationToken ct)
    {
        try
        {
            using var request = factory();
            using var response = await _http.SendAsync(request, ct);

            // SmarterMail puts the reason in the JSON body on 4xx as well as on 200, so the body is
            // always the source of truth. A 401 body still carries USERNAME_OR_PASSWORD_INCORRECT,
            // INVALID_TWO_FACTOR_CODE, or (on older builds) TWO_FACTOR_REQUIRED.
            JsonElement data = default;
            try
            {
                data = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            }
            catch (Exception)
            {
                // Not JSON (a proxy error page, say). Fall through to the status-only path.
            }

            var outcome = Interpret(data, baseUrl, username, readOnlyMode, clientId, (int)response.StatusCode);
            LogOutcome(baseUrl, (int)response.StatusCode, data, outcome);

            if (outcome is AuthOutcome.Success success)
                outcome = await WithRoleAsync(success, data, username, ct);

            return outcome;
        }
        catch (HttpRequestException)
        {
            logger.LogInformation("SmarterMail auth for host {Host}: connection failed.", baseUrl);
            return new AuthOutcome.Failed("CONNECTION_FAILED", "Could not connect to that SmarterMail server.");
        }
        catch (TaskCanceledException)
        {
            logger.LogInformation("SmarterMail auth for host {Host}: timed out.", baseUrl);
            return new AuthOutcome.Failed("TIMEOUT", "That SmarterMail server did not respond in time.");
        }
    }

    private static AuthOutcome Interpret(
        JsonElement data, string baseUrl, string username, bool readOnlyMode, string clientId, int status)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return new AuthOutcome.Failed(
                $"HTTP_{status}", $"SmarterMail refused the login (HTTP_{status}).");
        }

        var message = Str(data, "message") ?? string.Empty;
        var accessToken = Str(data, "accessToken");
        var success = Bool(data, "success") ?? status is >= 200 and < 300;

        // A 200 whose message says TWO_FACTOR_REQUIRED is NOT a login. The accessToken that comes
        // with it is a 10-minute AuthStep JWT, good only for authenticate-two-factor-code.
        if (message.StartsWith("TWO_FACTOR_REQUIRED", StringComparison.Ordinal))
        {
            var parts = message.Split('|');
            var detail = parts.Length > 1 ? parts[1] : string.Empty;

            if (detail.Equals("APP_PASSWORD", StringComparison.OrdinalIgnoreCase))
            {
                return new AuthOutcome.ActionRequired("APP_PASSWORD_REQUIRED",
                    "This account requires an app-specific password, which SmarterMail only accepts " +
                    "for mail protocols, not for this API. Generate one in SmarterMail webmail and use " +
                    "an account without that restriction here.");
            }

            if (detail.EndsWith("NOT_SETUP", StringComparison.OrdinalIgnoreCase))
            {
                return new AuthOutcome.ActionRequired("TWO_FACTOR_SETUP_REQUIRED",
                    "Two-step authentication is required on this account but has not been set up yet. " +
                    "Finish setting it up in SmarterMail webmail, then sign in here.");
            }

            var method = detail.Length > 0 ? detail.ToLowerInvariant() : "rfc6238";
            var email = parts.Length > 2 && parts[2].Length > 0
                ? parts[2]
                : Str(data, "emailAddress") ?? username;

            return new AuthOutcome.TwoFactorRequired(
                method, email, string.IsNullOrEmpty(accessToken) ? null : accessToken);
        }

        // These come back with success:true and a PasswordReset AuthStep token, not a usable one.
        if (Bool(data, "changePasswordNeeded") == true)
        {
            return new AuthOutcome.ActionRequired("CHANGE_PASSWORD_NEEDED",
                "SmarterMail wants this account's password changed before it can be used. " +
                "Change it in SmarterMail webmail, then sign in here.");
        }

        if (Bool(data, "passwordExpired") == true)
        {
            return new AuthOutcome.ActionRequired("PASSWORD_EXPIRED",
                "This account's password has expired. Reset it in SmarterMail webmail, then sign in here.");
        }

        if (success && !string.IsNullOrEmpty(accessToken))
        {
            return new AuthOutcome.Success(new TokenData
            {
                AccessToken = accessToken,
                BaseUrl = baseUrl.TrimEnd('/'),
                Username = username,
                ReadOnlyMode = readOnlyMode,
                Method = "simple",
                UserType = "user",
                ClientId = clientId,
                RefreshToken = Str(data, "refreshToken"),
                Expiration = Str(data, "accessTokenExpiration"),
                RefreshExpiration = Str(data, "refreshTokenExpiration"),
            });
        }

        var code = message.Length > 0 ? message.Split('|')[0] : $"HTTP_{status}";
        return new AuthOutcome.Failed(code, Friendly(code));
    }

    /// <summary>
    /// Cheap GET that only a domain administrator may make (<c>roleRequired: domainadmin</c> in
    /// SmarterMail's API docs). Used only when the login body carries no <c>isDomainAdmin</c> flag.
    /// </summary>
    public const string DomainAdminProbePath = "/api/v1/settings/domain/data";

    private static readonly string[] RoleClaimNames =
    [
        "role", "roles",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
    ];

    /// <summary>
    /// Decides the account's role from what the login returned, or null when only a probe can tell
    /// (a mailbox login whose body has no <c>isDomainAdmin</c> flag). Signals, strongest first:
    /// <list type="number">
    ///   <item>JWT <c>role</c> claim <c>SysAdmin</c>/<c>PrimarySysAdmin</c>, body <c>isAdmin: true</c>,
    ///         or a login with no <c>@</c> (SmarterMail sysadmins have no domain) → SysAdmin;</item>
    ///   <item>body <c>isDomainAdmin: true</c> or a JWT <c>DomainAdmin</c> role → DomainAdmin;</item>
    ///   <item>body <c>isDomainAdmin: false</c> → User.</item>
    /// </list>
    /// The JWT is decoded without checking its signature: it only picks a tool list, and SmarterMail
    /// checks the token itself on every call.
    /// </summary>
    public static AccountRole? DetectRole(string username, string? accessToken, JsonElement body)
    {
        var roles = JwtRoles(accessToken);
        var hasBody = body.ValueKind == JsonValueKind.Object;

        if (roles.Any(r => r.Equals("SysAdmin", StringComparison.OrdinalIgnoreCase) ||
                           r.Equals("PrimarySysAdmin", StringComparison.OrdinalIgnoreCase)) ||
            (hasBody && Bool(body, "isAdmin") == true) ||
            !username.Contains('@'))
            return AccountRole.SysAdmin;

        if ((hasBody && Bool(body, "isDomainAdmin") == true) ||
            roles.Any(r => r.Equals("DomainAdmin", StringComparison.OrdinalIgnoreCase)))
            return AccountRole.DomainAdmin;

        if (hasBody && Bool(body, "isDomainAdmin") == false)
            return AccountRole.User;

        return null;
    }

    /// <summary>The role claim(s) of a JWT's payload, read without verifying it. Empty on any error.</summary>
    public static IReadOnlyList<string> JwtRoles(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return [];

        var parts = token.Split('.');
        if (parts.Length != 3)
            return [];

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            var roles = new List<string>();
            foreach (var name in RoleClaimNames)
            {
                if (!document.RootElement.TryGetProperty(name, out var claim))
                    continue;

                if (claim.ValueKind == JsonValueKind.String)
                    roles.Add(claim.GetString()!);
                else if (claim.ValueKind == JsonValueKind.Array)
                    roles.AddRange(claim.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!));
            }

            return roles;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task<AuthOutcome.Success> WithRoleAsync(
        AuthOutcome.Success success, JsonElement body, string username, CancellationToken ct)
    {
        var role = DetectRole(username, success.TokenData.AccessToken, body)
                   ?? await ProbeDomainAdminAsync(success.TokenData, ct);

        if (role == AccountRole.SysAdmin)
            success.TokenData.UserType = "admin";

        return success with { Role = role };
    }

    /// <summary>One GET of <see cref="DomainAdminProbePath"/>; a 2xx means domain admin. Anything else is User.</summary>
    private async Task<AccountRole> ProbeDomainAdminAsync(TokenData tokenData, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{tokenData.BaseUrl!.TrimEnd('/')}{DomainAdminProbePath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode ? AccountRole.DomainAdmin : AccountRole.User;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation("Domain-admin probe failed for host {Host}; treating the account as a user.",
                tokenData.BaseUrl);
            return AccountRole.User;
        }
    }

    /// <summary>Maps a SmarterMail message code to something a stranger can act on.</summary>
    public static string Friendly(string code) => code switch
    {
        "USERNAME_OR_PASSWORD_INCORRECT" or "USER_NOT_FOUND" => "SmarterMail rejected those credentials.",
        "INVALID_TWO_FACTOR_CODE" => "That code was not accepted.",
        _ when code.Contains("DISABLED", StringComparison.OrdinalIgnoreCase) => "That account is disabled.",
        _ => $"SmarterMail refused the login ({code}).",
    };

    /// <summary>
    /// Logs the SmarterMail status and message code only. The message can carry the account's
    /// email address (TWO_FACTOR_REQUIRED|rfc6238|alice@example.com), so only the first two
    /// pipe-separated segments are ever logged, and never the token, password or code.
    /// </summary>
    private void LogOutcome(string baseUrl, int status, JsonElement data, AuthOutcome outcome)
    {
        if (outcome is AuthOutcome.Success)
            return;

        var message = data.ValueKind == JsonValueKind.Object ? Str(data, "message") ?? string.Empty : string.Empty;
        var parts = message.Split('|');
        var safe = parts.Length > 1 ? $"{parts[0]}|{parts[1]}" : parts[0];
        if (safe.Length == 0)
            safe = outcome is AuthOutcome.Failed f ? f.Code : outcome.GetType().Name;

        logger.LogInformation("SmarterMail auth for host {Host}: {Status} {Code}", baseUrl, status, safe);
    }

    /// <summary>Refreshes <paramref name="tokenData"/> in place. Never touches disk.</summary>
    public async Task<bool> RefreshAsync(TokenData tokenData, CancellationToken ct = default) =>
        await TryRefreshAsync(tokenData, ct) == RefreshResult.Refreshed;

    /// <summary>
    /// Refreshes <paramref name="tokenData"/> in place and says why not when it could not.
    /// SmarterMail rotates the refresh token on every call (one token per user and clientId), so a
    /// success leaves the previous refresh token dead. Never touches disk.
    /// </summary>
    public async Task<RefreshResult> TryRefreshAsync(TokenData tokenData, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tokenData.RefreshToken) || string.IsNullOrEmpty(tokenData.BaseUrl))
            return RefreshResult.Rejected;

        var url = $"{tokenData.BaseUrl.TrimEnd('/')}/api/v1/auth/refresh-token";
        try
        {
            using var response = await _http.PostAsJsonAsync(
                url, new { token = tokenData.RefreshToken, clientId = tokenData.ClientId }, ct);

            // A 4xx is SmarterMail refusing the token (expired, revoked, or already rotated); a 5xx
            // or a proxy page is the server having a bad moment, and the token may still be good.
            if (!response.IsSuccessStatusCode)
                return (int)response.StatusCode is >= 400 and < 500 ? RefreshResult.Rejected : RefreshResult.Unavailable;

            var data = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("accessToken", out var accessToken) ||
                accessToken.ValueKind != JsonValueKind.String)
                return RefreshResult.Unavailable;

            tokenData.AccessToken = accessToken.GetString();
            tokenData.RefreshToken = Str(data, "refreshToken") ?? tokenData.RefreshToken;
            tokenData.Expiration = Str(data, "accessTokenExpiration") ?? tokenData.Expiration;
            tokenData.RefreshExpiration = Str(data, "refreshTokenExpiration") ?? tokenData.RefreshExpiration;
            return RefreshResult.Refreshed;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return RefreshResult.Unavailable;
        }
    }

    /// <summary>
    /// Best-effort server-side revocation. Posts <c>logout-user</c> with the account's access token;
    /// SmarterMail then drops every token for that (user, clientId), refresh token included.
    /// Bounded by a short timeout and never throws: an account being removed goes away whether or
    /// not its mail server answers. An expired access token just gets a 401 and the refresh token
    /// lapses on its own. Logs the host and status only.
    /// </summary>
    public async Task LogoutAsync(TokenData tokenData, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tokenData.AccessToken) || string.IsNullOrEmpty(tokenData.BaseUrl))
            return;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{tokenData.BaseUrl.TrimEnd('/')}/api/v1/auth/logout-user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_logoutTimeout);
            using var response = await _http.SendAsync(request, timeout.Token);
            logger.LogInformation("SmarterMail logout for host {Host}: {Status}",
                tokenData.BaseUrl, (int)response.StatusCode);
        }
        catch (Exception)
        {
            logger.LogInformation("SmarterMail logout for host {Host}: failed or timed out.", tokenData.BaseUrl);
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
