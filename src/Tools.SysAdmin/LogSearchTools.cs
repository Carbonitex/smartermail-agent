using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.SystemAdmin.Tools;

/// <summary>
/// Plain log search. Kept apart from <see cref="LogAnalysisTools"/> (which launches the claude CLI)
/// so consumers such as smartermail-agent can copy this file without the analysis tools.
/// </summary>
[McpServerToolType]
public sealed class LogSearchTools
{
    [McpServerTool(ReadOnly = true)]
    [Description(
        "Search server log files by type, date range, and search term. " +
        "Available log types: smtpLog, delivery, imapLog, popLog, spamChecks, contentfilter, " +
        "administrative, generalErrors, event, ews, ewsRetrieval, activeSync, calendars, " +
        "certificates, autodiscover, imapRetrieval, popRetrieval, indexing, ldapLog, " +
        "activation, mailinglists, maintenance, mapi, messageId, oab, routingRules, " +
        "conversion, autoCleanFolders, webdav, xmppLog")]
    public static async Task<string> SearchLogFiles(
        [Description("Log type to search (e.g., 'smtpLog', 'delivery', 'spamChecks')")] string type,
        [Description("Start date for log search in yyyy-MM-dd format")] string startDate,
        [Description("End date for log search in yyyy-MM-dd format")] string endDate,
        [Description("Search term to filter log entries (empty string returns all entries)")] string search,
        [Description("Include related log entries (useful for tracking a message through multiple log types)")] bool related,
        UserContext userContext)
    {
        try
        {
            if (!DateTime.TryParse(startDate, out var start))
                start = DateTime.UtcNow.Date;
            if (!DateTime.TryParse(endDate, out var end))
                end = DateTime.UtcNow.Date;

            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sysadmin/log-files", new
            {
                start = start.ToString("o"),
                end = end.ToString("o"),
                type,
                search = search ?? "",
                related
            });

            var content = response.TryGetProperty("result", out var resultEl) ? resultEl.GetString() ?? "" : "";
            var isTruncated = response.TryGetProperty("isTruncated", out var truncEl) && truncEl.GetBoolean();

            return JsonSerializer.Serialize(new
            {
                success = true,
                logType = type,
                searchTerm = search ?? "",
                content,
                isTruncated,
                note = isTruncated ? "Results were truncated — try a more specific search term." : null
            });
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = apiEx.Message,
                statusCode = (int)apiEx.StatusCode,
                apiError = apiEx.ResponseBody
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }
}
