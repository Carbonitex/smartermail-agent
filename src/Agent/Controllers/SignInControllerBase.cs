using Microsoft.AspNetCore.Mvc;
using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Controllers;

public sealed record LoginRequest(string? Hostname, string? Email, string? Password, bool ReadOnly = true);

/// <summary>Answer to a sign-in that SmarterMail met with a second-factor challenge. No cookie.</summary>
public sealed record TwoFactorChallengeResponse(
    string ChallengeId, string Method, string EmailAddress, DateTimeOffset ExpiresAt)
{
    public bool TwoFactorRequired => true;
}

public sealed record AccountResponse(
    string Id, string Handle, string Role, string Username, string EmailAddress, string Domain,
    string BaseUrl, bool ReadOnly)
{
    public static AccountResponse From(Account account) => new(
        account.Id, account.Handle, account.Role.ToString(), account.Username, account.EmailAddress,
        account.Domain, account.BaseUrl, account.ReadOnly);
}

/// <summary>Whether the session has a live MCP token. The token itself is only ever shown once.</summary>
public sealed record McpTokenState(bool Active, DateTimeOffset? ExpiresAt);

/// <summary>Server mode: the profile this session belongs to, and whether its accounts are unlocked.</summary>
public sealed record ProfileState(string Id, bool Unlocked);

/// <param name="Remembered">"Remember me on this device" is on: the browser keeps a resume bundle.</param>
/// <param name="Profile">The session's profile (server mode); null for an ordinary session.</param>
public sealed record SessionResponse(
    DateTimeOffset ExpiresAt, int MaxAccounts, IReadOnlyList<AccountResponse> Accounts, McpTokenState McpToken,
    bool Remembered, ProfileState? Profile = null)
{
    public static SessionResponse From(Session session) => new(
        session.ExpiresAt(SessionStore.MaxAge),
        SessionStore.MaxAccounts,
        session.Accounts.Select(AccountResponse.From).ToList(),
        session.McpTokenExpiresAt is { } expiresAt ? new(true, expiresAt) : new(false, null),
        session.IsRemembered,
        session.Profile is { } profile ? new ProfileState(profile.ProfileId, profile.IsUnlocked) : null);
}

