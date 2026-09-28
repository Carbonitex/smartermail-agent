using System.ComponentModel;
using System.Net;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Server.Tools;

[McpServerToolType]
public sealed class TaskTools
{
    [McpServerTool(ReadOnly = true)]
    [Description("Get all tasks from a specific task folder")]
    public static async Task<string> GetTasks(
        [Description("The ID of the task source")]
        string folderId,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 2 to see available task folders." });

            var endpoint = $"/api/v1/tasks/{sourceIdPath}";

            var response = await userContext.GetAsync<JsonElement>(endpoint);

            // Parse the response to modify the tasks
            using var document = JsonDocument.Parse(response.GetRawText());
            var root = document.RootElement;

            // Check if there's a details array
            if (root.TryGetProperty("details", out var detailsArray) && detailsArray.ValueKind == JsonValueKind.Array)
            {
                var modifiedTasks = new List<Dictionary<string, object?>>();

                foreach (var task in detailsArray.EnumerateArray())
                {
                    var taskDict = new Dictionary<string, object?>();

                    // Copy all properties
                    foreach (var prop in task.EnumerateObject())
                    {
                        if (prop.Name == "description")
                        {
                            var description = prop.Value.GetString() ?? "";
                            var isTruncated = description.Length > 500;

                            // Truncate if necessary
                            if (isTruncated)
                            {
                                taskDict["description"] = description.Substring(0, 500) + "...";
                                taskDict["isTruncated"] = true;
                            }
                            else
                            {
                                taskDict["description"] = description;
                                taskDict["isTruncated"] = false;
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
                                JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object>>(prop.Value.GetRawText()),
                                JsonValueKind.Array => JsonSerializer.Deserialize<List<object>>(prop.Value.GetRawText()),
                                _ => prop.Value.GetRawText()
                            };
                            taskDict[prop.Name] = val;

                            // Map for frontend consistency
                            if (prop.Name == "start") taskDict["startDate"] = val;
                            if (prop.Name == "due") taskDict["dueDate"] = val;
                        }
                    }

                    modifiedTasks.Add(taskDict);
                }

                // Build the response with modified tasks
                var result = new Dictionary<string, object?>
                {
                    ["tasks"] = modifiedTasks
                };

                return JsonSerializer.Serialize(result);
            }

            // If no details array, return as-is
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
    [Description("Get details for a specific task")]
    public static async Task<string> GetTaskDetails(
        [Description("The ID of the task source")]
        string folderId,
        [Description("The ID of the task to retrieve")]
        string taskId,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 2 to see available task folders." });

            var endpoint = $"/api/v1/tasks/{sourceIdPath}/{taskId}";

            var response = await userContext.GetAsync<JsonElement>(endpoint);
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
    [Description("Delete a task from the specified folder")]
    public static async Task<string> DeleteTask(
        [Description("The ID of the task source")]
        string folderId,
        [Description("The ID of the task to delete")]
        string taskId,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot delete tasks." });

        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 2 to see available task folders." });

            var parts = sourceIdPath.Split('/');
            var sourceOwner = parts[0];
            var sourceId = parts[1];

            // API expects an array of TaskMetaData objects
            var payload = new[]
            {
                new
                {
                    sourceId,
                    sourceOwner,
                    id = taskId
                }
            };

            var success = await userContext.PostWithoutResponseAsync("/api/v1/tasks/delete", payload);
            return JsonSerializer.Serialize(new { success });
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
    [Description("Create a new task or update an existing one")]
    public static async Task<string> NewOrUpdateTask(
        [Description("The subject of the task")]
        string subject,
        [Description("The ID of the task source (if null, primary source will be used)")]
        string folderId,
        [Description("The ID of the task to update (if it exists)")]
        string taskId,
        [Description("The description of the task")]
        string description,
        [Description("The percentage of the task that is complete (0-100)")]
        int percentComplete,
        [Description("The start date of the task (YYYY-MM-DD HH:mm)")]
        string start,
        [Description("The due date of the task (YYYY-MM-DD HH:mm)")]
        string due,
        [Description("The reminder date of the task (YYYY-MM-DD HH:mm)")]
        string reminder,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot create/update tasks." });

        try
        {
            string sourceId;
            string sourceOwner;

            if (string.IsNullOrWhiteSpace(folderId))
            {
                // Get primary task source
                var sourcesResponse = await userContext.GetAsync<JsonElement>("/api/v1/tasks/sources");

                JsonElement? primarySource = null;
                if (sourcesResponse.TryGetProperty("sources", out var sources))
                    foreach (var source in sources.EnumerateArray())
                        if (source.TryGetProperty("isPrimary", out var isPrimary) && isPrimary.GetBoolean())
                        {
                            primarySource = source;
                            break;
                        }

                if (primarySource == null)
                    return JsonSerializer.Serialize(new { success = false, error = "No task folder found. Use list_folder_info_by_type with type 2 to see available task folders, then pass a folderId." });

                sourceId = primarySource.Value.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                sourceOwner = primarySource.Value.TryGetProperty("owner", out var owner) ? owner.GetString() ?? "" : "";
            }
            else
            {
                var sourceIdPath =
                    await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username,
                        userContext.Domain);
                if (sourceIdPath == null)
                    return JsonSerializer.Serialize(new
                        { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 2 to see available task folders." });

                var parts = sourceIdPath.Split('/');
                if (parts.Length < 2)
                    return JsonSerializer.Serialize(new { success = false, error = $"Invalid source ID path for '{folderId}'. Use list_folder_info_by_type with type 2 to see available task folder IDs." });

                sourceOwner = parts[0];
                sourceId = parts[1];
            }

            // If due date is provided but no start date, use current date/time for start
            if (!string.IsNullOrWhiteSpace(due) && string.IsNullOrWhiteSpace(start))
                start = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            // Build task data matching Python structure - include all fields even if null
            var taskData = new Dictionary<string, object?>
            {
                ["id"] = string.IsNullOrWhiteSpace(taskId) ? null : taskId,
                ["subject"] = subject,
                ["description"] = string.IsNullOrWhiteSpace(description) ? null : description,
                ["percentComplete"] = percentComplete,
                ["start"] = string.IsNullOrWhiteSpace(start) ? null : start,
                ["due"] = string.IsNullOrWhiteSpace(due) ? null : due,
                ["reminder"] = string.IsNullOrWhiteSpace(reminder) ? null : reminder,
                ["sourceId"] = sourceId,
                ["sourceOwner"] = sourceOwner,
                ["useDateTime"] = !string.IsNullOrWhiteSpace(start) || !string.IsNullOrWhiteSpace(due),
                ["reminderSet"] = !string.IsNullOrWhiteSpace(reminder)
            };

            // API expects an array of tasks, so wrap in array
            var tasksArray = new[] { taskData };
            var response = await userContext.PostAsync<JsonElement>("/api/v1/tasks/save", tasksArray);

            // API returns an array of TaskDetails objects
            if (response.ValueKind == JsonValueKind.Array)
            {
                if (response.GetArrayLength() == 0)
                {
                    // Empty array might indicate an error or no tasks to return
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "API returned empty array - task may not have been saved"
                    });
                }

                // Get the first task from the returned array
                var taskResult = response[0];

                // Extract the task ID from the response
                var resultTaskId = string.Empty;
                if (taskResult.TryGetProperty("id", out var id))
                {
                    resultTaskId = id.ValueKind == JsonValueKind.Number
                        ? id.GetInt64().ToString()
                        : id.GetString() ?? string.Empty;
                }

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    message = $"Task '{subject}' successfully {(string.IsNullOrWhiteSpace(taskId) ? "created" : "updated")}",
                    taskId = resultTaskId,
                    sourceId,
                    sourceOwner
                });
            }

            // Handle error responses
            var error = response.TryGetProperty("error", out var err) ? err.GetString() : "Failed to save task";
            var code = response.TryGetProperty("code", out var c) ? c.GetString() : null;
            return JsonSerializer.Serialize(new { success = false, error, code });
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