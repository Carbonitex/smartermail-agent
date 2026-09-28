using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using SmarterMailMcp.Client.Services;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Server.Tools;

[McpServerToolType]
public sealed class MailTools
{
    private const int DefaultWindowLimit = 8000;
    private const int MaxWindowChars = 200_000;
    private const int GetMessagePreviewChars = 4000;
    private const int GetMessageBodyCap = 10_000;
    private static readonly TimeSpan MessageCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, MessageCacheEntry> MessageCache = new();

    private sealed record MessageCacheEntry(JsonElement MessageData, DateTime ExpiresUtc);

    /// <summary>
    /// Expands contact group names and individual contact names to email addresses.
    /// Supports:
    /// - Direct email addresses (passed through)
    /// - "group:GroupName" syntax to force group lookup
    /// - Contact/group name lookup by displayAs
    /// </summary>
    private static async Task<string> ExpandRecipientsAsync(string recipients, UserContext userContext)
    {
        if (string.IsNullOrWhiteSpace(recipients))
            return recipients;

        var expandedEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = recipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Cache contact sources to avoid repeated API calls
        List<(string sourceIdPath, JsonElement contacts)>? contactSources = null;

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            // Check for explicit group: prefix
            if (trimmed.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
            {
                var groupName = trimmed.Substring(6).Trim();
                contactSources ??= await LoadContactSourcesAsync(userContext);
                var expanded = await ExpandGroupByNameAsync(groupName, contactSources, userContext);
                foreach (var email in expanded)
                    expandedEmails.Add(email);
                continue;
            }

            // If it contains @, treat as email address
            if (trimmed.Contains('@'))
            {
                // Strip any display name formatting like "Name" <email@domain.com>
                var email = ExtractEmailAddress(trimmed);
                if (!string.IsNullOrEmpty(email))
                    expandedEmails.Add(email);
                continue;
            }

            // Otherwise, look up by name in contacts
            contactSources ??= await LoadContactSourcesAsync(userContext);
            var lookupResult = await LookupContactByNameAsync(trimmed, contactSources, userContext);
            if (lookupResult.found)
            {
                foreach (var email in lookupResult.emails)
                    expandedEmails.Add(email);
            }
            else
            {
                // Not found - keep original to let API report the error
                expandedEmails.Add(trimmed);
            }
        }

        return string.Join(", ", expandedEmails);
    }

    private static string ExtractEmailAddress(string input)
    {
        // Handle formats like: "Name" <email@domain.com> or <email@domain.com> or email@domain.com
        var match = Regex.Match(input, @"<([^>]+@[^>]+)>");
        if (match.Success)
            return match.Groups[1].Value.Trim();

        // If no angle brackets, return as-is if it looks like an email
        if (input.Contains('@'))
            return input.Trim();

        return input;
    }

    private static async Task<List<(string sourceIdPath, JsonElement contacts)>> LoadContactSourcesAsync(UserContext userContext)
    {
        var sources = new List<(string sourceIdPath, JsonElement contacts)>();

        try
        {
            // Get contact sources
            var sourcesResponse = await userContext.GetAsync<JsonElement>("/api/v1/contacts/sources");
            if (sourcesResponse.TryGetProperty("success", out var sourcesSuccess) && sourcesSuccess.GetBoolean() &&
                sourcesResponse.TryGetProperty("sharedLists", out var sharedLists))
            {
                foreach (var source in sharedLists.EnumerateArray())
                {
                    var sourceOwner = source.TryGetProperty("ownerUsername", out var owner) ? owner.GetString() : "";
                    var sourceGuid = source.TryGetProperty("itemID", out var guid) ? guid.GetString() : null;

                    if (!string.IsNullOrEmpty(sourceGuid))
                    {
                        var sourceIdPath = $"{sourceOwner}/{sourceGuid}";
                        try
                        {
                            var contactsResponse = await userContext.GetAsync<JsonElement>($"api/v1/contacts/address-book/{sourceIdPath}/");
                            if (contactsResponse.TryGetProperty("success", out var contactsSuccess) && contactsSuccess.GetBoolean() &&
                                contactsResponse.TryGetProperty("contacts", out var contacts))
                            {
                                sources.Add((sourceIdPath, contacts));
                            }
                        }
                        catch
                        {
                            // Skip sources that fail to load
                        }
                    }
                }
            }
        }
        catch
        {
            // Return empty list on failure
        }

        return sources;
    }

    private static async Task<List<string>> ExpandGroupByNameAsync(
        string groupName,
        List<(string sourceIdPath, JsonElement contacts)> contactSources,
        UserContext userContext)
    {
        var emails = new List<string>();

        foreach (var (sourceIdPath, contacts) in contactSources)
        {
            foreach (var contact in contacts.EnumerateArray())
            {
                var displayAs = contact.TryGetProperty("displayAs", out var da) ? da.GetString() : null;
                var contactType = contact.TryGetProperty("contactType", out var ct) ? ct.GetInt32() : 0;

                // Check if this is a group with matching name
                if (contactType == 2 && string.Equals(displayAs, groupName, StringComparison.OrdinalIgnoreCase))
                {
                    var contactId = contact.TryGetProperty("id", out var id)
                        ? (id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString())
                        : null;

                    if (!string.IsNullOrEmpty(contactId))
                    {
                        // Try to expand the group using the unpack-group endpoint
                        var expanded = await ExpandGroupByIdAsync(contactId, userContext);
                        if (expanded.Count > 0)
                            return expanded;

                        // Fallback: read groupedContacts directly
                        if (contact.TryGetProperty("groupedContacts", out var groupedContacts))
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
                        return emails;
                    }
                }
            }
        }

        return emails;
    }

