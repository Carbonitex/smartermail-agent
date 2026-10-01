using System.Security.Cryptography;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Auth;

/// <summary>
/// What a signed-in SmarterMail account is allowed to be. Decides which tool scopes it sees; it is
/// never a grant of privilege. SmarterMail enforces authorization on every call regardless.
/// </summary>
public enum AccountRole
{
    User,
    DomainAdmin,
    SysAdmin,
}

/// <summary>
/// The parts of an account the tool policy needs. <see cref="Account"/> implements it; tests use a
/// plain record so the policy can be exercised without a live SmarterMail login.
/// </summary>
public interface IToolAccount
{
    string Handle { get; }
    AccountRole Role { get; }
    bool ReadOnly { get; }
}

/// <summary>
/// One SmarterMail login inside a browser session. Everything here lives in memory and is dropped
/// when the account is removed, its refresh fails, or its session ends; its tokens are revoked on
/// SmarterMail at the same time (best effort) — except when a remembered session expires, whose
/// browser still holds them in its resume bundle, or a profile forgets it, whose store still holds
/// its refresh token (server mode, <see cref="Persist"/>).
/// </summary>
public sealed class Account : IToolAccount, IAsyncDisposable
{
    public required string Id { get; init; }
    public required AccountRole Role { get; init; }

    /// <summary>
    /// The value the LLM passes as <c>account</c>. Assigned by <see cref="Session.Add"/>, which keeps
    /// it unique within the session. Contains an email address: never log it.
    /// </summary>
    public string Handle { get; internal set; } = string.Empty;

    public required TokenData TokenData { get; init; }
    public required GlobalContext GlobalContext { get; init; }
    public required UserContext UserContext { get; init; }
    public required SmarterMailAuth Auth { get; init; }

    public required string Username { get; init; }      // local part, or the sysadmin login
    public required string EmailAddress { get; init; }  // the login as typed, e.g. "matt@example.com"
    public required string Domain { get; init; }        // empty for a sysadmin
    public required string BaseUrl { get; init; }
    public required bool ReadOnly { get; init; }

    public string? ClientId => TokenData.ClientId;

    public DateTimeOffset LastTokenRefresh { get; set; } = DateTimeOffset.UtcNow;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private int _disposed;
    private volatile bool _releaseRevokes = true;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>8 characters of base64url. Unique enough within one session of at most a handful.</summary>
    public static string NewId()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>The handle before any clash suffix: the email, or <c>sysadmin:&lt;user&gt;@&lt;host&gt;</c>.</summary>
    public static string BaseHandle(AccountRole role, string emailAddress, string baseUrl) =>
        role == AccountRole.SysAdmin
            ? $"sysadmin:{emailAddress}@{HostOf(baseUrl)}"
            : emailAddress;

