using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.SystemAdmin.Tools;

[McpServerToolType]
public sealed class DomainTools
{
    [McpServerTool(ReadOnly = true)]
    [Description("List all domains on the server")]
    public static async Task<string> GetDomains(UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/sysadmin/domains");
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
    [Description("Get detailed information about a specific domain")]
    public static async Task<string> GetDomainInfo(
        [Description("Domain name (e.g., example.com)")] string domain,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>($"/api/v1/settings/sysadmin/domain/{Uri.EscapeDataString(domain)}");
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
    [Description("Create a new domain on the server")]
    public static async Task<string> CreateDomain(
        [Description("Domain name (e.g., example.com)")] string domainName,
        [Description("File system path for domain storage")] string path,
        [Description("Hostname for the domain")] string hostname,
        [Description("Maximum number of users allowed (0 for unlimited)")] int userLimit,
        [Description("Maximum domain storage size in MB (0 for unlimited)")] int maxSize,
        [Description("Domain admin username (optional)")] string adminUsername,
        [Description("Domain admin password (optional)")] string adminPassword,
        UserContext userContext)
    {
        try
        {
            var payload = new
            {
                domainData = new
                {
                    name = domainName,
                    path,
                    hostname,
                    userLimit,
                    maxSize
                },
                adminUsername,
                adminPassword
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/domain-put", payload);
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
    [Description("Delete a domain from the server")]
    public static async Task<string> DeleteDomain(
        [Description("Domain name to delete")] string domain,
        [Description("Whether to delete associated files from disk")] bool deleteFiles,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/domain-delete/{Uri.EscapeDataString(domain)}/{deleteFiles.ToString().ToLower()}",
                new { });
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
    [Description("Rename a domain")]
    public static async Task<string> RenameDomain(
        [Description("Current domain name")] string oldDomainName,
        [Description("New domain name")] string newDomainName,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/rename-domain", new
            {
                oldDomainName,
                newDomainName
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
    [Description("Get the settings/configuration for a specific domain")]
    public static async Task<string> GetDomainSettings(
        [Description("Domain name")] string domain,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>($"/api/v1/settings/sysadmin/domain-settings/{Uri.EscapeDataString(domain)}");
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
    [Description("Reload a domain's configuration from disk")]
    public static async Task<string> ReloadDomain(
        [Description("Domain name to reload")] string domain,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/reload-domain/{Uri.EscapeDataString(domain)}", new { });
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
