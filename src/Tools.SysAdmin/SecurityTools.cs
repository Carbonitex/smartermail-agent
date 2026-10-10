using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.SystemAdmin.Tools;

[McpServerToolType]
public sealed class SecurityTools
{
    // --- IP Blocking ---

    [McpServerTool(ReadOnly = true)]
    [Description("Get a list of currently blocked IP addresses")]
    public static async Task<string> GetBlockedIps(
        [Description("Search filter (optional)")] string search,
        [Description("Number of results to return (default 100)")] int count,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/blocked-ips", new
            {
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

    [McpServerTool]
    [Description("Unblock one or more IP addresses")]
    public static async Task<string> UnblockIps(
        [Description("Comma-separated list of IP addresses to unblock")] string ipAddresses,
        UserContext userContext)
    {
        try
        {
            var ips = ipAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ipBlocks = ips.Select(ip => new { ip, blockType = 2 }).ToList();
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/unblock-ips", new
            {
                ipBlocks
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

    // --- IP Access Rules ---

    [McpServerTool(ReadOnly = true)]
    [Description("Get IP access rules (whitelist or blacklist)")]
    public static async Task<string> GetIpAccessRules(
        [Description("True for whitelist, false for blacklist")] bool isWhitelist,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/ip-access/{isWhitelist.ToString().ToLower()}", new
                {
                    searchParams = new { Skip = 0, Take = 1000, Search = "" }
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
    [Description("Add an IP address to the access whitelist or blacklist")]
    public static async Task<string> AddIpAccessRule(
        [Description("IP address to add")] string address,
        [Description("True for whitelist, false for blacklist")] bool isWhitelist,
        [Description("Description/reason for the rule")] string description,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/ip-access", new
            {
                serviceList = new[] { "Smtp", "Imap", "Pop", "WebMail", "ActiveSync", "MapiEws" },
                dataType = isWhitelist ? 0 : 1,
                address,
                description = description ?? ""
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
    [Description("Delete an IP access rule")]
    public static async Task<string> DeleteIpAccessRule(
        [Description("IP address of the rule to delete")] string address,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/ip-access-delete", new
            {
                address
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

    // --- SMTP Rules ---

    [McpServerTool(ReadOnly = true)]
    [Description("Get SMTP blocking rules")]
    public static async Task<string> GetSmtpBlockRules(UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/sysadmin/smtp-block-rules");
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
    [Description("Get the SMTP authentication bypass list (addresses allowed to send without authentication)")]
    public static async Task<string> GetSmtpAuthBypass(UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/sysadmin/smtp-auth-bypass");
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

    // --- Spam Configuration ---

    [McpServerTool(ReadOnly = true)]
    [Description("Get SpamAssassin server configuration")]
    public static async Task<string> GetSpamAssassinServers(UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/sysadmin/spam-assassin-servers");
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
    [Description("Get Rspamd server configuration")]
    public static async Task<string> GetRspamdServers(UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/sysadmin/rspamd-servers");
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