    /// <summary>Hostname plus <c>:port</c> when the port is not the scheme's default.</summary>
    public static string HostOf(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Authority : baseUrl;

    /// <summary>
    /// Called after every successful refresh, i.e. every time SmarterMail rotates this account's
    /// refresh token. <see cref="Session.Add"/> points it at the session's resume version.
    /// </summary>
    internal Action? Rotated { get; set; }

    /// <summary>
    /// Server mode: writes the rotated refresh token to the profile store. Awaited <b>inside</b> the
    /// refresh lock, so no second refresh can rotate past a copy that was never saved, and a crash
    /// right after a refresh leaves the newest token on disk. A failure is logged by the caller's
    /// hook and does not fail the refresh: the account keeps working in memory.
    /// </summary>
    internal Func<Account, Task>? Persist { get; set; }

    /// <summary>
    /// Refreshes the SmarterMail access token in memory and pushes it into the UserContext.
    /// Serialised per account; safe to call from the sweeper and from a 401 retry at once.
    /// </summary>
    public async Task<bool> RefreshTokenAsync(CancellationToken ct = default) =>
        await RefreshAsync(onlyIfStale: false, ct) == RefreshResult.Refreshed;

    /// <summary>
    /// The lazy refresh a remembered session uses before each call: refreshes only when the access
    /// token is about to expire (<see cref="NeedsTokenRefresh"/> with <c>lazy: true</c>), checked
    /// again under the lock so concurrent calls rotate once, not once each. When the token is still
    /// good it only re-stamps Core's refresh clock. <see cref="RefreshResult.Refreshed"/> means the
    /// token is fresh now, whether or not SmarterMail was called.
    /// </summary>
    public async Task<RefreshResult> EnsureFreshAsync(CancellationToken ct = default)
    {
        if (!IsDisposed && !NeedsTokenRefresh(lazy: true))
        {
            UserContextFactory.StampRefreshClock(UserContext);
            return RefreshResult.Refreshed;
        }

        return await RefreshAsync(onlyIfStale: true, ct);
    }

    private async Task<RefreshResult> RefreshAsync(bool onlyIfStale, CancellationToken ct)
    {
        if (IsDisposed)
            return RefreshResult.Unavailable;

        await _refreshLock.WaitAsync(ct);

        try
        {
            // DisposeAsync held the lock while revoking; nothing left to refresh.
            if (IsDisposed)
                return RefreshResult.Unavailable;

            // Another call refreshed while this one waited for the lock.
            if (onlyIfStale && !NeedsTokenRefresh(lazy: true))
            {
                UserContextFactory.StampRefreshClock(UserContext);
                return RefreshResult.Refreshed;
            }

            var result = await Auth.TryRefreshAsync(TokenData, ct);
            var ok = result == RefreshResult.Refreshed;
            if (IsDisposed)
            {
                // Disposed while this refresh was in flight. DisposeAsync is waiting for the lock (or
                // gave up and revoked the old pair); either way only this path has the pair just
                // minted, so revoke it here before it can outlive the account on SmarterMail. A
                // profile account that is only being forgotten saves the new pair instead: its store
                // still holds the pair this refresh just rotated dead.
                if (ok && !_releaseRevokes && Persist is { } late)
                    await late(this);
                else if (ok)
                    await Auth.LogoutAsync(TokenData, CancellationToken.None);
                TokenData.AccessToken = null;
                TokenData.RefreshToken = null;
                return RefreshResult.Unavailable;
            }

            if (ok)
            {
                UserContextFactory.ApplyRefreshedToken(UserContext, TokenData);
                LastTokenRefresh = DateTimeOffset.UtcNow;
                if (Persist is { } persist)
                    await persist(this);
                Rotated?.Invoke();
            }
            return result;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// How close to expiry a lazily refreshed (remembered) account lets its access token get before
    /// a call refreshes it. SmarterMail access tokens live 15 minutes; the 401 retry covers the rest.
    /// </summary>
    public static readonly TimeSpan LazyRefreshMargin = TimeSpan.FromMinutes(2);

    /// <summary>
    /// True when the access token is close enough to expiry that we should refresh now.
    /// <para>
    /// The sweeper's proactive rule (<paramref name="lazy"/> false) also refreshes every 8 minutes,
    /// ahead of Core's 10-minute auto-refresh. A remembered session must not: every refresh rotates
    /// the refresh token and would leave the browser's saved copy dead while the browser is away.
    /// The lazy rule refreshes only near the token's own expiry, and the dispatcher keeps Core's
    /// clock stamped instead; with no expiry to go by it falls back to the 8-minute clock.
    /// </para>
    /// </summary>
    public bool NeedsTokenRefresh(bool lazy = false)
    {
        var sinceRefresh = DateTimeOffset.UtcNow - LastTokenRefresh;
        if (!lazy && sinceRefresh > TimeSpan.FromMinutes(8))
            return true;

        if (DateTime.TryParse(TokenData.Expiration, out var expiration))
            return DateTime.Now > expiration - (lazy ? LazyRefreshMargin : TimeSpan.FromMinutes(5));

        return lazy && sinceRefresh > TimeSpan.FromMinutes(8);
    }

    /// <summary>How long disposal waits for an in-flight refresh before revoking anyway.</summary>
    private static readonly TimeSpan RefreshWaitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Revokes the tokens on SmarterMail (best effort, bounded by
    /// <see cref="SmarterMailAuth.DefaultLogoutTimeout"/>) and then drops them from memory. Every
    /// removal path ends here — logout, account removal or replacement, a failed refresh, session
    /// expiry — except the expiry of a remembered session, which uses <see cref="ForgetAsync"/>.
    /// </summary>
    public ValueTask DisposeAsync() => ReleaseAsync(revoke: true);

    /// <summary>
    /// Drops the tokens from memory <b>without</b> revoking them on SmarterMail, so the refresh
    /// token in the browser's resume bundle stays usable. Only for a remembered session that idled
    /// or aged out; every explicit removal revokes (<see cref="DisposeAsync"/>).
    /// </summary>
    public ValueTask ForgetAsync() => ReleaseAsync(revoke: false);

    private async ValueTask ReleaseAsync(bool revoke)
    {
        // Written before _disposed, so a refresh that sees the account disposed also sees how.
        if (IsDisposed)
            return;
        _releaseRevokes = revoke;
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Take the refresh lock so a refresh and this revocation never interleave: a refresh that
        // mints a new pair while we log out with the old one (and null it) could leave the new pair
        // alive on SmarterMail. A refresh in flight sees IsDisposed when it lands and revokes the
        // pair it got, so we skip ours (TokenData is null by then). Bounded, so a refresh stuck on a
        // dead server cannot hold up removal. Forgetting waits too, so the tokens are never nulled
        // under a refresh; one that lands afterwards still revokes what it minted, because it has
        // rotated the browser's copy dead already.
        //
        // The semaphore is released, never disposed: SemaphoreSlim holds nothing to free unless its
        // wait handle is used, and disposing it would strand a refresh queued behind us.
        var locked = await _refreshLock.WaitAsync(RefreshWaitTimeout);

        try
        {
            if (revoke)
                await Auth.LogoutAsync(TokenData);

            try
            {
                await UserContext.DisposeAsync();
            }
            catch (Exception)
            {
                // A dead account must never take the process down.
            }

            TokenData.AccessToken = null;
            TokenData.RefreshToken = null;
        }
        finally
        {
            if (locked)
                _refreshLock.Release();
        }
    }
}
