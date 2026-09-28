using System.Text.Json;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Core.Auth;

public enum AuthResultKind
{
    /// <summary>Signed in; <see cref="AuthenticationResult.TokenData"/> is set and saved to the token file.</summary>
    Success,

    /// <summary>
    /// A reachable SmarterMail gave an authoritative "no" for this account: wrong username or password,
    /// unknown/disabled/locked account, two-factor or a password change required. Retrying will not help
    /// and repeated failed logins can get the source IP blocked.
    /// </summary>
    Rejected,

    /// <summary>Anything else: no connection, timeout, 5xx/408/429, non-JSON or malformed answer. Worth retrying.</summary>
    Transient,
}

/// <param name="Message">Safe to log: never contains the password or the raw response body.</param>
/// <param name="Code">SmarterMail's message code (first pipe segment only) or <c>HTTP_&lt;status&gt;</c>.</param>
public sealed record AuthenticationResult(
    AuthResultKind Kind, string Message, TokenData? TokenData = null, int? HttpStatus = null, string? Code = null)
{
    public bool Success => Kind == AuthResultKind.Success;
}

/// <summary>
/// Classifies SmarterMail's <c>/api/v1/auth/authenticate-user</c> answer. SmarterMail puts the reason in
/// the JSON body on 4xx as well as on 200 (see src/Agent/Auth/SmarterMailAuth.cs, which parses bodies the
/// same way), and a two-factor or password-change account answers <b>200 with an AuthStep token</b> that is
/// useless for the API. Ambiguous answers are <see cref="AuthResultKind.Transient"/>.
/// </summary>
public static class AuthResponseClassifier
{
    private static readonly string[] RejectionCodes =
    [
        "USERNAME_OR_PASSWORD_INCORRECT", "USER_NOT_FOUND", "INVALID_TWO_FACTOR_CODE", "TWO_FACTOR_REQUIRED",
        "CHANGE_PASSWORD_NEEDED", "PASSWORD_EXPIRED", "APP_PASSWORD_REQUIRED", "TWO_FACTOR_SETUP_REQUIRED",
    ];

    // Substrings that only ever describe an account or source the server has refused.
    private static readonly string[] RejectionMarkers = ["DISABLED", "LOCKED", "BLOCKED", "SUSPENDED"];

    /// <param name="body">The parsed body, or <c>default</c> when it was empty or not JSON.</param>
    public static (AuthResultKind Kind, string Code) Classify(int status, JsonElement body)
    {
        var httpCode = $"HTTP_{status}";

        // Server trouble (SmarterMail or its IIS still starting), timeouts and throttling: retry.
        if (status >= 500 || status is 408 or 429)
            return (AuthResultKind.Transient, httpCode);

        // Not JSON (a proxy error page, say): we do not know what answered, so retry.
        if (body.ValueKind != JsonValueKind.Object)
            return (AuthResultKind.Transient, httpCode);

        var message = Str(body, "message") ?? string.Empty;
        var code = message.Length > 0 ? message.Split('|')[0] : string.Empty;
        var successFlag = Bool(body, "success");

        // These arrive as 200 + success:true with an AuthStep token that every real endpoint refuses.
        if (code == "TWO_FACTOR_REQUIRED")
            return (AuthResultKind.Rejected, code);
        if (Bool(body, "changePasswordNeeded") == true)
            return (AuthResultKind.Rejected, "CHANGE_PASSWORD_NEEDED");
        if (Bool(body, "passwordExpired") == true)
            return (AuthResultKind.Rejected, "PASSWORD_EXPIRED");

        if (status is >= 200 and < 300 && successFlag != false && !string.IsNullOrEmpty(Str(body, "accessToken")))
            return (AuthResultKind.Success, code.Length > 0 ? code : httpCode);

        if (code.Length > 0 && IsRejectionCode(code))
            return (AuthResultKind.Rejected, code);

        // A JSON 401/403 that is not a success is SmarterMail itself refusing the login.
        if (status is 401 or 403 && successFlag != true)
            return (AuthResultKind.Rejected, code.Length > 0 ? code : httpCode);

        // 2xx without a usable token, 400/404 with an unknown code, etc.: ambiguous, so retry.
        return (AuthResultKind.Transient, code.Length > 0 ? code : httpCode);
    }

    private static bool IsRejectionCode(string code) =>
        RejectionCodes.Contains(code, StringComparer.OrdinalIgnoreCase)
        || RejectionMarkers.Any(m => code.Contains(m, StringComparison.OrdinalIgnoreCase));

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}
