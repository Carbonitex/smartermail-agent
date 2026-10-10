using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Controllers;

/// <summary>
/// Server-mode profiles: a passkey-encrypted copy of a chat's accounts and settings, kept on the
/// server so the same person can come back from any browser. See <c>Profiles/</c> for the design and
/// the root CLAUDE.md ("Profiles") for the contract.
/// </summary>
[ApiController]
[Route("api/profile")]
[ServerModeOnly]
public sealed class ProfileController(
    SessionStore sessions,
    ProfileStore store,
    ProfileRegistry registry,
    PasskeyService passkeys,
    AccountRestorer restorer,
    TaskInviteStore invites,
    ServerOptions options,
    ILogger<ProfileController> logger) : ControllerBase
{
    private const int MaxOpaque = 1024;
    private const int MaxSettings = 24 * 1024;
    private const int MaxLabel = 80;

    public sealed record RegisterOptionsRequest(string? Label);

    public sealed record RecoveryInput(string? WrappedKey, string? AuthKey);

    public sealed record CreateRequest(
        string? CeremonyId, JsonElement Credential, string? WrappedKey, string? AccountsKey, string? PublicKey,
        string? EncryptedPrivateKey, string? Settings, RecoveryInput? Recovery, string? Label);

    public sealed record CeremonyRequest(string? CeremonyId, JsonElement Credential);

    public sealed record AddPasskeyRequest(string? CeremonyId, JsonElement Credential, string? WrappedKey, string? Label);

    public sealed record RecoverRequest(string? ProfileId, string? AuthKey);

    public sealed record UnlockRequest(string? AccountsKey);

    public sealed record SettingsRequest(string? Settings, long Version);

    public sealed record DelegationRequest(bool Enabled);

    public sealed record TaskKeyRequest(string? Key);

    public sealed record PausedRequest(bool Paused);

    public sealed record TaskAccessRequest(string? Code);

    /// <param name="InviteOnly">TASKS_ACCESS=invite on this server.</param>
    /// <param name="Granted">This profile may use scheduled tasks (always true when not invite-only).</param>
    public sealed record TaskAccessView(bool InviteOnly, bool Granted);

    /// <param name="Minutes">Null = the server default.</param>
    public sealed record IdleRequest(int? Minutes);

    /// <summary>
    /// The session idle timeout: the profile's own choice (null = none), what it falls back to, and
    /// the range it may choose from.
    /// </summary>
    public sealed record IdleView(int? Minutes, int DefaultMinutes, int MinMinutes, int MaxMinutes);

    /// <summary>What a passkey or recovery sign-in hands the browser: the wrapped profile key, to open locally.</summary>
    public sealed record SignInResponse(string ProfileId, string WrappedKey, string? CredentialId, SessionResponse Session);

    public sealed record SkippedAccount(string Id, string BaseUrl, string Login, string Role, string Reason);

    public sealed record UnlockResponse(SessionResponse Session, IReadOnlyList<SkippedAccount> Skipped);

    public sealed record PasskeyView(string Id, string? Label, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

    /// <param name="Live">Signed in now; otherwise <c>State</c> says why not (<c>rejected</c> = sign in again).</param>
    public sealed record StoredAccountView(
        string Id, bool Live, string? Handle, string? Login, string? BaseUrl, string? Role, bool? ReadOnly, bool Delegated, string State);

    public sealed record ProfileView(
        string Id, bool Unlocked, IReadOnlyList<PasskeyView> Passkeys, IReadOnlyList<StoredAccountView> Accounts,
        bool Recovery, string PublicKey, string EncryptedPrivateKey, long SettingsVersion,
        bool CanDelegate, bool TasksEnabled, bool HasTaskKey, bool TasksPaused, IdleView Idle, TaskAccessView TaskAccess,
        bool Admin = false);

    // ------------------------------------------------------------------ creation

    /// <summary>Starts creating a profile for the current (ordinary) session. It needs at least one account.</summary>
    [HttpPost("register/options")]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult RegisterOptions([FromBody] RegisterOptionsRequest? request)
    {
        var session = HttpContext.RequireSession();
        if (Refusal(session) is { } refusal)
            return refusal;
        if (session.Profile is not null)
            return Conflict(new { error = "This chat is already saved to a profile.", code = "ALREADY_PROFILE" });
        if (session.Count == 0)
            return BadRequest(new { error = "Sign in to an account first.", code = "NO_ACCOUNTS" });
        if (store.CountProfiles() >= options.MaxProfiles)
            return StatusCode(StatusCodes.Status507InsufficientStorage, new { error = "This server holds no more profiles.", code = "PROFILE_LIMIT" });

        var profileId = ProfileCrypto.NewId();
        var name = Clean(request?.Label) ?? session.Accounts[0].EmailAddress;
        var (ceremonyId, creation) = passkeys.BeginRegistration(
            Request, profileId, $"{name} (SmarterMail Agent profile)", [], Binding(session));
        return Ok(new { ceremonyId, profileId, options = creation });
    }

    /// <summary>
    /// Creates the profile from the verified passkey and the browser's key material, saves every
    /// account in the session, and moves the chat onto a profile session (new cookie).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "SessionAccess")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Create([FromBody] CreateRequest request, CancellationToken ct)
    {
        var session = HttpContext.RequireSession();
        if (Refusal(session) is { } refusal)
            return refusal;
        if (session.Profile is not null)
            return Conflict(new { error = "This chat is already saved to a profile.", code = "ALREADY_PROFILE" });

        var accountsKey = Base64Url.Decode(request.AccountsKey);
        if (accountsKey is not { Length: 32 } || !Opaque(request.WrappedKey) || !Opaque(request.EncryptedPrivateKey, 4096) ||
            !ProfileCrypto.IsValidPublicKey(request.PublicKey) || (request.Settings?.Length ?? 0) > MaxSettings ||
            (request.Recovery is { } r && (!Opaque(r.WrappedKey) || Base64Url.Decode(r.AuthKey) is not { Length: 32 })))
            return BadRequest(new { error = "The profile keys are missing or malformed.", code = "PROFILE_INVALID" });

        var notAllowed = session.Accounts.Where(a => !options.AllowsProfileHost(a.BaseUrl)).ToList();
        if (notAllowed.Count > 0)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "This server only keeps accounts from its own mail servers in profiles. Remove the others first.",
                code = "PROFILE_HOST_NOT_ALLOWED",
            });
        }

        var registered = await passkeys.CompleteRegistrationAsync(request.CeremonyId, request.Credential, Binding(session), ct);
        if (registered is null)
            return BadRequest(new { error = "The passkey could not be verified. Try again.", code = "PASSKEY_INVALID" });

        var now = DataStore.Now();
        var profileId = registered.ProfileId;
        var runtime = registry.AcquireSession(profileId);
        runtime.UnlockNew(accountsKey);

        // Seal every account under the new accounts key before anything is written.
        var accounts = session.Accounts;
        var rows = new List<StoredAccountRow>();
        foreach (var account in accounts)
            rows.Add(new StoredAccountRow(account.Id, profileId, ProfileStore.SealProfile, "ok",
                runtime.SealAccount(account, ProfileStore.SealProfile)!, now));

        try
        {
            store.CreateProfile(
                new ProfileRow(profileId, now, now, request.PublicKey!, request.EncryptedPrivateKey!, request.Settings,
                    request.Settings is null ? 0 : 1, ProfileCrypto.AccountsKeyCheck(accountsKey),
                    request.Recovery?.WrappedKey, request.Recovery is { AuthKey: { } auth } ? ProfileCrypto.RecoveryAuthHash(Base64Url.Decode(auth)!) : null,
                    null, false),
                new PasskeyRow(registered.CredentialId, profileId, registered.PublicKey, registered.SignCount, registered.Aaguid,
                    Clean(request.Label), request.WrappedKey!, now, null),
                rows);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating a profile failed.");
            await runtime.ReleaseSessionAsync();
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "The profile could not be saved.", code = "PROFILE_SAVE_FAILED" });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(accountsKey);
        }

        // Move the live accounts (same objects, same tokens) into the profile and swap the cookie.
        foreach (var account in session.AccountSet.TakeAll())
        {
            runtime.Accounts.Add(account, SessionStore.MaxAccounts, out _);
            runtime.Save(account);
        }

        var profileSession = sessions.CreateForProfile(runtime);
        await sessions.RemoveAsync(session.Id);
        Response.Cookies.Append(SessionStore.CookieName, profileSession.Id, SessionCookie());
        logger.LogInformation("Profile created with {Count} account(s).", accounts.Count);
        return Ok(SessionResponse.From(profileSession));
    }

    // ------------------------------------------------------------------ signing in

    // The login view prefetches these (WebAuthn wants no await between the click and get()), so a
    // reload costs one: the looser two-factor limiter, not the five-a-minute sign-in one.
    [HttpPost("login/options")]
    [AllowAnonymous]
    [EnableRateLimiting("two-factor")]
    public IActionResult LoginOptions()
    {
        var (ceremonyId, assertion) = passkeys.BeginLogin(Request);
        return Ok(new { ceremonyId, options = assertion });
    }

    /// <summary>
    /// A verified passkey opens a profile session (accounts still locked) and returns that passkey's
    /// wrapped profile key. The browser opens it with the passkey's PRF output and calls <c>unlock</c>.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] CeremonyRequest request, CancellationToken ct)
    {
        var passkey = await passkeys.CompleteLoginAsync(request.CeremonyId, request.Credential, ct);
        if (passkey is null)
            return Unauthorized(new { error = "That passkey was not accepted here.", code = "PASSKEY_INVALID" });

        return await OpenProfileSessionAsync(passkey.ProfileId, passkey.WrappedKey, passkey.CredentialId);
    }

    /// <summary>The recovery code instead of a passkey. The browser should add a new passkey afterwards.</summary>
    [HttpPost("recover")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Recover([FromBody] RecoverRequest request)
    {
        var authKey = Base64Url.Decode(request.AuthKey);
        var profile = string.IsNullOrEmpty(request.ProfileId) ? null : store.GetProfile(request.ProfileId);
        if (authKey is not { Length: 32 } || profile?.RecoveryWrappedKey is null ||
            !ProfileCrypto.RecoveryAuthMatches(authKey, profile.RecoveryAuthHash))
        {
            logger.LogInformation("Profile recovery refused.");
            return Unauthorized(new { error = "That recovery code is not valid here.", code = "RECOVERY_INVALID" });
        }

        logger.LogInformation("Profile opened with its recovery code.");
        return await OpenProfileSessionAsync(profile.Id, profile.RecoveryWrappedKey, null);
    }

    private async Task<IActionResult> OpenProfileSessionAsync(string profileId, string wrappedKey, string? credentialId)
    {
        if (store.GetProfile(profileId) is null)
            return Unauthorized(new { error = "That profile no longer exists.", code = "PROFILE_GONE" });

        if (Request.Cookies.TryGetValue(SessionStore.CookieName, out var previous) && !string.IsNullOrEmpty(previous))
            await sessions.RemoveAsync(previous);

        store.TouchProfile(profileId);
        var runtime = registry.AcquireSession(profileId);
        var session = sessions.CreateForProfile(runtime);
        Response.Cookies.Append(SessionStore.CookieName, session.Id, SessionCookie());
        return Ok(new SignInResponse(profileId, wrappedKey, credentialId, SessionResponse.From(session)));
    }

    /// <summary>
    /// Hands the server the profile's accounts key for this unlock and brings the stored accounts
    /// back (each refresh rotates, and the new token is saved). Accounts that need a fresh sign-in
    /// come back in <c>skipped</c> and stay in the profile.
    /// </summary>
    [HttpPost("unlock")]
    [Authorize(Policy = "SessionAccess")]
    [EnableRateLimiting("api")]
    public async Task<IActionResult> Unlock([FromBody] UnlockRequest request, CancellationToken ct)
    {
        if (ProfileOf(out var session, out var profile) is { } refusal)
            return refusal;

        var key = Base64Url.Decode(request.AccountsKey);
        try
        {
            if (key is null || !session.Profile!.Unlock(key, profile.AccountsKeyCheck))
                return Unauthorized(new { error = "That key does not open this profile.", code = "PROFILE_KEY_INVALID" });
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
        }

        var skipped = await session.Profile.RestoreAsync(restorer, SessionStore.MaxAccounts, ct);
        UndelegateWithoutAccess(session.Profile, profile);
        logger.LogInformation("Profile unlocked: {Live} account(s) live, {Skipped} skipped.", session.Count, skipped.Count);
        return Ok(new UnlockResponse(SessionResponse.From(session), skipped
            .Select(s => new SkippedAccount(s.Candidate.Id ?? "", s.Entry.BaseUrl, s.Entry.Login, s.Entry.Role.ToString(), s.Reason))
            .ToList()));
    }

    // ------------------------------------------------------------------ the profile

    [HttpGet]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult Get()
    {
        if (ProfileOf(out var session, out var profile) is { } refusal)
            return refusal;
        return Ok(View(session, profile));
    }

    [HttpGet("settings")]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult GetSettings()
    {
        if (ProfileOf(out _, out var profile) is { } refusal)
            return refusal;
        return Ok(new { settings = profile.Settings, version = profile.SettingsVersion });
    }

    /// <summary>The browser's encrypted settings blob. <c>409 SETTINGS_STALE</c> when another browser saved first.</summary>
    [HttpPut("settings")]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult PutSettings([FromBody] SettingsRequest request)
    {
        if (ProfileOf(out _, out var profile) is { } refusal)
            return refusal;
        if (string.IsNullOrEmpty(request.Settings) || request.Settings.Length > MaxSettings)
            return BadRequest(new { error = "Settings are missing or too large.", code = "SETTINGS_INVALID" });

        if (!store.UpdateSettings(profile.Id, request.Settings, request.Version, out var version))
        {
            var current = store.GetProfile(profile.Id);
            return Conflict(new { error = "Settings changed in another browser.", code = "SETTINGS_STALE", settings = current?.Settings, version = current?.SettingsVersion });
        }
        return Ok(new { version });
    }

    [HttpPost("passkeys/options")]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult AddPasskeyOptions()
    {
        if (ProfileOf(out var session, out var profile) is { } refusal)
            return refusal;

        var existing = store.Passkeys(profile.Id).Select(p => p.CredentialId).ToList();
        var name = session.Accounts.FirstOrDefault()?.EmailAddress ?? "SmarterMail Agent";
        var (ceremonyId, creation) = passkeys.BeginRegistration(
            Request, profile.Id, $"{name} (SmarterMail Agent profile)", existing, Binding(session));
        return Ok(new { ceremonyId, options = creation });
    }

    [HttpPost("passkeys")]
    [Authorize(Policy = "SessionAccess")]
    public async Task<IActionResult> AddPasskey([FromBody] AddPasskeyRequest request, CancellationToken ct)
    {
        if (ProfileOf(out var session, out var profile) is { } refusal)
            return refusal;
        if (!Opaque(request.WrappedKey))
            return BadRequest(new { error = "The wrapped key is missing.", code = "PROFILE_INVALID" });

        var registered = await passkeys.CompleteRegistrationAsync(request.CeremonyId, request.Credential, Binding(session), ct);
        if (registered is null || registered.ProfileId != profile.Id)
            return BadRequest(new { error = "The passkey could not be verified. Try again.", code = "PASSKEY_INVALID" });

        store.AddPasskey(new PasskeyRow(registered.CredentialId, profile.Id, registered.PublicKey, registered.SignCount,
            registered.Aaguid, Clean(request.Label), request.WrappedKey!, DataStore.Now(), null));
        logger.LogInformation("Passkey added to a profile.");
        return Ok(View(session, profile));
    }

    [HttpDelete("passkeys/{credentialId}")]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult DeletePasskey(string credentialId)
    {
        if (ProfileOf(out _, out var profile) is { } refusal)
            return refusal;
        if (!store.DeletePasskey(profile.Id, credentialId))
            return Conflict(new { error = "That is the profile's last passkey (or not one of its passkeys).", code = "LAST_PASSKEY" });
        return NoContent();
    }

    /// <summary>Replaces the recovery code (the browser made a new one), or removes it with an empty body.</summary>
    [HttpPut("recovery")]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult PutRecovery([FromBody] RecoveryInput? request)
    {
        if (ProfileOf(out _, out var profile) is { } refusal)
            return refusal;

        if (request?.WrappedKey is null)
        {
            store.UpdateRecovery(profile.Id, null, null);
            return NoContent();
        }

        if (!Opaque(request.WrappedKey) || Base64Url.Decode(request.AuthKey) is not { Length: 32 } auth)
            return BadRequest(new { error = "The recovery code is malformed.", code = "PROFILE_INVALID" });

        store.UpdateRecovery(profile.Id, request.WrappedKey, ProfileCrypto.RecoveryAuthHash(auth));
        return NoContent();
    }

    /// <summary>Lets scheduled tasks use this account while nobody is signed in (re-sealed with the server key), or stops it.</summary>
    [HttpPut("accounts/{id}/delegation")]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult PutDelegation(string id, [FromBody] DelegationRequest request)
    {
        if (ProfileOf(out var session, out var profile) is { } refusal)
            return refusal;
        if (!session.Profile!.CanDelegate)
            return Conflict(new { error = "Scheduled tasks are not enabled on this server (no DATA_KEY).", code = "TASKS_DISABLED" });
        if (request.Enabled && !options.AllowsTasks(profile))
            return TaskAccess.NotInvited(this);
        if (!session.Profile.IsUnlocked)
            return Conflict(new { error = "Unlock your profile with your passkey first.", code = "PROFILE_LOCKED" });
        if (!session.Profile.SetDelegation(id, request.Enabled))
            return NotFound(new { error = "That account is not signed in to this profile.", code = "ACCOUNT_NOT_LIVE" });

        return Ok(View(session, profile));
    }

    /// <summary>The OpenRouter key scheduled tasks use, sealed with the server key. Null clears it.</summary>
    [HttpPut("task-key")]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult PutTaskKey([FromBody] TaskKeyRequest request)
    {
        if (ProfileOf(out var session, out var profile) is { } refusal)
            return refusal;
        if (registry.ServerSealer is not { } sealer)
            return Conflict(new { error = "Scheduled tasks are not enabled on this server (no DATA_KEY).", code = "TASKS_DISABLED" });

        if (string.IsNullOrWhiteSpace(request.Key))
        {
            store.UpdateTaskLlmKey(profile.Id, null);
        }
        else
        {
            if (!options.AllowsTasks(profile))
                return TaskAccess.NotInvited(this);
            if (request.Key.Length > 512)
                return BadRequest(new { error = "That key is too long.", code = "KEY_INVALID" });
            var plaintext = System.Text.Encoding.UTF8.GetBytes(request.Key.Trim());
            store.UpdateTaskLlmKey(profile.Id, sealer.SealString(plaintext, ProfileCrypto.TaskLlmKeyLabel, profile.Id));
            CryptographicOperations.ZeroMemory(plaintext);
        }

        return Ok(View(session, store.GetProfile(profile.Id)!));
    }

    /// <summary>
    /// Redeems an invite code for scheduled tasks (<c>TASKS_ACCESS=invite</c>). <c>400 INVITE_INVALID</c> for
    /// any code that cannot be used (unknown, used up, expired or revoked: the answer does not say which).
    /// A profile that already has access spends nothing.
    /// </summary>
    [HttpPost("task-access")]
    [Authorize(Policy = "SessionAccess")]
    [EnableRateLimiting("login")]
    public IActionResult RedeemTaskInvite([FromBody] TaskAccessRequest request)
    {
        if (ProfileOf(out var session, out var profile) is { } refusal)
            return refusal;
        if (!options.TasksEnabled)
            return NotFound(new { error = "Scheduled tasks are not enabled on this server.", code = "TASKS_DISABLED" });

        if (options.AllowsTasks(profile))
            return Ok(View(session, profile));

        switch (invites.Redeem(profile.Id, request.Code))
        {
            case TaskInviteStore.RedeemOutcome.Invalid:
                logger.LogInformation("Task invite refused.");
                return BadRequest(new { error = "That invite code is not valid (or has been used up).", code = "INVITE_INVALID" });
            case TaskInviteStore.RedeemOutcome.Granted:
                logger.LogInformation("Task invite redeemed.");
                break;
        }
        return Ok(View(session, store.GetProfile(profile.Id)!));
    }

    [HttpPut("tasks-paused")]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult PutTasksPaused([FromBody] PausedRequest request)
    {
        if (ProfileOf(out var session, out var profile) is { } refusal)
            return refusal;
        store.SetTasksPaused(profile.Id, request.Paused);
        return Ok(View(session, store.GetProfile(profile.Id)!));
    }

    /// <summary>
    /// The profile's session idle timeout, applied at once to every session of it. <c>400
    /// IDLE_OUT_OF_RANGE</c> outside <c>5</c> to <c>PROFILE_MAX_IDLE_MINUTES</c>.
    /// </summary>
    [HttpPut("idle")]
    [Authorize(Policy = "SessionAccess")]
    public IActionResult PutIdle([FromBody] IdleRequest request)
    {
        if (ProfileOf(out var session, out var profile) is { } refusal)
            return refusal;
        if (request.Minutes is { } m && (m < ServerOptions.ProfileMinIdleMinutes || m > options.ProfileMaxIdleMinutes))
        {
            return BadRequest(new
            {
                error = $"Choose between {ServerOptions.ProfileMinIdleMinutes} and {options.ProfileMaxIdleMinutes} minutes.",
                code = "IDLE_OUT_OF_RANGE",
            });
        }

        registry.SetIdleMinutes(profile.Id, request.Minutes);
        session.Touch();
        return Ok(View(session, profile));
    }

    /// <summary>Deletes the profile: every account is revoked on SmarterMail, every session of it ends, and its rows go.</summary>
    [HttpDelete]
    [Authorize(Policy = "SessionAccess")]
    public async Task<IActionResult> Delete()
    {
        if (ProfileOf(out var session, out var profile) is { } refusal)
            return refusal;

        await session.Profile!.RevokeAllAsync();
        store.DeleteProfile(profile.Id);
        foreach (var other in sessions.SessionsOf(profile.Id))
            await sessions.RemoveAsync(other.Id);

        Response.Cookies.Delete(SessionStore.CookieName, SessionCookie());
        logger.LogInformation("Profile deleted.");
        return NoContent();
    }

    // ------------------------------------------------------------------ helpers

    private ProfileView View(Session session, ProfileRow profile)
    {
        var runtime = session.Profile!;
        var live = runtime.Accounts.Accounts.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var accounts = new List<StoredAccountView>();

        foreach (var account in live.Values)
        {
            var row = runtime.Row(account.Id);
            accounts.Add(new StoredAccountView(account.Id, true, account.Handle, account.EmailAddress, account.BaseUrl,
                account.Role.ToString(), account.ReadOnly, row?.Seal == ProfileStore.SealServer, "ok"));
        }

        foreach (var row in runtime.Rows.Where(r => !live.ContainsKey(r.Id)))
        {
            accounts.Add(new StoredAccountView(row.Id, false, null, row.Entry?.Login, row.Entry?.BaseUrl,
                row.Entry?.Role.ToString(), row.Entry?.ReadOnly, row.Seal == ProfileStore.SealServer, row.State));
        }

        return new ProfileView(
            profile.Id,
            runtime.IsUnlocked,
            store.Passkeys(profile.Id).Select(p => new PasskeyView(p.CredentialId, p.Label,
                DateTimeOffset.FromUnixTimeMilliseconds(p.CreatedAt),
                p.LastUsedAt is { } used ? DateTimeOffset.FromUnixTimeMilliseconds(used) : null)).ToList(),
            accounts,
            profile.RecoveryWrappedKey is not null,
            profile.PublicKey,
            profile.EncryptedPrivateKey,
            profile.SettingsVersion,
            runtime.CanDelegate && options.AllowsTasks(profile),
            options.TasksEnabled,
            profile.TaskLlmKey is not null,
            profile.TasksPaused,
            new IdleView(store.IdleMinutes(profile.Id), (int)SessionStore.IdleTimeout.TotalMinutes,
                ServerOptions.ProfileMinIdleMinutes, options.ProfileMaxIdleMinutes),
            new TaskAccessView(options.TaskInviteOnly, options.AllowsTasks(profile)),
            options.IsAdmin(profile.Id));
    }

    /// <summary>
    /// A profile without task access (revoked, or the server became invite-only) gets its delegated live
    /// accounts back under its own key at unlock, so the server can no longer open them alone.
    /// </summary>
    private void UndelegateWithoutAccess(ProfileRuntime runtime, ProfileRow profile)
    {
        if (options.AllowsTasks(profile) || !runtime.CanDelegate)
            return;
        var delegated = runtime.Rows.Where(r => r.Seal == ProfileStore.SealServer && runtime.Accounts.FindById(r.Id) is not null).ToList();
        var moved = delegated.Count(r => runtime.SetDelegation(r.Id, false));
        if (moved > 0)
            logger.LogInformation("Profile without task access: {Count} delegated account(s) moved back under the profile key.", moved);
    }

    /// <summary>The request's profile session and its row, or the refusal to send instead.</summary>
    private IActionResult? ProfileOf(out Session session, out ProfileRow profile)
    {
        session = HttpContext.RequireSession();
        profile = null!;
        if (Refusal(session) is { } refusal)
            return refusal;
        if (session.Profile is null)
            return NotFound(new { error = "This chat is not saved to a profile.", code = "NO_PROFILE" });

        var row = store.GetProfile(session.Profile.ProfileId);
        if (row is null)
            return NotFound(new { error = "That profile no longer exists.", code = "PROFILE_GONE" });

        profile = row;
        return null;
    }

    /// <summary>Profiles are managed from the browser that holds the cookie, never with an MCP token.</summary>
    private IActionResult? Refusal(Session session) =>
        HttpContext.IsCookieAuthenticated()
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, new { error = "Use the browser for this.", code = "COOKIE_REQUIRED" });

    /// <summary>A ceremony started by one session can only be finished by the same session.</summary>
    private static string Binding(Session session) =>
        Base64Url.Encode(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(session.Id)));

    private CookieOptions SessionCookie()
    {
        var pathBase = Request.PathBase.HasValue ? Request.PathBase.Value! : "/";
        if (!pathBase.EndsWith('/')) pathBase += "/";
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps || !HostGuard.AllowPrivateHosts,
            SameSite = SameSiteMode.Strict,
            Path = pathBase,
            MaxAge = SessionStore.MaxAge,
        };
    }

    private static bool Opaque(string? value, int max = MaxOpaque) =>
        !string.IsNullOrEmpty(value) && value.Length <= max && Base64Url.Decode(value) is not null;

    private static string? Clean(string? label)
    {
        var trimmed = label?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        trimmed = new string(trimmed.Where(c => !char.IsControl(c)).ToArray());
        return trimmed.Length > MaxLabel ? trimmed[..MaxLabel] : trimmed;
    }
}
