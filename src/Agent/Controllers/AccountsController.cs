using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Controllers;

/// <summary>Adds accounts to, and removes them from, the current session.</summary>
[ApiController]
[Route("api/accounts")]
[Authorize(Policy = "SessionAccess")]
public sealed class AccountsController(
    SessionStore store,
    PendingLoginStore pending,
    SmarterMailAuth auth,
    HostLoginThrottle hostThrottle,
    ILogger<AccountsController> logger) : SignInControllerBase(store, pending, auth, hostThrottle, logger)
{
    /// <summary>
    /// Same body as login. Adds the account (replacing one with the same server and login), or
    /// answers a two-factor challenge bound to this session. 409 ACCOUNT_LIMIT at the cap.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("login")]
    public Task<IActionResult> Add([FromBody] LoginRequest request, CancellationToken ct) =>
        SignInAsync(request, HttpContext.RequireSession(), ct);

    /// <summary>204. Removing the last account ends the session and clears the cookie.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Remove(string id)
    {
        var session = HttpContext.RequireSession();
        var (found, sessionEnded) = await Store.RemoveAccountAsync(session, id);
        if (!found)
            return NotFound(new { error = "No such account in this session." });

        if (sessionEnded)
            Response.Cookies.Delete(SessionStore.CookieName, CookieOptions(Request));

        return NoContent();
    }
}
