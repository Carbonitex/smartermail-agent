using System.ComponentModel;
using System.Net;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Server.Tools;

[McpServerToolType]
public sealed class NoteTools
{
    [McpServerTool(ReadOnly = true)]
    [Description("Get notes from a specific note source")]
    public static async Task<string> GetNotes(
        [Description("The ID of the note source")]
        string folderId,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 4 to see available note folders." });

            var response = await userContext.GetAsync<JsonElement>($"/api/v1/notes/notes/{sourceIdPath}");

            // Parse the response to modify the notes
            using var document = JsonDocument.Parse(response.GetRawText());
            var root = document.RootElement;

            // Check if there's a notes array
            if (root.TryGetProperty("notes", out var notesArray) && notesArray.ValueKind == JsonValueKind.Array)
            {
                var modifiedNotes = new List<Dictionary<string, object?>>();

                foreach (var note in notesArray.EnumerateArray())
                {
                    var noteDict = new Dictionary<string, object?>();

                    // Copy all properties
                    foreach (var prop in note.EnumerateObject())
                    {
                        if (prop.Name == "text")
                        {
                            var text = prop.Value.GetString() ?? "";
                            var isTruncated = text.Length > 500;

                            // Truncate if necessary
                            if (isTruncated)
                            {
                                var truncated = text.Substring(0, 500) + "...";
                                noteDict["text"] = truncated;
                                noteDict["body"] = truncated;
                                noteDict["isTruncated"] = true;
                            }
                            else
                            {
                                noteDict["text"] = text;
                                noteDict["body"] = text;
                                noteDict["isTruncated"] = false;
                            }
                        }
                        else
                        {
                            // Copy the property as-is based on its type
                            var val = prop.Value.ValueKind switch
                            {
                                JsonValueKind.String => (object?)prop.Value.GetString(),
                                JsonValueKind.Number => prop.Value.GetDouble(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                JsonValueKind.Null => null,
                                _ => prop.Value.GetRawText()
                            };
                            noteDict[prop.Name] = val;

                            // Map for frontend consistency
                            if (prop.Name == "modified" || prop.Name == "lastModified" || prop.Name == "updated") 
                                noteDict["modifiedDate"] = val;
                            if (prop.Name == "created") 
                                noteDict["createdDate"] = val;
                        }
                    }

                    modifiedNotes.Add(noteDict);
                }

                // Build the response with modified notes
                var result = new Dictionary<string, object?>
                {
                    ["count"] = root.TryGetProperty("count", out var count) ? count.GetInt32() : modifiedNotes.Count,
                    ["notes"] = modifiedNotes,
                    ["success"] = root.TryGetProperty("success", out var success) ? success.GetBoolean() : true,
                    ["message"] = root.TryGetProperty("message", out var message) ? message.GetString() : ""
                };

                return JsonSerializer.Serialize(result);
            }

            // If no notes array, return as-is
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

    [McpServerTool(ReadOnly = true)]
    [Description("Get a single note's full information by ID")]
    public static async Task<string> GetNote(
        [Description("The ID of the note to retrieve")]
        string noteId,
        [Description("The ID of the note source")]
        string folderId,
        UserContext userContext)
    {
        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 4 to see available note folders." });

            // Flip the source_id_path order (source_id/owner)
            var parts = sourceIdPath.Split('/');
            var flippedPath = $"{parts[1]}/{parts[0]}";

            var response = await userContext.GetAsync<JsonElement>($"/api/v1/notes/note/{noteId}/{flippedPath}");
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
    [Description("Create a new note in the specified folder")]
    public static async Task<string> CreateNote(
        [Description("The ID of the note source")]
        string folderId,
        [Description("Title for the note")] string subject,
        [Description("The html or plain text content of the note")]
        string content,
        [Description("The color to associate with the note (white, yellow, pink, green, blue)")]
        string color,
        [Description("Whether the content is HTML")]
        bool isHtml,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot create notes." });

        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 4 to see available note folders." });

            // Flip the source_id_path order (source_id/owner)
            var parts = sourceIdPath.Split('/');
            var flippedPath = $"{parts[1]}/{parts[0]}";

            var payload = new
            {
                subject,
                text = content,
                color = color ?? "",
                isPlainText = !isHtml
            };

            var response = await userContext.PostAsync<JsonElement>($"/api/v1/notes/note-put/{flippedPath}", payload);
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
    [Description("Update a note in the specified folder")]
    public static async Task<string> UpdateNote(
        [Description("The ID of the note to update")]
        string noteId,
        [Description("The ID of the note source")]
        string folderId,
        [Description("Title for the note")] string subject,
        [Description("The html or plain text content of the note")]
        string content,
        [Description("The hex color to associate with the note")]
        string color,
        [Description("Whether the content is HTML")]
        bool isHtml,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot update notes." });

        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 4 to see available note folders." });

            // Flip the source_id_path order (source_id/owner)
            var parts = sourceIdPath.Split('/');
            var flippedPath = $"{parts[1]}/{parts[0]}";

            var payload = new
            {
                subject,
                text = content,
                color = color ?? "",
                isPlainText = !isHtml
            };

            var response =
                await userContext.PostAsync<JsonElement>($"/api/v1/notes/note-patch/{noteId}/{flippedPath}", payload);
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
    [Description("Delete a note in the specified folder")]
    public static async Task<string> DeleteNote(
        [Description("The ID of the note to delete")]
        string noteId,
        [Description("The ID of the note source")]
        string folderId,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot delete notes." });

        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 4 to see available note folders." });

            // Flip the source_id_path order (source_id/owner)
            var parts = sourceIdPath.Split('/');
            var flippedPath = $"{parts[1]}/{parts[0]}";

            var response =
                await userContext.PostAsync<JsonElement>($"/api/v1/notes/note-delete/{noteId}/{flippedPath}", new { });
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
}