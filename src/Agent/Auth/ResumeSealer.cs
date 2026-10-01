using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmarterMailAgent.Auth;

/// <summary>
/// One account inside a resume bundle: everything needed to rebuild it exactly as a fresh sign-in
/// would, minus the access token (a resume always refreshes, which also rotates the refresh token).
/// </summary>
public sealed record ResumeAccount(
    string BaseUrl,
    string Login,           // TokenData.Username: the login as typed (email, or the sysadmin name)
    AccountRole Role,
    bool ReadOnly,
    string ClientId,
    string RefreshToken,
    string? RefreshExpiration,
    string? UserType);

/// <summary>The sealed JSON. <see cref="RememberedSince"/> is the original sign-in, kept across resumes.</summary>
public sealed record ResumePayload(
    int Version,
    DateTimeOffset RememberedSince,
    DateTimeOffset IssuedAt,
    IReadOnlyList<ResumeAccount> Accounts);

/// <summary>
/// Seals and opens the "remember me on this device" bundle. The server keeps nothing per user: the
/// browser holds the bundle, AES-256-GCM sealed under <c>RESUME_KEY</c>, and presents it to
/// <c>POST /api/auth/resume</c> after a restart or an expired session.
/// <para>
/// Wire format, base64url: <c>[format 0x01][key id][nonce 12][ciphertext][tag 16]</c>. The key id
/// is the first byte of SHA-256(key), so <c>RESUME_KEY_PREVIOUS</c> can keep opening bundles sealed
/// before a rotation (new bundles always use the current key). The associated data binds the fixed
/// label <c>sma-resume-v1</c> and the two header bytes.
/// </para>
/// Never log a bundle, and never log why one failed beyond <see cref="UnsealFailure"/>.
/// </summary>
public sealed class ResumeSealer
{
    public const int DefaultDays = 30;

    /// <summary>SmarterMail refresh tokens live 60 days; a chain cannot usefully outlast one.</summary>
    public const int MaxDays = 60;

    /// <summary>Longer than any real bundle (five accounts are a few KB); refused before decoding.</summary>
    public const int MaxBundleLength = 32 * 1024;

    private const string Label = "sma-resume-v1";

    /// <summary>Tolerated clock skew for a bundle that claims to come from the future.</summary>
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Sealer? _sealer;

    public ResumeSealer(byte[]? key, byte[]? previousKey = null, int days = DefaultDays)
    {
        if (key is { Length: not 32 })
            throw new ArgumentException("RESUME_KEY must be 32 bytes.", nameof(key));
        if (previousKey is { Length: not 32 })
            throw new ArgumentException("RESUME_KEY_PREVIOUS must be 32 bytes.", nameof(previousKey));

        _sealer = key is null ? null : new Sealer(key, previousKey);
        MaxAge = TimeSpan.FromDays(Math.Clamp(days, 1, MaxDays));
    }

    /// <summary>
    /// <c>RESUME_KEY</c> (base64 or base64url, 32 bytes) turns the feature on; unset or invalid
    /// leaves it off. <c>RESUME_KEY_PREVIOUS</c> is only used to open bundles, and an invalid one is
    /// ignored. <c>RESUME_DAYS</c> (default 30, at most 60) is the chain's absolute lifetime.
    /// Never logs a key.
    /// </summary>
    public static ResumeSealer FromEnvironment(ILogger? logger = null)
    {
        var raw = Environment.GetEnvironmentVariable("RESUME_KEY");
        var key = DecodeKey(raw);
        if (!string.IsNullOrWhiteSpace(raw) && key is null)
            logger?.LogWarning("RESUME_KEY is not 32 bytes of base64; remember-me is disabled.");

        var rawPrevious = Environment.GetEnvironmentVariable("RESUME_KEY_PREVIOUS");
        var previous = key is null ? null : DecodeKey(rawPrevious);
        if (key is not null && !string.IsNullOrWhiteSpace(rawPrevious) && previous is null)
            logger?.LogWarning("RESUME_KEY_PREVIOUS is not 32 bytes of base64; ignored.");

        var days = int.TryParse(Environment.GetEnvironmentVariable("RESUME_DAYS"), out var d) && d > 0
            ? d
            : DefaultDays;

        var sealer = new ResumeSealer(key, previous, days);
        logger?.LogInformation("Remember-me {State}.", sealer.Enabled
            ? $"enabled ({(int)sealer.MaxAge.TotalDays} days{(previous is null ? "" : ", previous key accepted")})"
            : "disabled (no RESUME_KEY)");
        return sealer;
    }

