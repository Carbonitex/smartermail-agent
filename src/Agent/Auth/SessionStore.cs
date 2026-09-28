using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SmarterMailAgent.Auth;

public sealed class SessionStore(ILogger<SessionStore> logger)
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    // Hex SHA-256 of an MCP token → session id. Only an index: the session's own hash is the
    // authority, compared in constant time, so a stale entry here never authenticates anything.
    private readonly ConcurrentDictionary<string, string> _mcpTokens = new(StringComparer.Ordinal);

    public static readonly TimeSpan IdleTimeout =
        TimeSpan.FromMinutes(EnvInt("SESSION_IDLE_MINUTES", 30));

    public static readonly TimeSpan MaxAge =
        TimeSpan.FromHours(EnvInt("SESSION_MAX_HOURS", 12));

    /// <summary>How many SmarterMail accounts one browser session may hold at once.</summary>
    public static readonly int MaxAccounts = EnvInt("SESSION_MAX_ACCOUNTS", 5);

    /// <summary>
    /// Optional cap on an MCP token's lifetime (<c>MCP_TOKEN_HOURS</c>). Unset: the token lives as
    /// long as its session, which is never longer than <see cref="MaxAge"/>.
    /// </summary>
    public static readonly TimeSpan? McpTokenLifetime =
        EnvInt("MCP_TOKEN_HOURS", 0) is > 0 and var hours ? TimeSpan.FromHours(hours) : null;

    public const string CookieName = "sma_session";

    /// <summary>Makes an MCP token recognisable (in a config file, to a secret scanner).</summary>
    public const string McpTokenPrefix = "sma_mcp_";

    public int Count => _sessions.Count;

    public static string NewSessionId()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>Opens a new session holding <paramref name="first"/>.</summary>
    public Session Create(Account first)
    {
        var session = new Session { Id = NewSessionId() };
        session.Add(first, MaxAccounts, out _);

        _sessions[session.Id] = session;
        logger.LogInformation("Session opened. Active sessions: {Count}", _sessions.Count);
        return session;
    }

    public Session? Get(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_sessions.TryGetValue(id, out var session))
            return null;

        if (session.IsExpired(IdleTimeout, MaxAge) || session.Count == 0)
        {
            _ = ExpireAsync(id);
            return null;
        }

        return session;
    }

    /// <summary>
    /// Mints the session's MCP token, invalidating any previous one. The raw value is returned
    /// once and never kept: the session holds its SHA-256 only. Null if the session just closed.
    /// </summary>
    public (string Token, DateTimeOffset ExpiresAt)? IssueMcpToken(Session session)
    {
        var token = McpTokenPrefix + NewSessionId();
        var hash = HashMcpToken(token);

        var expiresAt = session.ExpiresAt(MaxAge);
        if (McpTokenLifetime is { } lifetime && DateTimeOffset.UtcNow + lifetime < expiresAt)
            expiresAt = DateTimeOffset.UtcNow + lifetime;

        var ok = session.TrySetMcpToken(hash, expiresAt, out var previous);
        if (previous is not null)
            _mcpTokens.TryRemove(Convert.ToHexString(previous), out _);
        if (!ok)
            return null;

        _mcpTokens[Convert.ToHexString(hash)] = session.Id;
        return (token, expiresAt);
    }

    public void RevokeMcpToken(Session session)
    {
        if (session.ClearMcpToken() is { } previous)
            _mcpTokens.TryRemove(Convert.ToHexString(previous), out _);
    }

    /// <summary>The live session whose current, unexpired MCP token this is; otherwise null.</summary>
    public Session? FindByMcpToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(McpTokenPrefix, StringComparison.Ordinal))
            return null;

        var hash = HashMcpToken(token);
        if (!_mcpTokens.TryGetValue(Convert.ToHexString(hash), out var id))
            return null;

        var session = Get(id);
        return session is not null && session.McpTokenMatches(hash) ? session : null;
    }

    /// <summary>
    /// The session's hash dies with it; drop its index entries too (a racing rotation can leave
    /// more than one).
    /// </summary>
    private void DropMcpTokens(string sessionId)
    {
        foreach (var entry in _mcpTokens.Where(e => e.Value == sessionId))
            _mcpTokens.TryRemove(entry);
    }

    private static byte[] HashMcpToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>Closes a session on request (logout, a new login): its tokens are revoked.</summary>
    public async Task RemoveAsync(string id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            DropMcpTokens(id);
            await session.DisposeAsync();
            logger.LogInformation("Session closed. Active sessions: {Count}", _sessions.Count);
        }
    }

    /// <summary>
    /// Closes a session that idled or aged out. A remembered one is only forgotten — dropped from
    /// memory with its tokens left valid — so the browser can resume from its bundle; anything else
    /// is revoked exactly as <see cref="RemoveAsync"/> does.
    /// </summary>
    public async Task ExpireAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var session))
            return;

        DropMcpTokens(id);

        if (session.IsRemembered)
        {
            await session.ForgetAsync();
            logger.LogInformation("Remembered session expired; forgotten without revoking. Active sessions: {Count}",
                _sessions.Count);
            return;
        }

        await session.DisposeAsync();
        logger.LogInformation("Session closed. Active sessions: {Count}", _sessions.Count);
    }

    /// <summary>
    /// Detaches and disposes one account. A session left with no accounts is closed, which
    /// <c>SessionEnded</c> reports so the caller can clear the cookie.
    /// </summary>
    public async Task<(bool Found, bool SessionEnded)> RemoveAccountAsync(Session session, string accountId)
    {
        var account = session.Remove(accountId);
        if (account is null)
            return (false, false);

        await account.DisposeAsync();
        logger.LogInformation("Account removed. Accounts in session: {Count}", session.Count);

        if (session.Count > 0)
            return (true, false);

        await RemoveAsync(session.Id);
        return (true, true);
    }

    public IReadOnlyCollection<Session> Snapshot() => _sessions.Values.ToArray();

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0
            ? value
            : fallback;
}

/// <summary>
/// Drops idle/expired sessions and keeps every live account's SmarterMail token fresh, in memory.
/// An account whose refresh fails is dropped on its own; a session left with none is closed.
/// Remembered sessions are left alone apart from expiry: they refresh lazily, per call (see
/// <see cref="Account.EnsureFreshAsync"/>), and are forgotten rather than revoked when they expire.
/// Also expires pending two-factor challenges and aged-out per-host login failures.
/// </summary>
public sealed class SessionSweeper(
    SessionStore store, PendingLoginStore pending, HostLoginThrottle hostThrottle,
    ILogger<SessionSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Session sweep failed.");
            }
        }
    }

    /// <summary>One pass; the timer calls it every minute.</summary>
    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        pending.Sweep();
        hostThrottle.Sweep();

        foreach (var session in store.Snapshot())
        {
            if (session.IsExpired(SessionStore.IdleTimeout, SessionStore.MaxAge))
            {
                await store.ExpireAsync(session.Id);
                continue;
            }

            // Every refresh rotates the refresh token. Done here, while the browser is away, it
            // would leave the browser's resume bundle holding a dead token by the time it returns.
            if (session.IsRemembered)
                continue;

            foreach (var account in session.Accounts)
            {
                if (account.NeedsTokenRefresh() && !await account.RefreshTokenAsync(ct))
                {
                    logger.LogWarning("Token refresh failed for an account ({Role}); dropping it.", account.Role);
                    await store.RemoveAccountAsync(session, account.Id);
                }
            }
        }
    }
}
