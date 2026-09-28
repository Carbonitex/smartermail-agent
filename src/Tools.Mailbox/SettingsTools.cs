using System.ComponentModel;
using System.Net;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Server.Tools;

[McpServerToolType]
public sealed class SettingsTools
{
    // ============================================================
    // BLOCKED/TRUSTED SENDERS
    // ============================================================

    [McpServerTool]
    [Description("Block email senders to prevent delivery. Accepts email addresses or domains.")]
    public static async Task<string> BlockSenders(
        [Description("Comma-separated email addresses or domains to block (e.g., 'spam@example.com, baddomain.com')")]
        string senders,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot block senders." });

        try
        {
            if (string.IsNullOrWhiteSpace(senders))
                return JsonSerializer.Serialize(new { success = false, error = "No senders provided" });

            // Parse comma-separated list and build the blockedSenders dictionary
            var senderList = senders.Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (senderList.Count == 0)
                return JsonSerializer.Serialize(new { success = false, error = "No valid senders provided" });

            // Build the dictionary with empty uint arrays as values
            var blockedSenders = new Dictionary<string, List<uint>>();
            foreach (var sender in senderList)
            {
                blockedSenders[sender] = new List<uint>();
            }

            var payload = new
            {
                blockedSenders,
                isSender = true
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/block-senders", payload);

            return JsonSerializer.Serialize(new
            {
                success = response.TryGetProperty("success", out var succ) && succ.GetBoolean(),
                blockedCount = senderList.Count,
                blocked = senderList,
                response
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
    [Description("Unblock previously blocked senders. Accepts email addresses or domains.")]
    public static async Task<string> UnblockSenders(
        [Description("Comma-separated email addresses or domains to unblock")]
        string senders,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot unblock senders." });

        try
        {
            if (string.IsNullOrWhiteSpace(senders))
                return JsonSerializer.Serialize(new { success = false, error = "No senders provided" });

            var senderList = senders.Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (senderList.Count == 0)
                return JsonSerializer.Serialize(new { success = false, error = "No valid senders provided" });

            // Build the dictionary with empty uint arrays as values (same format as block-senders)
            var blockedSenders = new Dictionary<string, List<uint>>();
            foreach (var sender in senderList)
            {
                blockedSenders[sender] = new List<uint>();
            }

            var payload = new
            {
                blockedSenders,
                sourceOwner = userContext.EmailAddress,
                sourceFolder = "Inbox",
                isSender = true
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/unblock-senders", payload);

            return JsonSerializer.Serialize(new
            {
                success = response.TryGetProperty("success", out var succ) && succ.GetBoolean(),
                unblockedCount = senderList.Count,
                unblocked = senderList,
                response
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
    [Description("Check if a sender email address is blocked")]
    public static async Task<string> CheckSenderBlocked(
        [Description("The email address to check")]
        string email,
        UserContext userContext)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(email))
                return JsonSerializer.Serialize(new { success = false, error = "No email address provided" });

            var payload = new { email = email.Trim() };
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sender-blocked", payload);

            // API returns "result" field for blocked status
            var isBlocked = (response.TryGetProperty("result", out var result) && result.GetBoolean()) ||
                            (response.TryGetProperty("isBlocked", out var blocked) && blocked.GetBoolean());

            return JsonSerializer.Serialize(new
            {
                success = response.TryGetProperty("success", out var succ) && succ.GetBoolean(),
                email = email.Trim(),
                isBlocked,
                response
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

    [McpServerTool(Destructive = true)]
    [Description("Set trusted senders (whitelist) to bypass spam filtering. Accepts email addresses and/or domains.")]
    public static async Task<string> SetTrustedSenders(
        [Description("Comma-separated trusted email addresses (e.g., 'friend@example.com, colleague@work.com')")]
        string emails = "",
        [Description("Comma-separated trusted domains (e.g., 'trusted.com, company.org')")]
        string domains = "",
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot set trusted senders." });

        try
        {
            var emailList = string.IsNullOrWhiteSpace(emails)
                ? new List<string>()
                : emails.Split(',').Select(e => e.Trim()).Where(e => !string.IsNullOrEmpty(e)).ToList();

            var domainList = string.IsNullOrWhiteSpace(domains)
                ? new List<string>()
                : domains.Split(',').Select(d => d.Trim()).Where(d => !string.IsNullOrEmpty(d)).ToList();

            if (emailList.Count == 0 && domainList.Count == 0)
                return JsonSerializer.Serialize(new { success = false, error = "No emails or domains provided" });

            var payload = new
            {
                domains = domainList,
                emails = emailList
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/whitelist", payload);

            return JsonSerializer.Serialize(new
            {
                success = response.TryGetProperty("success", out var succ) && succ.GetBoolean(),
                trustedEmails = emailList,
                trustedDomains = domainList,
                response
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
    [Description("Check if a sender email address is trusted (whitelisted)")]
    public static async Task<string> CheckSenderTrusted(
        [Description("The email address to check")]
        string email,
        UserContext userContext)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(email))
                return JsonSerializer.Serialize(new { success = false, error = "No email address provided" });

            var payload = new { email = email.Trim() };
            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/sender-trusted", payload);

            var isTrusted = response.TryGetProperty("isTrusted", out var trusted) && trusted.GetBoolean();
            var isBypassed = response.TryGetProperty("isBypassed", out var bypassed) && bypassed.GetBoolean();
            var trustSource = response.TryGetProperty("trustSource", out var source) ? source.GetString() : null;

            return JsonSerializer.Serialize(new
            {
                success = response.TryGetProperty("success", out var succ) && succ.GetBoolean(),
                email = email.Trim(),
                isTrusted,
                isBypassed,
                trustSource,
                response
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

    // ============================================================
    // CONTENT FILTERS (INBOX RULES)
    // ============================================================

    [McpServerTool(ReadOnly = true)]
    [Description(@"Get all content filters (inbox rules). Returns filter groups with their conditions and actions.

Condition Field Types: 0=FromAddress, 1=FromDomain, 3=WordsInSubject, 4=WordsInBody, 7=WordsInTo, 10=ToAddress, 16=HasAttachments, 17=AttachmentFilename
Comparison Types: 0=Equals, 1=DoesntEqual, 2=Contains, 3=DoesntContain
Action Types: 1=Delete, 4=MoveToFolder, 5=AddHeader, 6=PrefixSubject, 7=Forward, 8=MarkAsRead
Match Types: 0=And (all conditions), 1=Or (any condition)")]
    public static async Task<string> GetContentFilters(
        UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/settings/content-filter-groups");

            var filters = new List<object>();
            if (response.TryGetProperty("contentFilterGroups", out var filterGroups))
            {
                foreach (var group in filterGroups.EnumerateArray())
                {
                    var id = group.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;
                    var title = group.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : "";
                    var matchType = group.TryGetProperty("matchType", out var matchProp) ? matchProp.GetInt32() : 0;
                    var isEnabled = !group.TryGetProperty("disabled", out var disabledProp) || !disabledProp.GetBoolean();

                    // Parse conditions (filters)
                    var conditions = new List<object>();
                    if (group.TryGetProperty("filters", out var filtersArray))
                    {
                        foreach (var filter in filtersArray.EnumerateArray())
                        {
                            var fieldType = filter.TryGetProperty("fieldType", out var ft) ? ft.GetInt32() : 0;
                            var filterType = filter.TryGetProperty("filterType", out var flt) ? flt.GetInt32() : 0;
                            var searchArgs = new List<string>();
                            if (filter.TryGetProperty("searchArguments", out var args))
                            {
                                foreach (var arg in args.EnumerateArray())
                                {
                                    searchArgs.Add(arg.GetString() ?? "");
                                }
                            }

                            conditions.Add(new
                            {
                                fieldType,
                                fieldTypeName = GetFieldTypeName(fieldType),
                                filterType,
                                filterTypeName = GetComparisonTypeName(filterType),
                                searchArguments = searchArgs
                            });
                        }
                    }

                    // Parse actions
                    var actions = new List<object>();
                    if (group.TryGetProperty("actions", out var actionsArray))
                    {
                        foreach (var action in actionsArray.EnumerateArray())
                        {
                            var actionType = action.TryGetProperty("actionType", out var at) ? at.GetInt32() : 0;
                            var argument = action.TryGetProperty("argument", out var arg) ? arg.GetString() : "";

                            actions.Add(new
                            {
                                actionType,
                                actionTypeName = GetActionTypeName(actionType),
                                argument
                            });
                        }
                    }

                    filters.Add(new
                    {
                        id,
                        title,
                        matchType,
                        matchTypeName = matchType == 0 ? "And" : "Or",
                        isEnabled,
                        conditions,
                        actions
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                success = response.TryGetProperty("success", out var succ) && succ.GetBoolean(),
                filterCount = filters.Count,
                filters
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
    [Description(@"Create a content filter (inbox rule).

Conditions JSON format: [{""fieldType"": 0, ""filterType"": 2, ""searchArguments"": [""value""]}]
- fieldType: 0=FromAddress, 1=FromDomain, 3=WordsInSubject, 4=WordsInBody, 10=ToAddress, 16=HasAttachments, 17=AttachmentFilename
- filterType: 0=Equals, 1=DoesntEqual, 2=Contains, 3=DoesntContain

Actions JSON format: [{""actionType"": 4, ""argument"": ""FolderName""}]
- actionType: 1=Delete, 4=MoveToFolder (arg=folder), 5=AddHeader (arg=Header:Value), 6=PrefixSubject (arg=prefix), 7=Forward (arg=email), 8=MarkAsRead

Example: Create filter to move newsletters to a folder:
  title: ""Newsletter Filter""
  matchType: ""And""
  conditions: [{""fieldType"": 0, ""filterType"": 2, ""searchArguments"": [""newsletter@""]}]
  actions: [{""actionType"": 4, ""argument"": ""Newsletters""}]")]
    public static async Task<string> CreateContentFilter(
        [Description("Filter name/title")]
        string title,
        [Description("Match type: 'And' (all conditions must match) or 'Or' (any condition matches)")]
        string matchType = "And",
        [Description("JSON array of filter conditions")]
        string conditions = "[]",
        [Description("JSON array of filter actions")]
        string actions = "[]",
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot create content filters." });

        try
        {
            if (string.IsNullOrWhiteSpace(title))
                return JsonSerializer.Serialize(new { success = false, error = "Filter title is required" });

            // Parse match type
            var matchTypeValue = matchType?.ToLowerInvariant() == "or" ? 1 : 0;

            // Parse conditions JSON
            List<JsonElement> conditionsList;
            try
            {
                conditionsList = JsonSerializer.Deserialize<List<JsonElement>>(conditions ?? "[]") ?? new List<JsonElement>();
            }
            catch
            {
                return JsonSerializer.Serialize(new { success = false, error = "Invalid conditions JSON. Expected: [{\"fieldType\": 0, \"filterType\": 2, \"searchArguments\": [\"value\"]}]. Tip: use create_content_filter_simple for plain English conditions." });
            }

            // Parse actions JSON
            List<JsonElement> actionsList;
            try
            {
                actionsList = JsonSerializer.Deserialize<List<JsonElement>>(actions ?? "[]") ?? new List<JsonElement>();
            }
            catch
            {
                return JsonSerializer.Serialize(new { success = false, error = "Invalid actions JSON. Expected: [{\"actionType\": 4, \"argument\": \"FolderName\"}]. Tip: use create_content_filter_simple for plain English actions." });
            }

            if (conditionsList.Count == 0)
                return JsonSerializer.Serialize(new { success = false, error = "At least one condition is required" });

            if (actionsList.Count == 0)
                return JsonSerializer.Serialize(new { success = false, error = "At least one action is required" });

            // Build the filter object
            var newFilter = new
            {
                title,
                matchType = matchTypeValue,
                filters = conditionsList,
                actions = actionsList
            };

            var payload = new
            {
                toAdd = new[] { newFilter }
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/content-filter-groups", payload);

            return JsonSerializer.Serialize(new
            {
                success = response.TryGetProperty("success", out var succ) && succ.GetBoolean(),
                created = new { title, matchType = matchTypeValue == 0 ? "And" : "Or" },
                response
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
    [Description(@"Create a content filter (inbox rule) using simple human-readable descriptions instead of JSON.

Condition examples (separate multiple with semicolons):
  'from contains newsletter@' - match sender address containing text
  'from equals user@example.com' - match exact sender address
  'from domain is example.com' - match sender domain
  'subject contains invoice' - match subject line
  'body contains unsubscribe' - match email body
  'to contains sales@' - match recipient
  'has attachments' - match emails with attachments
  'attachment name contains .pdf' - match attachment filename
  'attachment extension is pdf' - match attachment extension

Action examples (separate multiple with semicolons):
  'move to Newsletters' - move to folder
  'delete' - delete the email
  'mark as read' - mark as read
  'forward to bob@example.com' - forward to address
  'prefix subject with [NEWS]' - add prefix to subject
  'set priority high' - set priority (high/normal/low)
  'flag' - flag for follow up

Supports negation: 'from does not contain X', 'subject doesn't contain X'")]
    public static async Task<string> CreateContentFilterSimple(
        [Description("Filter name/title")]
        string name,
        [Description("Condition(s) in plain English. Separate multiple conditions with semicolons.")]
        string when,
        [Description("Action(s) in plain English. Separate multiple actions with semicolons.")]
        string then,
        [Description("Whether all conditions must match (true) or any condition can match (false). Default: true")]
        bool matchAll = true,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot create content filters." });

        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return JsonSerializer.Serialize(new { success = false, error = "Filter name is required" });
            if (string.IsNullOrWhiteSpace(when))
                return JsonSerializer.Serialize(new { success = false, error = "At least one condition is required (when parameter)" });
            if (string.IsNullOrWhiteSpace(then))
                return JsonSerializer.Serialize(new { success = false, error = "At least one action is required (then parameter)" });

            // Parse conditions
            var conditionStrings = when.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var parsedConditions = new List<object>();
            foreach (var condStr in conditionStrings)
            {
                var result = ParseCondition(condStr);
                if (result.Error != null)
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = result.Error,
                        hint = "Use the create_content_filter tool for advanced rules with JSON format."
                    });
                parsedConditions.Add(new
                {
                    fieldType = result.FieldType,
                    filterType = result.FilterType,
                    searchArguments = result.SearchArgs
                });
            }

            // Parse actions
            var actionStrings = then.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var parsedActions = new List<object>();
            foreach (var actStr in actionStrings)
            {
                var result = ParseAction(actStr);
                if (result.Error != null)
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = result.Error,
                        hint = "Use the create_content_filter tool for advanced rules with JSON format."
                    });
                parsedActions.Add(new
                {
                    actionType = result.ActionType,
                    argument = result.Argument
                });
            }

            var newFilter = new
            {
                title = name,
                matchType = matchAll ? 0 : 1,
                filters = parsedConditions,
                actions = parsedActions
            };

            var payload = new { toAdd = new[] { newFilter } };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/content-filter-groups", payload);

            return JsonSerializer.Serialize(new
            {
                success = response.TryGetProperty("success", out var succ) && succ.GetBoolean(),
                created = new
                {
                    title = name,
                    matchType = matchAll ? "And" : "Or",
                    conditions = conditionStrings.Length,
                    actions = actionStrings.Length
                },
                response
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
    [Description(@"Update an existing content filter. Only provided fields will be updated.

See create_content_filter for conditions and actions JSON format.")]
    public static async Task<string> UpdateContentFilter(
        [Description("Filter ID to update (use get_content_filters to find IDs)")]
        string filterId,
        [Description("New filter name/title (optional)")]
        string title = "",
        [Description("New match type: 'And' or 'Or' (optional)")]
        string matchType = "",
        [Description("New conditions JSON array (optional)")]
        string conditions = "",
        [Description("New actions JSON array (optional)")]
        string actions = "",
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot update content filters." });

        try
        {
            if (string.IsNullOrWhiteSpace(filterId) || !long.TryParse(filterId, out var filterIdNum))
                return JsonSerializer.Serialize(new { success = false, error = "Valid numeric filter ID is required. Use get_content_filters to find filter IDs." });

            // First, get the existing filter
            var existingResponse = await userContext.GetAsync<JsonElement>("/api/v1/settings/content-filter-groups");

            JsonElement? existingFilter = null;
            if (existingResponse.TryGetProperty("contentFilterGroups", out var filterGroups))
            {
                foreach (var group in filterGroups.EnumerateArray())
                {
                    var id = group.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;
                    if (id == filterIdNum)
                    {
                        existingFilter = group;
                        break;
                    }
                }
            }

            if (!existingFilter.HasValue)
                return JsonSerializer.Serialize(new { success = false, error = $"Filter with ID {filterId} not found" });

            // Build updated filter, preserving existing values
            var existing = existingFilter.Value;
            var updatedTitle = !string.IsNullOrWhiteSpace(title) ? title :
                (existing.TryGetProperty("title", out var t) ? t.GetString() : "");

            int updatedMatchType;
            if (!string.IsNullOrWhiteSpace(matchType))
            {
                updatedMatchType = matchType.ToLowerInvariant() == "or" ? 1 : 0;
            }
            else
            {
                updatedMatchType = existing.TryGetProperty("matchType", out var m) ? m.GetInt32() : 0;
            }

            object updatedFilters;
            if (!string.IsNullOrWhiteSpace(conditions))
            {
                try
                {
                    updatedFilters = JsonSerializer.Deserialize<List<JsonElement>>(conditions) ?? new List<JsonElement>();
                }
                catch
                {
                    return JsonSerializer.Serialize(new { success = false, error = "Invalid conditions JSON. Expected: [{\"fieldType\": 0, \"filterType\": 2, \"searchArguments\": [\"value\"]}]. Tip: use create_content_filter_simple for plain English conditions." });
                }
            }
            else
            {
                updatedFilters = existing.TryGetProperty("filters", out var f) ? f : new List<object>();
            }

            object updatedActions;
            if (!string.IsNullOrWhiteSpace(actions))
            {
                try
                {
                    updatedActions = JsonSerializer.Deserialize<List<JsonElement>>(actions) ?? new List<JsonElement>();
                }
                catch
                {
                    return JsonSerializer.Serialize(new { success = false, error = "Invalid actions JSON. Expected: [{\"actionType\": 4, \"argument\": \"FolderName\"}]. Tip: use create_content_filter_simple for plain English actions." });
                }
            }
            else
            {
                updatedActions = existing.TryGetProperty("actions", out var a) ? a : new List<object>();
            }

            var updatedFilter = new
            {
                id = filterIdNum,
                title = updatedTitle,
                matchType = updatedMatchType,
                filters = updatedFilters,
                actions = updatedActions
            };

            var payload = new
            {
                contentFilterGroups = new[] { updatedFilter }
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/content-filter-groups", payload);

            return JsonSerializer.Serialize(new
            {
                success = response.TryGetProperty("success", out var succ) && succ.GetBoolean(),
                updated = new { id = filterIdNum, title = updatedTitle },
                response
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

    [McpServerTool(Destructive = true)]
    [Description("Delete content filters by ID")]
    public static async Task<string> DeleteContentFilters(
        [Description("Comma-separated filter IDs to delete")]
        string filterIds,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot delete content filters." });

        try
        {
            if (string.IsNullOrWhiteSpace(filterIds))
                return JsonSerializer.Serialize(new { success = false, error = "No filter IDs provided" });

            var idList = filterIds.Split(',')
                .Select(id => id.Trim())
                .Where(id => int.TryParse(id, out _))
                .Select(id => int.Parse(id))
                .ToList();

            if (idList.Count == 0)
                return JsonSerializer.Serialize(new { success = false, error = "No valid filter IDs provided. Expected comma-separated numeric IDs (e.g., '1,2,3'). Use get_content_filters to find filter IDs." });

            var payload = new
            {
                toRemove = idList
            };

            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/content-filter-groups", payload);

            return JsonSerializer.Serialize(new
            {
                success = response.TryGetProperty("success", out var succ) && succ.GetBoolean(),
                deletedCount = idList.Count,
                deletedIds = idList,
                response
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
    [Description("Run content filters against a folder to process existing emails")]
    public static async Task<string> RunContentFilters(
        [Description("Folder path to run filters on (e.g., 'Inbox')")]
        string folder,
        [Description("Optional: comma-separated filter IDs to run. If empty, runs all enabled filters.")]
        string filterIds = "",
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot run content filters." });

        try
        {
            if (string.IsNullOrWhiteSpace(folder))
                return JsonSerializer.Serialize(new { success = false, error = "Folder path is required" });

            var payload = new Dictionary<string, object>
            {
                { "folder", folder.Trim() }
            };

            // If specific filter IDs are provided, add them (API expects List<int>)
            if (!string.IsNullOrWhiteSpace(filterIds))
            {
                var idList = filterIds.Split(',')
                    .Select(id => id.Trim())
                    .Where(id => int.TryParse(id, out _))
                    .Select(id => int.Parse(id))
                    .ToList();

                if (idList.Count > 0)
                {
                    payload["toRun"] = idList;
                }
            }

            var response = await userContext.PostAsync<JsonElement>("/api/v1/settings/run-content-filters", payload);

            return JsonSerializer.Serialize(new
            {
                success = response.TryGetProperty("success", out var succ) && succ.GetBoolean(),
                folder = folder.Trim(),
                ranSpecificFilters = !string.IsNullOrWhiteSpace(filterIds),
                response
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

    // ============================================================
    // HELPER METHODS
    // ============================================================

    private static string GetFieldTypeName(int fieldType)
    {
        return fieldType switch
        {
            0 => "FromAddress",
            1 => "FromDomain",
            2 => "FromTrustedSender",
            3 => "WordsInSubject",
            4 => "WordsInBody",
            5 => "WordsInSubjectOrBody",
            6 => "WordsInFrom",
            7 => "WordsInTo",
            8 => "WordsInHeaders",
            9 => "WordsInMessage",
            10 => "ToAddress",
            11 => "ToDomain",
            12 => "ToMe",
            13 => "MyNameInTo",
            14 => "MyNameNotInTo",
            15 => "MyNameInToOrCC",
            16 => "HasAttachments",
            17 => "AttachmentFilename",
            18 => "AttachmentExtension",
            19 => "AttachmentSize",
            20 => "PriorityHigh",
            21 => "PriorityNormal",
            22 => "PriorityLow",
            _ => $"Unknown({fieldType})"
        };
    }

    private static string GetComparisonTypeName(int filterType)
    {
        return filterType switch
        {
            0 => "Equals",
            1 => "DoesntEqual",
            2 => "Contains",
            3 => "DoesntContain",
            _ => $"Unknown({filterType})"
        };
    }

    private static string GetActionTypeName(int actionType)
    {
        return actionType switch
        {
            0 => "NoAction",
            1 => "Delete",
            3 => "Bounce",
            4 => "MoveToFolder",
            5 => "AddHeader",
            6 => "PrefixSubject",
            7 => "Forward",
            8 => "MarkAsRead",
            9 => "SetPriority",
            10 => "FollowUp",
            _ => $"Unknown({actionType})"
        };
    }

    // ============================================================
    // NATURAL LANGUAGE FILTER PARSING
    // ============================================================

    private record ConditionParseResult(int FieldType, int FilterType, List<string> SearchArgs, string? Error);
    private record ActionParseResult(int ActionType, string Argument, string? Error);

    private static ConditionParseResult ParseCondition(string condition)
    {
        var input = NormalizeInput(condition);

        // "has attachments" / "has attachment" — no value needed
        if (input is "has attachments" or "has attachment")
            return new ConditionParseResult(16, 0, new List<string>(), null);

        // Order matters: try more specific prefixes first

        // "attachment extension"
        if (TryStripPrefix(input, "attachment extension", out var rem))
        {
            var cv = ParseComparisonAndValue(rem);
            if (cv == null)
                return ConditionError(condition);
            return new ConditionParseResult(18, cv.Value.FilterType, new List<string> { cv.Value.Value }, null);
        }

        // "attachment name" / "attachment filename"
        if (TryStripPrefix(input, "attachment filename", out rem) || TryStripPrefix(input, "attachment name", out rem))
        {
            var cv = ParseComparisonAndValue(rem);
            if (cv == null)
                return ConditionError(condition);
            return new ConditionParseResult(17, cv.Value.FilterType, new List<string> { cv.Value.Value }, null);
        }

        // "from domain"
        if (TryStripPrefix(input, "from domain", out rem) || TryStripPrefix(input, "domain", out rem))
        {
            var cv = ParseComparisonAndValue(rem);
            if (cv == null)
                return ConditionError(condition);
            return new ConditionParseResult(1, cv.Value.FilterType, new List<string> { cv.Value.Value }, null);
        }

        // "from"
        if (TryStripPrefix(input, "from", out rem))
        {
            var cv = ParseComparisonAndValue(rem);
            if (cv == null)
                return ConditionError(condition);
            return new ConditionParseResult(0, cv.Value.FilterType, new List<string> { cv.Value.Value }, null);
        }

        // "subject"
        if (TryStripPrefix(input, "subject", out rem))
        {
            var cv = ParseComparisonAndValue(rem);
            if (cv == null)
                return ConditionError(condition);
            return new ConditionParseResult(3, cv.Value.FilterType, new List<string> { cv.Value.Value }, null);
        }

        // "body"
        if (TryStripPrefix(input, "body", out rem))
        {
            var cv = ParseComparisonAndValue(rem);
            if (cv == null)
                return ConditionError(condition);
            return new ConditionParseResult(4, cv.Value.FilterType, new List<string> { cv.Value.Value }, null);
        }

        // "to address"
        if (TryStripPrefix(input, "to address", out rem))
        {
            var cv = ParseComparisonAndValue(rem);
            if (cv == null)
                return ConditionError(condition);
            return new ConditionParseResult(10, cv.Value.FilterType, new List<string> { cv.Value.Value }, null);
        }

        // "to domain"
        if (TryStripPrefix(input, "to domain", out rem))
        {
            var cv = ParseComparisonAndValue(rem);
            if (cv == null)
                return ConditionError(condition);
            return new ConditionParseResult(11, cv.Value.FilterType, new List<string> { cv.Value.Value }, null);
        }

        // "to"
        if (TryStripPrefix(input, "to", out rem))
        {
            var cv = ParseComparisonAndValue(rem);
            if (cv == null)
                return ConditionError(condition);
            // "to equals/is" -> ToAddress (exact match), "to contains" -> WordsInTo
            var fieldType = cv.Value.FilterType == 0 || cv.Value.FilterType == 1 ? 10 : 7;
            return new ConditionParseResult(fieldType, cv.Value.FilterType, new List<string> { cv.Value.Value }, null);
        }

        return ConditionError(condition);
    }

    private static ActionParseResult ParseAction(string action)
    {
        var input = NormalizeInput(action);

        // "delete"
        if (input == "delete")
            return new ActionParseResult(1, "", null);

        // "mark as read" / "mark read"
        if (input is "mark as read" or "mark read")
            return new ActionParseResult(8, "", null);

        // "flag" / "follow up" / "followup"
        if (input is "flag" or "follow up" or "followup")
            return new ActionParseResult(10, "", null);

        // "move to X"
        if (TryStripPrefix(input, "move to", out var rem))
        {
            var value = StripQuotes(rem.Trim());
            if (string.IsNullOrEmpty(value))
                return new ActionParseResult(0, "", $"Could not parse action: '{action}'. 'move to' requires a folder name.");
            return new ActionParseResult(4, value, null);
        }

        // "forward to X"
        if (TryStripPrefix(input, "forward to", out rem))
        {
            var value = StripQuotes(rem.Trim());
            if (string.IsNullOrEmpty(value))
                return new ActionParseResult(0, "", $"Could not parse action: '{action}'. 'forward to' requires an email address.");
            return new ActionParseResult(7, value, null);
        }

        // "prefix subject with X" / "prefix subject X"
        if (TryStripPrefix(input, "prefix subject with", out rem) || TryStripPrefix(input, "prefix subject", out rem))
        {
            var value = StripQuotes(rem.Trim());
            if (string.IsNullOrEmpty(value))
                return new ActionParseResult(0, "", $"Could not parse action: '{action}'. 'prefix subject' requires a prefix value.");
            return new ActionParseResult(6, value, null);
        }

        // "add header X"
        if (TryStripPrefix(input, "add header", out rem))
        {
            var value = StripQuotes(rem.Trim());
            if (string.IsNullOrEmpty(value))
                return new ActionParseResult(0, "", $"Could not parse action: '{action}'. 'add header' requires a Header:Value.");
            return new ActionParseResult(5, value, null);
        }

        // "set priority high/normal/low"
        if (TryStripPrefix(input, "set priority", out rem))
        {
            var priority = rem.Trim().ToLowerInvariant();
            if (priority is not ("high" or "normal" or "low"))
                return new ActionParseResult(0, "", $"Could not parse action: '{action}'. Priority must be 'high', 'normal', or 'low'.");
            return new ActionParseResult(9, priority, null);
        }

        return new ActionParseResult(0, "", $"Could not parse action: '{action}'. Expected formats like 'move to Folder', 'delete', 'mark as read', 'forward to email@example.com', 'prefix subject with [TAG]', 'set priority high', or 'flag'.");
    }

    private static (int FilterType, string Value)? ParseComparisonAndValue(string remainder)
    {
        var r = remainder.Trim();

        // Negation patterns (check first — more specific)
        if (TryStripPrefix(r, "does not contain", out var val) || TryStripPrefix(r, "doesn't contain", out val) || TryStripPrefix(r, "not contains", out val))
            return (3, StripQuotes(val.Trim()));

        if (TryStripPrefix(r, "does not equal", out val) || TryStripPrefix(r, "doesn't equal", out val) || TryStripPrefix(r, "is not", out val))
            return (1, StripQuotes(val.Trim()));

        // Positive patterns
        if (TryStripPrefix(r, "contains", out val))
            return (2, StripQuotes(val.Trim()));

        if (TryStripPrefix(r, "equals", out val) || TryStripPrefix(r, "is", out val))
            return (0, StripQuotes(val.Trim()));

        return null;
    }

    private static string NormalizeInput(string input)
    {
        // Lowercase, trim, collapse multiple spaces
        var normalized = input.Trim().ToLowerInvariant();
        while (normalized.Contains("  "))
            normalized = normalized.Replace("  ", " ");
        return normalized;
    }

    private static bool TryStripPrefix(string input, string prefix, out string remainder)
    {
        if (input.StartsWith(prefix + " "))
        {
            remainder = input[(prefix.Length + 1)..];
            return true;
        }
        if (input == prefix)
        {
            remainder = "";
            return true;
        }
        remainder = "";
        return false;
    }

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value.StartsWith('"') && value.EndsWith('"')) ||
             (value.StartsWith('\'') && value.EndsWith('\''))))
            return value[1..^1];
        return value;
    }

    private static ConditionParseResult ConditionError(string condition)
    {
        return new ConditionParseResult(0, 0, new List<string>(), $"Could not parse condition: '{condition}'. Expected formats like 'from contains value', 'subject equals value', 'from domain is value', 'has attachments', 'body contains value', or 'to contains value'.");
    }
}
