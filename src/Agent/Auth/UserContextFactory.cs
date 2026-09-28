using System.Reflection;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Auth;

/// <summary>
/// Builds a Core <see cref="UserContext"/> from an in-memory <see cref="TokenData"/>.
///
/// Core only offers <c>UserContext.InitializeFromFile(GlobalContext)</c>, which reads the token
/// JSON off disk. This service must never write SmarterMail tokens to disk (they belong to
/// arbitrary members of the public), so we replicate exactly what InitializeFromFile does to the
/// private state instead. The field set mirrors Core commit-for-commit; if Core changes,
/// <see cref="Apply"/> throws at startup-time of the first login rather than failing silently.
/// </summary>
public static class UserContextFactory
{
    private static readonly FieldInfo AccessTokenField = Field("_accessToken");
    private static readonly FieldInfo BaseUrlField = Field("_baseUrl");
    private static readonly FieldInfo IsConnectedField = Field("_isConnected");
    private static readonly FieldInfo HttpClientField = Field("_httpClient");
    private static readonly FieldInfo LastTokenRefreshField = Field("_lastTokenRefresh");

    private static FieldInfo Field(string name) =>
        typeof(UserContext).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            $"smartermail-mcp-core UserContext no longer has a '{name}' field; " +
            "UserContextFactory must be updated to match.");

    public static UserContext Create(GlobalContext globalContext, TokenData tokenData)
    {
        var userContext = new UserContext(globalContext);
        Apply(userContext, tokenData, ownsHttpClient: true);
        return userContext;
    }

    /// <summary>Pushes the current token into an existing UserContext after a refresh.</summary>
    public static void ApplyRefreshedToken(UserContext userContext, TokenData tokenData)
    {
        AccessTokenField.SetValue(userContext, tokenData.AccessToken ?? string.Empty);
        LastTokenRefreshField.SetValue(userContext, DateTime.Now);
    }

    /// <summary>
    /// Re-stamps Core's refresh clock without a refresh. A remembered session refreshes lazily, so
    /// an idle account can pass Core's 10-minute auto-refresh window, whose file-reading path must
    /// never run; the dispatcher stamps the clock before each call when the token is still good.
    /// </summary>
    public static void StampRefreshClock(UserContext userContext) =>
        LastTokenRefreshField.SetValue(userContext, DateTime.Now);

    private static void Apply(UserContext userContext, TokenData tokenData, bool ownsHttpClient)
    {
        var email = tokenData.Username ?? string.Empty;
        userContext.EmailAddress = email;
        userContext.ReadOnlyMode = tokenData.ReadOnlyMode;
        userContext.UserType = tokenData.UserType ?? "user";

        if (email.Contains('@'))
        {
            var parts = email.Split('@');
            userContext.Username = parts[0];
            userContext.Domain = parts[1];
        }
        else
        {
            userContext.Username = email;
            userContext.Domain = string.Empty;
        }

        var baseUrl = tokenData.BaseUrl?.TrimEnd('/') ?? string.Empty;
        AccessTokenField.SetValue(userContext, tokenData.AccessToken ?? string.Empty);
        BaseUrlField.SetValue(userContext, baseUrl);
        IsConnectedField.SetValue(userContext, true);

        // Stamp the refresh clock "now": Core's EnsureTokenFreshAsync only auto-refreshes after
        // 10 idle minutes, and its refresh path reads the (non-existent) token file. SessionSweeper
        // refreshes ahead of that window and re-stamps the clock, so Core's file path never runs.
        LastTokenRefreshField.SetValue(userContext, DateTime.Now);

        if (ownsHttpClient && !string.IsNullOrEmpty(baseUrl))
        {
            var previous = (HttpClient?)HttpClientField.GetValue(userContext);
            // The tools' client: same connect-time SSRF check as sign-in, and no redirects.
            HttpClientField.SetValue(userContext, new HttpClient(GuardedHttp.CreateHandler(), disposeHandler: true)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(100),
            });
            previous?.Dispose();
        }
    }
}
