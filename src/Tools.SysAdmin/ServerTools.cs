using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.SystemAdmin.Tools;

[McpServerToolType]
public sealed class ServerTools
{
    [McpServerTool(ReadOnly = true)]
    [Description("Get the SmarterMail server version")]
    public static async Task<string> GetServerVersion(UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/sysadmin/get-version");
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
    [Description("Get the status of all server services (SMTP, IMAP, POP, etc.)")]
    public static async Task<string> GetServices(UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/sysadmin/services");
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
    [Description("Start one or more server services")]
    public static async Task<string> StartServices(
        [Description("Comma-separated list of service names to start")] string services,
        UserContext userContext)
    {
        try
        {
            var serviceList = services.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/start-services",
                new { input = serviceList });
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
    [Description("Stop one or more server services")]
    public static async Task<string> StopServices(
        [Description("Comma-separated list of service names to stop")] string services,
        UserContext userContext)
    {
        try
        {
            var serviceList = services.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/stop-services",
                new { input = serviceList });
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
    [Description("Get server dashboard statistics including message counts, storage usage, and active connections")]
    public static async Task<string> GetDashboardStats(UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/sysadmin/dashboard-stats");
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
    [Description("Get troubleshooting counts for diagnostics")]
    public static async Task<string> GetTroubleshootingCounts(UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/sysadmin/troubleshooting-counts");
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
