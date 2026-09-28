using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.User;

/// <summary>
/// Decides whether the domain_* tools (src/Tools.DomainAdmin) are registered. They 403 for a
/// plain mailbox user, so by default they are only exposed when the signed-in account can read
/// domain settings. SMARTERMAIL_DOMAIN_TOOLS = auto (default) | true | false.
/// </summary>
public static class DomainToolsGate
{
    /// <summary>Same probe the agent uses (SmarterMailAuth.DomainAdminProbePath): 2xx = domain admin.</summary>
    public const string ProbePath = "/api/v1/settings/domain/data";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Returns true when the domain tools should be registered; logs the decision to stderr.</summary>
    public static async Task<bool> ShouldEnableAsync(UserContext userContext)
    {
        var mode = Environment.GetEnvironmentVariable("SMARTERMAIL_DOMAIN_TOOLS")?.Trim().ToLowerInvariant();
        switch (mode)
        {
            case "true":
                Console.Error.WriteLine("Domain-admin tools: enabled (SMARTERMAIL_DOMAIN_TOOLS=true)");
                return true;
            case "false":
                Console.Error.WriteLine("Domain-admin tools: disabled (SMARTERMAIL_DOMAIN_TOOLS=false)");
                return false;
            case null or "" or "auto":
                break;
            default:
                Console.Error.WriteLine($"SMARTERMAIL_DOMAIN_TOOLS='{mode}' is not auto|true|false; using auto.");
                break;
        }

        try
        {
            // UserContext throws SmarterMailApiException for any non-2xx (after its own 401 refresh
            // and retry), so returning normally means a 2xx.
            await userContext.GetBytesAsync(ProbePath).WaitAsync(ProbeTimeout);
            Console.Error.WriteLine("Domain-admin tools: enabled (probe 2xx)");
            return true;
        }
        catch (SmarterMailApiException ex)
        {
            Console.Error.WriteLine($"Domain-admin tools: disabled (probe {(int)ex.StatusCode})");
            return false;
        }
        catch (Exception ex)
        {
            // Network error, timeout, bad URL: never block startup, just leave the tools off.
            Console.Error.WriteLine($"Domain-admin tools: disabled (probe failed: {ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }
}
