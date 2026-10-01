using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Controllers;

/// <summary>
/// "Remember me on this device", with nothing stored on the server. The browser keeps a bundle
/// sealed under <c>RESUME_KEY</c> (<see cref="ResumeSealer"/>) holding each account's refresh
/// token, and presents it here after a restart, a redeploy or an expired session. Every resume
/// refreshes, which rotates the refresh tokens: the bundle presented is dead afterwards, so the
/// response carries its replacement.
/// </summary>
[ApiController]
[Route("api/auth/resume")]
public sealed class ResumeController(
    SessionStore store,
    PendingLoginStore pending,
    SmarterMailAuth auth,
    HostLoginThrottle hostThrottle,
    ResumeSealer sealer,
    ILogger<ResumeController> logger) : SignInControllerBase(store, pending, auth, hostThrottle, logger)
{
    public sealed record ResumeRequest(string? Bundle);

    public sealed record ConfigResponse(bool Enabled, int Days);

    public sealed record BundleResponse(string Bundle, long Version, DateTimeOffset RememberedUntil);

    /// <summary>
    /// An account from the bundle that did not come back. <c>Reason</c>: <c>REJECTED</c> (SmarterMail
    /// refused the refresh token), <c>EXPIRED</c> (its refresh token had lapsed), <c>UNAVAILABLE</c>
    /// (the server did not answer), <c>BLOCKED_HOST</c> (fails the SSRF guard now), <c>ACCOUNT_LIMIT</c>.
    /// </summary>
    public sealed record SkippedAccount(string BaseUrl, string Login, string Role, string Reason);

    /// <summary>A <see cref="SessionResponse"/> plus the replacement bundle and what was left behind.</summary>
    public sealed record ResumeResponse(
        DateTimeOffset ExpiresAt, int MaxAccounts, IReadOnlyList<AccountResponse> Accounts, bool Remembered,
        string Bundle, long Version, DateTimeOffset RememberedUntil, IReadOnlyList<SkippedAccount> Skipped);

    /// <summary>Whether the UI should offer "Remember me on this device" at all.</summary>
    [HttpGet("config")]
    [AllowAnonymous]
    public IActionResult Config() =>
        Ok(new ConfigResponse(sealer.Enabled, sealer.Enabled ? (int)sealer.MaxAge.TotalDays : 0));

    /// <summary>The current bundle for this remembered session. Cookie only.</summary>
    [HttpGet]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult Get()
    {
        if (Refusal() is { } refusal)
            return refusal;

        return Sealed(HttpContext.RequireSession()) ?? NotFound(new
        {
            error = "This session is not remembered on this device.",
            code = "NOT_REMEMBERED",
        });
    }

    /// <summary>
    /// Turns remembering on for this session (and every account in it, including ones added later)
    /// and returns the first bundle. The chain starts now for a password sign-in; a session that
    /// came from a resume, or was remembered before, keeps its original start.
    /// </summary>
    [HttpPut]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult Enable()
    {
        if (Refusal() is { } refusal)
            return refusal;

        var session = HttpContext.RequireSession();
        if (!session.IsRemembered)
        {
            var since = session.RememberedSince ?? DateTimeOffset.UtcNow;
            var until = session.RememberedUntil ?? sealer.UntilFor(since);
            if (until <= DateTimeOffset.UtcNow)
            {
                return StatusCode(StatusCodes.Status410Gone, new
                {
                    error = "Staying signed in on this device has run its course for this sign-in. Sign in again to renew it.",
                    code = "RESUME_EXPIRED",
                });
            }

            session.Remember(since, until);
            logger.LogInformation("Session remembered on its device ({Count} account(s)).", session.Count);
        }

        return Sealed(session)!;
    }

    /// <summary>
    /// Stops remembering. The session carries on as an ordinary one: the sweeper refreshes it
    /// again, which rotates the refresh tokens and so kills any copy of the bundle within minutes.
    /// </summary>
    [HttpDelete]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult Disable()
    {
        if (Refusal() is { } refusal)
            return refusal;

        HttpContext.RequireSession().StopRemembering();
        return NoContent();
    }

    /// <summary>
    /// Rebuilds a session from a bundle: each account's refresh token is refreshed on its own
    /// SmarterMail (no password, no second factor — the same as SmarterMail's own refresh), the
    /// cookie is set, and the new bundle comes back. Accounts that fail are reported in
    /// <c>skipped</c>; with none left the answer is <c>401 RESUME_EXPIRED</c> (discard the bundle)
    /// or <c>503 RESUME_UNAVAILABLE</c> (a server did not answer; keep it and retry later).
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Resume([FromBody] ResumeRequest request, CancellationToken ct)
    {
        if (!sealer.Enabled)
            return Disabled();

        var opened = sealer.Unseal(request.Bundle);
        if (!opened.Ok)
        {
            // The failure class only: never the bundle, never what was inside it.
            logger.LogInformation("Resume refused: {Reason}.", opened.Failure);
            return opened.Failure == ResumeSealer.UnsealFailure.Expired
                ? Expired("Your saved sign-in on this device has expired. Please sign in again.")
                : BadRequest(new { error = "That saved sign-in is not valid here. Please sign in again.", code = "RESUME_INVALID" });
        }

        var payload = opened.Payload!;
        var restorer = new AccountRestorer(SmarterMail, HostThrottle, logger);
        var restored = await restorer.RestoreAsync(
            payload.Accounts.Select(e => new AccountRestorer.Candidate(e)).ToList(), SessionStore.MaxAccounts,
            throttledFailsAll: true, "Resume", ct);

        // A throttled server refuses the whole resume before anything is refreshed: no token has
        // rotated, so the browser keeps a bundle that still works once the server cools down.
        if (restored.Throttled is { } throttled)
            return HostThrottled(throttled.BaseUrl, throttled.RetryAfter);

        var skipped = restored.Skipped.Select(s => Skip(s.Entry, s.Reason)).ToList();
        var accounts = restored.Accounts.Select(r => r.Account).ToList();

        if (accounts.Count == 0)
        {
            var unavailable = restored.AnyUnavailable;
            logger.LogInformation("Resume failed: no account could be restored ({Count} in the bundle, unavailable={Unavailable}).",
                payload.Accounts.Count, unavailable);

            return unavailable
                ? StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    error = "Your mail server did not answer, so this device could not sign you back in. Try again in a moment.",
                    code = "RESUME_UNAVAILABLE",
                    skipped,
                })
                : StatusCode(StatusCodes.Status401Unauthorized, new
                {
                    error = "Your saved sign-in on this device is no longer accepted. Please sign in again.",
                    code = "RESUME_EXPIRED",
                    skipped,
                });
        }

        await ReleasePreviousAsync(accounts.Select(a => a.ClientId).OfType<string>().ToHashSet(StringComparer.Ordinal));

        var session = Store.Create(accounts[0]);
        foreach (var account in accounts.Skip(1))
        {
            session.Add(account, SessionStore.MaxAccounts, out var replaced);
            if (replaced is not null)
                await replaced.DisposeAsync();
        }

        session.Remember(payload.RememberedSince, sealer.UntilFor(payload.RememberedSince));
        Response.Cookies.Append(SessionStore.CookieName, session.Id, CookieOptions(Request));

        logger.LogInformation("Session resumed with {Restored} of {Total} account(s).", accounts.Count, payload.Accounts.Count);

        var (bundle, version) = sealer.Seal(session)!.Value;
        var body = SessionResponse.From(session);
        return Ok(new ResumeResponse(body.ExpiresAt, body.MaxAccounts, body.Accounts, body.Remembered,
            bundle, version, session.RememberedUntil!.Value, skipped));
    }

    /// <summary>
    /// Closes a live session on the incoming cookie. Accounts that share a clientId with the
    /// bundle are only forgotten: revoking them would revoke the pair this resume just minted
    /// (SmarterMail revokes per user and clientId). Anything else in it is revoked as on a login.
    /// </summary>
    private async Task ReleasePreviousAsync(IReadOnlySet<string> clientIds)
    {
        if (!Request.Cookies.TryGetValue(SessionStore.CookieName, out var previousId) ||
            Store.Get(previousId) is not { } previous)
            return;

        foreach (var account in previous.Accounts.Where(a => a.ClientId is { } id && clientIds.Contains(id)))
        {
            if (previous.Remove(account.Id) is { } detached)
                await detached.ForgetAsync();
        }

        await Store.RemoveAsync(previous.Id);
    }

    /// <summary>Feature off, or a caller that is not the browser's cookie.</summary>
    private IActionResult? Refusal()
    {
        if (!sealer.Enabled)
            return Disabled();

        // The bundle is a credential for the browser that owns the session; a bearer caller (an MCP
        // client) never gets one, and cannot switch remembering on or off either.
        if (!HttpContext.IsCookieAuthenticated())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Remember-me is only available to the browser that holds the session cookie.",
                code = "COOKIE_REQUIRED",
            });
        }

        // A profile session is remembered by its profile (server mode); a browser bundle of the same
        // accounts would rotate against the stored copy.
        if (HttpContext.GetSession()?.Profile is not null)
        {
            return Conflict(new
            {
                error = "This chat is saved to a profile; sign in with your passkey on other devices.",
                code = "PROFILE_SESSION",
            });
        }

        return null;
    }

    private IActionResult? Sealed(Session session) =>
        sealer.Seal(session) is { } result
            ? Ok(new BundleResponse(result.Bundle, result.Version, session.RememberedUntil!.Value))
            : null;

    private IActionResult Disabled() => NotFound(new
    {
        error = "Remember-me is not enabled on this server.",
        code = "RESUME_DISABLED",
    });

    private IActionResult Expired(string message) => StatusCode(StatusCodes.Status401Unauthorized, new
    {
        error = message,
        code = "RESUME_EXPIRED",
    });

    private static SkippedAccount Skip(ResumeAccount entry, string reason) =>
        new(entry.BaseUrl, entry.Login, entry.Role.ToString(), reason);
}