    public static byte[]? DecodeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var bytes = Base64Url.Decode(value);
        return bytes is { Length: 32 } ? bytes : null;
    }

    public bool Enabled => _sealer is not null;

    /// <summary>The remember chain's absolute lifetime from the original sign-in (<c>RESUME_DAYS</c>).</summary>
    public TimeSpan MaxAge { get; }

    public enum UnsealFailure
    {
        /// <summary>Not base64url, too short or too long, or an unknown format byte.</summary>
        Malformed,

        /// <summary>Sealed under a key this process does not have (rotated away, or another server).</summary>
        UnknownKey,

        /// <summary>The GCM tag did not verify: tampered with, or a key-id collision with a foreign key.</summary>
        Tampered,

        /// <summary>Decrypted, but not a payload this version understands.</summary>
        BadPayload,

        /// <summary>Genuine, but the chain is past <see cref="MaxAge"/> (or claims to start in the future).</summary>
        Expired,
    }

    public sealed record UnsealResult(ResumePayload? Payload, UnsealFailure? Failure)
    {
        public bool Ok => Payload is not null;
    }

    /// <summary>
    /// The bundle for <paramref name="session"/>'s current accounts, and the resume version it
    /// matches; null when the feature is off or the session is not (or no longer) remembered.
    /// The version is read before the tokens, so a rotation racing this call leaves the browser
    /// with an older version number and it simply fetches again.
    /// </summary>
    public (string Bundle, long Version)? Seal(Session session)
    {
        if (!Enabled || !session.IsRemembered || session.RememberedSince is not { } since)
            return null;

        var version = session.ResumeVersion;
        var accounts = session.Accounts
            .Where(a => !a.IsDisposed && !string.IsNullOrEmpty(a.TokenData.RefreshToken) && !string.IsNullOrEmpty(a.ClientId))
            .Select(a => new ResumeAccount(
                a.BaseUrl, a.EmailAddress, a.Role, a.ReadOnly, a.ClientId!, a.TokenData.RefreshToken!,
                a.TokenData.RefreshExpiration, a.TokenData.UserType))
            .ToList();

        return (SealPayload(new ResumePayload(1, since, DateTimeOffset.UtcNow, accounts)), version);
    }

    /// <summary>Seals an arbitrary payload under the current key. Tests use it to build odd bundles.</summary>
    internal string SealPayload(ResumePayload payload)
    {
        if (_sealer is null)
            throw new InvalidOperationException("Remember-me is disabled (no RESUME_KEY).");

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        try
        {
            return _sealer.SealString(plaintext, Label);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Opens and validates a bundle. Accounts past their own refresh expiry are kept; the caller skips them.</summary>
    public UnsealResult Unseal(string? bundle)
    {
        if (_sealer is null || string.IsNullOrEmpty(bundle) || bundle.Length > MaxBundleLength)
            return Fail(UnsealFailure.Malformed);

        var data = Base64Url.Decode(bundle);
        if (data is null || data.Length < Sealer.HeaderSize + Sealer.NonceSize + Sealer.TagSize + 2)
            return Fail(UnsealFailure.Malformed);

        var plaintext = _sealer.Open(data, Label, out var failure);
        if (plaintext is null)
        {
            return Fail(failure switch
            {
                Sealer.OpenFailure.UnknownKey => UnsealFailure.UnknownKey,
                Sealer.OpenFailure.Tampered => UnsealFailure.Tampered,
                _ => UnsealFailure.Malformed,
            });
        }

        ResumePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ResumePayload>(plaintext, Json);
        }
        catch (JsonException)
        {
            payload = null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        if (payload is null || payload.Version != 1 || payload.Accounts is null || payload.Accounts.Count == 0 ||
            payload.Accounts.Any(a => a is null || string.IsNullOrEmpty(a.BaseUrl) || string.IsNullOrEmpty(a.Login) ||
                                      string.IsNullOrEmpty(a.ClientId) || string.IsNullOrEmpty(a.RefreshToken)))
            return Fail(UnsealFailure.BadPayload);

        var now = DateTimeOffset.UtcNow;
        if (payload.RememberedSince > now + Skew || now - payload.RememberedSince > MaxAge)
            return Fail(UnsealFailure.Expired);

        return new UnsealResult(payload, null);
    }

    /// <summary>When a chain that began at <paramref name="since"/> stops being resumable.</summary>
    public DateTimeOffset UntilFor(DateTimeOffset since) => since + MaxAge;

    /// <summary>
    /// True when SmarterMail's own refresh expiry for this entry has passed, so asking it would
    /// only earn a failed attempt against our IP. Unparseable or missing means "ask".
    /// </summary>
    public static bool RefreshExpired(ResumeAccount account) =>
        DateTimeOffset.TryParse(account.RefreshExpiration, out var expiration) && expiration <= DateTimeOffset.UtcNow;

    private static UnsealResult Fail(UnsealFailure failure) => new(null, failure);
}
