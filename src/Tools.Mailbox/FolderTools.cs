using System.ComponentModel;
using System.Net;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Server.Tools;

[McpServerToolType]
public sealed class FolderTools
{
    [McpServerTool(ReadOnly = true)]
    [Description("List folder information of folders by type")]
    public static async Task<string> ListFolderInfoByType(
        [Description("The type of folders to list: 1=email, 2=calendars/tasks, 3=contacts, 4=notes")]
        int type,
        UserContext userContext)
    {
        try
        {
            if (type < 1 || type > 4)
                return JsonSerializer.Serialize(new { success = false, message = "Invalid type" });

            var folderInfoList = new List<object>();

            if (type == 1) // Email folders
            {
                var response = await userContext.GetAsync<JsonElement>("/api/v1/folders/list-email-folders");
                if (response.TryGetProperty("success", out var success) && success.GetBoolean() &&
                    response.TryGetProperty("folderList", out var folderList))
                    foreach (var folder in folderList.EnumerateArray())
                    {
                        var ownerEmail = folder.TryGetProperty("ownerEmailAddress", out var email)
                            ? email.GetString()
                            : "";
                        var ownerUsername = ownerEmail?.Split('@')[0] ?? "";

                        var folderInfo = new Dictionary<string, object?>
                        {
                            { "folderId", $"{ownerUsername}/{folder.GetProperty("id").GetInt64()}" },
                            {
                                "folderIdParent",
                                folder.TryGetProperty("folderIdParent", out var parent) ? parent.GetInt64() : 0
                            },
                            { "name", folder.TryGetProperty("name", out var name) ? name.GetString() : "" },
                            { "path", folder.TryGetProperty("path", out var path) ? path.GetString() : "" },
                            {
                                "access",
                                folder.TryGetProperty("isSharedItem", out var shared) && shared.GetBoolean()
                                    ? "shared email support temporarily disabled"
                                    : "full"
                            },
                            { "unread", folder.TryGetProperty("unread", out var unread) ? unread.GetInt32() : 0 },
                            {
                                "totalMessages",
                                folder.TryGetProperty("totalMessages", out var total) ? total.GetInt32() : 0
                            },
                            {
                                "sizeMb",
                                folder.TryGetProperty("size", out var size)
                                    ? Math.Round(size.GetDouble() / 1024 / 1024, 3)
                                    : 0
                            }
                        };

                        folderInfoList.Add(folderInfo);
                    }
            }
            else if (type == 2) // Calendar and Task folders
            {
                var response = await userContext.GetAsync<JsonElement>("/api/v1/calendars/sources");

                if (response.TryGetProperty("calendars", out var calendars))
                    foreach (var calendar in calendars.EnumerateArray())
                    {
                        var owner = calendar.TryGetProperty("owner", out var o) ? o.GetString() : "@domain";

                        // Try to get folderId as different types
                        var folderIdStr = "";
                        if (calendar.TryGetProperty("folderId", out var folderIdProp))
                        {
                            if (folderIdProp.ValueKind == JsonValueKind.Number)
                                folderIdStr = folderIdProp.GetInt64().ToString();
                            else if (folderIdProp.ValueKind == JsonValueKind.String)
                                folderIdStr = folderIdProp.GetString() ?? "";
                        }

                        var folderInfo = new Dictionary<string, object?>
                        {
                            { "folderId", $"{owner}/{folderIdStr}" },
                            { "name", calendar.TryGetProperty("name", out var name) ? name.GetString() : "" },
                            {
                                "isPrimary",
                                calendar.TryGetProperty("isPrimary", out var isPrimary) && isPrimary.GetBoolean()
                            },
                            {
                                "isWebCalendar",
                                calendar.TryGetProperty("isWebCalendar", out var isWeb) && isWeb.GetBoolean()
                            },
                            {
                                "access", calendar.TryGetProperty("isSharedItem", out var shared) && shared.GetBoolean()
                                    ? calendar.TryGetProperty("permission", out var perm) && perm.GetInt32() == 8
                                        ? "read"
                                        : "full"
                                    : "full"
                            }
                        };
                        folderInfoList.Add(folderInfo);
                    }

                if (response.TryGetProperty("tasks", out var tasks))
                    foreach (var task in tasks.EnumerateArray())
                    {
                        var owner = task.TryGetProperty("owner", out var o) ? o.GetString() : "@domain";

                        // Try to get folderId as different types
                        var folderIdStr = "";
                        if (task.TryGetProperty("folderId", out var folderIdProp))
                        {
                            if (folderIdProp.ValueKind == JsonValueKind.Number)
                                folderIdStr = folderIdProp.GetInt64().ToString();
                            else if (folderIdProp.ValueKind == JsonValueKind.String)
                                folderIdStr = folderIdProp.GetString() ?? "";
                        }

                        var folderInfo = new Dictionary<string, object?>
                        {
                            { "folderId", $"{owner}/{folderIdStr}" },
                            { "name", task.TryGetProperty("name", out var name) ? name.GetString() : "" },
                            {
                                "isPrimary",
                                task.TryGetProperty("isPrimary", out var isPrimary) && isPrimary.GetBoolean()
                            },
                            { "color", task.TryGetProperty("color", out var color) ? color.GetString() : "" },
                            {
                                "access", task.TryGetProperty("isSharedItem", out var shared) && shared.GetBoolean()
                                    ? task.TryGetProperty("permission", out var perm) && perm.GetInt32() == 8
                                        ? "read"
                                        : "full"
                                    : "full"
                            }
                        };
                        folderInfoList.Add(folderInfo);
                    }
            }
            else if (type == 3) // Contact folders
            {
                var response = await userContext.GetAsync<JsonElement>("/api/v1/contacts/sources");
                if (response.TryGetProperty("success", out var success) && success.GetBoolean() &&
                    response.TryGetProperty("sharedLists", out var sharedLists))
                    foreach (var folder in sharedLists.EnumerateArray())
                    {
                        var owner = folder.TryGetProperty("ownerUsername", out var o) ? o.GetString() : "@domain";
                        var folderId = folder.TryGetProperty("folderId", out var fid) ? fid.GetInt64() : -1;
                        var folderIdStr = folderId == -1 ? $"{owner}/gal" : $"{owner}/{folderId}";

                        var folderInfo = new Dictionary<string, object?>
                        {
                            { "folderId", folderIdStr },
                            {
                                "displayName",
                                folder.TryGetProperty("displayName", out var display) ? display.GetString() : ""
                            },
                            {
                                "isPrimary",
                                folder.TryGetProperty("isPrimary", out var isPrimary) && isPrimary.GetBoolean()
                            },
                            {
                                "access", folder.TryGetProperty("isSharedItem", out var shared) && shared.GetBoolean()
                                    ? folder.TryGetProperty("access", out var acc) && acc.GetInt32() == 8
                                        ? "read"
                                        : "full"
                                    : "full"
                            }
                        };
                        folderInfoList.Add(folderInfo);
                    }
            }
            else if (type == 4) // Note folders
            {
                var response = await userContext.GetAsync<JsonElement>("/api/v1/notes/sources");
                if (response.TryGetProperty("success", out var success) && success.GetBoolean() &&
                    response.TryGetProperty("sharedLists", out var sharedLists))
                    foreach (var folder in sharedLists.EnumerateArray())
                    {
                        var owner = folder.TryGetProperty("ownerUsername", out var o) ? o.GetString() : "@domain";
                        var folderInfo = new Dictionary<string, object?>
                        {
                            { "folderId", $"{owner}/{folder.GetProperty("folderId").GetInt64()}" },
                            {
                                "displayName",
                                folder.TryGetProperty("displayName", out var display) ? display.GetString() : ""
                            },
                            {
                                "isPrimary",
                                folder.TryGetProperty("isPrimary", out var isPrimary) && isPrimary.GetBoolean()
                            },
                            {
                                "access", folder.TryGetProperty("isSharedItem", out var shared) && shared.GetBoolean()
                                    ? folder.TryGetProperty("access", out var acc) && acc.GetInt32() == 8
                                        ? "read"
                                        : "full"
                                    : "full"
                            }
                        };
                        folderInfoList.Add(folderInfo);
                    }
            }

            return JsonSerializer.Serialize(new { success = true, folders = folderInfoList });
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
    [Description("Create a new folder")]
    public static async Task<string> CreateFolder(
        [Description("The name of the new folder")]
        string name,
        [Description("The type of the new folder: 1=email, 2=calendar, 3=contact, 4=note, 5=task")]
        int type,
        [Description("The ID of the parent folder (for mail folders)")]
        string parentFolderId,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, message = "Read-only mode is enabled. Cannot create folders." });

        try
        {
            switch (type)
            {
                case 1: // Email folder
                {
                    var parentPath = "";
                    if (!string.IsNullOrWhiteSpace(parentFolderId) && parentFolderId != "undefined" &&
                        parentFolderId != "null")
                    {
                        parentPath = await userContext.GetFolderPathFromFolderIdAsync(parentFolderId,
                            userContext.Username, userContext.Domain);
                        if (parentPath == null)
                            return JsonSerializer.Serialize(
                                new { success = false, message = "Parent folder not found" });

                        // Strip owner prefix from path since API will add it automatically
                        if (parentPath.Contains('/'))
                        {
                            var pathParts = parentPath.Split('/', 2);
                            if (pathParts.Length > 1)
                                parentPath = pathParts[1];
                        }
                    }

                    var payload = new { folder = name, parentFolder = parentPath };
                    var response = await userContext.PostAsync<JsonElement>("/api/v1/folders/folder-put", payload);
                    return JsonSerializer.Serialize(response);
                }

                case 2: // Calendar folder
                {
                    var payload = new { setting = new { friendlyName = name } };
                    var response = await userContext.PostAsync<JsonElement>("/api/v1/calendars/calendar-put", payload);
                    return JsonSerializer.Serialize(response);
                }

                case 3: // Contact folder
                {
                    var payload = new { folder = name };
                    var response =
                        await userContext.PostAsync<JsonElement>("/api/v1/contacts/address-book/add", payload);
                    return JsonSerializer.Serialize(response);
                }

                case 4: // Note folder
                {
                    var payload = new { folder = name };
                    var response = await userContext.PostAsync<JsonElement>("/api/v1/notes/sources/add", payload);
                    return JsonSerializer.Serialize(response);
                }

                case 5: // Task folder
                {
                    var payload = new { folder = name };
                    var response = await userContext.PostAsync<JsonElement>("/api/v1/tasks/sources/add", payload);
                    return JsonSerializer.Serialize(response);
                }

                default:
                    return JsonSerializer.Serialize(new { success = false, message = "Invalid type" });
            }
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

    private static async Task<(int type, string folderName)> DetermineFolderTypeAndName(UserContext userContext, string folderId)
    {
        // Type 1: Email folders - Try using the folder API directly (works for nested folders too)
        try
        {
            // Use GetFolderPathFromFolderIdAsync which works for all email folders including nested ones
            var folderPath = await userContext.GetFolderPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (!string.IsNullOrEmpty(folderPath))
            {
                // Strip owner prefix if present (format might be "owner/path")
                var path = folderPath;
                if (path.Contains('/'))
                {
                    var parts = path.Split('/', 2);
                    if (parts.Length > 1)
                        path = parts[1];
                }
                return (1, path);
            }
        }
        catch { }

        // Type 2: Calendar/Task folders
        try
        {
            var result = await userContext.GetAsync<JsonElement>("/api/v1/calendars/sources");
            if (result.TryGetProperty("calendars", out var calendars))
            {
                foreach (var calendar in calendars.EnumerateArray())
                {
                    var owner = calendar.TryGetProperty("owner", out var o) ? o.GetString() : "";
                    var folderIdStr = "";
                    if (calendar.TryGetProperty("folderId", out var folderIdProp))
                    {
                        if (folderIdProp.ValueKind == JsonValueKind.Number)
                            folderIdStr = folderIdProp.GetInt64().ToString();
                        else if (folderIdProp.ValueKind == JsonValueKind.String)
                            folderIdStr = folderIdProp.GetString() ?? "";
                    }
                    var currentFolderId = $"{owner}/{folderIdStr}";

                    if (currentFolderId == folderId)
                        return (2, "");
                }
            }

            // Check tasks
            if (result.TryGetProperty("tasks", out var tasks))
            {
                foreach (var task in tasks.EnumerateArray())
                {
                    var owner = task.TryGetProperty("owner", out var o) ? o.GetString() : "";
                    var folderIdStr = "";
                    if (task.TryGetProperty("folderId", out var folderIdProp))
                    {
                        if (folderIdProp.ValueKind == JsonValueKind.Number)
                            folderIdStr = folderIdProp.GetInt64().ToString();
                        else if (folderIdProp.ValueKind == JsonValueKind.String)
                            folderIdStr = folderIdProp.GetString() ?? "";
                    }
                    var currentFolderId = $"{owner}/{folderIdStr}";

                    if (currentFolderId == folderId)
                        return (5, ""); // Use type 5 for tasks to differentiate from calendars
                }
            }
        }
        catch { }

        // Type 3: Contact folders
        try
        {
            var result = await userContext.GetAsync<JsonElement>("/api/v1/contacts/sources");
            if (result.TryGetProperty("success", out var success) && success.GetBoolean() &&
                result.TryGetProperty("sharedLists", out var sharedLists))
            {
                foreach (var folder in sharedLists.EnumerateArray())
                {
                    var owner = folder.TryGetProperty("ownerUsername", out var o) ? o.GetString() : "";
                    var id = folder.TryGetProperty("folderId", out var fid) ? fid.GetInt64() : -1;
                    var currentFolderId = id == -1 ? $"{owner}/gal" : $"{owner}/{id}";

                    if (currentFolderId == folderId)
                        return (3, "");
                }
            }
        }
        catch { }

        // Type 4: Note folders
        try
        {
            var result = await userContext.GetAsync<JsonElement>("/api/v1/notes/sources");
            if (result.TryGetProperty("success", out var success) && success.GetBoolean() &&
                result.TryGetProperty("sharedLists", out var sharedLists))
            {
                foreach (var folder in sharedLists.EnumerateArray())
                {
                    var owner = folder.TryGetProperty("ownerUsername", out var o) ? o.GetString() : "";
                    var folderIdNum = folder.TryGetProperty("folderId", out var fid) ? fid.GetInt64() : 0;
                    var currentFolderId = $"{owner}/{folderIdNum}";

                    if (currentFolderId == folderId)
                        return (4, "");
                }
            }
        }
        catch { }

        return (0, ""); // Not found
    }

    [McpServerTool(Destructive = true)]
    [Description("Remove folders for the user")]
    public static async Task<string> RemoveFolder(
        [Description(
            "The id's of the folders to remove, in the format 'owner/numeric_id'. Child folders are also removed. (comma separated)")]
        string folderIds,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, message = "Read-only mode is enabled. Cannot remove folders." });

        try
        {
            var targetFolderIds = folderIds.Split(',').Select(f => f.Trim()).Where(f => !string.IsNullOrEmpty(f))
                .ToList();
            var removedCount = 0;
            var errors = new List<string>();

            foreach (var folderId in targetFolderIds)
                try
                {
                    // Parse the folderId to extract the numeric ID
                    // Format is either "owner/id" or just "id"
                    var parts = folderId.Split('/');
                    var numericIdStr = parts.Length > 1 ? parts[1] : parts[0];

                    if (!long.TryParse(numericIdStr, out var numericId))
                    {
                        errors.Add($"Could not remove folder {folderId} - invalid folder ID format");
                        continue;
                    }

                    // Try to determine folder type and get folder name for email folders
                    var (folderType, folderName) = await DetermineFolderTypeAndName(userContext, folderId);

                    if (folderType == 0)
                    {
                        errors.Add($"Could not remove folder {folderId} - folder not found in any folder list");
                        continue;
                    }

                    // Each folder type uses a different API endpoint
                    if (folderType == 1) // Email folders - use Folder property with the folder path
                    {
                        var result = await userContext.PostAsync<JsonElement>("/api/v1/folders/delete-folder",
                            new { Folder = folderName });
                        if (result.TryGetProperty("success", out var success) && success.GetBoolean())
                        {
                            removedCount++;
                        }
                        else
                        {
                            var apiMessage = result.TryGetProperty("message", out var msg) ? msg.GetString() : "unknown error";
                            errors.Add($"Could not remove folder {folderId} - API error: {apiMessage}");
                        }
                    }
                    else if (folderType == 2) // Calendar/Tasks folders
                    {
                        // Convert numeric folder ID to GUID using utility function
                        var sourceIdPath = await userContext.GetSourceIdPathFromFolderIdAsync(folderId,
                            userContext.Username, userContext.Domain);
                        if (sourceIdPath == null)
                        {
                            errors.Add($"Could not remove folder {folderId} - failed to get source ID");
                            continue;
                        }

                        // Split sourceIdPath to get owner and guid (format is "owner/guid")
                        var sourceIdParts = sourceIdPath.Split('/');
                        var owner = sourceIdParts[0];
                        var guid = sourceIdParts[1];

                        // This endpoint returns 200 OK on success with no body
                        var success = await userContext.PostWithoutResponseAsync($"/api/v1/calendars/calendar-delete/{guid}/{owner}");
                        if (success)
                        {
                            removedCount++;
                        }
                        else
                        {
                            errors.Add($"Could not remove folder {folderId} - API returned error");
                        }
                    }
                    else if (folderType == 3) // Contacts folders
                    {
                        // Convert numeric folder ID to GUID using utility function
                        var sourceIdPath = await userContext.GetSourceIdPathFromFolderIdAsync(folderId,
                            userContext.Username, userContext.Domain);
                        if (sourceIdPath == null)
                        {
                            errors.Add($"Could not remove folder {folderId} - failed to get source ID");
                            continue;
                        }

                        // Split sourceIdPath to get owner and guid (format is "owner/guid")
                        var sourceIdParts = sourceIdPath.Split('/');
                        var guid = sourceIdParts[1];

                        var result = await userContext.PostAsync<JsonElement>("/api/v1/contacts/address-book/delete",
                            new { uid = guid });
                        if (result.TryGetProperty("success", out var success) && success.GetBoolean())
                        {
                            removedCount++;
                        }
                        else
                        {
                            var apiMessage = result.TryGetProperty("message", out var msg) ? msg.GetString() : "unknown error";
                            errors.Add($"Could not remove folder {folderId} - API error: {apiMessage}");
                        }
                    }
                    else if (folderType == 4) // Notes folders
                    {
                        // Convert numeric folder ID to GUID using utility function
                        var sourceIdPath = await userContext.GetSourceIdPathFromFolderIdAsync(folderId,
                            userContext.Username, userContext.Domain);
                        if (sourceIdPath == null)
                        {
                            errors.Add($"Could not remove folder {folderId} - failed to get source ID");
                            continue;
                        }

                        // Split sourceIdPath to get owner and guid (format is "owner/guid")
                        var sourceIdParts = sourceIdPath.Split('/');
                        var guid = sourceIdParts[1];

                        var result = await userContext.PostAsync<JsonElement>("/api/v1/notes/sources/delete",
                            new { uid = guid });
                        if (result.TryGetProperty("success", out var success) && success.GetBoolean())
                        {
                            removedCount++;
                        }
                        else
                        {
                            var apiMessage = result.TryGetProperty("message", out var msg) ? msg.GetString() : "unknown error";
                            errors.Add($"Could not remove folder {folderId} - API error: {apiMessage}");
                        }
                    }
                    else if (folderType == 5) // Tasks folders
                    {
                        // Convert numeric folder ID to GUID using utility function
                        var sourceIdPath = await userContext.GetSourceIdPathFromFolderIdAsync(folderId,
                            userContext.Username, userContext.Domain);
                        if (sourceIdPath == null)
                        {
                            errors.Add($"Could not remove folder {folderId} - failed to get source ID");
                            continue;
                        }

                        // Split sourceIdPath to get owner and guid (format is "owner/guid")
                        var sourceIdParts = sourceIdPath.Split('/');
                        var guid = sourceIdParts[1];

                        var result = await userContext.PostAsync<JsonElement>("/api/v1/tasks/sources/delete",
                            new { uid = guid });
                        if (result.TryGetProperty("success", out var success) && success.GetBoolean())
                        {
                            removedCount++;
                        }
                        else
                        {
                            var apiMessage = result.TryGetProperty("message", out var msg) ? msg.GetString() : "unknown error";
                            errors.Add($"Could not remove folder {folderId} - API error: {apiMessage}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Error removing folder {folderId}: {ex.Message}");
                }

            var message = removedCount > 0 ? $"Removed {removedCount} folder(s)." : "No folders were removed.";
            if (errors.Count > 0) message += $"\nErrors: {string.Join(", ", errors)}";

            return JsonSerializer.Serialize(new { success = removedCount > 0, removedCount, message });
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
    [Description("Update or create a folder for the user")]
    public static async Task<string> UpdateFolder(
        [Description("The type of the folder to update (email, calendar, notes, tasks, contacts)")]
        string type,
        [Description("The new name for the folder")]
        string name,
        [Description("The parent folder ID (owner/numeric_id) to nest the folder in (only for email create/update)")]
        string parentFolderId,
        [Description("The ID (owner/numeric_id) of the folder to update. If omitted, a new folder is created")]
        string folderId,
        [Description("The hex color for the folder (only for calendars and tasks, e.g., #FF0000)")]
        string color,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, message = "Read-only mode is enabled. Cannot update folders." });

        try
        {
            type = type.ToLower();
            var isUpdate = !string.IsNullOrWhiteSpace(folderId);

            // Extract the numeric ID and owner from folderId (format: "owner/numericId")
            var numericId = "";
            var owner = "";
            if (isUpdate && !string.IsNullOrWhiteSpace(folderId))
            {
                var parts = folderId.Split('/');
                if (parts.Length > 1)
                {
                    owner = parts[0];
                    numericId = parts[1];
                }
                else
                {
                    numericId = parts[0];
                }
            }

            switch (type)
            {
                case "email":
                {
                    if (isUpdate)
                    {
                        // For email folder rename/update, use folder-patch endpoint
                        var (folderType, oldFolderPath) = await DetermineFolderTypeAndName(userContext, folderId);
                        if (folderType != 1)
                            return JsonSerializer.Serialize(new
                                { success = false, message = $"Email folder '{folderId}' not found." });

                        // Extract just the folder name from the full path
                        var folderName = oldFolderPath;
                        var parentPath = "";
                        if (oldFolderPath.Contains('/'))
                        {
                            var parts = oldFolderPath.Split('/');
                            folderName = parts[parts.Length - 1];  // Get last part (folder name)
                            parentPath = string.Join("/", parts.Take(parts.Length - 1));
                        }

                        // Determine new parent folder path
                        var newParentPath = parentPath; // Default to same parent
                        if (!string.IsNullOrWhiteSpace(parentFolderId) && parentFolderId != "null")
                        {
                            newParentPath = await userContext.GetFolderPathFromFolderIdAsync(parentFolderId,
                                userContext.Username, userContext.Domain);
                            if (newParentPath == null)
                                return JsonSerializer.Serialize(new
                                    { success = false, message = $"Parent folder ID '{parentFolderId}' not found." });

                            // Strip owner prefix from path since API expects just the path
                            if (newParentPath.Contains('/'))
                            {
                                var pathParts = newParentPath.Split('/', 2);
                                if (pathParts.Length > 1)
                                    newParentPath = pathParts[1];
                            }
                        }

                        var payload = new
                        {
                            folder = folderName,  // Send just the folder name, not the full path
                            newFolder = name,
                            parentFolder = parentPath,
                            newParentFolder = newParentPath
                        };
                        var response = await userContext.PostAsync<JsonElement>("/api/v1/folders/folder-patch", payload);
                        return JsonSerializer.Serialize(response);
                    }
                    else
                    {
                        var parentPath = "";
                        if (!string.IsNullOrWhiteSpace(parentFolderId) && parentFolderId != "null")
                        {
                            parentPath = await userContext.GetFolderPathFromFolderIdAsync(parentFolderId,
                                userContext.Username, userContext.Domain);
                            if (parentPath == null)
                                return JsonSerializer.Serialize(new
                                    { success = false, message = $"Parent folder ID '{parentFolderId}' not found." });

                            // Strip owner prefix from path since API will add it automatically
                            if (parentPath.Contains('/'))
                            {
                                var pathParts = parentPath.Split('/', 2);
                                if (pathParts.Length > 1)
                                    parentPath = pathParts[1];
                            }
                        }

                        var payload = new { folder = name, parentFolder = parentPath };
                        var response = await userContext.PostAsync<JsonElement>("/api/v1/folders/folder-put", payload);
                        return JsonSerializer.Serialize(response);
                    }
                }

                case "calendar":
                {
                    if (isUpdate)
                    {
                        // Use the rename endpoint for calendar rename
                        if (!long.TryParse(numericId, out var calendarId))
                            return JsonSerializer.Serialize(new
                                { success = false, message = $"Invalid calendar ID: {numericId}" });

                        var payload = new
                        {
                            id = calendarId,
                            name = name,
                            owner = owner
                        };
                        var response = await userContext.PostAsync<JsonElement>("/api/v1/calendars/rename", payload);
                        return JsonSerializer.Serialize(response);
                    }
                    else
                    {
                        var payload = new
                        {
                            setting = new
                            {
                                friendlyName = name,
                                calendarViewColor = color ?? "#7FC56F",
                                isPrimary = false
                            }
                        };
                        var response =
                            await userContext.PostAsync<JsonElement>("/api/v1/calendars/calendar-put", payload);
                        return JsonSerializer.Serialize(response);
                    }
                }

                case "tasks":
                {
                    var payload = new { folder = name, uid = "", color = color ?? "#7FC56F" };
                    var endpoint = isUpdate ? "/api/v1/tasks/sources/edit" : "/api/v1/tasks/sources/add";
                    if (isUpdate)
                    {
                        // Convert numeric folder ID to GUID using utility function
                        var sourceIdPath = await userContext.GetSourceIdPathFromFolderIdAsync(folderId,
                            userContext.Username, userContext.Domain);
                        if (sourceIdPath == null)
                            return JsonSerializer.Serialize(new
                                { success = false, message = $"Task folder '{folderId}' not found." });

                        var uid = sourceIdPath.Split('/')[1]; // Extract GUID from "owner/guid"
                        payload = new { folder = name, uid = uid, color = color ?? "#7FC56F" };
                    }

                    var response = await userContext.PostAsync<JsonElement>(endpoint, payload);
                    return JsonSerializer.Serialize(response);
                }

                case "notes":
                {
                    var payload = new { folder = name, uid = "" };
                    var endpoint = isUpdate ? "/api/v1/notes/sources/edit" : "/api/v1/notes/sources/add";
                    if (isUpdate)
                    {
                        // Convert numeric folder ID to GUID using utility function
                        var sourceIdPath = await userContext.GetSourceIdPathFromFolderIdAsync(folderId,
                            userContext.Username, userContext.Domain);
                        if (sourceIdPath == null)
                            return JsonSerializer.Serialize(new
                                { success = false, message = $"Note folder '{folderId}' not found." });

                        var uid = sourceIdPath.Split('/')[1]; // Extract GUID from "owner/guid"
                        payload = new { folder = name, uid = uid };
                    }

                    var response = await userContext.PostAsync<JsonElement>(endpoint, payload);
                    return JsonSerializer.Serialize(response);
                }

                case "contacts":
                {
                    var payload = new { folder = name, uid = "" };
                    var endpoint =
                        isUpdate ? "/api/v1/contacts/address-book/edit" : "/api/v1/contacts/address-book/add";
                    if (isUpdate)
                    {
                        // Convert numeric folder ID to GUID using utility function
                        var sourceIdPath = await userContext.GetSourceIdPathFromFolderIdAsync(folderId,
                            userContext.Username, userContext.Domain);
                        if (sourceIdPath == null)
                            return JsonSerializer.Serialize(new
                                { success = false, message = $"Contact folder '{folderId}' not found." });

                        var uid = sourceIdPath.Split('/')[1]; // Extract GUID from "owner/guid"
                        payload = new { folder = name, uid = uid };
                    }

                    var response = await userContext.PostAsync<JsonElement>(endpoint, payload);
                    return JsonSerializer.Serialize(response);
                }

                default:
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        message =
                            $"Folder type '{type}' not supported. Supported types are: email, calendar, notes, tasks, contacts"
                    });
            }
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody, endpoint = apiEx.Endpoint });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, message = $"Exception: {ex.Message}" });
        }
    }
}