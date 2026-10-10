using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.SystemAdmin.Tools;

[McpServerToolType]
public sealed class CertificateTools
{
    [McpServerTool(ReadOnly = true)]
    [Description("List all SSL/TLS certificates on the server")]
    public static async Task<string> GetSslCertificates(
        [Description("Sort field (optional, e.g., 'name', 'expiration')")] string sortBy,
        [Description("Sort descending (true/false)")] bool sortDescending,
        UserContext userContext)
    {
        try
        {
            var sort = string.IsNullOrEmpty(sortBy) ? "name" : sortBy;
            var response = await userContext.GetAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/ssl-certificates/{Uri.EscapeDataString(sort)}/{sortDescending.ToString().ToLower()}");
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
    [Description("Get counts of SSL/TLS and ACME certificates")]
    public static async Task<string> GetSslCertificateCounts(UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/sysadmin/ssl-certificate-counts");
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
    [Description("Upload an SSL/TLS certificate to the server")]
    public static async Task<string> UploadSslCertificate(
        [Description("Certificate filename")] string filename,
        [Description("Base64-encoded certificate data")] string data,
        [Description("Certificate password (if encrypted, otherwise empty)")] string password,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/ssl-cert-upload", new
            {
                filename,
                data,
                password,
                length = data.Length
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
    [Description("Delete SSL/TLS certificates from the server")]
    public static async Task<string> DeleteSslCertificate(
        [Description("Comma-separated list of certificate filenames to delete")] string certFilenames,
        UserContext userContext)
    {
        try
        {
            var filenames = certFilenames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/ssl-cert-delete", new
            {
                certFilenames = filenames
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
    [Description("List all ACME (Let's Encrypt) certificates")]
    public static async Task<string> GetAcmeCertificates(
        [Description("Sort field (optional)")] string sortBy,
        [Description("Sort descending (true/false)")] bool sortDescending,
        UserContext userContext)
    {
        try
        {
            var sort = string.IsNullOrEmpty(sortBy) ? "name" : sortBy;
            var response = await userContext.GetAsync<JsonElement>(
                $"/api/v1/settings/sysadmin/acme-certificates/{Uri.EscapeDataString(sort)}/{sortDescending.ToString().ToLower()}");
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
    [Description("Refresh the list of ACME certificates")]
    public static async Task<string> RefreshAcmeCertificates(UserContext userContext)
    {
        try
        {
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/acme-certificates/refresh", new { });
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
    [Description("Force immediate renewal of certificates by their GUIDs")]
    public static async Task<string> RenewCertificatesNow(
        [Description("Comma-separated list of certificate GUIDs to renew")] string certificateGuids,
        UserContext userContext)
    {
        try
        {
            var guids = certificateGuids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/renew-certificates-now", new
            {
                input = guids
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