/// <summary>
/// The sign-in flow shared by <c>POST /api/auth/login</c> (opens a new session),
/// <c>POST /api/accounts</c> (adds to the current one) and <c>POST /api/auth/two-factor</c>
/// (finishes either).
/// </summary>
public abstract class SignInControllerBase(
    SessionStore store, PendingLoginStore pending, SmarterMailAuth auth, HostLoginThrottle hostThrottle,
    ILogger logger) : ControllerBase
{
    protected SessionStore Store => store;
    protected PendingLoginStore Pending => pending;
    protected SmarterMailAuth SmarterMail => auth;
    protected HostLoginThrottle HostThrottle => hostThrottle;
    protected ILogger Logger => logger;

    /// <summary>
    /// Password step. <paramref name="addTo"/> null opens a new session on success; otherwise the
    /// account joins <paramref name="addTo"/>, and a two-factor challenge is bound to it.
    /// </summary>
    protected async Task<IActionResult> SignInAsync(LoginRequest request, Session? addTo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "Email and password are required." });

        var host = await HostGuard.ValidateAsync(request.Hostname, ct);
        if (!host.Ok || host.BaseUrl is null)
        {
            logger.LogInformation("Login rejected: blocked or invalid host.");
            return BadRequest(new { error = host.Error ?? "That host is not allowed." });
        }

        // Refuse before spending a SmarterMail login when there is no slot and nothing to replace.
        if (addTo is not null && addTo.IsFullFor(host.BaseUrl, request.Email, SessionStore.MaxAccounts))
            return LimitReached();

        if (addTo is not null && ProfileRefusal(addTo, host.BaseUrl) is { } refusal)
            return refusal;

        // Every visitor signs in from our one egress IP, and SmarterMail's IDS counts failures per
        // source IP: stop before a run of wrong passwords gets this service blocked on that server.
        using var attempt = hostThrottle.TryBegin(host.BaseUrl, out var retryAfter);
        if (attempt is null)
            return HostThrottled(host.BaseUrl, retryAfter);

        var clientId = SmarterMailAuth.NewClientId();
        var outcome = await auth.AuthenticateAsync(host.BaseUrl, request.Email, request.Password,
            readOnlyMode: request.ReadOnly, clientId: clientId, ct);
        attempt.Complete(outcome);

        switch (outcome)
        {
            case AuthOutcome.TwoFactorRequired challenge:
            {
                // Correct password, second factor outstanding. No account and no cookie yet: the
                // step token SmarterMail handed back only opens authenticate-two-factor-code.
                var login = pending.Create(
                    host.BaseUrl, request.Email, request.ReadOnly, clientId,
                    challenge.Method, challenge.EmailAddress, challenge.StepToken,
                    // Legacy builds have no step token, so the password must be replayed with the
                    // code. PendingLoginStore erases it on success, expiry or exhaustion.
                    password: challenge.StepToken is null ? request.Password : null,
                    sessionId: addTo?.Id);

                logger.LogInformation(
                    "Two-factor challenge issued for host {Host} (method {Method}, stepToken={HasStepToken}, add={Add}).",
                    host.BaseUrl, challenge.Method, challenge.StepToken is not null, addTo is not null);

                return Ok(new TwoFactorChallengeResponse(
                    login.Id, login.Method, login.EmailAddress, login.ExpiresAt));
            }

            case AuthOutcome.ActionRequired action:
                logger.LogInformation("Login blocked for host {Host}: {Kind}.", host.BaseUrl, action.Kind);
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { error = action.Message, code = action.Kind });

            case AuthOutcome.Failed failed:
                // Hostname and code only: never the email, never the password.
                logger.LogInformation("Login failed for host {Host}: {Code}.", host.BaseUrl, failed.Code);
                return StatusCode(StatusCodes.Status401Unauthorized,
                    new { error = failed.FriendlyMessage, code = failed.Code });
        }

        return await CompleteAsync((AuthOutcome.Success)outcome, host.BaseUrl, request.ReadOnly, addTo);
    }

    /// <summary>Turns a successful login into an account, in a new session or in <paramref name="addTo"/>.</summary>
    protected async Task<IActionResult> CompleteAsync(
        AuthOutcome.Success success, string baseUrl, bool readOnly, Session? addTo)
    {
        // A profile keeps one row per login: signing in again reuses the row's id, which its
        // scheduled tasks refer to.
        var profile = addTo?.Profile;
        var existingId = profile?.RowForLogin(baseUrl, success.TokenData.Username ?? string.Empty)?.Id;
        var account = AccountBuilder.Build(auth, success, baseUrl, readOnly, existingId);

        logger.LogInformation("Login succeeded for host {Host} (readOnly={ReadOnly}, role={Role}).",
            baseUrl, readOnly, account.Role);

        if (addTo is null)
            return await StartSessionAsync(account);

        if (ProfileRefusal(addTo, account.BaseUrl) is { } refusal)
        {
            await account.DisposeAsync();
            return refusal;
        }

        var status = addTo.Add(account, SessionStore.MaxAccounts, out var replaced);
        if (status == AccountSet.AddStatus.LimitReached)
        {
            await account.DisposeAsync();
            return LimitReached();
        }

        if (replaced is not null)
            await replaced.DisposeAsync();

        if (profile is not null && !profile.Save(account))
        {
            logger.LogWarning("A new account could not be saved to its profile (locked).");
            await addTo.AccountSet.Remove(account.Id)!.DisposeAsync();
            return ProfileLocked();
        }

        logger.LogInformation("Account {Change}. Accounts in session: {Count}",
            status == AccountSet.AddStatus.Replaced ? "replaced" : "added", addTo.Count);

        return Ok(SessionResponse.From(addTo));
    }

    /// <summary>An in-memory account from a successful sign-in (or a resume's refresh).</summary>
    protected Account BuildAccount(AuthOutcome.Success success, string baseUrl, bool readOnly) =>
        AccountBuilder.Build(auth, success, baseUrl, readOnly);

    /// <summary>
    /// Opens a new session with its first account. Any session already on the incoming cookie is
    /// closed first: a fresh login replaces it rather than leaving it to idle out.
    /// </summary>
    private async Task<IActionResult> StartSessionAsync(Account account)
    {
        if (Request.Cookies.TryGetValue(SessionStore.CookieName, out var previous) && !string.IsNullOrEmpty(previous))
            await store.RemoveAsync(previous);

        var session = store.Create(account);
        Response.Cookies.Append(SessionStore.CookieName, session.Id, CookieOptions(Request));
        return Ok(SessionResponse.From(session));
    }

    /// <summary>
    /// <c>429 { error, code: "HOST_THROTTLED", retryAfterSeconds }</c> plus <c>Retry-After</c>:
    /// too many failed sign-ins to this mail server from this service (see <see cref="HostLoginThrottle"/>).
    /// </summary>
    protected IActionResult HostThrottled(string baseUrl, TimeSpan retryAfter)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        var minutes = Math.Max(1, (int)Math.Ceiling(seconds / 60.0));

        logger.LogInformation("Sign-in refused for host {Host}: too many failed sign-ins (retry in {Seconds}s).",
            HostLoginThrottle.NormalizeHost(baseUrl), seconds);

        Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            error = "Too many failed sign-ins to this mail server from this service. " +
                    "To keep the server from blocking everyone who uses it here, sign-ins to it are paused. " +
                    $"Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.",
            code = "HOST_THROTTLED",
            retryAfterSeconds = seconds,
        });
    }

    /// <summary>
    /// Why an account cannot join this profile session, or null: the profile must be unlocked (the
    /// new account is sealed with its key) and the server must be on <c>PROFILE_MAIL_HOSTS</c>.
    /// </summary>
    private IActionResult? ProfileRefusal(Session session, string baseUrl)
    {
        if (session.Profile is not { } profile)
            return null;
        if (!profile.IsUnlocked)
            return ProfileLocked();

        var options = HttpContext.RequestServices.GetRequiredService<Server.ServerOptions>();
        if (!options.AllowsProfileHost(baseUrl))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "This server only keeps accounts from its own mail servers in profiles.",
                code = "PROFILE_HOST_NOT_ALLOWED",
            });
        }

        return null;
    }

    protected IActionResult ProfileLocked() => StatusCode(StatusCodes.Status409Conflict, new
    {
        error = "Unlock your profile with your passkey first.",
        code = "PROFILE_LOCKED",
    });

    protected IActionResult LimitReached() => StatusCode(StatusCodes.Status409Conflict, new
    {
        error = $"This session already has the maximum of {SessionStore.MaxAccounts} accounts. Remove one first.",
        code = "ACCOUNT_LIMIT",
    });

    protected static CookieOptions CookieOptions(HttpRequest request)
    {
        var pathBase = request.PathBase.HasValue ? request.PathBase.Value! : "/";
        if (!pathBase.EndsWith('/')) pathBase += "/";

        return new CookieOptions
        {
            HttpOnly = true,
            Secure = request.IsHttps || !HostGuard.AllowPrivateHosts,
            SameSite = SameSiteMode.Strict,
            Path = pathBase,
            MaxAge = SessionStore.MaxAge,
        };
    }
}
