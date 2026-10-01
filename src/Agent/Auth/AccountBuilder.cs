using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Auth;

/// <summary>Turns a successful sign-in (or a refreshed stored token) into an in-memory <see cref="Account"/>.</summary>
public static class AccountBuilder
{
    /// <param name="id">A stored account keeps its row id; a fresh sign-in gets a new one.</param>
    public static Account Build(
        SmarterMailAuth auth, AuthOutcome.Success success, string baseUrl, bool readOnly, string? id = null)
    {
        var tokenData = success.TokenData;

        // GlobalContext demands a token file path. Point it at a file inside the existing temp
        // directory that is never created, never read and never written. Nothing appears on disk.
        var globalContext = new GlobalContext(
            Path.Combine(Path.GetTempPath(), $"sma-never-{Guid.NewGuid():N}.json"),
            readOnlyMode: readOnly);

        var userContext = UserContextFactory.Create(globalContext, tokenData);

        return new Account
        {
            Id = id ?? Account.NewId(),
            Role = success.Role,
            TokenData = tokenData,
            GlobalContext = globalContext,
            UserContext = userContext,
            Auth = auth,
            Username = userContext.Username,
            EmailAddress = tokenData.Username ?? string.Empty,
            Domain = userContext.Domain,
            BaseUrl = tokenData.BaseUrl ?? baseUrl.TrimEnd('/'),
            ReadOnly = readOnly,
        };
    }

    /// <summary>What a stored copy of <paramref name="account"/> holds: everything but the access token.</summary>
    public static ResumeAccount ToStored(Account account) => new(
        account.BaseUrl, account.EmailAddress, account.Role, account.ReadOnly, account.ClientId ?? string.Empty,
        account.TokenData.RefreshToken ?? string.Empty, account.TokenData.RefreshExpiration, account.TokenData.UserType);
}

/// <summary>
/// Brings stored accounts (a resume bundle's, a profile's) back to life: each refresh token is
/// refreshed on its own SmarterMail, which also rotates it. Shared by remember-me, profile unlock
/// and scheduled tasks so all three re-check the SSRF guard and count against the per-server
/// failed-login cap the same way.
/// </summary>
public sealed class AccountRestorer(SmarterMailAuth auth, HostLoginThrottle hostThrottle, ILogger logger)
{
    /// <param name="Id">The account id to rebuild with (a profile row); null for a fresh id.</param>
    public sealed record Candidate(ResumeAccount Entry, string? Id = null);

    /// <summary>
    /// <c>Reason</c>: <c>REJECTED</c> (SmarterMail refused the refresh token), <c>EXPIRED</c> (it had
    /// lapsed), <c>UNAVAILABLE</c> (no answer), <c>BLOCKED_HOST</c> (fails the SSRF guard now),
    /// <c>ACCOUNT_LIMIT</c>, <c>THROTTLED</c> (that server is over the failed-login cap).
    /// </summary>
    public sealed record Skipped(Candidate Candidate, string Reason)
    {
        public ResumeAccount Entry => Candidate.Entry;
    }

    public sealed record Restored(Candidate Candidate, Account Account);

    /// <param name="Throttled">Set when <c>throttledFailsAll</c> stopped the whole restore before any refresh.</param>
    public sealed record Result(
        IReadOnlyList<Restored> Accounts, IReadOnlyList<Skipped> Skipped, (string BaseUrl, TimeSpan RetryAfter)? Throttled)
    {
        public bool AnyUnavailable => Skipped.Any(s => s.Reason == "UNAVAILABLE");
    }

    /// <param name="throttledFailsAll">
    /// Remember-me: one throttled server refuses the whole resume before anything rotates, so the
    /// browser keeps a bundle that still works later. A profile instead skips that server's accounts
    /// (<c>THROTTLED</c>): their stored tokens are untouched and come back on the next unlock.
    /// </param>
    public async Task<Result> RestoreAsync(
        IReadOnlyList<Candidate> candidates, int maxAccounts, bool throttledFailsAll, string purpose, CancellationToken ct)
    {
        var skipped = new List<Skipped>();
        var ready = new List<(Candidate Candidate, string BaseUrl)>();

        foreach (var (candidate, index) in candidates.Select((c, i) => (c, i)))
        {
            var entry = candidate.Entry;
            if (index >= maxAccounts)
                skipped.Add(new Skipped(candidate, "ACCOUNT_LIMIT"));
            else if (ResumeSealer.RefreshExpired(entry))
                skipped.Add(new Skipped(candidate, "EXPIRED"));
            else if (await HostGuard.ValidateAsync(entry.BaseUrl, ct) is { Ok: true, BaseUrl: { } baseUrl } &&
                     string.Equals(baseUrl, entry.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                ready.Add((candidate, baseUrl));
            else
                skipped.Add(new Skipped(candidate, "BLOCKED_HOST"));
        }

        foreach (var (candidate, baseUrl) in ready.ToList())
        {
            if (hostThrottle.RetryAfter(baseUrl) is not { } wait)
                continue;
            if (throttledFailsAll)
                return new Result([], skipped, (baseUrl, wait));
            ready.Remove((candidate, baseUrl));
            skipped.Add(new Skipped(candidate, "THROTTLED"));
        }

        var refreshed = await Task.WhenAll(ready.Select(r => RefreshAsync(r.Candidate, r.BaseUrl, purpose, ct)));

        var accounts = new List<Restored>();
        foreach (var (candidate, baseUrl, tokenData, result) in refreshed)
        {
            if (result == RefreshResult.Refreshed)
            {
                var account = AccountBuilder.Build(
                    auth, new AuthOutcome.Success(tokenData, candidate.Entry.Role), baseUrl, candidate.Entry.ReadOnly, candidate.Id);
                accounts.Add(new Restored(candidate, account));
            }
            else
            {
                skipped.Add(new Skipped(candidate, result == RefreshResult.Rejected ? "REJECTED" : "UNAVAILABLE"));
            }
        }

        return new Result(accounts, skipped, null);
    }

    /// <summary>
    /// One account's refresh, counted against its server like a sign-in: SmarterMail's intrusion
    /// detection counts an invalid refresh token as a failed login from our IP.
    /// </summary>
    private async Task<(Candidate Candidate, string BaseUrl, TokenData TokenData, RefreshResult Result)> RefreshAsync(
        Candidate candidate, string baseUrl, string purpose, CancellationToken ct)
    {
        var entry = candidate.Entry;
        var tokenData = new TokenData
        {
            BaseUrl = baseUrl,
            Username = entry.Login,
            ReadOnlyMode = entry.ReadOnly,
            Method = "simple",
            UserType = entry.UserType ?? (entry.Role == AccountRole.SysAdmin ? "admin" : "user"),
            ClientId = entry.ClientId,
            RefreshToken = entry.RefreshToken,
            RefreshExpiration = entry.RefreshExpiration,
        };

        using var attempt = hostThrottle.TryBegin(baseUrl, out _);
        if (attempt is null)
            return (candidate, baseUrl, tokenData, RefreshResult.Unavailable);

        var result = await auth.TryRefreshAsync(tokenData, ct);
        attempt.Finish(failed: result == RefreshResult.Rejected);
        logger.LogInformation("{Purpose} refresh for host {Host}: {Result}.", purpose, baseUrl, result);
        return (candidate, baseUrl, tokenData, result);
    }
}
