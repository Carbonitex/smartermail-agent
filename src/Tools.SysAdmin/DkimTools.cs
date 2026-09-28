using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.SystemAdmin.Tools;

[McpServerToolType]
public sealed class DkimTools
{
    [McpServerTool(ReadOnly = true)]
    [Description("Get DKIM settings for a domain")]
    public static async Task<string> GetDkimSettings(
        [Description("Domain name")] string domain,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/domain-settings/{Uri.EscapeDataString(domain)}");
            // Extract DKIM-related fields from domain settings
            if (response.TryGetProperty("settings", out var settings) && settings.TryGetProperty("dkim", out var dkim))
                return JsonSerializer.Serialize(new { dkim, success = true });
            return JsonSerializer.Serialize(new { success = true, message = "No DKIM settings found in domain settings", raw = response });
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
    [Description("Update DKIM settings for a domain (canonicalization, header fields, max sign size)")]
    public static async Task<string> SetDkimSettings(
        [Description("Domain name")] string domain,
        [Description("Body canonicalization algorithm: 'simple' or 'relaxed' (default: relaxed)")] string canonicalizationBody,
        [Description("Header canonicalization algorithm: 'simple' or 'relaxed' (default: relaxed)")] string canonicalizationHeader,
        [Description("Header field option: 'include' or 'exclude' (default: include)")] string fieldOption,
        [Description("Comma-separated list of header fields to sign (e.g., 'from,to,date,subject,message-id,mime-version,content-type')")] string fields,
        [Description("Maximum message size in bytes to sign (default: 100)")] int maxMessageSign,
        UserContext userContext)
    {
        try
        {
            var fieldList = string.IsNullOrEmpty(fields)
                ? new[] { "from", "to", "date", "subject", "message-id", "mime-version", "content-type" }
                : fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/dkim-settings-set/{Uri.EscapeDataString(domain)}",
                new
                {
                    canonicalizationBody = string.IsNullOrEmpty(canonicalizationBody) ? "relaxed" : canonicalizationBody,
                    canonicalizationHeader = string.IsNullOrEmpty(canonicalizationHeader) ? "relaxed" : canonicalizationHeader,
                    fieldOption = string.IsNullOrEmpty(fieldOption) ? "include" : fieldOption,
                    fields = fieldList,
                    maxMessageSign = maxMessageSign > 0 ? maxMessageSign : 100
                });
            return JsonSerializer.Serialize(response);
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
    [Description("Enable DKIM signing for a domain")]
    public static async Task<string> EnableDkim(
        [Description("Domain name")] string domain,
        [Description("Force activation even if DNS verification fails")] bool forceActivation,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/dkim-enable/{Uri.EscapeDataString(domain)}/{forceActivation.ToString().ToLower()}",
                new { });
            return JsonSerializer.Serialize(response);
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
    [Description("Disable DKIM signing for a domain")]
    public static async Task<string> DisableDkim(
        [Description("Domain name")] string domain,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/dkim-disable/{Uri.EscapeDataString(domain)}", new { });
            return JsonSerializer.Serialize(response);
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
    [Description("Create a DKIM rollover key for key rotation")]
    public static async Task<string> CreateDkimRolloverKey(
        [Description("Domain name")] string domain,
        [Description("Key size in bits (1024, 2048, or 4096)")] int keySize,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/dkim-create-rollover/{Uri.EscapeDataString(domain)}/{keySize}",
                new { });
            return JsonSerializer.Serialize(response);
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
    [Description("Delete a pending DKIM rollover key")]
    public static async Task<string> DeleteDkimRolloverKey(
        [Description("Domain name")] string domain,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/dkim-delete-rollover/{Uri.EscapeDataString(domain)}", new { });
            return JsonSerializer.Serialize(response);
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
