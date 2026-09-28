using System.Runtime.InteropServices;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Core.Auth;

/// <summary>
/// Startup sign-in for the fixed-account MCP hosts (src/McpUser, src/McpAdmin, via src/Mcp.Hosting). SmarterMail may
/// still be booting when the container starts, so a <b>transient</b> failure (no connection, timeout,
/// 5xx, non-JSON answer...) is retried with capped exponential backoff until it succeeds or the process
/// is asked to stop. A <b>rejection</b> (SmarterMail answered and refused the account) is not retried:
/// repeated failed logins can get the source IP blocked, and in Docker that is often a bridge or NAT
/// address shared by other containers. Classification: <see cref="AuthResponseClassifier"/>.
/// Configuration checks stay in the hosts: missing settings exit immediately.
/// </summary>
public static class StartupSignIn
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>Process exit code when SmarterMail rejected the credentials.</summary>
    public const int CredentialsRejectedExitCode = 2;

    /// <summary>Delay after failed attempt <paramref name="attempt"/> (1-based): 2s, 4s, 8s, ... capped at 60s.</summary>
    public static TimeSpan DelayAfterAttempt(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var seconds = InitialDelay.TotalSeconds * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }

    /// <summary>
    /// Signs in and initializes a <see cref="UserContext"/> from the saved token. Transient failures
    /// retry forever; a rejection or <paramref name="cancellationToken"/> (shutdown) stops with
    /// <see cref="StartupSignInResult.UserContext"/> null. The password is never logged.
    /// </summary>
    /// <param name="delaySchedule">Test hook; defaults to <see cref="DelayAfterAttempt"/>.</param>
    public static async Task<StartupSignInResult> SignInWithRetryAsync(
        GlobalContext globalContext,
        AuthenticationService authService,
        string baseUrl,
        string username,
        string password,
        bool readOnlyMode,
        string userType,
        CancellationToken cancellationToken,
        Func<int, TimeSpan>? delaySchedule = null)
    {
        delaySchedule ??= DelayAfterAttempt;

        for (var attempt = 1; ; attempt++)
        {
            string failure;
            try
            {
                var result = await authService.AuthenticateWithResultAsync(
                    baseUrl, username, password, readOnlyMode, userType, cancellationToken);

                if (result.Kind == AuthResultKind.Rejected)
                {
                    Console.Error.WriteLine(
                        $"Sign-in attempt {attempt}: {result.Message}. Not retrying: the credentials or account " +
                        $"need fixing, and repeated failed logins can get this IP blocked. Exiting with code " +
                        $"{CredentialsRejectedExitCode}.");
                    return new StartupSignInResult(null, StartupSignInOutcome.Rejected, attempt);
                }

                if (result.Kind == AuthResultKind.Success)
                {
                    var userContext = new UserContext(globalContext);
                    userContext.InitializeFromFile(globalContext);
                    userContext.SetAuthenticationService(authService);

                    // The token from sign-in is already usable; a false here only means the immediate
                    // refresh (and its re-auth fallback) failed, which later calls retry.
                    if (!await userContext.RefreshTokenAsync().WaitAsync(cancellationToken))
                        Console.Error.WriteLine("Warning: initial token refresh failed; continuing with the token from sign-in.");

                    return new StartupSignInResult(userContext, StartupSignInOutcome.SignedIn, attempt);
                }

                failure = result.Message;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Shutdown requested during sign-in; exiting.");
                return new StartupSignInResult(null, StartupSignInOutcome.Cancelled, attempt);
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
            }

            var delay = delaySchedule(attempt);
            Console.Error.WriteLine(
                $"Sign-in attempt {attempt} failed: {Shorten(failure)} Retrying in {delay.TotalSeconds:0.#}s.");

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Shutdown requested while waiting to retry sign-in; exiting.");
                return new StartupSignInResult(null, StartupSignInOutcome.Cancelled, attempt);
            }
        }
    }

    // Keep one readable log line whatever the failure text looks like.
    private static string Shorten(string message)
    {
        var line = message.ReplaceLineEndings(" ").Trim().TrimEnd('.');
        return (line.Length > 300 ? line[..300] + "..." : line) + ".";
    }
}

public enum StartupSignInOutcome { SignedIn, Rejected, Cancelled }

/// <param name="UserContext">Set only when <paramref name="Outcome"/> is SignedIn.</param>
/// <param name="Attempts">Sign-in attempts made, including the last.</param>
public sealed record StartupSignInResult(UserContext? UserContext, StartupSignInOutcome Outcome, int Attempts)
{
    /// <summary>
    /// What the host should exit with when <see cref="UserContext"/> is null: 2 for rejected
    /// credentials, 0 for a requested shutdown.
    /// </summary>
    public int ExitCode => Outcome == StartupSignInOutcome.Rejected ? StartupSignIn.CredentialsRejectedExitCode : 0;
}

/// <summary>
/// Cancels <see cref="Token"/> on SIGINT (Ctrl+C) or SIGTERM (docker stop) and suppresses the
/// default termination so the caller can return cleanly. Dispose it before starting the web host
/// so ASP.NET's own shutdown handling takes over.
/// </summary>
public sealed class StartupShutdownSignal : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly PosixSignalRegistration[] _registrations;

    public StartupShutdownSignal()
    {
        _registrations =
        [
            PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal),
        ];
    }

    public CancellationToken Token => _cts.Token;

    private void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        _cts.Cancel();
    }

    public void Dispose()
    {
        foreach (var registration in _registrations)
            registration.Dispose();
        _cts.Dispose();
    }
}
