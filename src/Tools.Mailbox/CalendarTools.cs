using System.ComponentModel;
using System.Net;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Server.Tools;

[McpServerToolType]
public sealed class CalendarTools
{
    [McpServerTool(ReadOnly = true)]
    [Description("Get calendar events for a given folder ID and date range")]
    public static async Task<string> GetCalendarEvents(
        [Description("The folder ID to get calendar events for")]
        string folderId,
        [Description("Start date in ISO 8601 format")]
        string startDate,
        [Description("End date in ISO 8601 format")]
        string endDate,
        [Description("Filter events by searching various fields (optional)")]
        string searchQuery,
        UserContext userContext)
    {
        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 2 to see available calendar folders." });

            var payload = new
            {
                startDate,
                endDate
            };

            var response =
                await userContext.PostAsync<JsonElement>($"/api/v1/calendars/events/{sourceIdPath}", payload);

            var simplifiedEvents = new List<object>();
            if (response.TryGetProperty("events", out var events))
                foreach (var evt in events.EnumerateArray())
                {
                    if (!string.IsNullOrWhiteSpace(searchQuery))
                    {
                        var searchLower = searchQuery.ToLower().Trim();
                        var title = (evt.TryGetProperty("title", out var t) ? t.GetString() : null) ??
                                    (evt.TryGetProperty("subject", out var sb) ? sb.GetString() : null) ?? "";
                        var description = evt.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                        var location = evt.TryGetProperty("location", out var l) ? l.GetString() ?? "" : "";
                        var owner = evt.TryGetProperty("owner", out var o) ? o.GetString() ?? "" : "";

                        if (!title.ToLower().Contains(searchLower) &&
                            !description.ToLower().Contains(searchLower) &&
                            !location.ToLower().Contains(searchLower) &&
                            !owner.ToLower().Contains(searchLower))
                            continue;
                    }

                    var simplifiedEvent = new
                    {
                        id = evt.TryGetProperty("id", out var id)
                            ? (id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString())
                            : null,
                        subject = (evt.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null) ??
                                  (evt.TryGetProperty("Title", out var tp2) ? tp2.GetString() : null) ??
                                  (evt.TryGetProperty("subject", out var subjectProp) ? subjectProp.GetString() : null) ??
                                  (evt.TryGetProperty("Subject", out var sp2) ? sp2.GetString() : null),
                        allDayEvent = (evt.TryGetProperty("allDay", out var allDay) && allDay.GetBoolean()) ||
                                      (evt.TryGetProperty("AllDay", out var ad2) && ad2.GetBoolean()) ||
                                      (evt.TryGetProperty("allDayEvent", out var ade) && ade.GetBoolean()) ||
                                      (evt.TryGetProperty("AllDayEvent", out var ade2) && ade2.GetBoolean()),
                        startDate = (evt.TryGetProperty("startWithTZ", out var startTz) &&
                                 startTz.TryGetProperty("dateTime", out var startDt) ? startDt.GetString() : null) ??
                                    (evt.TryGetProperty("start", out var s) ? (s.ValueKind == JsonValueKind.String ? s.GetString() : (s.TryGetProperty("dt", out var sdt) ? sdt.GetString() : null)) : null) ??
                                    (evt.TryGetProperty("startDate", out var sd) ? sd.GetString() : null),
                        endDate = (evt.TryGetProperty("endWithTZ", out var endTz) &&
                               endTz.TryGetProperty("dateTime", out var endDt) ? endDt.GetString() : null) ??
                                  (evt.TryGetProperty("end", out var e) ? (e.ValueKind == JsonValueKind.String ? e.GetString() : (e.TryGetProperty("dt", out var edt) ? edt.GetString() : null)) : null) ??
                                  (evt.TryGetProperty("endDate", out var ed) ? ed.GetString() : null),
                        location = evt.TryGetProperty("location", out var loc) ? loc.GetString() : null,
                        owner = evt.TryGetProperty("owner", out var own) ? own.GetString() : null,
                        isRecurring = evt.TryGetProperty("isRecurring", out var isRec) && isRec.GetBoolean(),
                        isOnlineMeeting = evt.TryGetProperty("isOnlineMeeting", out var isOnline) &&
                                          isOnline.GetBoolean(),
                        status = (evt.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 1) switch {
                            0 => "Tentative",
                            1 => "Confirmed",
                            2 => "Cancelled",
                            3 => "NeedsAction",
                            4 => "Completed",
                            5 => "InProcess",
                            _ => "Confirmed"
                        }
                    };
                    simplifiedEvents.Add(simplifiedEvent);
                }

            var result = new
            {
                events = simplifiedEvents,
                success = response.TryGetProperty("events", out _)
            };

            return JsonSerializer.Serialize(result);
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody, endpoint = apiEx.Endpoint });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(ReadOnly = true)]
    [Description("Get a calendar event for a given folder ID and event ID")]
    public static async Task<string> GetCalendarEvent(
        [Description("The folder ID to get the calendar event for")]
        string folderId,
        [Description("The event ID to get the calendar event for")]
        string eventId,
        UserContext userContext)
    {
        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 2 to see available calendar folders." });

            var response =
                await userContext.GetAsync<JsonElement>($"/api/v1/calendars/events/{sourceIdPath}/{eventId}");

            if (!response.TryGetProperty("details", out var details))
                return JsonSerializer.Serialize(new { success = false, error = $"Event not found for ID '{eventId}' in folder '{folderId}'. Use get_calendar_events to find valid event IDs." });

            // Map status enums
            var eventStatusMap = new Dictionary<int, string>
            {
                { 0, "Tentative" }, { 1, "Confirmed" }, { 2, "Cancelled" },
                { 3, "NeedsAction" }, { 4, "Completed" }, { 5, "InProcess" }
            };

            var meetingStatusMap = new Dictionary<int, string>
            {
                { 0, "IsNotAMeeting" }, { 1, "IsAMeeting" },
                { 2, "MeetingReceived" }, { 4, "MeetingIsCancelled" }
            };

            var permissionMap = new Dictionary<int, string>
            {
                { 0, "None" }, { 2, "Availability" }, { 4, "Read" },
                { 8, "Manage" }, { 10, "Owner" }
            };

            // Format attendees
            var attendees = new List<object>();
            if (details.TryGetProperty("attendees", out var attendeesArray))
                foreach (var attendee in attendeesArray.EnumerateArray())
                {
                    var statusMap = new Dictionary<int, string>
                    {
                        { 0, "None" }, { 1, "Organizer" }, { 2, "Tentative" },
                        { 3, "Accepted" }, { 4, "Declined" }
                    };

                    var status = attendee.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0;
                    attendees.Add(new
                    {
                        name = attendee.TryGetProperty("name", out var n) ? n.GetString() : null,
                        email = attendee.TryGetProperty("email", out var e) ? e.GetString() : null,
                        status = statusMap.ContainsKey(status) ? statusMap[status] : "Unknown",
                        isOrganizer = attendee.TryGetProperty("isOrganizer", out var isOrg) && isOrg.GetBoolean(),
                        isResource = attendee.TryGetProperty("isResource", out var isRes) && isRes.GetBoolean()
                    });
                }

            var simplifiedEvent = new
            {
                id = details.TryGetProperty("id", out var id)
                    ? (id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString())
                    : null,
                uid = details.TryGetProperty("uid", out var uid) ? uid.GetString() : null,
                subject = details.TryGetProperty("subject", out var subj) ? subj.GetString() : null,
                description = details.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                location = details.TryGetProperty("location", out var loc) ? loc.GetString() : null,
                allDay = details.TryGetProperty("allDay", out var allDay) && allDay.GetBoolean(),
                start = details.TryGetProperty("start", out var start)
                    ? new
                    {
                        dateTime = start.TryGetProperty("dt", out var dt) ? dt.GetString() : null,
                        timeZone = start.TryGetProperty("tz", out var tz) ? tz.GetString() : null
                    }
                    : null,
                end = details.TryGetProperty("end", out var end)
                    ? new
                    {
                        dateTime = end.TryGetProperty("dt", out var edt) ? edt.GetString() : null,
                        timeZone = end.TryGetProperty("tz", out var etz) ? etz.GetString() : null
                    }
                    : null,
                calendarId = details.TryGetProperty("calendarId", out var calId) ? calId.GetString() : null,
                calendarOwner = details.TryGetProperty("calendarOwner", out var calOwn) ? calOwn.GetString() : null,
                status = eventStatusMap.ContainsKey(
                    details.TryGetProperty("status", out var stat) && stat.ValueKind == JsonValueKind.Number ? stat.GetInt32() : 0)
                    ? eventStatusMap[details.TryGetProperty("status", out stat) && stat.ValueKind == JsonValueKind.Number ? stat.GetInt32() : 0]
                    : "Unknown",
                meetingStatus = meetingStatusMap.ContainsKey(details.TryGetProperty("meetingStatus", out var mstat) && mstat.ValueKind == JsonValueKind.Number
                    ? mstat.GetInt32()
                    : 0)
                    ? meetingStatusMap[details.TryGetProperty("meetingStatus", out mstat) && mstat.ValueKind == JsonValueKind.Number ? mstat.GetInt32() : 0]
                    : "Unknown",
                permission =
                    permissionMap.ContainsKey(details.TryGetProperty("permission", out var perm) && perm.ValueKind == JsonValueKind.Number ? perm.GetInt32() : 0)
                        ? permissionMap[details.TryGetProperty("permission", out perm) && perm.ValueKind == JsonValueKind.Number ? perm.GetInt32() : 0]
                        : "Unknown",
                isTentative = details.TryGetProperty("isTentative", out var isTent) && isTent.GetBoolean(),
                isPrivate = details.TryGetProperty("isPrivate", out var isPriv) && isPriv.GetBoolean(),
                isOrganizer = details.TryGetProperty("isOrganizer", out var isOrg2) && isOrg2.GetBoolean(),
                organizerName = details.TryGetProperty("organizerName", out var orgName) ? orgName.GetString() : null,
                organizerEmail = details.TryGetProperty("organizerEmail", out var orgEmail)
                    ? orgEmail.GetString()
                    : null,
                isRecurring = details.TryGetProperty("recurrence", out var rec) &&
                              rec.TryGetProperty("type", out var recType) && recType.ValueKind == JsonValueKind.Number && recType.GetInt32() > 0,
                recurrenceType =
                    details.TryGetProperty("recurrence", out rec) && rec.TryGetProperty("type", out recType) && recType.ValueKind == JsonValueKind.Number
                        ? recType.GetInt32()
                        : 0,
                reminderMinutes = details.TryGetProperty("reminder", out var reminder) && reminder.ValueKind == JsonValueKind.Number
                    ? reminder.GetInt32()
                    : (int?)null,
                emailNotification = details.TryGetProperty("emailNotification", out var emailNot)
                    ? emailNot.GetString()
                    : null,
                emailNotificationEnabled = details.TryGetProperty("emailNotificationEnabled", out var emailNotEn) &&
                                           emailNotEn.GetBoolean(),
                attendees,
                attendeeCount = attendees.Count,
                isOnlineMeeting = details.TryGetProperty("meetingUrl", out var meetUrl) &&
                                  !string.IsNullOrEmpty(meetUrl.GetString()),
                meetingUrl = details.TryGetProperty("meetingUrl", out meetUrl) ? meetUrl.GetString() : null,
                hasAttachments = details.TryGetProperty("hasAttachments", out var hasAtt) && hasAtt.GetBoolean(),
                attachmentCount = details.TryGetProperty("attachedFiles", out var attFiles)
                    ? attFiles.GetArrayLength()
                    : 0
            };

            var result = new
            {
                evt = simplifiedEvent,
                success = true
            };

            return JsonSerializer.Serialize(result);
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody, endpoint = apiEx.Endpoint });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Create a new calendar event or update an existing one")]
    public static async Task<string> NewOrUpdateCalendarEvent(
        [Description("The ID of the calendar folder to add/update the event to")]
        string folderId,
        [Description("The title of the event")]
        string subject,
        [Description("The start time of the event (YYYY-MM-DDTHH:mm:ss)")]
        string startTime,
        [Description("The end time of the event (YYYY-MM-DDTHH:mm:ss)")]
        string endTime,
        [Description("The ID of the event to update (if it exists)")]
        string eventId,
        [Description("The location of the event")]
        string location,
        [Description("The description of the event")]
        string description,
        [Description("Comma separated list of email addresses of attendees")]
        string attendees,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot create/update calendar events." });

        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 2 to see available calendar folders." });

            var parts = sourceIdPath.Split('/');
            var calendarOwner = parts[0];
            var calendarGuid = parts.Length > 1 ? parts[1] : sourceIdPath;

            // Extract just the numeric folder ID from the folderId parameter (e.g., "30003" from "jdoe/30003")
            var folderIdParts = folderId.Split('/');
            var numericFolderId = folderIdParts.Length > 1 ? folderIdParts[1] : folderId;

            // Build event data matching Python structure
            // Note: id must be 0 (long) for new events, not null - API expects non-nullable long
            var eventData = new Dictionary<string, object?>
            {
                ["id"] = string.IsNullOrWhiteSpace(eventId) ? 0L : long.Parse(eventId),
                ["subject"] = subject,
                ["start"] = new { dt = startTime, tz = "UTC" },
                ["end"] = new { dt = endTime, tz = "UTC" },
                ["location"] = string.IsNullOrWhiteSpace(location) ? null : location,
                ["description"] = string.IsNullOrWhiteSpace(description) ? null : description,
                ["calendarId"] = calendarGuid, // Use just the numeric folder ID like Python
                ["calendarOwner"] = calendarOwner,
                ["attendees"] = new List<object>()
            };

            // Add attendees if provided - match Python structure (only email field)
            if (!string.IsNullOrWhiteSpace(attendees))
            {
                var attendeesList = new List<object>();
                foreach (var email in attendees.Split(','))
                {
                    var trimmedEmail = email.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmedEmail))
                        attendeesList.Add(new { email = trimmedEmail });
                }
                eventData["attendees"] = attendeesList;
            }

            var eventIdParam = string.IsNullOrWhiteSpace(eventId) ? "null" : eventId;
            Console.Error.WriteLine($"[DEBUG] Posting to: /api/v1/calendars/events/save/{sourceIdPath}/{eventIdParam}");
            Console.Error.WriteLine($"[DEBUG] Event data: {JsonSerializer.Serialize(eventData)}");
            var response =
                await userContext.PostAsync<JsonElement>($"/api/v1/calendars/events/save/{sourceIdPath}/{eventIdParam}",
                    eventData);
            return JsonSerializer.Serialize(response);
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody, endpoint = apiEx.Endpoint });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(Destructive = true)]
    [Description("Delete a calendar event")]
    public static async Task<string> DeleteCalendarEvent(
        [Description("The ID of the calendar folder to delete the event from")]
        string folderId,
        [Description("The ID of the event to delete")]
        string eventId,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot delete calendar events." });

        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 2 to see available calendar folders." });

            var response =
                await userContext.PostAsync<JsonElement>(
                    $"/api/v1/calendars/events/delete/{sourceIdPath}/{eventId}/true", new { });
            return JsonSerializer.Serialize(response);
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody, endpoint = apiEx.Endpoint });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Respond to a meeting invitation (accept, decline, or tentative)")]
    public static async Task<string> RespondToMeeting(
        [Description("The ID of the calendar folder containing the meeting")]
        string folderId,
        [Description("The event ID of the meeting")]
        string eventId,
        [Description("Response type: 'accept', 'decline', or 'tentative'")]
        string response,
        [Description("Optional comment to include with your response")]
        string comment = "",
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot respond to meetings." });

        try
        {
            var validResponses = new[] { "accept", "decline", "tentative" };
            var responseLower = response?.ToLower().Trim();
            if (string.IsNullOrEmpty(responseLower) || !validResponses.Contains(responseLower))
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Invalid response. Must be 'accept', 'decline', or 'tentative'"
                });

            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 2 to see available calendar folders." });

            var parts = sourceIdPath.Split('/');
            var owner = parts[0];
            var calId = parts.Length > 1 ? parts[1] : sourceIdPath;

            // Build the endpoint based on response type
            var endpoint = responseLower switch
            {
                "accept" => $"/api/v1/calendars/meeting-accept/{owner}/{calId}/{eventId}",
                "decline" => $"/api/v1/calendars/meeting-decline/{owner}/{calId}/{eventId}",
                "tentative" => $"/api/v1/calendars/meeting-tentatively-accept/{owner}/{calId}/{eventId}",
                _ => throw new ArgumentException("Invalid response type")
            };

            var payload = new
            {
                replyingComment = string.IsNullOrEmpty(comment) ? null : comment
            };

            // This endpoint may return empty response on success, so use string response
            var apiResponse = await userContext.PostAsyncRaw(endpoint, payload);

            return JsonSerializer.Serialize(new
            {
                success = true,
                response = responseLower,
                eventId,
                folderId,
                message = $"Meeting {responseLower}ed successfully"
            });
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody, endpoint = apiEx.Endpoint });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }
}