using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Server.Tools;

[McpServerToolType]
public sealed class ContactGroupTools
{
    /// <summary>
    /// Contact types from SmarterMail:
    /// Contact = 0 (individual contact)
    /// ContactGroup = 2 (contact group)
    /// </summary>
    private const int ContactTypeGroup = 2;

    /// <summary>
    /// GroupedContact types:
    /// OneOff = 0 (ad-hoc email address)
    /// Contact = 1 (reference to existing contact)
    /// Gal = 2 (global address list entry)
    /// </summary>
    private const int GroupedContactTypeOneOff = 0;

    [McpServerTool(ReadOnly = true)]
    [Description("Get all contact groups from a specific folder")]
    public static async Task<string> GetContactGroups(
        [Description("The ID of the contact folder")]
        string folderId,
        UserContext userContext)
    {
        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 3 to see available contact folders." });

            var response = await userContext.GetAsync<JsonElement>($"api/v1/contacts/address-book/{sourceIdPath}/");

            var groups = new List<object>();
            if (response.TryGetProperty("success", out var success) && success.GetBoolean() &&
                response.TryGetProperty("contacts", out var contacts))
            {
                foreach (var contact in contacts.EnumerateArray())
                {
                    // Check if this is a contact group (contactType == 2)
                    var contactType = contact.TryGetProperty("contactType", out var ct) ? ct.GetInt32() : 0;
                    if (contactType != ContactTypeGroup)
                        continue;

                    var memberCount = 0;
                    var memberEmails = new List<string>();
                    if (contact.TryGetProperty("groupedContacts", out var groupedContacts))
                    {
                        memberCount = groupedContacts.GetArrayLength();
                        foreach (var member in groupedContacts.EnumerateArray())
                        {
                            if (member.TryGetProperty("emailAddress", out var email))
                            {
                                var emailStr = email.GetString();
                                if (!string.IsNullOrEmpty(emailStr))
                                    memberEmails.Add(emailStr);
                            }
                        }
                    }

                    var group = new
                    {
                        id = contact.TryGetProperty("id", out var id)
                            ? (id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString())
                            : null,
                        name = contact.TryGetProperty("displayAs", out var display) ? display.GetString() : null,
                        description = contact.TryGetProperty("additionalInfo", out var info) ? info.GetString() : null,
                        memberCount,
                        memberEmails
                    };
                    groups.Add(group);
                }
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                groups,
                totalCount = groups.Count
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

    [McpServerTool(ReadOnly = true)]
    [Description("Get details of a specific contact group including all members")]
    public static async Task<string> GetContactGroup(
        [Description("The ID of the contact folder")]
        string folderId,
        [Description("The ID of the contact group to retrieve")]
        string groupId,
        UserContext userContext)
    {
        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 3 to see available contact folders." });

            var response = await userContext.GetAsync<JsonElement>($"api/v1/contacts/get/{sourceIdPath}/{groupId}/");

            if (response.TryGetProperty("success", out var success) && success.GetBoolean() &&
                response.TryGetProperty("contact", out var contact))
            {
                // Verify this is a group
                var contactType = contact.TryGetProperty("contactType", out var ct) ? ct.GetInt32() : 0;
                if (contactType != ContactTypeGroup)
                    return JsonSerializer.Serialize(new { success = false, error = $"Contact '{groupId}' is not a group (contactType != 2). Use get_contact_groups to find groups, or use get_contact/update_contact for individual contacts." });

                var members = new List<object>();
                if (contact.TryGetProperty("groupedContacts", out var groupedContacts))
                {
                    foreach (var member in groupedContacts.EnumerateArray())
                    {
                        members.Add(new
                        {
                            type = member.TryGetProperty("type", out var t) ? t.GetInt32() : 0,
                            typeName = GetGroupedContactTypeName(member.TryGetProperty("type", out var tn) ? tn.GetInt32() : 0),
                            emailAddress = member.TryGetProperty("emailAddress", out var email) ? email.GetString() : null,
                            displayName = member.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                            contactId = member.TryGetProperty("contactId", out var cid)
                                ? (cid.ValueKind == JsonValueKind.Number ? cid.GetInt64().ToString() : cid.GetString())
                                : null,
                            folderId = member.TryGetProperty("folderId", out var fid)
                                ? (fid.ValueKind == JsonValueKind.Number ? fid.GetInt64().ToString() : fid.GetString())
                                : null
                        });
                    }
                }

                var group = new
                {
                    id = contact.TryGetProperty("id", out var id)
                        ? (id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString())
                        : null,
                    name = contact.TryGetProperty("displayAs", out var display) ? display.GetString() : null,
                    description = contact.TryGetProperty("additionalInfo", out var info) ? info.GetString() : null,
                    memberCount = members.Count,
                    members
                };

                return JsonSerializer.Serialize(new { success = true, group });
            }

            return JsonSerializer.Serialize(new { success = false, error = $"Contact group not found for ID '{groupId}' in folder '{folderId}'. Use get_contact_groups to find valid group IDs." });
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
    [Description("Create a new contact group in the specified folder")]
    public static async Task<string> CreateContactGroup(
        UserContext userContext,
        [Description("The ID of the contact folder")]
        string folderId,
        [Description("The name of the contact group")]
        string groupName,
        [Description("Comma-separated list of email addresses to include in the group")]
        string memberEmails = "",
        [Description("Optional description for the group")]
        string description = "")
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot create contact groups." });

        try
        {
            if (string.IsNullOrWhiteSpace(groupName))
                return JsonSerializer.Serialize(new { success = false, error = "Group name is required" });

            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 3 to see available contact folders." });

            // Build the grouped contacts array
            var groupedContacts = new List<object>();
            if (!string.IsNullOrWhiteSpace(memberEmails))
            {
                var emails = memberEmails.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var email in emails)
                {
                    groupedContacts.Add(new
                    {
                        type = GroupedContactTypeOneOff,
                        emailAddress = email.Trim(),
                        displayName = email.Trim(),
                        addressType = "SMTP"
                    });
                }
            }

            // Build the contact group data
            var contactData = new Dictionary<string, object>
            {
                ["DisplayAs"] = groupName,
                ["ContactType"] = ContactTypeGroup,
                ["GroupedContacts"] = groupedContacts
            };

            if (!string.IsNullOrWhiteSpace(description))
            {
                contactData["AdditionalInfo"] = description;
                contactData["IsHtml"] = false;
            }

            // Extract the folder UID from the sourceIdPath
            var parts = sourceIdPath.Split('/');
            var folderUid = parts.Length > 1 ? parts[1] : parts[0];

            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/contacts/contact-put/{userContext.Username}/{folderUid}",
                contactData);

            if (response.TryGetProperty("success", out var success) && success.GetBoolean())
            {
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    message = $"Contact group '{groupName}' created with {groupedContacts.Count} members",
                    groupId = response.TryGetProperty("contactId", out var cid) ? cid.GetInt64().ToString() : null,
                    memberCount = groupedContacts.Count
                });
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

    [McpServerTool]
    [Description("Update an existing contact group (add/remove members or rename)")]
    public static async Task<string> UpdateContactGroup(
        UserContext userContext,
        [Description("The ID of the contact folder")]
        string folderId,
        [Description("The ID of the contact group to update")]
        string groupId,
        [Description("Comma-separated list of email addresses to add to the group")]
        string addEmails = "",
        [Description("Comma-separated list of email addresses to remove from the group")]
        string removeEmails = "",
        [Description("New name for the group (leave empty to keep current name)")]
        string newGroupName = "",
        [Description("New description for the group (leave empty to keep current)")]
        string newDescription = "")
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot update contact groups." });

        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 3 to see available contact folders." });

            // Get the existing group
            var existingResponse = await userContext.GetAsync<JsonElement>($"api/v1/contacts/get/{sourceIdPath}/{groupId}/");

            if (!existingResponse.TryGetProperty("success", out var getSuccess) || !getSuccess.GetBoolean() ||
                !existingResponse.TryGetProperty("contact", out var existingContact))
                return JsonSerializer.Serialize(new { success = false, error = $"Contact group not found for ID '{groupId}' in folder '{folderId}'. Use get_contact_groups to find valid group IDs." });

            // Verify this is a group
            var contactType = existingContact.TryGetProperty("contactType", out var ct) ? ct.GetInt32() : 0;
            if (contactType != ContactTypeGroup)
                return JsonSerializer.Serialize(new { success = false, error = $"Contact '{groupId}' is not a group (contactType != 2). Use get_contact_groups to find groups, or use get_contact/update_contact for individual contacts." });

            // Extract current members
            var currentMembers = new List<Dictionary<string, object>>();
            if (existingContact.TryGetProperty("groupedContacts", out var groupedContacts))
            {
                foreach (var member in groupedContacts.EnumerateArray())
                {
                    var memberDict = new Dictionary<string, object>
                    {
                        ["type"] = member.TryGetProperty("type", out var t) ? t.GetInt32() : 0,
                        ["emailAddress"] = member.TryGetProperty("emailAddress", out var email) ? email.GetString() ?? "" : "",
                        ["displayName"] = member.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                        ["addressType"] = member.TryGetProperty("addressType", out var at) ? at.GetString() ?? "SMTP" : "SMTP"
                    };

                    if (member.TryGetProperty("contactId", out var cid) && cid.ValueKind == JsonValueKind.Number)
                        memberDict["contactId"] = cid.GetInt64();
                    if (member.TryGetProperty("folderId", out var fid) && fid.ValueKind == JsonValueKind.Number)
                        memberDict["folderId"] = fid.GetInt64();

                    currentMembers.Add(memberDict);
                }
            }

            // Remove specified emails
            if (!string.IsNullOrWhiteSpace(removeEmails))
            {
                var emailsToRemove = removeEmails.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(e => e.Trim().ToLowerInvariant())
                    .ToHashSet();

                currentMembers = currentMembers
                    .Where(m => !emailsToRemove.Contains((m["emailAddress"] as string ?? "").ToLowerInvariant()))
                    .ToList();
            }

            // Add new emails
            if (!string.IsNullOrWhiteSpace(addEmails))
            {
                var existingEmails = currentMembers
                    .Select(m => (m["emailAddress"] as string ?? "").ToLowerInvariant())
                    .ToHashSet();

                var emailsToAdd = addEmails.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var email in emailsToAdd)
                {
                    if (!existingEmails.Contains(email.Trim().ToLowerInvariant()))
                    {
                        currentMembers.Add(new Dictionary<string, object>
                        {
                            ["type"] = GroupedContactTypeOneOff,
                            ["emailAddress"] = email.Trim(),
                            ["displayName"] = email.Trim(),
                            ["addressType"] = "SMTP"
                        });
                    }
                }
            }