    private static async Task<List<string>> ExpandGroupByIdAsync(string groupId, UserContext userContext)
    {
        var emails = new List<string>();

        try
        {
            var response = await userContext.GetAsync<JsonElement>(
                $"/api/v1/contacts/unpack-group/{groupId}/{userContext.Username}/false");

            if (response.TryGetProperty("success", out var success) && success.GetBoolean())
            {
                // Try various property names the API might use
                JsonElement? membersElement = null;

                if (response.TryGetProperty("emails", out var emailsArray))
                    membersElement = emailsArray;
                else if (response.TryGetProperty("members", out var membersArray))
                    membersElement = membersArray;
                else if (response.TryGetProperty("groupedContacts", out var groupedContacts))
                    membersElement = groupedContacts;

                if (membersElement.HasValue)
                {
                    foreach (var member in membersElement.Value.EnumerateArray())
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
            }
        }
        catch
        {
            // Return empty on failure
        }

        return emails;
    }

    private static async Task<(bool found, List<string> emails)> LookupContactByNameAsync(
        string name,
        List<(string sourceIdPath, JsonElement contacts)> contactSources,
        UserContext userContext)
    {
        foreach (var (sourceIdPath, contacts) in contactSources)
        {
            foreach (var contact in contacts.EnumerateArray())
            {
                var displayAs = contact.TryGetProperty("displayAs", out var da) ? da.GetString() : null;

                if (string.Equals(displayAs, name, StringComparison.OrdinalIgnoreCase))
                {
                    var contactType = contact.TryGetProperty("contactType", out var ct) ? ct.GetInt32() : 0;

                    if (contactType == 2)
                    {
                        // This is a group - expand it
                        var contactId = contact.TryGetProperty("id", out var id)
                            ? (id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString())
                            : null;

                        if (!string.IsNullOrEmpty(contactId))
                        {
                            var expanded = await ExpandGroupByIdAsync(contactId, userContext);
                            if (expanded.Count > 0)
                                return (true, expanded);
                        }

                        // Fallback: read groupedContacts directly
                        var emails = new List<string>();
                        if (contact.TryGetProperty("groupedContacts", out var groupedContacts))
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
                        return (true, emails);
                    }
                    else
                    {
                        // Individual contact - get email
                        string? emailAddress = null;
                        if (contact.TryGetProperty("emailAddressList", out var emailList) && emailList.GetArrayLength() > 0)
                        {
                            var firstEmail = emailList[0];
                            if (firstEmail.ValueKind == JsonValueKind.String)
                                emailAddress = firstEmail.GetString();
                            else if (firstEmail.TryGetProperty("address", out var addr))
                                emailAddress = addr.GetString();
                        }

                        if (!string.IsNullOrEmpty(emailAddress))
                            return (true, new List<string> { emailAddress });
                    }
                }
            }
        }

        return (false, new List<string>());
    }


    private static string GetFriendlyEncryptSignFlags(JsonElement encryptSignFlags)
    {
        var friendlyFlags = new List<string>();

        if (encryptSignFlags.TryGetProperty("isPGPSigned", out var isPGPSigned) && isPGPSigned.GetBoolean())
            friendlyFlags.Add("PGP Signed");
        if (encryptSignFlags.TryGetProperty("isPGPEncrypted", out var isPGPEncrypted) && isPGPEncrypted.GetBoolean())
            friendlyFlags.Add("PGP Encrypted");
        if (encryptSignFlags.TryGetProperty("containsPGPKeys", out var containsPGPKeys) && containsPGPKeys.GetBoolean())
            friendlyFlags.Add("Contains PGP Keys");
        if (encryptSignFlags.TryGetProperty("isSMIMESigned", out var isSMIMESigned) && isSMIMESigned.GetBoolean())
            friendlyFlags.Add("SMIME Signed");
        if (encryptSignFlags.TryGetProperty("isSMIMEEncrypted", out var isSMIMEEncrypted) &&
            isSMIMEEncrypted.GetBoolean())
            friendlyFlags.Add("SMIME Encrypted");
        if (encryptSignFlags.TryGetProperty("isSignedPart", out var isSignedPart) && isSignedPart.GetBoolean())
            friendlyFlags.Add("Signed Part");
        if (encryptSignFlags.TryGetProperty("passedVerification", out var passedVerification) &&
            passedVerification.GetBoolean())
            friendlyFlags.Add("Passed Verification");

        return friendlyFlags.Count == 0 ? "None" : string.Join(", ", friendlyFlags);
    }

    private static string GetFriendlyTrustInfo(JsonElement trustInfo)
    {
        var trustInfoStr = new List<string>();

        // TrustedSenderLevel
        var senderLevels = new Dictionary<int, string>
        {
            { 0, "None" },
            { 1, "User Trusted" },
            { 2, "Contact Trusted" },
            { 3, "Domain Trusted" },
            { 4, "GAL Trusted" },
            { 5, "System Trusted" }
        };

        if (trustInfo.TryGetProperty("trustedSenderLevel", out var trustedSenderLevel))
        {
            var level = trustedSenderLevel.GetInt32();
            if (level > 0 && senderLevels.ContainsKey(level))
                trustInfoStr.Add(senderLevels[level]);
        }

        // Trust levels for DMARC, DKIM, SPF
        var trustLevels = new Dictionary<int, string>
        {
            { 0, "None" },
            { 1, "Bad" },
            { 2, "Good" }
        };

        if (trustInfo.TryGetProperty("dmarcStatus", out var dmarcStatus))
        {
            var status = dmarcStatus.GetInt32();
            if (status > 0 && trustLevels.ContainsKey(status))
                trustInfoStr.Add($"DMARC: {trustLevels[status]}");
        }

        if (trustInfo.TryGetProperty("dkimStatus", out var dkimStatus))
        {
            var status = dkimStatus.GetInt32();
            if (status > 0 && trustLevels.ContainsKey(status))
                trustInfoStr.Add($"DKIM: {trustLevels[status]}");
        }

        if (trustInfo.TryGetProperty("spfStatus", out var spfStatus))
        {
            var status = spfStatus.GetInt32();
            if (status > 0 && trustLevels.ContainsKey(status))
                trustInfoStr.Add($"SPF: {trustLevels[status]}");
        }

        if (trustInfo.TryGetProperty("totalLevel", out var totalLevel))
        {
            var level = totalLevel.GetInt32();
            if (level > 0 && trustLevels.ContainsKey(level))
                trustInfoStr.Add($"Overall Trust: {trustLevels[level]}");
        }

        if (trustInfo.TryGetProperty("authenticated", out var authenticated) && authenticated.GetBoolean())
            trustInfoStr.Add("Authenticated");

        if (trustInfo.TryGetProperty("ipWhitelisted", out var ipWhitelisted) && ipWhitelisted.GetBoolean())
            trustInfoStr.Add("IP Whitelisted");

        if (trustInfo.TryGetProperty("unsafeCodeFound", out var unsafeCodeFound) && unsafeCodeFound.GetBoolean())
            trustInfoStr.Add("Unsafe Code Detected");

        if (trustInfo.TryGetProperty("mismatchFromAddress", out var mismatchFromAddress) &&
            mismatchFromAddress.GetBoolean())
            trustInfoStr.Add("From Address Mismatch");

        if (trustInfo.TryGetProperty("isTrustBypassed", out var isTrustBypassed) && isTrustBypassed.GetBoolean())
            trustInfoStr.Add("Trust Bypass Applied");

        return trustInfoStr.Count == 0 ? "None" : string.Join(", ", trustInfoStr);
    }

    [McpServerTool(ReadOnly = true)]
    [Description(
        "Get email metadata like subject, from, date, id, read, flagged, etc. from a specific folder with optional search and pagination")]
    public static async Task<string> GetEmails(
        [Description(
            "The folderID to get emails from (accepts <username>/<folderPath> as well, ex: jdoe/Inbox or jdoe/<folderid>)")]
        string folderId,
        [Description("The number of emails to skip (for pagination).")]
        int skip = 0,
        [Description("The maximum number of emails to return.")]
        int take = 50,
        [Description(
            "Optional search query to filter emails. Supports flags in query:\n" +
            "                - is:(un)read/is:(un)flagged/is:(un)replied/is:attachment - filter by status\n" +
            "                - Can combine multiple filters with spaces and optional search query (ex: \"is:attachment is:unread searchterm\")")]
        string query = "",
        //SmarterMailDatabase db,
        UserContext userContext = null!)
    {
        try
        {
            var folderPath =
                await userContext.GetFolderPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (folderPath == null)
                return JsonSerializer.Serialize(new
                    { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type to see available folders and their IDs." });

            var pathParts = folderPath.Split('/', 2);
            var username = pathParts[0];
            var folder = pathParts.Length > 1 ? pathParts[1] : folderPath;

            query = query ?? string.Empty;

            var payload = new Dictionary<string, object>
            {
                { "folder", folder },
                { "ownerEmailAddress", $"{username}@{userContext.Domain}" },
                { "sortType", 5 },
                { "sortAscending", false },
                { "query", query },
                { "skip", skip },
                { "take", take },
                { "selectedIds", new List<int>() }
            };

            // Parse search flags from query
            var searchFlags = new Dictionary<int, bool>();

            if (query.Contains("is:unread"))
                searchFlags[0] = false; // unread
            else if (query.Contains("is:read"))
                searchFlags[0] = true; // read

            if (query.Contains("is:replied"))
                searchFlags[1] = true; // replied
            else if (query.Contains("is:unreplied"))
                searchFlags[1] = false; // not replied

            if (query.Contains("is:flagged"))
                searchFlags[4] = true; // flagged
            else if (query.Contains("is:unflagged"))
                searchFlags[4] = false; // unflagged

            if (query.Contains("is:attachment"))
                searchFlags[7] = true; // attachment

            if (searchFlags.Count > 0)
                payload["searchFlags"] = searchFlags;

            // Remove all is:* flags from query
            query = Regex.Replace(query, @"is:\w+", "").Trim();
            payload["query"] = query;

            var response = await userContext.PostAsync<JsonElement>("/api/v1/mail/messages", payload);

            var emails = new List<object>();
            if (response.TryGetProperty("results", out var results))
                foreach (var message in results.EnumerateArray())
                {
                    var importance = message.TryGetProperty("importance", out var imp) ? imp.GetInt32() : 0;
                    var importanceString = importance switch
                    {
                        -1 => "low",
                        1 => "high",
                        _ => "normal"
                    };

                    var email = new
                    {
                        uid = message.TryGetProperty("uid", out var uid) ? uid.GetInt32() : 0,
                        senderName =
                            message.TryGetProperty("from", out var from) && from.TryGetProperty("name", out var name)
                                ? name.GetString()
                                : null,
                        senderAddress = message.TryGetProperty("fromAddress", out var fromAddr)
                            ? fromAddr.GetString()
                            : null,
                        subject = message.TryGetProperty("subject", out var subj) ? subj.GetString() : null,
                        dateSent = message.TryGetProperty("dateSent", out var date) ? date.GetString() : null,
                        size = message.TryGetProperty("size", out var sz) ? sz.GetInt32() : 0,
                        hasAttachments =
                            message.TryGetProperty("hasAttachments", out var hasAtt) && hasAtt.GetBoolean(),
                        isRead = message.TryGetProperty("isSeen", out var isSeen) && isSeen.GetBoolean(),
                        isAnswered = message.TryGetProperty("isAnswered", out var isAnsw) && isAnsw.GetBoolean(),
                        isCalendarMessage = message.TryGetProperty("isCalendarMessage", out var isCal) &&
                                            isCal.GetBoolean(),
                        isVerifiedSender = message.TryGetProperty("isVerifiedSender", out var isVer) &&
                                           isVer.GetBoolean(),
                        isFlagged = message.TryGetProperty("isFlagged", out var isFlag) && isFlag.GetBoolean(),
                        importance = importanceString,
                        encryptSignFlags = message.TryGetProperty("encryptSignFlags", out var encFlags)
                            ? GetFriendlyEncryptSignFlags(encFlags)
                            : "None"
                    };
                    emails.Add(email);
                }

            var result = new
            {
                success = response.TryGetProperty("success", out var succ) && succ.GetBoolean(),
                emails,
                totalCount = response.TryGetProperty("totalCount", out var total) ? total.GetInt32() : 0,
                unreadCount = response.TryGetProperty("unreadCount", out var unread) ? unread.GetInt32() : 0,
                folderId,
                folderPath = folder
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
    [Description("Get a specific email message by UID and folder")]
    public static async Task<string> GetEmailMessage(
        [Description("The UID of the email to retrieve")]
        int uid,
        [Description("The folder path where the email is located")]
        string folderId,
        [Description("Whether to include the message body in the response")]
        bool includeBody,
        [Description("Whether to include the message headers in the response")]
        bool includeHeader,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        try
        {
            var folderPath =
                await userContext.GetFolderPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (folderPath == null)
                return JsonSerializer.Serialize(new
                    { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type to see available folders and their IDs." });

            var pathParts = folderPath.Split('/', 2);
            var username = pathParts[0];
            var folder = pathParts.Length > 1 ? pathParts[1] : folderPath;

            var payload = new
            {
                UID = uid,
                Folder = folder,
                OwnerEmailAddress = $"{username}@{userContext.Domain}"
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/mail/message", payload);

            if (!response.TryGetProperty("messageData", out var messageData))
                return JsonSerializer.Serialize(new { success = false, error = $"Email not found for UID {uid} in folder '{folderId}'. Verify the UID and folderId are correct. Use get_emails to find valid UIDs." });

            // Convert JsonElement to mutable dictionary for manipulation
            var messageDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(messageData.GetRawText());
            var htmlLength = 0;
            var plainLength = 0;
            var truncated = false;

            if (messageDict != null)
            {
                // Apply friendly formatted fields
                if (messageDict.TryGetValue("encryptSignFlags", out var encFlags))
                {
                    var friendlyFlags = GetFriendlyEncryptSignFlags(encFlags);
                    messageDict["encryptSignFlags"] = JsonSerializer.SerializeToElement(friendlyFlags);
                }

                if (messageDict.TryGetValue("trustInfo", out var trustInfo))
                {
                    var friendlyTrust = GetFriendlyTrustInfo(trustInfo);
                    messageDict["trustInfo"] = JsonSerializer.SerializeToElement(friendlyTrust);
                }

                // Handle body content
                if (!includeBody)
                {
                    messageDict.Remove("messageHTML");
                    messageDict.Remove("messagePlainText");
                }
                else
                {
                    if (messageDict.TryGetValue("messageHTML", out var messageHtml))
                    {
                        var messageHtmlString = messageHtml.GetString() ?? "";
                        htmlLength = messageHtmlString.Length;
                        if (messageHtmlString.Length > GetMessageBodyCap)
                        {
                            truncated = true;
                            var previewLen = Math.Min(GetMessagePreviewChars, messageHtmlString.Length);
                            messageDict["messageHTML"] = JsonSerializer.SerializeToElement(messageHtmlString[..previewLen]);
                        }
                    }

                    if (messageDict.TryGetValue("messagePlainText", out var messagePlainText))
                    {
                        var messagePlainTextString = messagePlainText.GetString() ?? "";
                        plainLength = messagePlainTextString.Length;
                        if (messagePlainTextString.Length > GetMessageBodyCap)
                        {
                            truncated = true;
                            var previewLen = Math.Min(GetMessagePreviewChars, messagePlainTextString.Length);
                            messageDict["messagePlainText"] = JsonSerializer.SerializeToElement(messagePlainTextString[..previewLen]);
                        }
                    }
                }

                // Remove header if not requested
                if (!includeHeader) 
                    messageDict.Remove("header");

                // Clean up empty or unnecessary fields
                var fieldsToRemove = new List<string>();
                
                foreach (var kvp in messageDict)
                {
                    // Remove empty strings
                    if (kvp.Value.ValueKind == JsonValueKind.String)
                    {
                        var stringValue = kvp.Value.GetString();
                        if (string.IsNullOrEmpty(stringValue))
                            fieldsToRemove.Add(kvp.Key);
                    }
                    // Remove empty arrays
                    else if (kvp.Value.ValueKind == JsonValueKind.Array && kvp.Value.GetArrayLength() == 0)
                    {
                        fieldsToRemove.Add(kvp.Key);
                    }
                    // Remove empty objects (but keep important ones)
                    else if (kvp.Value.ValueKind == JsonValueKind.Object)
                    {
                        var objString = kvp.Value.GetRawText();
                        if (objString == "{}" && kvp.Key != "originalCidLinks" && kvp.Key != "replyInfo")
                            fieldsToRemove.Add(kvp.Key);
                        
                        // Check for objects with only potentialHomograph:false
                        try
                        {
                            var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(objString);
                            if (obj != null && obj.Count == 1 && 
                                obj.ContainsKey("potentialHomograph") && 
                                obj["potentialHomograph"].ValueKind == JsonValueKind.False)
                            {
                                fieldsToRemove.Add(kvp.Key);
                            }
                        }
                        catch { /* Ignore deserialization errors */ }
                    }
                    // Remove null values
                    else if (kvp.Value.ValueKind == JsonValueKind.Null)
                    {
                        fieldsToRemove.Add(kvp.Key);
                    }
                }

                // Remove the identified fields
                foreach (var field in fieldsToRemove)
                {
                    messageDict.Remove(field);
                }
                
                // Simplify address objects to keep them readable but clean
                if (messageDict.TryGetValue("fromAddress", out var fromAddr) && 
                    fromAddr.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        var addrObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fromAddr.GetRawText());
                        if (addrObj != null)
                        {
                            // Remove potentialHomograph if false
                            if (addrObj.TryGetValue("potentialHomograph", out var homograph) && 
                                homograph.ValueKind == JsonValueKind.False)
                            {
                                addrObj.Remove("potentialHomograph");
                                messageDict["fromAddress"] = JsonSerializer.SerializeToElement(addrObj);
                            }
                        }
                    }
                    catch { /* Keep original if parsing fails */ }
                }
                
                // Clean up toAddresses array
                if (messageDict.TryGetValue("toAddresses", out var toAddrs) && 
                    toAddrs.ValueKind == JsonValueKind.Array)
                {
                    var cleanedAddresses = new List<object>();
                    foreach (var addr in toAddrs.EnumerateArray())
                    {
                        try
                        {
                            var addrObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(addr.GetRawText());
                            if (addrObj != null)
                            {
                                // Remove potentialHomograph if false
                                if (addrObj.TryGetValue("potentialHomograph", out var homograph) && 
                                    homograph.ValueKind == JsonValueKind.False)
                                {
                                    addrObj.Remove("potentialHomograph");
                                }
                                cleanedAddresses.Add(addrObj);
                            }
                        }
                        catch { cleanedAddresses.Add(addr); }
                    }
                    messageDict["toAddresses"] = JsonSerializer.SerializeToElement(cleanedAddresses);
                }
            }

            var result = new Dictionary<string, object?>
            {
                ["success"] = true,
                ["message"] = messageDict
            };

            if (includeBody)
            {
                result["htmlLength"] = htmlLength;
                result["plainLength"] = plainLength;
                result["truncated"] = truncated;
                if (truncated)
                {
                    result["hint"] =
                        "Body truncated. Use read_email_part with part=html|plain|text and offset/limit to read the full body in chunks, or pattern to grep.";
                }
            }

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
    [Description(
        "Inventory, window, or grep any part of an email. Omit part for an inventory of html/plain/text/headers/attachments with sizes. " +
        "Set part (html, plain, text derived from HTML, headers, or attachment:<filename>) to read a window with offset/limit. " +
        "Set pattern to grep (literal by default; isRegex=true for regex). Window responses include totalChars, nextOffset, and hasMore so you can page through large bodies.")]
    public static async Task<string> ReadEmailPart(
        [Description("The UID of the email")]
        int uid,
        [Description("The folder ID or path where the email is located")]
        string folderId,
        [Description("Part to read: html, plain, text (HTML converted to plaintext), headers, or attachment:<filename>. Omit or leave empty for inventory.")]
        string part = "",
        [Description("Search pattern. When set, switches to grep mode instead of a windowed read.")]
        string pattern = "",
        [Description("Treat pattern as a regular expression. Default false (literal substring).")]
        bool isRegex = false,
        [Description("Case-insensitive match. Default true.")]
        bool ignoreCase = true,
        [Description("Window start offset in characters. Default 0.")]
        int offset = 0,
        [Description("Window size in characters. Default 8000. 0 means through the end, still capped at 200000.")]
        int limit = DefaultWindowLimit,
        [Description("Characters of context around each grep match. Default 300.")]
        int contextChars = 300,
        [Description("Maximum grep matches to return. Default 20.")]
        int maxMatches = 20,
        UserContext userContext = null!)
    {
        try
        {
            var (message, error) = await FetchMessageDataAsync(userContext, uid, folderId);
            if (error != null)
                return JsonSerializer.Serialize(new { success = false, error });

            if (string.IsNullOrWhiteSpace(part))
                return JsonSerializer.Serialize(BuildInventory(uid, folderId, message));

            var (content, resolveError, contentType) = await ResolvePartAsync(message, part, userContext);
            if (resolveError != null)
                return JsonSerializer.Serialize(new { success = false, error = resolveError, part, contentType });

            content ??= "";

            if (!string.IsNullOrEmpty(pattern))
                return JsonSerializer.Serialize(BuildGrep(part, content, pattern, isRegex, ignoreCase, contextChars, maxMatches));

            return JsonSerializer.Serialize(BuildWindow(part, content, offset, limit));
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody, endpoint = apiEx.Endpoint });
        }
        catch (RegexMatchTimeoutException)
        {
            return JsonSerializer.Serialize(new { success = false, error = "Regex search timed out after 2 seconds. Simplify the pattern or use a literal search (isRegex=false)." });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Send a simple email")]
    public static async Task<string> SendEmail(
        [Description("The email addresses to send the email to, separated by commas")]
        string to,
        [Description("The subject of the email")]
        string subject,
        [Description("The body of the email")] string body,
        [Description("The email address to cc")]
        string cc,
        [Description("The email address to bcc")]
        string bcc,
        //SmarterMailDatabase db,
        UserContext userContext,
        GlobalContext globalContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot send emails." });

        try
        {
            var userInfo = await userContext.GetUserInfoAsync();

            // Validate inputs
            if (string.IsNullOrWhiteSpace(to) || to == "undefined" || to == "null")
                to = userContext.EmailAddress;

            if (string.IsNullOrWhiteSpace(subject) || subject == "undefined" || subject == "null")
                subject = "No subject";

            if (string.IsNullOrWhiteSpace(body) || body == "undefined" || body == "null")
                body = "No body";

            // Expand contact groups in recipients
            to = await ExpandRecipientsAsync(to, userContext);
            cc = await ExpandRecipientsAsync(cc ?? "", userContext);
            bcc = await ExpandRecipientsAsync(bcc ?? "", userContext);

            // Get email preferences
            var composeFont = "arial";
            var composeFontSize = "14px";
            var maxAllowedMessageSize = 10240000; // Default 10MB

            if (userInfo.TryGetProperty("userMailSettings", out var mailSettings))
            {
                if (mailSettings.TryGetProperty("composeFont", out var font))
                    composeFont = font.GetString() ?? "arial";
                if (mailSettings.TryGetProperty("composeFontSize", out var fontSize))
                    composeFontSize = fontSize.GetString() ?? "14px";
            }

            if (userInfo.TryGetProperty("diskSpace", out var diskSpace))
                if (diskSpace.TryGetProperty("maxAllowedMessageSize", out var maxSize))
                    maxAllowedMessageSize = (int)maxSize.GetDouble();

            // Get signature
            var signatureHtml = "";
            if (userInfo.TryGetProperty("signatureMappings", out var signatureMappings))
            {
                foreach (var mapping in signatureMappings.EnumerateArray())
                    if (mapping.TryGetProperty("email", out var email) &&
                        email.GetString() == userContext.EmailAddress &&
                        mapping.TryGetProperty("signatures", out var signatures) &&
                        signatures.GetArrayLength() > 0)
                    {
                        var signature = signatures[0].GetString();
                        if (!string.IsNullOrEmpty(signature))
                            signatureHtml = $"<div class='signature'>{signature}</div>";
                        break;
                    }
            }

            // Format the to address exactly like the working example: "name" <email>;
            var toFormatted = $"\"{userContext.Username}\" <{to}>;";

            // Format HTML body with exact styling from working example
            var styledBody = $"<div fr-original-style=\"\" style=\"box-sizing: border-box; font-family: {composeFont}; font-size: {composeFontSize};\" dir=\"auto\">{body}</div>";

            // Generate a GUID for attachmentGuid
            var attachmentGuid = Guid.NewGuid().ToString();

            var payload = new
            {
                to = toFormatted,
                cc = cc ?? "",
                bcc = bcc ?? "",
                ownerEmailAddress = userContext.EmailAddress,
                folder = "drafts",
                date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                from = userContext.EmailAddress,
                replyTo = userContext.EmailAddress,
                subject,
                priority = 1,
                readReceiptRequested = false,
                deliveryReceiptRequested = false,
                markForFollowup = false,
                selectedFrom = $":{userContext.EmailAddress}",
                messageHTML = styledBody,
                attachmentGuid,
                actions = new { },
                inlineToRemove = new string[] { },
                sendImmediately = true
            };

            // Check size
            var estimatedSize = styledBody.Length + subject.Length + to.Length + (cc?.Length ?? 0) +
                                (bcc?.Length ?? 0);
            if (estimatedSize > maxAllowedMessageSize)
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error =
                        $"Email size exceeds the maximum allowed size of {maxAllowedMessageSize / 1024.0 / 1024.0:F2}MB"
                });

            var response = await userContext.PostAsync<JsonElement>("/api/v1/mail/message-put", payload);
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
    [Description("Upload a file attachment for use in composing emails. Returns an attachmentGuid that can be used with send_email_with_attachments.")]
    public static async Task<string> UploadAttachment(
        [Description("The full local file path to upload (e.g., /path/to/file.png)")]
        string filePath,
        [Description("Optional: An existing attachment GUID to add more files to. If not provided, a new GUID will be generated.")]
        string attachmentGuid = "",
        [Description("Optional: Content-ID for inline/embedded images. Use this ID in HTML as src=\"cid:yourContentId\". If not provided, the image will be a regular attachment.")]
        string contentId = "",
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot upload attachments." });

        try
        {
            // Use UploadService for the upload
            var result = await UploadService.UploadAttachmentAsync(
                userContext,
                filePath,
                string.IsNullOrEmpty(attachmentGuid) ? null : attachmentGuid,
                string.IsNullOrEmpty(contentId) ? null : contentId);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new { success = false, error = result.Error });
            }

            var response = new Dictionary<string, object>
            {
                { "success", true },
                { "attachmentGuid", result.Guid! },
                { "fileName", result.FileName! },
                { "fileSize", result.FileSize },
                { "contentType", result.ContentType! },
                { "uploadResult", result.ServerResponse! }
            };

            // Include contentId info - use the actual returned contentId (may differ if "cidgenerate" was used)
            var actualContentId = result.ContentId ?? contentId;
            if (!string.IsNullOrEmpty(actualContentId))
            {
                response["contentId"] = actualContentId;
                response["htmlReference"] = $"cid:{actualContentId}";
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
    [Description("Send an email with previously uploaded attachments. Use upload_attachment first to upload files, then use the returned attachmentGuid here.")]
    public static async Task<string> SendEmailWithAttachments(
        [Description("The email addresses to send the email to, separated by commas")]
        string to,
        [Description("The subject of the email")]
        string subject,
        [Description("The body of the email (HTML supported)")]
        string body,
        [Description("The attachment GUID from upload_attachment tool")]
        string attachmentGuid,
        [Description("The email address to cc")]
        string cc = "",
        [Description("The email address to bcc")]
        string bcc = "",
        UserContext userContext = null!,
        GlobalContext globalContext = null!)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot send emails." });

        try
        {
            var userInfo = await userContext.GetUserInfoAsync();

            // Validate inputs
            if (string.IsNullOrWhiteSpace(to) || to == "undefined" || to == "null")
                to = userContext.EmailAddress;

            if (string.IsNullOrWhiteSpace(subject) || subject == "undefined" || subject == "null")
                subject = "No subject";

            if (string.IsNullOrWhiteSpace(body) || body == "undefined" || body == "null")
                body = "No body";

            if (string.IsNullOrWhiteSpace(attachmentGuid))
                return JsonSerializer.Serialize(new { success = false, error = "attachmentGuid is required. Use upload_attachment first." });

            // Expand contact groups in recipients
            to = await ExpandRecipientsAsync(to, userContext);
            cc = await ExpandRecipientsAsync(cc ?? "", userContext);
            bcc = await ExpandRecipientsAsync(bcc ?? "", userContext);

            // Get email preferences
            var composeFont = "arial";
            var composeFontSize = "14px";

            if (userInfo.TryGetProperty("userMailSettings", out var mailSettings))
            {
                if (mailSettings.TryGetProperty("composeFont", out var font))
                    composeFont = font.GetString() ?? "arial";
                if (mailSettings.TryGetProperty("composeFontSize", out var fontSize))
                    composeFontSize = fontSize.GetString() ?? "14px";
            }

            // Format the to address
            var toFormatted = $"\"{userContext.Username}\" <{to}>;";

            // Format HTML body
            var styledBody = $"<div fr-original-style=\"\" style=\"box-sizing: border-box; font-family: {composeFont}; font-size: {composeFontSize};\" dir=\"auto\">{body}</div>";

            var payload = new
            {
                to = toFormatted,
                cc = cc ?? "",
                bcc = bcc ?? "",
                ownerEmailAddress = userContext.EmailAddress,
                folder = "drafts",
                date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                from = userContext.EmailAddress,
                replyTo = userContext.EmailAddress,
                subject,
                priority = 1,
                readReceiptRequested = false,
                deliveryReceiptRequested = false,
                markForFollowup = false,
                selectedFrom = $":{userContext.EmailAddress}",
                messageHTML = styledBody,
                attachmentGuid,
                actions = new { },
                inlineToRemove = new string[] { },
                sendImmediately = true
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/mail/message-put", payload);
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
    [Description("Mark emails as read/unread, flagged/unflagged, or add tags")]
    public static async Task<string> SetEmailProperties(
        [Description("The UIDs of the emails to mark, comma separated")]
        string uid,
        [Description("The folder the emails are in")]
        string folderId,
        [Description("Whether to mark the emails as read or unread")]
        bool? read,
        [Description("Whether to mark the emails as flagged or unflagged")]
        bool? flagged,
        [Description("The tags to add to the emails, comma separated")]
        string tags,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot modify emails." });

        try
        {
            var folderPath =
                await userContext.GetFolderPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (folderPath == null)
                return JsonSerializer.Serialize(new
                    { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type to see available folders and their IDs." });

            var pathParts = folderPath.Split('/', 2);
            var username = pathParts[0];
            var folder = pathParts.Length > 1 ? pathParts[1] : folderPath;
            var userEmail = $"{username}@{userContext.Domain}";

            var uids = uid.Split(',').Select(u => u.Trim()).ToList();
            var results = new List<object>();
            var allSuccess = true;
            var errors = new List<string>();

            // Handle read status using messages-patch endpoint
            if (read.HasValue)
            {
                var readPayload = new Dictionary<string, object>
                {
                    { "UID", uids },
                    { "folder", folder },
                    { "ownerEmailAddress", userEmail },
                    { "markRead", read.Value }
                };

                try
                {
                    var readResponse = await userContext.PostAsync<JsonElement>("/api/v1/mail/messages-patch", readPayload);
                    var readSuccess = readResponse.TryGetProperty("success", out var succ) && succ.GetBoolean();
                    results.Add(new { operation = "markRead", success = readSuccess, response = readResponse });
                    if (!readSuccess)
                    {
                        allSuccess = false;
                        if (readResponse.TryGetProperty("message", out var msg))
                            errors.Add($"markRead: {msg.GetString()}");
                    }
                }
                catch (Exception ex)
                {
                    allSuccess = false;
                    errors.Add($"markRead: {ex.Message}");
                    results.Add(new { operation = "markRead", success = false, error = ex.Message });
                }
            }

            // Handle flagged status using messages-flag-patch endpoint
            if (flagged.HasValue)
            {
                var flagPayload = new Dictionary<string, object>
                {
                    { "UID", uids },
                    { "folder", folder },
                    { "ownerEmailAddress", userEmail },
                    { "flagAction", new { type = flagged.Value ? "SetBasic" : "Clear" } }
                };

                try
                {
                    var flagResponse = await userContext.PostAsync<JsonElement>("/api/v1/mail/messages-flag-patch", flagPayload);
                    var flagSuccess = flagResponse.TryGetProperty("success", out var succ) && succ.GetBoolean();
                    results.Add(new { operation = "flagAction", success = flagSuccess, response = flagResponse });
                    if (!flagSuccess)
                    {
                        allSuccess = false;
                        if (flagResponse.TryGetProperty("message", out var msg))
                            errors.Add($"flagAction: {msg.GetString()}");
                    }
                }
                catch (Exception ex)
                {
                    allSuccess = false;
                    errors.Add($"flagAction: {ex.Message}");
                    results.Add(new { operation = "flagAction", success = false, error = ex.Message });
                }
            }

            // Handle categories using messages-category-patch endpoint
            if (!string.IsNullOrWhiteSpace(tags))
            {
                var categoryPayload = new Dictionary<string, object>
                {
                    { "UID", uids },
                    { "folder", folder },
                    { "ownerEmailAddress", userEmail },
                    { "categories", tags.Split(',').Select(t => t.Trim()).ToList() }
                };

                try
                {
                    var categoryResponse = await userContext.PostAsync<JsonElement>("/api/v1/mail/messages-category-patch", categoryPayload);
                    var categorySuccess = categoryResponse.TryGetProperty("success", out var succ) && succ.GetBoolean();
                    results.Add(new { operation = "categories", success = categorySuccess, response = categoryResponse });
                    if (!categorySuccess)
                    {
                        allSuccess = false;
                        if (categoryResponse.TryGetProperty("message", out var msg))
                            errors.Add($"categories: {msg.GetString()}");
                    }
                }
                catch (Exception ex)
                {
                    allSuccess = false;
                    errors.Add($"categories: {ex.Message}");
                    results.Add(new { operation = "categories", success = false, error = ex.Message });
                }
            }

            var finalResult = new
            {
                success = allSuccess,
                operationsCompleted = results.Count,
                results,
                errors = errors.Count > 0 ? errors : null
            };

            return JsonSerializer.Serialize(finalResult);
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
    [Description("Remove emails from the system")]
    public static async Task<string> RemoveEmails(
        [Description("The UIDs of the emails to remove, comma separated")]
        string uid,
        [Description("The folder the emails are in")]
        string folderId,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot delete emails." });

        try
        {
            var folderPath =
                await userContext.GetFolderPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (folderPath == null)
                return JsonSerializer.Serialize(new
                    { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type to see available folders and their IDs." });

            var pathParts = folderPath.Split('/', 2);
            var username = pathParts[0];
            var folder = pathParts.Length > 1 ? pathParts[1] : folderPath;
            var userEmail = $"{username}@{userContext.Domain}";

            var uids = uid.Split(',').Select(u => u.Trim()).ToList();

            var payload = new
            {
                UID = uids,
                folder,
                ownerEmailAddress = userEmail,
                moveToDeleted = true
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/mail/delete-messages", payload);
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
    [Description("Move emails to a different folder")]
    public static async Task<string> MoveEmails(
        [Description("The UIDs of the emails to move, comma separated")]
        string uid,
        [Description("The folder the emails are in")]
        string srcFolderId,
        [Description("The folder to move the emails to")]
        string destFolderId,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot move emails." });

        try
        {
            var srcFolderPath =
                await userContext.GetFolderPathFromFolderIdAsync(srcFolderId, userContext.Username, userContext.Domain);
            if (srcFolderPath == null)
                return JsonSerializer.Serialize(new
                    { success = false, error = $"No folder found for source '{srcFolderId}'. Use list_folder_info_by_type to see available folders and their IDs." });

            var destFolderPath =
                await userContext.GetFolderPathFromFolderIdAsync(destFolderId, userContext.Username,
                    userContext.Domain);
            if (destFolderPath == null)
                return JsonSerializer.Serialize(new
                    { success = false, error = $"No folder found for destination '{destFolderId}'. Use list_folder_info_by_type to see available folders and their IDs." });

            var username = srcFolderPath.Split("/")[0];
            var srcFolder = srcFolderPath.Split("/")[1];
            var destFolder = destFolderPath.Split("/")[1];
            var userEmail = $"{username}@{userContext.Domain}";

            var uids = uid.Split(',').Select(u =>
            {
                var trimmed = u.Trim();
                return int.TryParse(trimmed, out var intVal) ? (object)intVal : trimmed;
            }).ToList();

            var payload = new
            {
                UID = uids,
                folder = srcFolder,
                ownerEmailAddress = userEmail,
                destinationFolder = destFolder,
                destinationOwnerEmailAddress = userEmail
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/mail/move-messages", payload);
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
    [Description("Reply to an email message")]
    public static async Task<string> ReplyToEmail(
        [Description("The UID of the email to reply to")]
        int uid,
        [Description("The folder where the original email is located")]
        string folderId,
        [Description("The reply message body (HTML supported)")]
        string body,
        [Description("Set to true to reply to all recipients, false to reply only to sender")]
        bool replyAll = false,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot send emails." });

        try
        {
            // Get the original message to extract reply info
            var folderPath =
                await userContext.GetFolderPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (folderPath == null)
                return JsonSerializer.Serialize(new
                    { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type to see available folders and their IDs." });

            var pathParts = folderPath.Split('/', 2);
            var username = pathParts[0];
            var folder = pathParts.Length > 1 ? pathParts[1] : folderPath;
            var ownerEmail = $"{username}@{userContext.Domain}";

            // Get original message
            var msgPayload = new { UID = uid, Folder = folder, OwnerEmailAddress = ownerEmail };
            var msgResponse = await userContext.PostAsync<JsonElement>("/api/v1/mail/message", msgPayload);

            if (!msgResponse.TryGetProperty("messageData", out var messageData))
                return JsonSerializer.Serialize(new { success = false, error = $"Original email not found for UID {uid} in folder '{folderId}'. Verify the UID and folder are correct." });

            // Extract sender info for reply
            var originalFrom = messageData.TryGetProperty("fromAddress", out var fromAddr) &&
                               fromAddr.TryGetProperty("email", out var fromEmail)
                ? fromEmail.GetString()
                : "";
            var originalSubject = messageData.TryGetProperty("subject", out var subj)
                ? subj.GetString() ?? ""
                : "";

            // Build To field - for reply all, include CC recipients
            var toAddress = originalFrom;
            var ccAddress = "";

            if (replyAll)
            {
                var ccList = new List<string>();
                if (messageData.TryGetProperty("toAddresses", out var toAddrs))
                {
                    foreach (var addr in toAddrs.EnumerateArray())
                    {
                        if (addr.TryGetProperty("email", out var email))
                        {
                            var emailStr = email.GetString();
                            if (!string.IsNullOrEmpty(emailStr) &&
                                !emailStr.Equals(userContext.EmailAddress, StringComparison.OrdinalIgnoreCase))
                                ccList.Add(emailStr);
                        }
                    }
                }
                if (messageData.TryGetProperty("ccAddresses", out var ccAddrs))
                {
                    foreach (var addr in ccAddrs.EnumerateArray())
                    {
                        if (addr.TryGetProperty("email", out var email))
                        {
                            var emailStr = email.GetString();
                            if (!string.IsNullOrEmpty(emailStr) &&
                                !emailStr.Equals(userContext.EmailAddress, StringComparison.OrdinalIgnoreCase))
                                ccList.Add(emailStr);
                        }
                    }
                }
                ccAddress = string.Join(", ", ccList.Distinct());
            }

            // Format subject
            var replySubject = originalSubject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                ? originalSubject
                : $"Re: {originalSubject}";

            var payload = new
            {
                to = $"\"{toAddress}\" <{toAddress}>;",
                cc = ccAddress,
                bcc = "",
                ownerEmailAddress = ownerEmail,
                folder,
                date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                from = userContext.EmailAddress,
                replyTo = userContext.EmailAddress,
                subject = replySubject,
                priority = 1,
                messageHTML = $"<div style=\"font-family: arial; font-size: 14px;\">{body}</div>",
                attachmentGuid = Guid.NewGuid().ToString(),
                replyUid = (uint)uid,
                replyOwner = ownerEmail,
                replyFromFolder = folder,
                actions = new Dictionary<int, bool> { { 1, true } }, // Mark as replied
                sendImmediately = true
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/mail/message-put", payload);
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
    [Description("Forward an email message to other recipients")]
    public static async Task<string> ForwardEmail(
        [Description("The UID of the email to forward")]
        int uid,
        [Description("The folder where the original email is located")]
        string folderId,
        [Description("The email addresses to forward to, comma separated")]
        string to,
        [Description("Optional message to include above the forwarded content")]
        string message = "",
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot send emails." });

        try
        {
            var folderPath =
                await userContext.GetFolderPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (folderPath == null)
                return JsonSerializer.Serialize(new
                    { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type to see available folders and their IDs." });

            var pathParts = folderPath.Split('/', 2);
            var username = pathParts[0];
            var folder = pathParts.Length > 1 ? pathParts[1] : folderPath;
            var ownerEmail = $"{username}@{userContext.Domain}";

            // Get original message
            var msgPayload = new { UID = uid, Folder = folder, OwnerEmailAddress = ownerEmail };
            var msgResponse = await userContext.PostAsync<JsonElement>("/api/v1/mail/message", msgPayload);

            if (!msgResponse.TryGetProperty("messageData", out var messageData))
                return JsonSerializer.Serialize(new { success = false, error = $"Original email not found for UID {uid} in folder '{folderId}'. Verify the UID and folder are correct." });

            var originalSubject = messageData.TryGetProperty("subject", out var subj)
                ? subj.GetString() ?? ""
                : "";

            // Format subject
            var fwdSubject = originalSubject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) ||
                             originalSubject.StartsWith("Fw:", StringComparison.OrdinalIgnoreCase)
                ? originalSubject
                : $"Fwd: {originalSubject}";

            // Expand contact groups in recipients
            to = await ExpandRecipientsAsync(to, userContext);

            // Format To addresses
            var toFormatted = string.Join("; ",
                to.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).Select(t => $"<{t}>"));

            var bodyHtml = string.IsNullOrEmpty(message)
                ? ""
                : $"<div style=\"font-family: arial; font-size: 14px;\">{message}</div><br/>";

            var payload = new
            {
                to = toFormatted,
                cc = "",
                bcc = "",
                ownerEmailAddress = ownerEmail,
                folder,
                date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                from = userContext.EmailAddress,
                replyTo = userContext.EmailAddress,
                subject = fwdSubject,
                priority = 1,
                messageHTML = bodyHtml,
                attachmentGuid = Guid.NewGuid().ToString(),
                replyUid = (uint)uid,
                replyOwner = ownerEmail,
                replyFromFolder = folder,
                actions = new Dictionary<int, bool> { { 2, true } }, // Mark as forwarded
                excludeFiles = new List<int>(),
                sendImmediately = true
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/mail/message-put", payload);
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
    [Description("Get attachments from an email message")]
    public static async Task<string> GetEmailAttachments(
        [Description("The UID of the email")]
        int uid,
        [Description("The folder where the email is located")]
        string folderId,
        UserContext userContext = null!)
    {
        try
        {
            var folderPath =
                await userContext.GetFolderPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (folderPath == null)
                return JsonSerializer.Serialize(new
                    { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type to see available folders and their IDs." });

            var pathParts = folderPath.Split('/', 2);
            var username = pathParts[0];
            var folder = pathParts.Length > 1 ? pathParts[1] : folderPath;

            var payload = new
            {
                UID = uid,
                Folder = folder,
                OwnerEmailAddress = $"{username}@{userContext.Domain}"
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/mail/message", payload);

            if (!response.TryGetProperty("messageData", out var messageData))
                return JsonSerializer.Serialize(new { success = false, error = $"Email not found for UID {uid} in folder '{folderId}'. Verify the UID and folderId are correct. Use get_emails to find valid UIDs." });

            var attachments = new List<object>();
            if (messageData.TryGetProperty("attachments", out var attachmentsArray))
            {
                foreach (var att in attachmentsArray.EnumerateArray())
                {
                    attachments.Add(new
                    {
                        filename = att.TryGetProperty("filename", out var fn) ? fn.GetString() : null,
                        size = att.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                        contentType = att.TryGetProperty("contentType", out var ct) ? ct.GetString() : null,
                        contentId = att.TryGetProperty("contentId", out var cid) ? cid.GetString() : null,
                        isInline = att.TryGetProperty("isInline", out var inline) && inline.GetBoolean(),
                        index = att.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                uid,
                folderId,
                attachmentCount = attachments.Count,
                attachments
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
    [Description("Download an email attachment and save it to a local file path. Use get_email_attachments first to get the list of attachment filenames.")]
    public static async Task<string> DownloadEmailAttachment(
        [Description("The UID of the email")]
        int uid,
        [Description("The folder where the email is located")]
        string folderId,
        [Description("The filename of the attachment to download (from get_email_attachments)")]
        string filename,
        [Description("The local file path to save the attachment to. If a directory is provided, the attachment filename will be appended.")]
        string savePath,
        UserContext userContext = null!)
    {
        try
        {
            var folderPath =
                await userContext.GetFolderPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (folderPath == null)
                return JsonSerializer.Serialize(new
                    { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type to see available folders and their IDs." });

            var pathParts = folderPath.Split('/', 2);
            var username = pathParts[0];
            var folder = pathParts.Length > 1 ? pathParts[1] : folderPath;

            // Get message data to find the attachment's download link
            var msgPayload = new
            {
                UID = uid,
                Folder = folder,
                OwnerEmailAddress = $"{username}@{userContext.Domain}"
            };

            var msgResponse = await userContext.PostAsync<JsonElement>("/api/v1/mail/message", msgPayload);

            if (!msgResponse.TryGetProperty("messageData", out var messageData))
                return JsonSerializer.Serialize(new { success = false, error = $"Email not found for UID {uid} in folder '{folderId}'. Verify the UID and folderId are correct. Use get_emails to find valid UIDs." });

            // Find the matching attachment by filename
            string? downloadLink = null;
            string? actualFilename = filename;
            if (messageData.TryGetProperty("attachments", out var attachments))
            {
                foreach (var att in attachments.EnumerateArray())
                {
                    var attFilename = att.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
                    if (string.Equals(attFilename, filename, StringComparison.OrdinalIgnoreCase))
                    {
                        downloadLink = att.TryGetProperty("link", out var link) ? link.GetString() : null;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadLink))
                return JsonSerializer.Serialize(new { success = false, error = $"Attachment '{filename}' not found in email UID {uid}. Use get_email_attachments to see available attachment filenames." });

            // If savePath is a directory, append the filename
            if (Directory.Exists(savePath))
                savePath = Path.Combine(savePath, actualFilename);

            // Ensure parent directory exists
            var parentDir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);

            // Download the attachment using the encrypted link
            var (data, contentType) = await userContext.GetBytesAsync(downloadLink);

            await File.WriteAllBytesAsync(savePath, data);

            return JsonSerializer.Serialize(new
            {
                success = true,
                savedPath = savePath,
                filename = actualFilename,
                size = data.Length,
                contentType
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

    [McpServerTool]
    [Description("Save an email as a draft without sending")]
    public static async Task<string> CreateDraft(
        [Description("The email addresses to send to, comma separated")]
        string to,
        [Description("The subject of the email")]
        string subject,
        [Description("The body of the email (HTML supported)")]
        string body,
        [Description("The email addresses to CC, comma separated")]
        string cc = "",
        [Description("The email addresses to BCC, comma separated")]
        string bcc = "",
        [Description("Optional: UID of email being replied to")]
        int? replyToUid = null,
        [Description("Optional: Folder of email being replied to")]
        string replyToFolder = "",
        [Description("Set to true if this is a reply")]
        bool isReply = false,
        [Description("Set to true if this is a forward")]
        bool isForward = false,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot create drafts." });

        try
        {
            // Expand contact groups in recipients
            to = await ExpandRecipientsAsync(to ?? "", userContext);
            cc = await ExpandRecipientsAsync(cc ?? "", userContext);
            bcc = await ExpandRecipientsAsync(bcc ?? "", userContext);

            // Format To addresses
            var toFormatted = string.IsNullOrWhiteSpace(to)
                ? ""
                : string.Join("; ",
                    to.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).Select(t => $"<{t}>"));

            var payload = new Dictionary<string, object>
            {
                { "to", toFormatted },
                { "cc", cc ?? "" },
                { "bcc", bcc ?? "" },
                { "ownerEmailAddress", userContext.EmailAddress },
                { "folder", "Drafts" },
                { "date", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
                { "from", userContext.EmailAddress },
                { "replyTo", userContext.EmailAddress },
                { "subject", subject ?? "" },
                { "priority", 1 },
                { "messageHTML", $"<div style=\"font-family: arial; font-size: 14px;\">{body}</div>" },
                { "attachmentGuid", Guid.NewGuid().ToString() },
                { "isReply", isReply },
                { "isForward", isForward }
            };

            if (replyToUid.HasValue && !string.IsNullOrEmpty(replyToFolder))
            {
                var replyFolderPath = await userContext.GetFolderPathFromFolderIdAsync(
                    replyToFolder, userContext.Username, userContext.Domain);
                if (replyFolderPath != null)
                {
                    var parts = replyFolderPath.Split('/', 2);
                    payload["replyUid"] = (uint)replyToUid.Value;
                    payload["replyOwner"] = $"{parts[0]}@{userContext.Domain}";
                    payload["replyFromFolder"] = parts.Length > 1 ? parts[1] : replyFolderPath;
                }
            }

            var response = await userContext.PostAsync<JsonElement>("/api/v1/mail/draft-put", payload);
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
    [Description("Send a previously saved draft email")]
    public static async Task<string> SendDraft(
        [Description("The UID of the draft to send")]
        int draftUid,
        [Description("The folder where the draft is located (usually 'Drafts')")]
        string folderId = "Drafts",
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot send emails." });

        try
        {
            var folderPath =
                await userContext.GetFolderPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (folderPath == null)
                folderPath = $"{userContext.Username}/Drafts";

            var pathParts = folderPath.Split('/', 2);
            var username = pathParts[0];
            var folder = pathParts.Length > 1 ? pathParts[1] : folderPath;
            var ownerEmail = $"{username}@{userContext.Domain}";

            // Get the draft message to extract its content
            var msgPayload = new { UID = draftUid, Folder = folder, OwnerEmailAddress = ownerEmail };
            var msgResponse = await userContext.PostAsync<JsonElement>("/api/v1/mail/message", msgPayload);

            if (!msgResponse.TryGetProperty("messageData", out var draftData))
                return JsonSerializer.Serialize(new { success = false, error = $"Draft not found for UID {draftUid}. Use get_emails with folderId 'Drafts' to find valid draft UIDs." });

            // Extract draft fields
            var to = draftData.TryGetProperty("to", out var toField) ? toField.GetString() ?? "" : "";
            var cc = draftData.TryGetProperty("cc", out var ccField) ? ccField.GetString() ?? "" : "";
            var bcc = draftData.TryGetProperty("bcc", out var bccField) ? bccField.GetString() ?? "" : "";
            var subject = draftData.TryGetProperty("subject", out var subjField) ? subjField.GetString() ?? "" : "";
            var messageHtml = draftData.TryGetProperty("messageHTML", out var htmlField) ? htmlField.GetString() ?? "" : "";
            var messagePlain = draftData.TryGetProperty("messagePlainText", out var plainField) ? plainField.GetString() ?? "" : "";

            // Build send payload
            var sendPayload = new
            {
                to,
                cc,
                bcc,
                ownerEmailAddress = ownerEmail,
                folder,
                date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                from = userContext.EmailAddress,
                replyTo = userContext.EmailAddress,
                subject,
                priority = 1,
                messageHTML = messageHtml,
                messagePlainText = messagePlain,
                attachmentGuid = Guid.NewGuid().ToString(),
                draftUid = (uint)draftUid,
                sendImmediately = true
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/mail/message-put", sendPayload);
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

    private static async Task<(JsonElement Message, string? Error)> FetchMessageDataAsync(
        UserContext userContext, int uid, string folderId)
    {
        var folderPath = await userContext.GetFolderPathFromFolderIdAsync(
            folderId, userContext.Username, userContext.Domain);
        if (folderPath == null)
            return (default, $"No folder found for '{folderId}'. Use list_folder_info_by_type to see available folders and their IDs.");

        var pathParts = folderPath.Split('/', 2);
        var username = pathParts[0];
        var folder = pathParts.Length > 1 ? pathParts[1] : folderPath;
        var owner = $"{username}@{userContext.Domain}";
        var cacheKey = $"{owner}|{folder}|{uid}";

        if (MessageCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return (cached.MessageData, null);

        var payload = new
        {
            UID = uid,
            Folder = folder,
            OwnerEmailAddress = owner
        };

        var response = await userContext.PostAsync<JsonElement>("/api/v1/mail/message", payload);
        if (!response.TryGetProperty("messageData", out var messageData))
            return (default, $"Email not found for UID {uid} in folder '{folderId}'. Verify the UID and folderId are correct. Use get_emails to find valid UIDs.");

        var cloned = messageData.Clone();
        MessageCache[cacheKey] = new MessageCacheEntry(cloned, DateTime.UtcNow.Add(MessageCacheTtl));
        PruneMessageCache();
        return (cloned, null);
    }

    private static void PruneMessageCache()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in MessageCache)
        {
            if (kvp.Value.ExpiresUtc <= now)
                MessageCache.TryRemove(kvp.Key, out _);
        }
    }

    private static object BuildInventory(int uid, string folderId, JsonElement message)
    {
        var html = GetStringProp(message, "messageHTML");
        var plain = GetStringProp(message, "messagePlainText");
        var header = GetStringProp(message, "header");
        var derived = HtmlToPlainText(html);

        var parts = new List<object>
        {
            new { part = "html", chars = html.Length, readable = true },
            new { part = "plain", chars = plain.Length, readable = true },
            new { part = "text", chars = derived.Length, readable = true, description = "Plain text derived from HTML" },
            new { part = "headers", chars = header.Length, readable = true }
        };

        if (message.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var att in attachments.EnumerateArray())
            {
                var filename = att.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
                var contentType = att.TryGetProperty("contentType", out var ct) ? ct.GetString() : null;
                var size = att.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number
                    ? sz.GetInt64()
                    : 0L;
                var readable = IsTextDecodableAttachment(contentType, filename);
                parts.Add(new
                {
                    part = $"attachment:{filename}",
                    filename,
                    contentType,
                    bytes = size,
                    readable
                });
            }
        }

        return new
        {
            success = true,
            mode = "inventory",
            uid,
            folderId,
            parts,
            hint = "Call again with part=html|plain|text|headers|attachment:<filename>. Window with offset/limit, or grep with pattern. Binary attachments: use download_email_attachment."
        };
    }

    private static async Task<(string? Content, string? Error, string? ContentType)> ResolvePartAsync(
        JsonElement message, string part, UserContext userContext)
    {
        if (string.Equals(part, "html", StringComparison.OrdinalIgnoreCase))
            return (GetStringProp(message, "messageHTML"), null, "text/html");

        if (string.Equals(part, "plain", StringComparison.OrdinalIgnoreCase))
            return (GetStringProp(message, "messagePlainText"), null, "text/plain");

        if (string.Equals(part, "text", StringComparison.OrdinalIgnoreCase))
            return (HtmlToPlainText(GetStringProp(message, "messageHTML")), null, "text/plain");

        if (string.Equals(part, "headers", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(part, "header", StringComparison.OrdinalIgnoreCase))
            return (GetStringProp(message, "header"), null, "message/rfc822");

        const string attPrefix = "attachment:";
        if (part.StartsWith(attPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var filename = part[attPrefix.Length..].Trim();
            return await ResolveAttachmentTextAsync(message, filename, userContext);
        }

        return (null,
            $"Unknown part '{part}'. Use html, plain, text, headers, or attachment:<filename>. Omit part for inventory.",
            null);
    }

    private static async Task<(string? Content, string? Error, string? ContentType)> ResolveAttachmentTextAsync(
        JsonElement message, string filename, UserContext userContext)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return (null, "Attachment filename is required after attachment:.", null);

        if (!message.TryGetProperty("attachments", out var attachments) || attachments.ValueKind != JsonValueKind.Array)
            return (null, $"Attachment '{filename}' not found. Use get_email_attachments or omit part for inventory.", null);

        string? downloadLink = null;
        string? contentType = null;
        string? actualFilename = null;
        foreach (var att in attachments.EnumerateArray())
        {
            var attFilename = att.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
            if (!string.Equals(attFilename, filename, StringComparison.OrdinalIgnoreCase))
                continue;

            downloadLink = att.TryGetProperty("link", out var link) ? link.GetString() : null;
            contentType = att.TryGetProperty("contentType", out var ct) ? ct.GetString() : null;
            actualFilename = attFilename;
            break;
        }

        if (string.IsNullOrEmpty(downloadLink) || actualFilename == null)
            return (null, $"Attachment '{filename}' not found. Use get_email_attachments or omit part for inventory.", null);

        if (!IsTextDecodableAttachment(contentType, actualFilename))
        {
            return (null,
                $"Attachment '{actualFilename}' is binary ({contentType ?? "unknown type"}). Use download_email_attachment to save it.",
                contentType);
        }

        var (data, fetchedType) = await userContext.GetBytesAsync(downloadLink);
        var resolvedType = string.IsNullOrEmpty(contentType) ? fetchedType : contentType;
        return (Encoding.UTF8.GetString(data), null, resolvedType);
    }

    private static bool IsTextDecodableAttachment(string? contentType, string? filename)
    {
        var ext = Path.GetExtension(filename ?? "").ToLowerInvariant();
        if (ext is ".md" or ".log" or ".csv")
            return true;

        if (string.IsNullOrEmpty(contentType))
            return false;

        var media = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (media.StartsWith("text/"))
            return true;

        return media is "application/json" or "application/xml" or "message/rfc822";
    }

    private static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html))
            return "";

        var s = RemoveTagBlocks(html, "script");
        s = RemoveTagBlocks(s, "style");
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</p>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</div>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</tr>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t]+\r?\n", "\n");
        s = Regex.Replace(s, @"(?:\r?\n){3,}", "\n\n");
        return s.Trim();
    }

    private static string RemoveTagBlocks(string html, string tag)
    {
        var open = "<" + tag;
        var close = "</" + tag + ">";
        var sb = new StringBuilder(html.Length);
        var i = 0;
        while (i < html.Length)
        {
            var start = html.IndexOf(open, i, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                sb.Append(html, i, html.Length - i);
                break;
            }

            sb.Append(html, i, start - i);
            var tagEnd = html.IndexOf('>', start);
            if (tagEnd < 0)
                break;

            var closeIdx = html.IndexOf(close, tagEnd + 1, StringComparison.OrdinalIgnoreCase);
            if (closeIdx < 0)
                break;

            i = closeIdx + close.Length;
        }

        return sb.ToString();
    }

    private static Dictionary<string, object?> BuildWindow(string part, string content, int offset, int limit)
    {
        var total = content.Length;
        if (offset < 0)
            offset = 0;
        if (offset > total)
            offset = total;

        var requested = limit == 0 ? MaxWindowChars : limit;
        if (requested < 0)
            requested = DefaultWindowLimit;

        var capped = Math.Min(requested, MaxWindowChars);
        var remaining = total - offset;
        var take = Math.Min(capped, remaining);
        var slice = take > 0 ? content.Substring(offset, take) : "";
        var nextOffset = offset + take;
        var hasMore = nextOffset < total;

        var result = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["mode"] = "window",
            ["part"] = part,
            ["totalChars"] = total,
            ["offset"] = offset,
            ["returnedChars"] = slice.Length,
            ["nextOffset"] = nextOffset,
            ["hasMore"] = hasMore,
            ["content"] = slice
        };

        if (requested > MaxWindowChars || (limit == 0 && remaining > MaxWindowChars))
            result["note"] = $"Hard ceiling is {MaxWindowChars} characters per call. Use nextOffset to continue.";
        else if (slice.Length > 30_000)
            result["note"] = "Large window; some MCP clients clip results above ~30k characters. Prefer smaller limit or grep.";

        return result;
    }

    private static Dictionary<string, object?> BuildGrep(
        string part, string content, string pattern, bool isRegex, bool ignoreCase, int contextChars, int maxMatches)
    {
        if (contextChars < 0)
            contextChars = 0;
        if (contextChars > 5_000)
            contextChars = 5_000;
        if (maxMatches <= 0)
            maxMatches = 20;
        if (maxMatches > 200)
            maxMatches = 200;

        var lineStarts = BuildLineStarts(content);
        var matches = new List<object>();
        Regex? regex = null;

        if (isRegex)
        {
            var (built, regexError) = BuildSearchRegex(pattern, ignoreCase);
            if (regexError != null)
            {
                return new Dictionary<string, object?>
                {
                    ["success"] = false,
                    ["error"] = regexError
                };
            }

            regex = built;
        }

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var pos = 0;
        while (matches.Count < maxMatches)
        {
            int found;
            int matchLength;
            if (regex != null)
            {
                var m = regex.Match(content, pos);
                if (!m.Success)
                    break;
                found = m.Index;
                matchLength = m.Length;
            }
            else
            {
                found = content.IndexOf(pattern, pos, comparison);
                if (found < 0)
                    break;
                matchLength = pattern.Length;
            }

            var snippetStart = Math.Max(0, found - contextChars);
            var snippetEnd = Math.Min(content.Length, found + Math.Max(matchLength, 1) + contextChars);
            matches.Add(new
            {
                offset = found,
                line = LineNumberAt(lineStarts, found),
                length = matchLength,
                snippet = content[snippetStart..snippetEnd]
            });

            var advance = Math.Max(matchLength, 1);
            pos = found + advance;
            if (pos >= content.Length)
                break;
        }

        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["mode"] = "grep",
            ["part"] = part,
            ["totalChars"] = content.Length,
            ["pattern"] = pattern,
            ["isRegex"] = isRegex,
            ["ignoreCase"] = ignoreCase,
            ["matchCount"] = matches.Count,
            ["matches"] = matches,
            ["hint"] = "Use a match offset with read_email_part offset/limit to window the surrounding content."
        };
    }

    private static (Regex? Regex, string? Error) BuildSearchRegex(string pattern, bool ignoreCase)
    {
        var timeout = TimeSpan.FromSeconds(2);
        var options = RegexOptions.CultureInvariant;
        if (ignoreCase)
            options |= RegexOptions.IgnoreCase;

        try
        {
            return (new Regex(pattern, options | RegexOptions.NonBacktracking, timeout), null);
        }
        catch (NotSupportedException)
        {
            try
            {
                return (new Regex(pattern, options, timeout), null);
            }
            catch (ArgumentException ex)
            {
                return (null, $"Invalid regex: {ex.Message}");
            }
        }
        catch (ArgumentException)
        {
            try
            {
                return (new Regex(pattern, options, timeout), null);
            }
            catch (ArgumentException inner)
            {
                return (null, $"Invalid regex: {inner.Message}");
            }
        }
    }

    private static List<int> BuildLineStarts(string content)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
                starts.Add(i + 1);
        }

        return starts;
    }

    private static int LineNumberAt(List<int> lineStarts, int offset)
    {
        var idx = lineStarts.BinarySearch(offset);
        if (idx < 0)
            idx = ~idx - 1;
        if (idx < 0)
            idx = 0;
        return idx + 1;
    }

    private static string GetStringProp(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return "";
        if (!obj.TryGetProperty(name, out var el))
            return "";
        return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
    }
}