using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;
using SmarterMailAgent.Tasks.Approvals;

namespace SmarterMailAgent.Controllers;

/// <summary>
/// The approval queue of the current profile session: what task runs proposed, and approving
/// (executing once) or denying it. Needs server mode and <c>DATA_KEY</c>; every endpoint is
/// cookie-only (an MCP client, itself prompt-injectable, must never be the approver) and acts on the
/// session's own profile. Deciding needs the profile unlocked, and a fresh passkey assertion when the
/// proposal is destructive, admin-scope, or its task asks for one.
/// </summary>
[ApiController]
[Route("api/tasks/proposals")]
[ServerModeOnly]
[Authorize(Policy = "SessionAccess")]
public sealed class ApprovalsController(
    ProposalStore store,
    ProfileStore profiles,
    ProfileRegistry registry,
    PasskeyService passkeys,
    ServerOptions options,
    IServiceProvider services,
    ILogger<ApprovalsController> logger) : ControllerBase
{
    public sealed record HashRequest(string? ArgsHash);

    public sealed record ApproveRequest(string? ArgsHash, string? CeremonyId, JsonElement Credential);

    public sealed record DenyRunRequest(string? RunId);

    /// <param name="Display">Sealed to the profile's public key (<c>task-proposal|profileId|id</c>): the browser opens it.</param>
    /// <param name="Result">Sealed likewise (<c>task-proposal-result|profileId|id</c>) once it ran.</param>
    public sealed record ProposalView(
        string Id, string TaskId, string RunId, string Status, bool NeedsPasskey, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
        DateTimeOffset? DecidedAt, DateTimeOffset? ExecutedAt, string? ErrorCode, string? ErrorMessage, bool Read, string Display,
        string? Result);

    [HttpGet]
    public IActionResult List([FromQuery] string? status, [FromQuery] string? taskId, [FromQuery] int limit = 50)
    {
        if (Gate(out var profileId, out _) is { } refusal)
            return refusal;
        var filter = status is "decided" or "all" ? status : "pending";
        var now = DataStore.Now();
        return Ok(store.List(profileId, filter, string.IsNullOrEmpty(taskId) ? null : taskId, Math.Clamp(limit, 1, 200))
            .Select(p => View(p, now)).ToList());
    }

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        if (Gate(out var profileId, out _) is { } refusal)
            return refusal;
        return store.Get(profileId, id) is { } row ? Ok(View(row, DataStore.Now())) : NotFoundProposal();
    }

    /// <summary>
    /// Starts an approval: a passkey ceremony bound to this session, this proposal and the argument
    /// hash the browser showed, or <c>{ passkey: false }</c> when none is needed.
    /// </summary>
    [HttpPost("{id}/approve/options")]
    [EnableRateLimiting("two-factor")]
    public IActionResult ApproveOptions(string id, [FromBody] HashRequest? request)
    {
        if (Gate(out var profileId, out var session, unlocked: true) is { } refusal)
            return refusal;
        if (!ProposalHash.IsWellFormed(request?.ArgsHash))
            return BadRequest(new { error = "Send the hash of the arguments you are approving.", code = "PROPOSAL_MISMATCH" });
        if (store.Get(profileId, id) is not { } row)
            return NotFoundProposal();
        if (NotPending(row) is { } notPending)
            return notPending;
        if (!row.NeedsPasskey)
            return Ok(new { passkey = false });

        var credentialIds = profiles.Passkeys(profileId).Select(p => p.CredentialId).ToList();
        var (ceremonyId, assertion) = passkeys.BeginStepUp(Request, profileId, credentialIds, Binding(session, id, request!.ArgsHash!));
        return Ok(new { passkey = true, ceremonyId, options = assertion });
    }

    /// <summary>Approves and executes, synchronously (at most 60 seconds).</summary>
    [HttpPost("{id}/approve")]
    [EnableRateLimiting("api")]
    public async Task<IActionResult> Approve(string id, [FromBody] ApproveRequest request, CancellationToken ct)
    {
        if (Gate(out var profileId, out var session, unlocked: true) is { } refusal)
            return refusal;
        if (!ProposalHash.IsWellFormed(request.ArgsHash))
            return Conflict(new { error = "The approval does not match the proposed change. Nothing ran.", code = "PROPOSAL_MISMATCH" });
        if (store.Get(profileId, id) is not { } row)
            return NotFoundProposal();
        if (NotPending(row) is { } notPending)
            return notPending;

        var passkeyUsed = false;
        if (row.NeedsPasskey)
        {
            if (string.IsNullOrEmpty(request.CeremonyId) || request.Credential.ValueKind != JsonValueKind.Object)
            {
                return Unauthorized(new { error = "Confirm this change with your passkey.", code = "PASSKEY_REQUIRED" });
            }
            var passkey = await passkeys.CompleteStepUpAsync(request.CeremonyId, request.Credential,
                Binding(session, id, request.ArgsHash!), profileId, ct);
            if (passkey is null)
                return Unauthorized(new { error = "The passkey did not confirm this change. Nothing ran.", code = "PASSKEY_INVALID" });
            passkeyUsed = true;
        }

        logger.LogInformation("Proposal {Proposal} approved (passkey {Passkey}).", id, passkeyUsed ? "yes" : "no");
        var executor = services.GetRequiredService<ProposalExecutor>();
        // Not the request's token: once claimed, the change runs to its end even if the browser goes away.
        var execution = await executor.ExecuteAsync(profileId, id, request.ArgsHash!);
        return execution.Kind switch
        {
            ProposalExecutor.Kind.Executed or ProposalExecutor.Kind.Failed or ProposalExecutor.Kind.Unknown => Ok(new
            {
                status = execution.Kind switch
                {
                    ProposalExecutor.Kind.Executed => ProposalStore.Executed,
                    ProposalExecutor.Kind.Failed => ProposalStore.Failed,
                    _ => ProposalStore.Unknown,
                },
                errorCode = execution.ErrorCode,
                errorMessage = execution.ErrorCode is { } c ? ProposalExecutor.Explain(c) : null,
                result = execution.Result,
            }),
            ProposalExecutor.Kind.NotFound => NotFoundProposal(),
            ProposalExecutor.Kind.Expired => Expired(),
            ProposalExecutor.Kind.Mismatch => Conflict(new
            {
                error = "What you approved is not what was proposed. Nothing ran; reload and check it again.",
                code = "PROPOSAL_MISMATCH",
            }),
            ProposalExecutor.Kind.Unavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "The mail server did not answer. Nothing ran; the change is still waiting. Try again later.",
                code = "ACCOUNT_UNAVAILABLE",
            }),
            _ => NotPendingResult(),
        };
    }

    [HttpPost("{id}/deny")]
    public IActionResult Deny(string id)
    {
        if (Gate(out var profileId, out _, unlocked: true) is { } refusal)
            return refusal;
        if (store.Get(profileId, id) is null)
            return NotFoundProposal();
        if (!store.Deny(profileId, id))
            return NotPendingResult();
        logger.LogInformation("Proposal {Proposal} denied.", id);
        return NoContent();
    }

    /// <summary>Denies every pending proposal of one run.</summary>
    [HttpPost("deny")]
    public IActionResult DenyRun([FromBody] DenyRunRequest request)
    {
        if (Gate(out var profileId, out _, unlocked: true) is { } refusal)
            return refusal;
        if (string.IsNullOrEmpty(request.RunId))
            return BadRequest(new { error = "Which run?", code = "RUN_NOT_FOUND" });
        var denied = store.DenyRun(profileId, request.RunId);
        logger.LogInformation("{Count} proposal(s) of run {Run} denied.", denied, LoggableId(request.RunId));
        return Ok(new { denied });
    }

    [HttpPost("{id}/read")]
    public IActionResult MarkRead(string id)
    {
        if (Gate(out var profileId, out _) is { } refusal)
            return refusal;
        store.MarkRead(profileId, id);
        return NoContent();
    }

    // ------------------------------------------------------------------ helpers

    private IActionResult? Gate(out string profileId, out Session session, bool unlocked = false)
    {
        profileId = "";
        session = null!;
        if (!options.TasksEnabled || registry.ServerSealer is null)
            return NotFound(new { error = "Scheduled tasks are not enabled on this server.", code = "TASKS_DISABLED" });
        if (!HttpContext.IsCookieAuthenticated())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Use the browser for this.", code = "COOKIE_REQUIRED" });
        session = HttpContext.RequireSession();
        if (session.Profile is not { } profile)
            return NotFound(new { error = "Save this chat to a profile first.", code = "NO_PROFILE" });
        if (profiles.GetProfile(profile.ProfileId) is null)
            return NotFound(new { error = "That profile no longer exists.", code = "PROFILE_GONE" });
        if (unlocked && !profile.IsUnlocked)
            return Conflict(new { error = "Unlock your profile with your passkey first.", code = "PROFILE_LOCKED" });

        profileId = profile.ProfileId;
        return null;
    }

    /// <summary>A step-up can only finish for this session, this proposal and these exact arguments.</summary>
    private static string Binding(Session session, string proposalId, string argsHash) =>
        $"{Base64Url.Encode(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(session.Id)))}|{proposalId}|{argsHash}";

    /// <summary>A client-supplied id goes into the log only when it has the shape of one of ours (base64url, ≤ 64).</summary>
    internal static string LoggableId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? id : "(malformed)";

    private IActionResult? NotPending(ProposalRow row) =>
        ProposalExecutor.IsExpired(row, DataStore.Now()) ? Expired()
        : row.Status != ProposalStore.Pending ? NotPendingResult()
        : null;

    private ObjectResult Expired() =>
        StatusCode(StatusCodes.Status410Gone, new { error = "This proposal expired. Run the task again for a fresh one.", code = "PROPOSAL_EXPIRED" });

    private ConflictObjectResult NotPendingResult() =>
        Conflict(new { error = "This proposal was already decided.", code = "PROPOSAL_NOT_PENDING" });

    private NotFoundObjectResult NotFoundProposal() =>
        NotFound(new { error = "No such proposal.", code = "PROPOSAL_NOT_FOUND" });

    private static ProposalView View(ProposalRow p, long now) => new(
        p.Id, p.TaskId, p.RunId, ProposalExecutor.IsExpired(p, now) ? ProposalStore.Expired : p.Status, p.NeedsPasskey,
        DateTimeOffset.FromUnixTimeMilliseconds(p.CreatedAt), DateTimeOffset.FromUnixTimeMilliseconds(p.ExpiresAt),
        Time(p.DecidedAt), Time(p.ExecutedAt), p.ErrorCode, p.ErrorCode is { } code ? ProposalExecutor.Explain(code) : null,
        p.Read, p.Display, p.Result);

    private static DateTimeOffset? Time(long? ms) => ms is { } v ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;
}
