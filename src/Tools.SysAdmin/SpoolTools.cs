using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.SystemAdmin.Tools;

[McpServerToolType]
public sealed class SpoolTools
{
    [McpServerTool(ReadOnly = true)]
    [Description("Get the total count of messages in the spool")]
    public static async Task<string> GetSpoolMessageCount(
        [Description("Whether to reload the count from disk")] bool reload,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/spool-message-count/{reload.ToString().ToLower()}");
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
    [Description("Get detailed spool message counts broken down by type (pending, badmail, quarantine, etc.)")]
    public static async Task<string> GetSpoolMessageCounts(UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/sysadmin/spool-message-counts");
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
    [Description("List spool messages with optional filtering")]
    public static async Task<string> GetSpoolMessages(
        [Description("Spool type: Pending, Badmail, or Pickup")] string type,
        [Description("Search filter (optional)")] string search,
        [Description("Number of messages to return (default 50)")] int count,
        [Description("Start index for pagination (default 0)")] int startIndex,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/spool-messages", new
            {
                spoolInput = new[]
                {
                    new
                    {
                        spoolName = type ?? "Pending",
                        search = search ?? "",
                        count = count > 0 ? count : 50,
                        startIndex = startIndex >= 0 ? startIndex : 0,
                        ascending = false
                    }
                }
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
    [Description("Delete a single message from the spool")]
    public static async Task<string> DeleteSpoolMessage(
        [Description("Spool message filename")] string fileName,
        [Description("Spool name/type the message is in")] string spoolName,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/spool-delete-message", new
            {
                spoolInput = new[]
                {
                    new { fileName, spoolName }
                }
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
    [Description("Delete multiple messages from the spool")]
    public static async Task<string> DeleteSpoolMessages(
        [Description("Comma-separated list of spool message filenames")] string fileNames,
        [Description("Spool name/type the messages are in")] string spoolName,
        UserContext userContext)
    {
        try
        {
            var files = fileNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var spoolInput = files.Select(f => new { fileName = f, spoolName }).ToArray();
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/spool-delete-messages", new
            {
                spoolInput
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
    [Description("Reset (retry) spool messages for delivery")]
    public static async Task<string> ResetSpoolMessages(
        [Description("Comma-separated list of spool message filenames to reset")] string fileNames,
        [Description("Spool name/type the messages are in")] string spoolName,
        UserContext userContext)
    {
        try
        {
            var files = fileNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var spoolInput = files.Select(f => new { fileName = f, spoolName }).ToArray();
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/reset-spool-messages", new
            {
                spoolInput
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
    [Description("Reset all spool messages for redelivery")]
    public static async Task<string> ResetAllSpoolMessages(UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/reset-all-spool-messages", new { });
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
