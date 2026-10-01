using System.Security.Cryptography;

namespace SmarterMailAgent.Auth;

/// <summary>
/// One browser session: its clocks and the SmarterMail accounts signed in to it. Everything here
/// lives in memory and is dropped on logout or expiry. A browser-only session owns its accounts; a
/// profile session (server mode) borrows its profile's (<see cref="Profile"/>).
/// </summary>
public sealed class Session : IAsyncDisposable
{
    public required string Id { get; init; }

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; private set; } = DateTimeOffset.UtcNow;

    private readonly object _lock = new();
    private int _disposed;
    private long _resumeVersion;
    private RememberWindow? _remember;

    private sealed record RememberWindow(DateTimeOffset Since, DateTimeOffset Until, bool On);

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    // At most one MCP token, kept only as its SHA-256 (see SessionStore.IssueMcpToken).
    private byte[]? _mcpTokenHash;
    private DateTimeOffset _mcpTokenExpiresAt;

    public void Touch() => LastSeenAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// "Remember me on this device": the browser holds a sealed resume bundle for this session's
    /// accounts. Such a session refreshes tokens lazily (per call, not in the sweeper) and is
    /// forgotten rather than revoked when it expires. False once the remember chain has aged out.
    /// </summary>
    public bool IsRemembered => _remember is { On: true } window && DateTimeOffset.UtcNow < window.Until;

    /// <summary>
    /// When the remember chain began: the original password sign-in, carried across resumes. Kept
    /// when remembering is switched off, so switching it back on cannot start a fresh chain.
    /// </summary>
    public DateTimeOffset? RememberedSince => _remember?.Since;

    /// <summary>The chain's absolute end; no bundle is issued or accepted after it.</summary>
    public DateTimeOffset? RememberedUntil => _remember?.Until;

    public void Remember(DateTimeOffset since, DateTimeOffset until)
    {
        _remember = new RememberWindow(since, until, On: true);
        BumpResumeVersion();
    }

    public void StopRemembering()
    {
        if (_remember is { } window)
            _remember = window with { On = false };
    }

    /// <summary>
    /// Changes whenever the resume bundle would: every token rotation and every account change. A
    /// Unix-millisecond clock forced strictly upward, so a new session's first version is already
    /// above anything an older session (or an older server process) handed the same browser.
    /// </summary>
    public long ResumeVersion => Interlocked.Read(ref _resumeVersion);

    internal void BumpResumeVersion()
    {
        while (true)
        {
            var current = Interlocked.Read(ref _resumeVersion);
            var next = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), current + 1);
            if (Interlocked.CompareExchange(ref _resumeVersion, next, current) == current)
                return;
        }
    }

    public DateTimeOffset ExpiresAt(TimeSpan maxAge) => CreatedAt + maxAge;

    public bool IsExpired(TimeSpan idle, TimeSpan maxAge) =>
        DateTimeOffset.UtcNow - LastSeenAt > idle || DateTimeOffset.UtcNow - CreatedAt > maxAge;

    /// <summary>When the session's MCP token lapses; null when it has none (or it already lapsed).</summary>
    public DateTimeOffset? McpTokenExpiresAt
    {
        get
        {
            lock (_lock)
                return _mcpTokenHash is not null && DateTimeOffset.UtcNow < _mcpTokenExpiresAt ? _mcpTokenExpiresAt : null;
        }
    }

    /// <summary>
    /// Replaces the MCP token hash; the old hash comes back in <paramref name="previous"/> so the
    /// store can drop it from its index. False once the session is disposed.
    /// </summary>
    public bool TrySetMcpToken(byte[] hash, DateTimeOffset expiresAt, out byte[]? previous)
    {
        lock (_lock)
        {
            previous = _mcpTokenHash;
            if (_disposed != 0)
                return false;
            _mcpTokenHash = hash;
            _mcpTokenExpiresAt = expiresAt;
            return true;
        }
    }

    /// <summary>Forgets the MCP token and returns its hash (null when there was none).</summary>
    public byte[]? ClearMcpToken()
    {
        lock (_lock)
        {
            var previous = _mcpTokenHash;
            _mcpTokenHash = null;
            return previous;
        }
    }

    /// <summary>Constant-time check of a presented token's hash against the live MCP token.</summary>
    public bool McpTokenMatches(byte[] hash)
    {
        lock (_lock)
            return _mcpTokenHash is not null && DateTimeOffset.UtcNow < _mcpTokenExpiresAt &&
                   CryptographicOperations.FixedTimeEquals(_mcpTokenHash, hash);
    }

    /// <summary>The accounts this chat can use: its own, or its profile's shared set.</summary>
    public AccountSet AccountSet { get; }

    /// <summary>
    /// Set when this session belongs to a server-mode profile. Its accounts are then the profile's,
    /// shared with the profile's other sessions and its scheduled tasks; closing the session only
    /// releases its hold on them (<see cref="Profiles.ProfileRuntime.ReleaseSession"/>).
    /// </summary>
    public Profiles.ProfileRuntime? Profile { get; }

    /// <summary>A browser-only session that owns its accounts.</summary>
    public Session()
    {
        AccountSet = new AccountSet();
        AccountSet.Changed += BumpResumeVersion;
    }

    /// <summary>A session of <paramref name="profile"/>, which must already count it (<c>AcquireSession</c>).</summary>
    public Session(Profiles.ProfileRuntime profile)
    {
        Profile = profile;
        AccountSet = profile.Accounts;
        AccountSet.Changed += BumpResumeVersion;
    }

    /// <summary>
    /// Whether the dispatcher refreshes tokens per call instead of the sweeper refreshing them on a
    /// clock: remembered sessions (the browser holds a copy of the refresh tokens) and profile
    /// sessions (the stored copy is rewritten on every rotation; fewer rotations, fewer writes).
    /// </summary>
    public bool RefreshesLazily => IsRemembered || Profile is not null;

    /// <summary>A snapshot, in the order the accounts were added.</summary>
    public IReadOnlyList<Account> Accounts => AccountSet.Accounts;

    public int Count => AccountSet.Count;

    /// <inheritdoc cref="Auth.AccountSet.Add"/>
    public AccountSet.AddStatus Add(Account account, int maxAccounts, out Account? replaced) =>
        AccountSet.Add(account, maxAccounts, out replaced);

    /// <summary>Whether adding this login would need a free slot (false when it would replace one).</summary>
    public bool IsFullFor(string baseUrl, string emailAddress, int maxAccounts) =>
        AccountSet.IsFullFor(baseUrl, emailAddress, maxAccounts);

    /// <summary>Detaches the account; the caller disposes it.</summary>
    public Account? Remove(string accountId) => AccountSet.Remove(accountId);

    public Account? Find(string? handle) => AccountSet.Find(handle);

    /// <inheritdoc cref="Auth.AccountSet.UniqueHandle"/>
    public static string UniqueHandle(string baseHandle, string host, IEnumerable<string> existing) =>
        AccountSet.UniqueHandle(baseHandle, host, existing);

    /// <summary>Closes the session and revokes every account's tokens on SmarterMail.</summary>
    public ValueTask DisposeAsync() => ReleaseAsync(revoke: true);

    /// <summary>
    /// Closes the session <b>without</b> revoking: for a remembered session that expired, whose
    /// browser still holds the refresh tokens in its resume bundle (<see cref="Account.ForgetAsync"/>).
    /// </summary>
    public ValueTask ForgetAsync() => ReleaseAsync(revoke: false);

    private async ValueTask ReleaseAsync(bool revoke)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_lock)
            _mcpTokenHash = null;

        AccountSet.Changed -= BumpResumeVersion;

        // A profile's accounts outlive any one of its sessions: logging out (or idling out) of one
        // browser locks that browser, and the profile decides what to forget once no session is left.
        if (Profile is not null)
        {
            await Profile.ReleaseSessionAsync();
            return;
        }

        var accounts = AccountSet.TakeAll();

        // In parallel: each disposal revokes on its own mail server with a bounded wait, so closing
        // a full session costs one timeout at worst, not one per account.
        await Task.WhenAll(accounts.Select(a => (revoke ? a.DisposeAsync() : a.ForgetAsync()).AsTask()));
    }
}
