using System.Text.Json;
using SmarterMailMcp.Core.Auth;
using SmarterMailMcp.Core.Models;

namespace SmarterMail.Tests;

/// <summary>
/// StartupSignIn / AuthResponseClassifier (src/Core/Auth), used by the fixed-account MCP hosts
/// (McpClient, McpAdmin) at startup.
/// </summary>
public sealed class StartupSignInTests : IDisposable
{
    private static readonly Func<int, TimeSpan> Fast = _ => TimeSpan.FromMilliseconds(20);
    private readonly string _tokenFile = Path.Combine(Path.GetTempPath(), $"sma-startup-test-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_tokenFile);

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(7, 60)]
    [InlineData(1000, 60)]
    public void Backoff_doubles_from_two_seconds_and_caps_at_sixty(int attempt, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), StartupSignIn.DelayAfterAttempt(attempt));

    [Theory]
    // Authoritative "no" from a reachable SmarterMail.
    [InlineData(401, """{"success":false,"message":"USERNAME_OR_PASSWORD_INCORRECT"}""", AuthResultKind.Rejected)]
    [InlineData(400, """{"success":false,"message":"USER_NOT_FOUND"}""", AuthResultKind.Rejected)]
    [InlineData(200, """{"success":false,"message":"USERNAME_OR_PASSWORD_INCORRECT"}""", AuthResultKind.Rejected)]
    [InlineData(403, """{"success":false,"message":"ACCOUNT_DISABLED"}""", AuthResultKind.Rejected)]
    [InlineData(403, """{"success":false,"message":"SOMETHING_NEW"}""", AuthResultKind.Rejected)]
    [InlineData(401, """{"success":false}""", AuthResultKind.Rejected)]
    [InlineData(200, """{"success":true,"accessToken":"step","message":"TWO_FACTOR_REQUIRED|rfc6238|a@b.c"}""", AuthResultKind.Rejected)]
    [InlineData(401, """{"success":false,"message":"TWO_FACTOR_REQUIRED|email"}""", AuthResultKind.Rejected)]
    [InlineData(200, """{"success":true,"accessToken":"reset","changePasswordNeeded":true}""", AuthResultKind.Rejected)]
    [InlineData(200, """{"success":true,"accessToken":"reset","passwordExpired":true}""", AuthResultKind.Rejected)]
    // Server trouble, throttling, or an answer we cannot read: retry.
    [InlineData(503, "", AuthResultKind.Transient)]
    [InlineData(502, "<html>Bad Gateway</html>", AuthResultKind.Transient)]
    [InlineData(500, """{"success":false,"message":"USERNAME_OR_PASSWORD_INCORRECT"}""", AuthResultKind.Transient)]
    [InlineData(429, """{"success":false,"message":"TOO_MANY"}""", AuthResultKind.Transient)]
    [InlineData(401, "<html>Unauthorized</html>", AuthResultKind.Transient)]
    [InlineData(404, """{"message":"NOT_FOUND"}""", AuthResultKind.Transient)]
    [InlineData(200, """{"success":true}""", AuthResultKind.Transient)]
    [InlineData(200, """{"success":false,"message":"SOMETHING_NEW"}""", AuthResultKind.Transient)]
    [InlineData(200, "", AuthResultKind.Transient)]
    // A real login.
    [InlineData(200, """{"success":true,"accessToken":"t","refreshToken":"r"}""", AuthResultKind.Success)]
    public void Classifies_authenticate_user_answers(int status, string body, AuthResultKind expected)
    {
        JsonElement parsed = default;
        try { parsed = JsonDocument.Parse(body).RootElement.Clone(); } catch (JsonException) { }

        Assert.Equal(expected, AuthResponseClassifier.Classify(status, parsed).Kind);
    }

    [Fact]
    public async Task Credential_rejection_is_not_retried_and_signals_exit_code_2()
    {
        using var server = new FakeSmarterMail((_, _) => (401, """{"success":false,"message":"USERNAME_OR_PASSWORD_INCORRECT"}"""));

        var result = await SignInAsync(server.BaseUrl, CancellationToken.None);

        Assert.Equal(StartupSignInOutcome.Rejected, result.Outcome);
        Assert.Null(result.UserContext);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, server.AuthenticateCalls);
        Assert.False(File.Exists(_tokenFile));
    }

    [Fact]
    public async Task Two_factor_step_token_is_a_rejection_not_a_login()
    {
        using var server = new FakeSmarterMail((_, _) =>
            (200, """{"success":true,"accessToken":"step","message":"TWO_FACTOR_REQUIRED|rfc6238|a@b.c"}"""));

        var result = await SignInAsync(server.BaseUrl, CancellationToken.None);

        Assert.Equal(StartupSignInOutcome.Rejected, result.Outcome);
        Assert.Equal(1, server.AuthenticateCalls);
        Assert.False(File.Exists(_tokenFile));
    }

    [Fact]
    public async Task Transient_failures_are_retried_until_sign_in_succeeds()
    {
        using var server = new FakeSmarterMail((path, n) => path.EndsWith("/refresh-token")
            ? (200, """{"accessToken":"t2","refreshToken":"r2"}""")
            : n switch
            {
                1 => (503, ""),
                2 => (502, "<html>Bad Gateway</html>"),
                3 => (500, """{"message":"starting"}"""),
                _ => (200, """{"success":true,"accessToken":"t1","refreshToken":"r1"}"""),
            });

        var result = await SignInAsync(server.BaseUrl, CancellationToken.None);

        Assert.Equal(StartupSignInOutcome.SignedIn, result.Outcome);
        Assert.NotNull(result.UserContext);
        Assert.Equal(4, result.Attempts);
        Assert.Equal(4, server.AuthenticateCalls);
        Assert.True(File.Exists(_tokenFile));
        result.UserContext!.Dispose();
    }

    [Fact]
    public async Task Cancellation_during_the_retry_wait_returns_promptly_with_exit_code_0()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;

        // Port 9 (discard) on loopback: connection refused, so every attempt is transient. Real schedule.
        var globalContext = new GlobalContext(_tokenFile, readOnlyMode: true);
        var result = await StartupSignIn.SignInWithRetryAsync(
            globalContext, new AuthenticationService(globalContext), "http://127.0.0.1:9",
            "nobody", "not-a-password", readOnlyMode: true, userType: "user", cts.Token);

        Assert.Equal(StartupSignInOutcome.Cancelled, result.Outcome);
        Assert.Null(result.UserContext);
        Assert.Equal(0, result.ExitCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(_tokenFile));
    }

    private Task<StartupSignInResult> SignInAsync(string baseUrl, CancellationToken ct)
    {
        var globalContext = new GlobalContext(_tokenFile, readOnlyMode: true);
        return StartupSignIn.SignInWithRetryAsync(
            globalContext, new AuthenticationService(globalContext), baseUrl,
            "user@example.com", "not-a-password", readOnlyMode: true, userType: "user", ct, Fast);
    }
}
