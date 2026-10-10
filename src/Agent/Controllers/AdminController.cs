using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Controllers;

/// <summary>
/// Task invites from the browser, for the profiles named in <c>ADMIN_PROFILES</c>: the same operations
/// as <c>Server/AdminCli.cs</c>. Every request needs the cookie (never an MCP token), a profile session
/// whose profile is on the list, and that profile unlocked with its passkey; anything else is a plain
/// <c>404</c>, so the endpoints look absent. Logs the action and ids, never a code.
/// </summary>
[ApiController]
[Route("api/admin")]
[ServerModeOnly]
[Authorize(Policy = "SessionAccess")]
[EnableRateLimiting("api")]
public sealed class AdminController(
    TaskInviteStore invites,
    ServerOptions options,
    ILogger<AdminController> logger) : ControllerBase
{
    public sealed record CreateInviteRequest(string? Note, int? Uses, int? Days);

    /// <param name="State"><c>open</c>, <c>used up</c>, <c>expired</c> or <c>revoked</c>.</param>
    public sealed record InviteView(
        string Id, string? Note, int Uses, int MaxUses, string State, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

    public sealed record AccessView(string ProfileId, DateTimeOffset GrantedAt, string? InviteId, string? InviteNote, DateTimeOffset LastSeenAt, bool You);

    [HttpGet("invites")]
    public IActionResult List()
    {
        if (Admin(out var self) is { } refusal)
            return refusal;
        return Ok(new
        {
            inviteOnly = options.TaskInviteOnly,
            invites = invites.List().Select(View).ToList(),
            access = invites.AccessList().Select(a => new AccessView(a.ProfileId, Time(a.GrantedAt), a.InviteId, a.InviteNote,
                Time(a.LastSeenAt), a.ProfileId == self)).ToList(),
        });
    }

    /// <summary>A new code, returned once (only its hash is stored).</summary>
    [HttpPost("invites")]
    public IActionResult Create([FromBody] CreateInviteRequest request)
    {
        if (Admin(out _) is { } refusal)
            return refusal;
        var uses = request.Uses ?? 1;
        if (uses is < 1 or > TaskInviteStore.MaxUses || request.Days is < 1 or > TaskInviteStore.MaxDays)
        {
            return BadRequest(new
            {
                error = $"Uses 1–{TaskInviteStore.MaxUses}; days 1–{TaskInviteStore.MaxDays} or none.",
                code = "INVITE_OPTIONS_INVALID",
            });
        }

        var (invite, code) = invites.Create(TaskInviteStore.CleanNote(request.Note), uses,
            request.Days is { } days ? TimeSpan.FromDays(days) : null);
        logger.LogInformation("Admin created task invite {Invite} ({Uses} use(s)).", invite.Id, uses);
        return Ok(new { code, invite = View(invite) });
    }

    /// <summary>Stops a code; <c>?profiles=true</c> also revokes every profile that redeemed it.</summary>
    [HttpDelete("invites/{id}")]
    public IActionResult RevokeInvite(string id, [FromQuery] bool profiles = false)
    {
        if (Admin(out _) is { } refusal)
            return refusal;
        if (!invites.RevokeInvite(id, profiles, out var revoked))
            return NotFound(new { error = "No such invite.", code = "INVITE_NOT_FOUND" });
        logger.LogInformation("Admin revoked task invite {Invite}; {Count} profile(s) lost access.", id, revoked);
        return Ok(new { profilesRevoked = revoked });
    }

    [HttpPost("access/{profileId}")]
    public IActionResult Grant(string profileId)
    {
        if (Admin(out _) is { } refusal)
            return refusal;
        if (!invites.Grant(profileId))
            return NotFound(new { error = "No such profile.", code = "PROFILE_NOT_FOUND" });
        logger.LogInformation("Admin granted task access to profile {Profile}.", profileId);
        return NoContent();
    }

    [HttpDelete("access/{profileId}")]
    public IActionResult Revoke(string profileId)
    {
        if (Admin(out _) is { } refusal)
            return refusal;
        if (!invites.RevokeAccess(profileId))
            return NotFound(new { error = "That profile has no task access.", code = "PROFILE_NOT_FOUND" });
        logger.LogInformation("Admin revoked task access of profile {Profile}.", profileId);
        return NoContent();
    }

    /// <summary>Null for an unlocked admin profile session (its id in <paramref name="profileId"/>); otherwise a bare 404.</summary>
    private IActionResult? Admin(out string profileId)
    {
        profileId = "";
        if (options.AdminProfiles.Count == 0 || !HttpContext.IsCookieAuthenticated() ||
            HttpContext.RequireSession().Profile is not { IsUnlocked: true } runtime || !options.IsAdmin(runtime.ProfileId))
            return NotFound();
        profileId = runtime.ProfileId;
        return null;
    }

    private static InviteView View(TaskInviteRow r) => new(r.Id, r.Note, r.Uses, r.MaxUses,
        r.RevokedAt is not null ? "revoked"
        : r.ExpiresAt is { } e && e <= DataStore.Now() ? "expired"
        : r.Uses >= r.MaxUses ? "used up" : "open",
        Time(r.CreatedAt), r.ExpiresAt is { } x ? Time(x) : null);

    private static DateTimeOffset Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
}
