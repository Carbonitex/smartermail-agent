using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    SessionStore store,
    PendingLoginStore pending,
    SmarterMailAuth auth,
    HostLoginThrottle hostThrottle,
    ILogger<AuthController> logger) : SignInControllerBase(store, pending, auth, hostThrottle, logger)
{
    public sealed record TwoFactorRequest(string? ChallengeId, string? Code);

    public sealed record TokenResponse(string Token, DateTimeOffset ExpiresAt);

    /// <summary>Always opens a <b>new</b> session with its first account, closing any on the cookie.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct) =>
        SignInAsync(request, addTo: null, ct);

    /// <summary>
    /// Completes a challenge from <c>POST /api/auth/login</c> (opens a session, sets the cookie) or
    /// from <c>POST /api/accounts</c> (adds to the session it is bound to; only a request carrying
    /// that same session may complete it). Either way the success response is a SessionResponse.
    /// </summary>
    [HttpPost("two-factor")]
    [AllowAnonymous]
    [EnableRateLimiting("two-factor")]
    public async Task<IActionResult> TwoFactor([FromBody] TwoFactorRequest request, CancellationToken ct)
    {
        var code = new string((request.Code ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (string.IsNullOrEmpty(request.ChallengeId))
            return BadRequest(new { error = "A challenge id is required.", code = "MISSING_CHALLENGE_ID" });
        if (code.Length is < 4 or > 12)
            return BadRequest(new { error = "Enter the code from your authenticator.", code = "INVALID_CODE_FORMAT" });

        var login = Pending.Get(request.ChallengeId);
        if (login is null)
            return Expired();

        Session? addTo = null;
        if (login.SessionId is not null)
        {
            // An add-account challenge belongs to one session. Presented by anything else (no
            // session, an expired one, another browser) it is burned rather than honoured.
            var current = HttpContext.GetSession();
            if (current is null || !string.Equals(current.Id, login.SessionId, StringComparison.Ordinal))
            {
                Pending.Remove(login.Id);
                Logger.LogInformation("Two-factor challenge presented outside its session; discarded.");
                return Expired();
            }

            if (current.IsFullFor(login.BaseUrl, login.Username, SessionStore.MaxAccounts))
                return LimitReached();

            addTo = current;
        }

        // A wrong code is a failed login to SmarterMail's IDS too. The challenge is left alone: it
        // can still be finished once the host cools down, if it has not expired by then.
        using var attempt = HostThrottle.TryBegin(login.BaseUrl, out var retryAfter);
        if (attempt is null)
            return HostThrottled(login.BaseUrl, retryAfter);

        var outcome = await SmarterMail.CompleteTwoFactorAsync(
            login.BaseUrl, login.Username, login.TakePasswordCopy(), login.StepToken, code,
            readOnlyMode: login.ReadOnly, clientId: login.ClientId, ct);
        attempt.Complete(outcome, twoFactorStep: true);

        if (outcome is AuthOutcome.Success success)
        {
            // Single use: the step token is spent and the stored password is erased.
            Pending.Remove(login.Id);
            return await CompleteAsync(success, login.BaseUrl, login.ReadOnly, addTo);
        }

        if (outcome is AuthOutcome.ActionRequired action)
        {
            Pending.Remove(login.Id);
            Logger.LogInformation("Two-factor blocked for host {Host}: {Kind}.", login.BaseUrl, action.Kind);
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = action.Message, code = action.Kind });
        }

        // Anything else is a rejected code. The legacy inline path answers a wrong code with
        // TWO_FACTOR_REQUIRED again rather than INVALID_TWO_FACTOR_CODE; both burn an attempt.
        var failure = outcome as AuthOutcome.Failed;
        if (failure is { Code: "CONNECTION_FAILED" or "TIMEOUT" })
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = failure.FriendlyMessage, code = failure.Code });
        }

        var attemptsLeft = Pending.RecordFailure(login);
        Logger.LogInformation("Two-factor code rejected for host {Host}: {Code}.",
            login.BaseUrl, failure?.Code ?? "INVALID_TWO_FACTOR_CODE");

        return StatusCode(StatusCodes.Status401Unauthorized, new
        {
            error = "That code was not accepted.",
            code = "INVALID_TWO_FACTOR_CODE",
            attemptsLeft,
        });
    }

    private IActionResult Expired() => StatusCode(StatusCodes.Status410Gone, new
    {
        error = "That sign-in attempt expired. Please log in again.",
        code = "CHALLENGE_EXPIRED",
    });

    [HttpPost("logout")]
    [Authorize(Policy = "SessionAccess")]
    public async Task<IActionResult> Logout()
    {
        var session = HttpContext.GetSession();
        if (session is not null)
            await Store.RemoveAsync(session.Id);

        Response.Cookies.Delete(SessionStore.CookieName, CookieOptions(Request));
        return NoContent();
    }

    [HttpGet("session")]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult Current() => Ok(SessionResponse.From(HttpContext.RequireSession()));

    /// <summary>
    /// Mints the session's MCP token for <c>Authorization: Bearer</c> against <c>/mcp</c>, replacing
    /// any previous one. Cookie only (SessionAccess), so an MCP token cannot mint its successor.
    /// </summary>
    [HttpPost("token")]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult Token()
    {
        if (Store.IssueMcpToken(HttpContext.RequireSession()) is not { } issued)
            return Unauthorized();

        Response.Headers.CacheControl = "no-store";
        Logger.LogInformation("MCP token issued.");
        return Ok(new TokenResponse(issued.Token, issued.ExpiresAt));
    }

    [HttpDelete("token")]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult RevokeToken()
    {
        Store.RevokeMcpToken(HttpContext.RequireSession());
        Logger.LogInformation("MCP token revoked.");
        return NoContent();
    }
}
