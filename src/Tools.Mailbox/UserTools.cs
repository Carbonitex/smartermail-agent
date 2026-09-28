using System.ComponentModel;
using System.Net;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Server.Tools;

[McpServerToolType]
public sealed class UserTools
{
    private static string GetFriendlyMessageFlags(JsonElement messageFlags)
    {
        var flags = new List<string>();

        if (messageFlags.TryGetProperty("isSeen", out var isSeen) && isSeen.GetBoolean())
            flags.Add("Read");
        else if (messageFlags.TryGetProperty("isSeen", out var notSeen) && !notSeen.GetBoolean())
            flags.Add("Unread");

        if (messageFlags.TryGetProperty("isAnswered", out var isAnswered) && isAnswered.GetBoolean())
            flags.Add("Answered");
        
        if (messageFlags.TryGetProperty("isFlagged", out var isFlagged) && isFlagged.GetBoolean())
            flags.Add("Flagged");
        
        if (messageFlags.TryGetProperty("isDraft", out var isDraft) && isDraft.GetBoolean())
            flags.Add("Draft");
        
        if (messageFlags.TryGetProperty("hasAttachments", out var hasAttachments) && hasAttachments.GetBoolean())
            flags.Add("Has Attachments");
        
        if (messageFlags.TryGetProperty("isCalendarMessage", out var isCalendar) && isCalendar.GetBoolean())
            flags.Add("Calendar");

        if (messageFlags.TryGetProperty("markedAsSpam", out var isSpam) && isSpam.GetBoolean())
            flags.Add("Spam");

        return flags.Count == 0 ? "None" : string.Join(", ", flags);
    }

