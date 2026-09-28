using System.Collections.Concurrent;

namespace SmarterMailAgent.Auth;

/// <summary>
/// One in-flight two-factor challenge: the state between a correct password and a correct code.
/// Lives in memory for at most <see cref="PendingLoginStore.Ttl"/> and is destroyed the moment it
/// is used, expires, or runs out of attempts.
/// </summary>
public sealed class PendingLogin
{
    public required string Id { get; init; }
    public required string BaseUrl { get; init; }
    public required string Username { get; init; }
    public required string ClientId { get; init; }
    public required string Method { get; init; }
    public required string EmailAddress { get; init; }
    public required bool ReadOnly { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Set when the challenge came from <c>POST /api/accounts</c>: completing it adds the account to
    /// this session, and only a request carrying this same session may complete it. Null for a
    /// login that will open a new session.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>The 10-minute AuthStep JWT, when the server offered one. Null on the legacy path.</summary>
    public string? StepToken { get; init; }

    /// <summary>
    /// Legacy path only: older SmarterMail builds have no step token, so the password has to be
    /// replayed alongside the code. Held as a char[] so <see cref="Erase"/> can overwrite it rather
    /// than leave an immutable string for the GC. Null whenever <see cref="StepToken"/> is set.
    /// </summary>
    private char[]? _password;

    internal void SetPassword(string? password) =>
        _password = string.IsNullOrEmpty(password) ? null : password.ToCharArray();

    internal string? TakePasswordCopy() => _password is null ? null : new string(_password);

    /// <summary>Overwrites the stored password in place. Called on success, expiry and exhaustion.</summary>
    internal void Erase()
    {
        if (_password is null) return;
        Array.Clear(_password);
        _password = null;
    }

    public int Attempts;

    public DateTimeOffset ExpiresAt => CreatedAt + PendingLoginStore.Ttl;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}

/// <summary>
/// The challenges issued by <c>POST /api/auth/login</c> and <c>POST /api/accounts</c> when SmarterMail asks for a second factor.
/// Nothing here is written to disk; a challenge is single-use and short-lived.
/// </summary>
public sealed class PendingLoginStore
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    public const int MaxAttempts = 5;

    private readonly ConcurrentDictionary<string, PendingLogin> _pending = new(StringComparer.Ordinal);

    public int Count => _pending.Count;

    public PendingLogin Create(
        string baseUrl, string username, bool readOnly, string clientId,
        string method, string emailAddress, string? stepToken, string? password, string? sessionId = null)
    {
        var login = new PendingLogin
        {
            Id = SessionStore.NewSessionId(),   // 32 random bytes, base64url
            BaseUrl = baseUrl,
            Username = username,
            ReadOnly = readOnly,
            ClientId = clientId,
            Method = method,
            EmailAddress = emailAddress,
            StepToken = stepToken,
            SessionId = sessionId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // The password is only ever kept for the legacy inline path, where there is no step token.
        if (string.IsNullOrEmpty(stepToken))
            login.SetPassword(password);

        _pending[login.Id] = login;
        return login;
    }

    /// <summary>Returns the live challenge, or null if it is unknown, consumed, or expired.</summary>
    public PendingLogin? Get(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_pending.TryGetValue(id, out var login))
            return null;

        if (login.IsExpired(DateTimeOffset.UtcNow))
        {
            Remove(id);
            return null;
        }

        return login;
    }

    /// <summary>Removes and erases the challenge. Idempotent.</summary>
    public void Remove(string? id)
    {
        if (!string.IsNullOrEmpty(id) && _pending.TryRemove(id, out var login))
            login.Erase();
    }

    /// <summary>
    /// Counts one rejected code. Returns the attempts left; at zero the challenge has been removed
    /// and the next request gets CHALLENGE_EXPIRED.
    /// </summary>
    public int RecordFailure(PendingLogin login)
    {
        var attempts = Interlocked.Increment(ref login.Attempts);
        var left = Math.Max(0, MaxAttempts - attempts);
        if (left == 0)
            Remove(login.Id);
        return left;
    }

    /// <summary>Drops (and erases) everything past its TTL. Called by <c>SessionSweeper</c>.</summary>
    public int Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        var dropped = 0;
        foreach (var login in _pending.Values)
        {
            if (login.IsExpired(now))
            {
                Remove(login.Id);
                dropped++;
            }
        }

        return dropped;
    }
}
