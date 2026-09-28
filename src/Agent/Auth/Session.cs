using System.Security.Cryptography;

namespace SmarterMailAgent.Auth;

/// <summary>
/// One browser session: its clocks and the SmarterMail accounts signed in to it. Everything here
/// lives in memory and is dropped on logout or expiry. Nothing is persisted.
/// </summary>
public sealed class Session : IAsyncDisposable
{
    public required string Id { get; init; }

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; private set; } = DateTimeOffset.UtcNow;

    private readonly object _lock = new();
    private readonly List<Account> _accounts = [];
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

    /// <summary>A snapshot, in the order the accounts were added.</summary>
    public IReadOnlyList<Account> Accounts
    {
        get { lock (_lock) return _accounts.ToArray(); }
    }

    public int Count
    {
        get { lock (_lock) return _accounts.Count; }
    }

    public enum AddStatus { Added, Replaced, LimitReached }

    /// <summary>
    /// Adds <paramref name="account"/> and gives it a handle unique within this session. An existing
    /// account with the same (baseUrl, email) is replaced in place and returned in
    /// <paramref name="replaced"/>; the caller disposes it. At <paramref name="maxAccounts"/> nothing
    /// changes and <see cref="AddStatus.LimitReached"/> comes back.
    /// </summary>
    public AddStatus Add(Account account, int maxAccounts, out Account? replaced)
    {
        lock (_lock)
        {
            var index = _accounts.FindIndex(a => SameLogin(a, account.BaseUrl, account.EmailAddress));
            var existing = index >= 0 ? _accounts[index] : null;
            replaced = existing;

            if (existing is null && _accounts.Count >= maxAccounts)
                return AddStatus.LimitReached;

            var others = _accounts.Where(a => !ReferenceEquals(a, existing)).Select(a => a.Handle);
            account.Handle = UniqueHandle(
                Account.BaseHandle(account.Role, account.EmailAddress, account.BaseUrl),
                Account.HostOf(account.BaseUrl), others);
            account.Rotated = BumpResumeVersion;
            BumpResumeVersion();

            if (existing is not null)
            {
                _accounts[index] = account;
                return AddStatus.Replaced;
            }

            _accounts.Add(account);
            return AddStatus.Added;
        }
    }

    /// <summary>Whether adding this login would need a free slot (false when it would replace one).</summary>
    public bool IsFullFor(string baseUrl, string emailAddress, int maxAccounts)
    {
        lock (_lock)
            return _accounts.Count >= maxAccounts && !_accounts.Any(a => SameLogin(a, baseUrl, emailAddress));
    }

    /// <summary>Detaches the account; the caller disposes it.</summary>
    public Account? Remove(string accountId)
    {
        lock (_lock)
        {
            var index = _accounts.FindIndex(a => string.Equals(a.Id, accountId, StringComparison.Ordinal));
            if (index < 0) return null;
            var account = _accounts[index];
            _accounts.RemoveAt(index);
            BumpResumeVersion();
            return account;
        }
    }

    public Account? Find(string? handle)
    {
        if (string.IsNullOrEmpty(handle)) return null;
        lock (_lock)
            return _accounts.FirstOrDefault(a => string.Equals(a.Handle, handle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <paramref name="baseHandle"/>, or <c>baseHandle#host</c> on a clash, then a counter. Handles
    /// compare case-insensitively because they are mostly email addresses.
    /// </summary>
    public static string UniqueHandle(string baseHandle, string host, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseHandle))
            return baseHandle;

        var withHost = $"{baseHandle}#{host}";
        if (!taken.Contains(withHost))
            return withHost;

        for (var n = 2; ; n++)
        {
            var candidate = $"{withHost}-{n}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    private static bool SameLogin(Account account, string baseUrl, string emailAddress) =>
        string.Equals(account.BaseUrl.TrimEnd('/'), baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(account.EmailAddress, emailAddress, StringComparison.OrdinalIgnoreCase);

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

        Account[] accounts;
        lock (_lock)
        {
            accounts = _accounts.ToArray();
            _accounts.Clear();
            _mcpTokenHash = null;
        }

        // In parallel: each disposal revokes on its own mail server with a bounded wait, so closing
        // a full session costs one timeout at worst, not one per account.
        await Task.WhenAll(accounts.Select(a => (revoke ? a.DisposeAsync() : a.ForgetAsync()).AsTask()));
    }
}
