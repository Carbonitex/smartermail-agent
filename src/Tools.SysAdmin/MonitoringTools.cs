using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.SystemAdmin.Tools;

[McpServerToolType]
public sealed class MonitoringTools
{
    // --- Throttling ---

    [McpServerTool(ReadOnly = true)]
    [Description("Get a list of currently throttled users")]
    public static async Task<string> GetThrottledUsers(UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/sysadmin/throttled-users");
            return JsonSerializer.Serialize(SecretRedactor.Redact(response));
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(ReadOnly = true)]
    [Description("Get a list of currently throttled domains")]
    public static async Task<string> GetThrottledDomains(UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/sysadmin/throttled-domains");
            return JsonSerializer.Serialize(SecretRedactor.Redact(response));
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(ReadOnly = true)]
    [Description("Get throttling statistics counts")]
    public static async Task<string> GetThrottledCounts(UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/sysadmin/throttled-counts");
            return JsonSerializer.Serialize(SecretRedactor.Redact(response));
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Reset throttling for a specific user")]
    public static async Task<string> ResetThrottledUser(
        [Description("Email address of the user to un-throttle")] string email,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/throttled-user-reset", new
            {
                input = new List<string> { email }
            });
            return JsonSerializer.Serialize(SecretRedactor.Redact(response));
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Reset throttling for a specific domain")]
    public static async Task<string> ResetThrottledDomain(
        [Description("Domain name to un-throttle")] string domain,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/throttled-domain-reset", new
            {
                input = new List<string> { domain }
            });
            return JsonSerializer.Serialize(SecretRedactor.Redact(response));
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    // --- Connections ---

    [McpServerTool(ReadOnly = true)]
    [Description("Get active server connections with optional filtering")]
    public static async Task<string> GetConnections(
        [Description("Search filter (optional)")] string search,
        [Description("Number of results to return (default 100)")] int count,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/connections", new
            {
                serviceTypes = new[] { 0, 1, 2, 4, 7, 8, 9, 10, 12 },
                search = search ?? "",
                ascending = true,
                startindex = 0,
                count = count > 0 ? count : 100
            });
            return JsonSerializer.Serialize(SecretRedactor.Redact(response));
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(ReadOnly = true)]
    [Description("Get the total count of active server connections")]
    public static async Task<string> GetConnectionsCount(UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/connections-count", new
            {
                serviceTypes = new[] { 0, 1, 2, 4, 7, 8, 9, 10, 12 }
            });
            return JsonSerializer.Serialize(SecretRedactor.Redact(response));
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(Destructive = true)]
    [Description("Drop all connections for a specific user")]
    public static async Task<string> DropUserConnections(
        [Description("Username or email address")] string username,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/drop-user-connections", new
            {
                email = username,
                dropInfo = new List<string> { username }
            });
            return JsonSerializer.Serialize(SecretRedactor.Redact(response));
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(Destructive = true)]
    [Description("Drop all connections from a specific IP address")]
    public static async Task<string> DropIpConnections(
        [Description("IP address to disconnect")] string ip,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/drop-ip-connections/{Uri.EscapeDataString(ip)}", new { });
            return JsonSerializer.Serialize(SecretRedactor.Redact(response));
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(Destructive = true)]
    [Description("Kill all active sessions for a specific user")]
    public static async Task<string> KillUserSessions(
        [Description("Username or email address")] string username,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/kill-user-sessions", new
            {
                sessions = new[] { new { email = username, ip = "" } }
            });
            return JsonSerializer.Serialize(SecretRedactor.Redact(response));
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }
}
