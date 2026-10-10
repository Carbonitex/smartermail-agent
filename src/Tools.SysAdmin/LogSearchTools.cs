using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.SystemAdmin.Tools;

/// <summary>
/// Windowed log search. Kept apart from <see cref="LogAnalysisTools"/> (which launches the claude CLI)
/// so consumers such as smartermail-agent can copy this file without the analysis tools.
/// </summary>
[McpServerToolType]
public sealed class LogSearchTools
{
    [McpServerTool(ReadOnly = true)]
    [Description(
        "Search server log files by type, date range, and search term. A busy day of a log can be megabytes, " +
        "so the result is a WINDOW (maxChars, default 16000): narrow first with a one-day range and a specific " +
        "search term (the server filters on it), optionally 'contains' to keep only matching lines, then page. " +
        "The window shows the most recent part first (tail=true); the result reports totalChars, returnedChars, " +
        "hasMore and nextOffset - call again with offset=nextOffset to read further back (tail=true) or forward (tail=false). " +
        "Available log types: smtpLog, delivery, imapLog, popLog, spamChecks, contentfilter, " +
        "administrative, generalErrors, event, ews, ewsRetrieval, activeSync, calendars, " +
        "certificates, autodiscover, imapRetrieval, popRetrieval, indexing, ldapLog, " +
        "activation, mailinglists, maintenance, mapi, messageId, oab, routingRules, " +
        "conversion, autoCleanFolders, webdav, xmppLog")]
    public static async Task<string> SearchLogFiles(
        [Description("Log type to search (e.g., 'smtpLog', 'delivery', 'spamChecks')")] string type,
        [Description("Start date for log search in yyyy-MM-dd format")] string startDate,
        [Description("End date for log search in yyyy-MM-dd format")] string endDate,
        [Description("Search term the server uses to filter log entries (empty string returns all entries - avoid for busy logs)")] string search,
        [Description("Include related log entries (useful for tracking a message through multiple log types)")] bool related,
        UserContext userContext,
        [Description("Maximum characters to return (default 16000, capped at 100000).")] int maxChars = TextWindow.DefaultMaxChars,
        [Description("Window offset in characters. With tail=true it counts back from the end of the log; with tail=false from the start. Use nextOffset from the previous result. Default 0.")] int offset = 0,
        [Description("true (default): return the most recent part of the log first. false: start from the beginning.")] bool tail = true,
        [Description("Optional extra filter: keep only lines containing this text (case-insensitive), applied before windowing. The result reports matchedLines of totalLines.")] string? contains = null)
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

            var content = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("result", out var resultEl) &&
                          resultEl.ValueKind == JsonValueKind.String ? resultEl.GetString() ?? "" : "";
            var isTruncated = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("isTruncated", out var truncEl) &&
                              truncEl.ValueKind == JsonValueKind.True;

            return ShapeResult(type, search, content, isTruncated, maxChars, offset, tail, contains);
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

    /// <summary>Filters then windows a log body into the tool's result JSON (offline-testable).</summary>
    public static string ShapeResult(string type, string? search, string content, bool serverTruncated,
        int maxChars, int offset, bool tail, string? contains)
    {
        var (filtered, matched, totalLines) = TextWindow.FilterLines(content, contains);
        var w = TextWindow.Slice(filtered, maxChars, offset, tail);
        string? note = null;
        if (w.HasMore)
            note = "Output is a window. Page with offset=" + w.NextOffset + (tail ? " (further back)" : "") +
                   ", or narrow with a shorter date range, a more specific search term, or contains.";
        if (serverTruncated)
            note = (note is null ? "" : note + " ") + "The server truncated the log itself - use a shorter date range or a more specific search term.";
        return JsonSerializer.Serialize(new
        {
            success = true,
            logType = type,
            searchTerm = search ?? "",
            contains = string.IsNullOrEmpty(contains) ? null : contains,
            matchedLines = matched,
            totalLines,
            totalChars = w.TotalChars,
            offset = w.Offset,
            returnedChars = w.ReturnedChars,
            hasMore = w.HasMore,
            nextOffset = w.NextOffset,
            tail = w.Tail,
            isTruncated = serverTruncated,
            content = w.Text,
            note
        });
    }
}