    [McpServerTool(ReadOnly = true)]
    [Description("Get user data")]
    public static async Task<string> GetUserData(
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        try
        {
            var response = await userContext.GetUserInfoAsync();
            return JsonSerializer.Serialize(response);
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody, endpoint = apiEx.Endpoint });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
                { success = false, message = $"Failed to fetch user data: {ex.Message}", error = ex.Message });
        }
    }

    [McpServerTool(ReadOnly = true)]
    [Description("Search for items in the system (mail, notes, tasks, contacts, events)")]
    public static async Task<string> SearchItems(
        [Description("The area to search in: Everywhere, Email, Notes, Contacts, Calendar, Tasks")]
        string section,
        [Description("The search query string")]
        string searchQuery,
        [Description("Use OR logic for multiple search terms (default: false for AND logic)")]
        bool or = false,
        [Description("Maximum number of email results to return (default: 250)")]
        int maxEmailsToReturn = 250,
        UserContext userContext = null!)
    {
        try
        {
            // Map the section string to the SearchableArea enum value
            int sectionValue;
            switch (section.ToLower())
            {
                case "everywhere":
                    sectionValue = 0;
                    break;
                case "email":
                    sectionValue = 1;
                    break;
                case "notes":
                    sectionValue = 2;
                    break;
                case "contacts":
                    sectionValue = 3;
                    break;
                case "calendar":
                    sectionValue = 4;
                    break;
                case "tasks":
                    sectionValue = 5;
                    break;
                default:
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error =
                            $"Invalid section: {section}. Valid values are: Everywhere, Email, Notes, Contacts, Calendar, Tasks"
                    });
            }

            var requestBody = new
            {
                section = sectionValue,
                searchQuery,
                or,
                maxEmailsToReturn,
                searchCriteriaMap = new Dictionary<string, string>()
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/advanced-search", requestBody);

            // Process and clean up the response
            if (response.TryGetProperty("advancedSearchResult", out var searchResult))
            {
                var cleanedResult = new Dictionary<string, object>();
                
                // Process email search results
                if (searchResult.TryGetProperty("emailSearchResults", out var emailResults) && emailResults.GetArrayLength() > 0)
                {
                    var cleanedEmails = new List<object>();
                    foreach (var email in emailResults.EnumerateArray())
                    {
                        var cleanedEmail = new Dictionary<string, object?>();
                        
                        // Add essential fields
                        if (email.TryGetProperty("subject", out var subject))
                            cleanedEmail["subject"] = subject.GetString();
                        if (email.TryGetProperty("from", out var from))
                            cleanedEmail["from"] = from.GetString();
                        if (email.TryGetProperty("fromName", out var fromName) && !string.IsNullOrEmpty(fromName.GetString()))
                            cleanedEmail["fromName"] = fromName.GetString();
                        if (email.TryGetProperty("folder", out var folder))
                            cleanedEmail["folder"] = folder.GetString();
                        if (email.TryGetProperty("dateSent", out var dateSent))
                            cleanedEmail["dateSent"] = dateSent.GetString();
                        if (email.TryGetProperty("size", out var size))
                            cleanedEmail["size"] = size.GetInt32();
                        if (email.TryGetProperty("uid", out var uid))
                            cleanedEmail["uid"] = uid.GetInt32();
                        
                        // Add importance if not normal
                        if (email.TryGetProperty("importanceHigh", out var impHigh) && impHigh.GetBoolean())
                            cleanedEmail["importance"] = "high";
                        else if (email.TryGetProperty("importanceLow", out var impLow) && impLow.GetBoolean())
                            cleanedEmail["importance"] = "low";
                        
                        // Simplify message flags using helper
                        if (email.TryGetProperty("messageFlags", out var msgFlags))
                        {
                            cleanedEmail["flags"] = GetFriendlyMessageFlags(msgFlags);
                        }
                        
                        // Add recipients if present and not empty
                        if (email.TryGetProperty("recipients", out var recipients) && recipients.GetArrayLength() > 0)
                        {
                            var recipList = new List<string>();
                            foreach (var recip in recipients.EnumerateArray())
                            {
                                var recipStr = recip.GetString();
                                if (!string.IsNullOrEmpty(recipStr))
                                    recipList.Add(recipStr);
                            }
                            if (recipList.Count > 0)
                                cleanedEmail["recipients"] = recipList;
                        }
                        
                        cleanedEmails.Add(cleanedEmail);
                    }
                    cleanedResult["emails"] = cleanedEmails;
                }
                
                // Process note search results
                if (searchResult.TryGetProperty("noteSearchResults", out var noteResults) && noteResults.GetArrayLength() > 0)
                {
                    var cleanedNotes = new List<object?>();
                    foreach (var note in noteResults.EnumerateArray())
                    {
                        var noteDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(note.GetRawText());
                        // Remove unnecessary fields
                        noteDict?.Remove("id");
                        cleanedNotes.Add(noteDict);
                    }
                    cleanedResult["notes"] = cleanedNotes;
                }
                
                // Process contact search results
                if (searchResult.TryGetProperty("contactSearchResults", out var contactResults) && contactResults.GetArrayLength() > 0)
                {
                    var cleanedContacts = new List<object?>();
                    foreach (var contact in contactResults.EnumerateArray())
                    {
                        var contactDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(contact.GetRawText());
                        cleanedContacts.Add(contactDict);
                    }
                    cleanedResult["contacts"] = cleanedContacts;
                }
                
                // Process event search results
                if (searchResult.TryGetProperty("eventSearchResults", out var eventResults) && eventResults.GetArrayLength() > 0)
                {
                    var cleanedEvents = new List<object?>();
                    foreach (var evt in eventResults.EnumerateArray())
                    {
                        var eventDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.GetRawText());
                        cleanedEvents.Add(eventDict);
                    }
                    cleanedResult["events"] = cleanedEvents;
                }
                
                // Process task search results
                if (searchResult.TryGetProperty("taskSearchResults", out var taskResults) && taskResults.GetArrayLength() > 0)
                {
                    var cleanedTasks = new List<object?>();
                    foreach (var task in taskResults.EnumerateArray())
                    {
                        var taskDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(task.GetRawText());
                        cleanedTasks.Add(taskDict);
                    }
                    cleanedResult["tasks"] = cleanedTasks;
                }
                
                var result = new
                {
                    success = response.TryGetProperty("success", out var succ) && succ.GetBoolean(),
                    searchQuery,
                    section,
                    results = cleanedResult
                };
                
                return JsonSerializer.Serialize(result);
            }

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
    [Description("Remove items from the system (notes, tasks, contacts, events)")]
    public static async Task<string> RemoveItems(
        [Description("The IDs of the items to remove, comma separated (id/guid only)")]
        string itemId,
        [Description("The type of the item to remove - note, task, contact, event")]
        string itemType,
        [Description("The ID of the calendar to remove the event from (required for events)")]
        string calendarId,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot remove items." });

        try
        {
            var ids = itemId.Split(',').Select(id => id.Trim()).ToList();
            var type = itemType.ToLower();

            if (ids.Count == 0)
                return JsonSerializer.Serialize(new { success = false, error = "No valid IDs provided. Expected comma-separated item IDs." });

            switch (type)
            {
                case "note":
                    await userContext.PostAsync<JsonElement>("/api/v1/notes/batch/delete", ids);
                    break;

                case "task":
                {
                    var taskSourcesResponse = await userContext.GetAsync<JsonElement>("/api/v1/tasks/sources");

                    if (!taskSourcesResponse.TryGetProperty("sources", out var sources) ||
                        sources.GetArrayLength() == 0)
                        return JsonSerializer.Serialize(new { success = false, error = "No task folders found. Use list_folder_info_by_type with type 2 to verify task folders exist." });

                    var defaultSource = sources[0];
                    var tasksToDelete = ids.Select(id => new
                    {
                        sourceOwner = defaultSource.TryGetProperty("owner", out var owner)
                            ? owner.GetString()
                            : "default",
                        sourceId = defaultSource.TryGetProperty("id", out var sourceId) ? sourceId.GetString() : "",
                        id
                    }).ToList();

                    await userContext.PostAsync<JsonElement>("/api/v1/tasks/delete", tasksToDelete);
                    break;
                }

                case "contact":
                {
                    var contactsToDelete = ids.Select(id => new
                    {
                        sourceOwner = "default",
                        sourceId = "default",
                        id
                    }).ToList();

                    await userContext.PostAsync<JsonElement>("/api/v1/contacts/delete-bulk", contactsToDelete);
                    break;
                }

                case "event":
                {
                    if (string.IsNullOrWhiteSpace(calendarId))
                        return JsonSerializer.Serialize(new
                            { success = false, error = "Calendar ID is required when removing events. Use list_folder_info_by_type with type 2 to find calendar folder IDs." });

                    var calendarsResponse = await userContext.GetAsync<JsonElement>("/api/v1/calendars");

                    if (!calendarsResponse.TryGetProperty("calendars", out var calendars))
                        return JsonSerializer.Serialize(new { success = false, error = "Failed to get calendar sources. Use list_folder_info_by_type with type 2 to verify calendar folders exist." });

                    JsonElement? calendar = null;
                    foreach (var cal in calendars.EnumerateArray())
                        if (cal.TryGetProperty("id", out var id) && id.GetString() == calendarId)
                        {
                            calendar = cal;
                            break;
                        }

                    if (calendar == null)
                        return JsonSerializer.Serialize(new
                            { success = false, error = $"Calendar with ID {calendarId} not found" });

                    var deleteMetadata = ids.Select(id => new
                    {
                        owner = calendar.Value.TryGetProperty("owner", out var owner) ? owner.GetString() : "default",
                        calendarId,
                        eventId = id
                    }).ToList();

                    await userContext.PostAsync<JsonElement>("/api/v1/calendars/events/delete-bulk", deleteMetadata);
                    break;
                }

                default:
                    return JsonSerializer.Serialize(new { success = false, error = $"Invalid item type: '{itemType}'. Valid types are: note, task, contact, event" });
            }

            return JsonSerializer.Serialize(new { success = true, result = "Items removed" });
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