            // Build update payload
            var contactData = new Dictionary<string, object?>
            {
                ["Id"] = groupId,
                ["ContactType"] = ContactTypeGroup,
                ["GroupedContacts"] = currentMembers
            };

            // Update name if provided, otherwise keep existing
            var currentName = existingContact.TryGetProperty("displayAs", out var da) ? da.GetString() : "";
            contactData["DisplayAs"] = !string.IsNullOrWhiteSpace(newGroupName) ? newGroupName : currentName;

            // Update description if provided
            if (!string.IsNullOrWhiteSpace(newDescription))
            {
                contactData["AdditionalInfo"] = newDescription;
                contactData["IsHtml"] = false;
            }
            else if (existingContact.TryGetProperty("additionalInfo", out var ai) && !string.IsNullOrEmpty(ai.GetString()))
            {
                contactData["AdditionalInfo"] = ai.GetString()!;
            }

            var parts = sourceIdPath.Split('/');
            var folderUid = parts.Length > 1 ? parts[1] : parts[0];

            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/contacts/contact-put/{userContext.Username}/{folderUid}",
                contactData);

            if (response.TryGetProperty("success", out var success) && success.GetBoolean())
            {
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    message = $"Contact group updated",
                    groupName = contactData["DisplayAs"],
                    memberCount = currentMembers.Count
                });
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
    [Description("Delete a contact group from the specified folder")]
    public static async Task<string> DeleteContactGroup(
        [Description("The ID of the contact folder")]
        string folderId,
        [Description("The ID of the contact group to delete")]
        string groupId,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot delete contact groups." });

        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 3 to see available contact folders." });

            // Verify this is actually a group before deleting
            var existingResponse = await userContext.GetAsync<JsonElement>($"api/v1/contacts/get/{sourceIdPath}/{groupId}/");

            if (existingResponse.TryGetProperty("success", out var getSuccess) && getSuccess.GetBoolean() &&
                existingResponse.TryGetProperty("contact", out var existingContact))
            {
                var contactType = existingContact.TryGetProperty("contactType", out var ct) ? ct.GetInt32() : 0;
                if (contactType != ContactTypeGroup)
                    return JsonSerializer.Serialize(new { success = false, error = $"Contact '{groupId}' is not a group (contactType != 2). Use delete_contacts for individual contacts." });
            }

            // Extract just the GUID from sourceIdPath (format is "owner/guid")
            var parts = sourceIdPath.Split('/');
            var sourceGuid = parts.Length > 1 ? parts[1] : parts[0];

            var inputData = new[]
            {
                new
                {
                    sourceOwner = userContext.Username,
                    sourceId = sourceGuid,
                    id = groupId
                }
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/contacts/delete-bulk", inputData);

            if (response.TryGetProperty("success", out var success) && success.GetBoolean())
            {
                return JsonSerializer.Serialize(new { success = true, message = "Contact group deleted successfully" });
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

    [McpServerTool(ReadOnly = true)]
    [Description("Expand a contact group to get all member email addresses (including nested groups)")]
    public static async Task<string> ExpandContactGroup(
        [Description("The ID of the contact group to expand")]
        string groupId,
        [Description("Set to true to ignore nested groups and only return direct members")]
        bool ignoreSubgroups = false,
        UserContext userContext = null!)
    {
        try
        {
            // The unpack-group endpoint expands all members including nested groups
            var response = await userContext.GetAsync<JsonElement>(
                $"/api/v1/contacts/unpack-group/{groupId}/{userContext.Username}/{ignoreSubgroups.ToString().ToLower()}");

            if (response.TryGetProperty("success", out var success) && success.GetBoolean())
            {
                var emails = new List<string>();

                // Check for expanded members in various possible property names
                if (response.TryGetProperty("emails", out var emailsArray))
                {
                    foreach (var email in emailsArray.EnumerateArray())
                    {
                        var emailStr = email.GetString();
                        if (!string.IsNullOrEmpty(emailStr))
                            emails.Add(emailStr);
                    }
                }
                else if (response.TryGetProperty("members", out var membersArray))
                {
                    foreach (var member in membersArray.EnumerateArray())
                    {
                        if (member.ValueKind == JsonValueKind.String)
                        {
                            var emailStr = member.GetString();
                            if (!string.IsNullOrEmpty(emailStr))
                                emails.Add(emailStr);
                        }
                        else if (member.TryGetProperty("emailAddress", out var email))
                        {
                            var emailStr = email.GetString();
                            if (!string.IsNullOrEmpty(emailStr))
                                emails.Add(emailStr);
                        }
                    }
                }
                else if (response.TryGetProperty("groupedContacts", out var groupedContacts))
                {
                    foreach (var member in groupedContacts.EnumerateArray())
                    {
                        if (member.TryGetProperty("emailAddress", out var email))
                        {
                            var emailStr = email.GetString();
                            if (!string.IsNullOrEmpty(emailStr))
                                emails.Add(emailStr);
                        }
                    }
                }

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    emails = emails.Distinct().ToList(),
                    count = emails.Distinct().Count()
                });
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

    private static string GetGroupedContactTypeName(int type)
    {
        return type switch
        {
            0 => "OneOff",
            1 => "Contact",
            2 => "GlobalAddressList",
            _ => $"Unknown({type})"
        };
    }
}
