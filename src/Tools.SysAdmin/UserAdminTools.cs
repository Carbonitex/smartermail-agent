using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.SystemAdmin.Tools;

[McpServerToolType]
public sealed class UserAdminTools
{
    [McpServerTool(ReadOnly = true)]
    [Description("List all users in a specific domain")]
    public static async Task<string> ListUsers(
        [Description("Domain name (e.g., example.com)")] string domain,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>($"/api/v1/settings/sysadmin/list-users/{Uri.EscapeDataString(domain)}");
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

    [McpServerTool(ReadOnly = true)]
    [Description("Search for users within a domain")]
    public static async Task<string> SearchUsers(
        [Description("Domain name")] string domain,
        [Description("Search query string")] string search,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/search-users/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(search)}");
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
    [Description("Delete users from a domain. Provide a comma-separated list of usernames or email addresses.")]
    public static async Task<string> DeleteUsers(
        [Description("Domain name")] string domain,
        [Description("Comma-separated list of usernames or email addresses to delete")] string users,
        UserContext userContext)
    {
        try
        {
            var userList = users.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/users-delete/{Uri.EscapeDataString(domain)}",
                new { input = userList });
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
    [Description("Disable or enable users in a domain")]
    public static async Task<string> DisableUsers(
        [Description("Domain name")] string domain,
        [Description("Comma-separated list of usernames or email addresses")] string users,
        [Description("True to disable users, false to enable them")] bool disable,
        UserContext userContext)
    {
        try
        {
            var userList = users.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/users-disable/{disable.ToString().ToLower()}/{Uri.EscapeDataString(domain)}",
                new { input = userList });
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

    [McpServerTool(ReadOnly = true)]
    [Description("Get a list of inactive users across all domains")]
    public static async Task<string> GetInactiveUsers(
        [Description("Cutoff date - users inactive since this date (YYYY-MM-DD)")] string cutoffDate,
        [Description("Maximum number of results to return")] int count,
        [Description("Search filter (optional)")] string search,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/inactive-users", new
            {
                cutoffDate,
                startindex = 0,
                count,
                ascending = true,
                search = search ?? ""
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
}
