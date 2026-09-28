using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Server.Tools;

/// <summary>
/// Domain-administrator tools for how a domain receives and presents mail: domain aliases, the
/// catch-all, the forwarding blacklist, domain signatures and their mappings, domain-level shared
/// resources (calendars, rooms, equipment, address books, tasks, notes), event hooks, and the
/// domain settings object itself. Companion to <see cref="DomainAdminTools"/>; the same rules apply:
/// the signed-in account must be a domain admin (a plain user gets 403 from SmarterMail), every
/// endpoint is under <c>/api/v1/settings/domain/*</c> and acts on the token's own domain, and no
/// tool takes a <c>domain</c> parameter.
///
/// Paths and shapes come from the SmarterMail API reference
/// (https://mail.smartertools.com/Documentation/api, "Domain Settings" controller).
///
/// Every result is passed through <see cref="Redact"/> before it is returned: results go on to an
/// LLM provider, and several of these objects carry credentials (LDAP passwords, event-hook inputs).
/// Updates are read-modify-write so a field the caller did not mention is never blanked.
/// </summary>
[McpServerToolType]
public sealed class DomainRoutingTools
{
    private const string Root = "/api/v1/settings/domain";

    // ================================================================ reads

    [McpServerTool(Name = "domain_list_domain_aliases", ReadOnly = true)]
    [Description("List the domain aliases of your domain: other domain names (e.g. example.net) whose mail is delivered to this domain's mailboxes.")]
    public static async Task<string> DomainListDomainAliases(UserContext userContext) =>
        await Run(async () => await userContext.GetAsync<JsonElement>($"{Root}/domain-aliases"));

    [McpServerTool(Name = "domain_verify_domain_alias_mx", ReadOnly = true)]
    [Description("Check that a domain alias's MX records point at this mail server. Only checks DNS; changes nothing. Use after adding a domain alias.")]
    public static async Task<string> DomainVerifyDomainAliasMx(
        [Description("The domain alias name, e.g. 'example.net'")] string aliasName,
        UserContext userContext) =>
        await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/domain-alias-verify/{Uri.EscapeDataString(aliasName.Trim())}"));

    [McpServerTool(Name = "domain_check_address_available", ReadOnly = true)]
    [Description("Check whether an address (local part, e.g. 'sales') is free to use for a new user or alias in your domain. success:true means it is available.")]
    public static async Task<string> DomainCheckAddressAvailable(
        [Description("Local part to check, e.g. 'sales' (a full address is trimmed to its local part)")] string address,
        UserContext userContext) =>
        await Run(async () => await userContext.GetAsync<JsonElement>(
            $"{Root}/email-alias-does-not-exists/{Uri.EscapeDataString(ToLocalPart(address))}"));

    [McpServerTool(Name = "domain_get_forward_blacklist", ReadOnly = true)]
    [Description("Get the forwarding blacklist: addresses and domains that users in your domain may not auto-forward mail to.")]
    public static async Task<string> DomainGetForwardBlacklist(UserContext userContext) =>
        await Run(async () => await userContext.GetAsync<JsonElement>($"{Root}/forward-blacklist"));

    [McpServerTool(Name = "domain_list_signatures", ReadOnly = true)]
    [Description("List the domain's email signature templates (id, guid, name, HTML text, isDefault) and whether a domain signature is in use.")]
    public static async Task<string> DomainListSignatures(UserContext userContext) =>
        await Run(async () => await userContext.GetAsync<JsonElement>($"{Root}/signatures"));

    [McpServerTool(Name = "domain_get_signature_mappings", ReadOnly = true)]
    [Description("Get which signature template applies to which user, alias, domain alias or the whole domain. " +
                 "type: 1 DomainAlias, 2 Domain, 3 Alias, 4 User, 5 SmtpAccount. mapOption: 0 None, 1 UsePrimary, 2 SignatureSpecified (signatureGuid), 3 UseDomainDefault.")]
    public static async Task<string> DomainGetSignatureMappings(UserContext userContext) =>
        await Run(async () => await userContext.GetAsync<JsonElement>($"{Root}/signature-mappings"));

    [McpServerTool(Name = "domain_list_shared_resources", ReadOnly = true)]
    [Description("List the domain-level shared resources: shared calendars, conference rooms, equipment, address books, task and note lists, with their folderId, type, email alias, enabled state, booking window and who has access.")]
    public static async Task<string> DomainListSharedResources(UserContext userContext) =>
        await Run(async () => await userContext.GetAsync<JsonElement>($"{Root}/shared-resources/list"));

    [McpServerTool(Name = "domain_list_event_hooks", ReadOnly = true)]
    [Description("List event hooks (automated actions such as notifications that fire on mail events). By default lists the domain-level hooks; pass an owner to list one user's hooks.")]
    public static async Task<string> DomainListEventHooks(
        [Description("Owner username to filter by (optional; empty for the domain-level hooks)")] string owner = "",
        [Description("How many hooks to return (default 100)")] int count = 100,
        [Description("Index of the first hook to return, for paging (default 0)")] int startIndex = 0,
        UserContext userContext = null!) =>
        await Run(async () => await userContext.PostAsync<JsonElement>($"{Root}/event-hooks-by-owner", new
        {
            name = owner ?? string.Empty,
            sortDescending = false,
            count = Math.Clamp(count, 1, 1000),
            startIndex = Math.Max(0, startIndex),
        }));

    [McpServerTool(Name = "domain_get_event_catalog", ReadOnly = true)]
    [Description("Get the event types an event hook can fire on (with their conditions, arguments and available actions) and, optionally, the variables usable in action text. Read this before creating an event hook. Large response.")]
    public static async Task<string> DomainGetEventCatalog(
        [Description("Also include the event variables (default true)")] bool includeVariables = true,
        UserContext userContext = null!) =>
        await Run(async () =>
        {
            var events = await userContext.GetAsync<JsonElement>($"{Root}/domain-events");
            if (!includeVariables)
                return events;

            var variables = await userContext.GetAsync<JsonElement>($"{Root}/event-variables");
            return JsonSerializer.SerializeToElement(new
            {
                success = true,
                events = events.ValueKind == JsonValueKind.Object && events.TryGetProperty("events", out var e) ? e : events,
                variables = variables.ValueKind == JsonValueKind.Object && variables.TryGetProperty("variables", out var v) ? v : variables,
            });
        });

    [McpServerTool(Name = "domain_get_permissions", ReadOnly = true)]
    [Description("Get the feature permissions the system administrator has granted your domain (which settings and features the domain may use or override).")]
    public static async Task<string> DomainGetPermissions(UserContext userContext) =>
        await Run(async () => await userContext.GetAsync<JsonElement>($"{Root}/permissions"));

    // ================================================================ writes: domain aliases & catch-all

    [McpServerTool(Name = "domain_add_domain_alias")]
    [Description("Add a domain alias: mail to <anything>@aliasName will be delivered to the matching mailbox in your domain. Optionally require the alias's MX records to point here first.")]
    public static async Task<string> DomainAddDomainAlias(
        [Description("The domain alias name, e.g. 'example.net'")] string aliasName,
        [Description("Refuse unless the alias's MX records point at this server (default false)")] bool checkMx = false,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (string.IsNullOrWhiteSpace(aliasName)) return Invalid("aliasName is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/domain-alias-put/{Uri.EscapeDataString(aliasName.Trim())}/{Lower(checkMx)}"));
    }

    [McpServerTool(Name = "domain_rename_domain_alias")]
    [Description("Rename a domain alias (e.g. example.net → example.org). Optionally require the new name's MX records to point here first.")]
    public static async Task<string> DomainRenameDomainAlias(
        [Description("Current domain alias name")] string oldName,
        [Description("New domain alias name")] string newName,
        [Description("Refuse unless the new name's MX records point at this server (default false)")] bool verifyMx = false,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
            return Invalid("oldName and newName are required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/domain-alias/{Uri.EscapeDataString(oldName.Trim())}/{Uri.EscapeDataString(newName.Trim())}/{Lower(verifyMx)}"));
    }

    [McpServerTool(Name = "domain_delete_domain_aliases", Destructive = true)]
    [Description("Delete one or more domain aliases. Mail to those domain names will no longer be delivered to this domain.")]
    public static async Task<string> DomainDeleteDomainAliases(
        [Description("Domain alias names to delete, e.g. ['example.net']")] string[] aliasNames,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        var names = (aliasNames ?? []).Select(n => n?.Trim()).Where(n => !string.IsNullOrEmpty(n)).ToArray();
        if (names.Length == 0) return Invalid("Give at least one domain alias name.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/domain-aliases-delete", new { input = names }));
    }

    [McpServerTool(Name = "domain_set_catch_all")]
    [Description("Set the catch-all: the alias that receives mail sent to any address in your domain that does not exist. Pass an empty value to turn the catch-all off.")]
    public static async Task<string> DomainSetCatchAll(
        [Description("Existing alias name (local part) to use as the catch-all, or empty to disable")] string aliasName,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        var local = ToLocalPart(aliasName ?? string.Empty);
        var path = local.Length == 0
            ? $"{Root}/set-catch-all"
            : $"{Root}/set-catch-all/{Uri.EscapeDataString(local)}";
        return await Run(async () => await userContext.PostAsync<JsonElement>(path));
    }

    // ================================================================ writes: signatures

    [McpServerTool(Name = "domain_create_signature")]
    [Description("Create a domain signature template. The text may be HTML and may contain SmarterMail signature variables. Assign it with domain_set_signature_mapping.")]
    public static async Task<string> DomainCreateSignature(
        [Description("Signature name")] string name,
        [Description("Signature body (HTML allowed)")] string text,
        [Description("Make this the domain's default signature (default false)")] bool isDefault = false,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (string.IsNullOrWhiteSpace(name)) return Invalid("name is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>($"{Root}/signature-put", new
        {
            signatureConfig = new { id = 0, guid = string.Empty, name = name.Trim(), text = text ?? string.Empty, isDefault },
        }));
    }

    [McpServerTool(Name = "domain_update_signature")]
    [Description("Change a domain signature template's name, text or default flag. Only the fields you pass change.")]
    public static async Task<string> DomainUpdateSignature(
        [Description("Signature id (from domain_list_signatures)")] int id,
        [Description("New name (optional)")] string? name = null,
        [Description("New body, HTML allowed (optional)")] string? text = null,
        [Description("Set or clear the default flag (optional)")] bool? isDefault = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        return await Run(async () =>
        {
            var list = await userContext.GetAsync<JsonElement>($"{Root}/signatures");
            JsonObject? current = null;
            if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("signatureConfigs", out var configs) &&
                configs.ValueKind == JsonValueKind.Array)
            {
                foreach (var config in configs.EnumerateArray())
                {
                    if (Num(config, "id") == id)
                        current = JsonNode.Parse(config.GetRawText()) as JsonObject;
                }
            }

            if (current is null)
                return Fail($"No domain signature with id {id}. Use domain_list_signatures.");

            if (name is not null) current["name"] = name.Trim();
            if (text is not null) current["text"] = text;
            if (isDefault is not null) current["isDefault"] = isDefault.Value;

            return await userContext.PostAsync<JsonElement>($"{Root}/signature", new JsonObject { ["signatureConfig"] = current });
        });
    }

    [McpServerTool(Name = "domain_delete_signature", Destructive = true)]
    [Description("Permanently delete a domain signature template. Anyone mapped to it stops getting it on outgoing mail.")]
    public static async Task<string> DomainDeleteSignature(
        [Description("Signature id (from domain_list_signatures)")] int id,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        return await Run(async () => await userContext.PostAsync<JsonElement>($"{Root}/signature-delete/{id}"));
    }

    [McpServerTool(Name = "domain_set_signature_mapping")]
    [Description("Assign a signature to a user, alias, domain alias or the whole domain, or remove such an assignment. Other mappings are kept.")]
    public static async Task<string> DomainSetSignatureMapping(
        [Description("What the mapping is for: 'user', 'alias', 'domain_alias', 'domain' or 'smtp_account'")] string targetType,
        [Description("The target's name: a username/local part for user or alias, a domain name for domain_alias or domain")] string target,
        [Description("'specified' (use signatureGuid), 'domain_default', 'primary', 'none', or 'remove' to delete this mapping")] string option,
        [Description("Signature guid from domain_list_signatures (required when option is 'specified')")] string? signatureGuid = null,
        [Description("Let the user override this signature (default true)")] bool allowUsersToOverride = true,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        var type = (targetType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "domain_alias" or "domainalias" => 1,
            "domain" => 2,
            "alias" => 3,
            "user" => 4,
            "smtp_account" or "smtpaccount" => 5,
            _ => -1,
        };
        if (type < 0) return Invalid("targetType must be user, alias, domain_alias, domain or smtp_account.");

        var opt = (option ?? string.Empty).Trim().ToLowerInvariant();
        int? mapOption = opt switch
        {
            "none" => 0,
            "primary" or "use_primary" => 1,
            "specified" or "signature_specified" => 2,
            "domain_default" or "use_domain_default" => 3,
            "remove" => null,
            _ => -1,
        };
        if (mapOption == -1) return Invalid("option must be specified, domain_default, primary, none or remove.");
        if (mapOption == 2 && string.IsNullOrWhiteSpace(signatureGuid))
            return Invalid("signatureGuid is required when option is 'specified'.");

        var key = type is 3 or 4 ? ToLocalPart(target ?? string.Empty) : (target ?? string.Empty).Trim();
        if (key.Length == 0 && type != 2) return Invalid("target is required.");

        return await Run(async () =>
        {
            var current = await userContext.GetAsync<JsonElement>($"{Root}/signature-mappings");
            var maps = current.ValueKind == JsonValueKind.Object && current.TryGetProperty("maps", out var m) &&
                       m.ValueKind == JsonValueKind.Array
                ? JsonNode.Parse(m.GetRawText()) as JsonArray ?? []
                : [];

            var toAdd = new JsonArray();
            var toRemove = new JsonArray();
            if (mapOption is null)
            {
                toRemove.Add(key);
            }
            else
            {
                toAdd.Add(new JsonObject
                {
                    ["allowUsersToOverride"] = allowUsersToOverride,
                    ["key"] = key,
                    ["mapOption"] = mapOption.Value,
                    ["signatureGuid"] = mapOption == 2 ? signatureGuid!.Trim() : string.Empty,
                    ["type"] = type,
                });
            }

            return await userContext.PostAsync<JsonElement>($"{Root}/signature-mappings", new JsonObject
            {
                ["signatureMaps"] = maps,
                ["toAdd"] = toAdd,
                ["toRemove"] = toRemove,
            });
        });
    }

    // ================================================================ writes: shared resources

    [McpServerTool(Name = "domain_create_shared_resource")]
    [Description("Create a domain-level shared resource: a shared calendar, conference room, piece of equipment, address book, task list or note list. Rooms and equipment can be booked by inviting their email alias.")]
    public static async Task<string> DomainCreateSharedResource(
        [Description("Display name")] string name,
        [Description("'calendar', 'conference_room', 'equipment', 'address_book', 'tasks' or 'notes'")] string resourceType,
        [Description("Email alias (local part) for booking rooms/equipment (optional)")] string? emailAlias = null,
        [Description("Usernames to give access, e.g. ['jane','bob'] (optional)")] string[]? users = null,
        [Description("Access level for those users: 'availability', 'read', 'manage' or 'owner' (default 'read')")] string userAccess = "read",
        [Description("How far ahead rooms/equipment can be booked: 'three_months', 'six_months', 'one_year', 'two_years' or 'no_limit' (default 'no_limit')")] string bookingWindow = "no_limit",
        [Description("Create it enabled (default true)")] bool enabled = true,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (string.IsNullOrWhiteSpace(name)) return Invalid("name is required.");

        // ShareType (folder kind) and SharedResourceType (what it represents) both come from the one choice.
        (int FolderType, int SharedType)? kind = (resourceType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "calendar" => (2, 1),
            "conference_room" or "room" => (2, 2),
            "equipment" => (2, 3),
            "address_book" or "contacts" => (1, 4),
            "tasks" => (4, 5),
            "notes" => (5, 6),
            _ => null,
        };
        if (kind is null) return Invalid("resourceType must be calendar, conference_room, equipment, address_book, tasks or notes.");

        var access = ShareAccess(userAccess);
        if (access is null) return Invalid("userAccess must be availability, read, manage or owner.");
        var window = BookingWindow(bookingWindow);
        if (window is null) return Invalid("bookingWindow must be three_months, six_months, one_year, two_years or no_limit.");

        return await Run(async () => await userContext.PostAsync<JsonElement>($"{Root}/shared-resources/create", new
        {
            folderName = name.Trim(),
            folderType = kind.Value.FolderType,
            sharedResourceType = kind.Value.SharedType,
            isEnabled = enabled,
            emailAlias = string.IsNullOrWhiteSpace(emailAlias) ? string.Empty : ToLocalPart(emailAlias),
            accessUsers = (users ?? []).Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => new { userId = 0, username = ToLocalPart(u), access = access.Value }).ToArray(),
            accessGroups = Array.Empty<object>(),
            maximumBookingWindow = window.Value,
        }));
    }

    [McpServerTool(Name = "domain_update_shared_resource")]
    [Description("Rename a domain shared resource and/or change who can access it. For access, the users you list get the given level; pass removeUsers to take access away. Group access and anyone not mentioned are kept.")]
    public static async Task<string> DomainUpdateSharedResource(
        [Description("folderId from domain_list_shared_resources")] int folderId,
        [Description("New display name (optional)")] string? newName = null,
        [Description("Usernames to grant or change access for (optional)")] string[]? users = null,
        [Description("Access level for those users: 'availability', 'read', 'manage' or 'owner' (default 'read')")] string userAccess = "read",
        [Description("Usernames whose access to remove (optional)")] string[]? removeUsers = null,
        [Description("New booking window: 'three_months', 'six_months', 'one_year', 'two_years' or 'no_limit' (optional)")] string? bookingWindow = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        var access = ShareAccess(userAccess);
        if (access is null) return Invalid("userAccess must be availability, read, manage or owner.");
        int? window = null;
        if (!string.IsNullOrWhiteSpace(bookingWindow))
        {
            window = BookingWindow(bookingWindow);
            if (window is null) return Invalid("bookingWindow must be three_months, six_months, one_year, two_years or no_limit.");
        }

        var grant = (users ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).Select(ToLocalPart).ToList();
        var revoke = (removeUsers ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).Select(ToLocalPart)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changeShares = grant.Count > 0 || revoke.Count > 0 || window is not null;

        if (string.IsNullOrWhiteSpace(newName) && !changeShares)
            return Invalid("Nothing to change: pass newName, users, removeUsers or bookingWindow.");

        return await Run(async () =>
        {
            var results = new JsonObject { ["success"] = true };

            if (!string.IsNullOrWhiteSpace(newName))
            {
                var renamed = await userContext.PostAsync<JsonElement>($"{Root}/shared-resources/rename",
                    new { folderId, folderName = newName.Trim() });
                results["rename"] = JsonNode.Parse(renamed.GetRawText());
                if (Bool(renamed, "success") == false)
                    return JsonSerializer.SerializeToElement(renamed);
            }

            if (changeShares)
            {
                var list = await userContext.GetAsync<JsonElement>($"{Root}/shared-resources/list");
                JsonElement? folder = null;
                if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("virtualFolders", out var folders) &&
                    folders.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in folders.EnumerateArray())
                        if (Num(f, "folderId") == folderId) folder = f;
                }

                if (folder is null)
                    return Fail($"No domain shared resource with folderId {folderId}. Use domain_list_shared_resources.");

                // update-shares replaces the whole permission set, so start from the current one.
                var accessUsers = new JsonArray();
                if (folder.Value.TryGetProperty("accessUsers", out var existingUsers) && existingUsers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var u in existingUsers.EnumerateArray())
                    {
                        var username = Str(u, "username") ?? string.Empty;
                        if (revoke.Contains(username) || grant.Contains(username, StringComparer.OrdinalIgnoreCase))
                            continue;
                        accessUsers.Add(JsonNode.Parse(u.GetRawText()));
                    }
                }
                foreach (var username in grant)
                    accessUsers.Add(new JsonObject { ["userId"] = 0, ["username"] = username, ["access"] = access.Value });

                var accessGroups = folder.Value.TryGetProperty("accessGroups", out var existingGroups) &&
                                   existingGroups.ValueKind == JsonValueKind.Array
                    ? JsonNode.Parse(existingGroups.GetRawText())
                    : new JsonArray();

                var shares = await userContext.PostAsync<JsonElement>($"{Root}/shared-resources/update-shares", new JsonObject
                {
                    ["ownerUsername"] = Str(folder.Value, "ownerUserName") ?? string.Empty,
                    ["folderId"] = folderId,
                    ["accessUsers"] = accessUsers,
                    ["accessGroups"] = accessGroups,
                    ["maximumBookingWindow"] = window ?? (int?)Num(folder.Value, "maximumBookingWindow") ?? 4,
                });
                results["shares"] = JsonNode.Parse(shares.GetRawText());
                if (Bool(shares, "success") == false)
                    results["success"] = false;
            }

            return JsonSerializer.SerializeToElement(results);
        });
    }

    [McpServerTool(Name = "domain_delete_shared_resource", Destructive = true)]
    [Description("Permanently delete a domain shared resource and everything in it (events, contacts, tasks or notes). Cannot be undone.")]
    public static async Task<string> DomainDeleteSharedResource(
        [Description("folderId from domain_list_shared_resources")] int folderId,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/shared-resources/delete", new { id = folderId }));
    }

    // ================================================================ writes: event hooks

    [McpServerTool(Name = "domain_save_event_hook")]
    [Description("Create an event hook, or replace an existing one, from a full event-hook JSON object " +
                 "(name, enabled, eventID/eventGroup, conditions[], actions[] with inputs[]; the shape domain_list_event_hooks returns). " +
                 "Get valid events, conditions and actions from domain_get_event_catalog. To edit a hook, read it, change it, and pass it back with its id and isNew:false.")]
    public static async Task<string> DomainSaveEventHook(
        [Description("The event hook as a JSON object string")] string eventHookJson,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (!TryParseObject(eventHookJson, out var hook, out var error)) return Invalid(error);

        if (hook["id"] is null || string.IsNullOrEmpty(hook["id"]?.ToString()))
            hook["isNew"] = true;

        return await Run(async () => await userContext.PostAsync<JsonElement>($"{Root}/event-hook", hook));
    }

    [McpServerTool(Name = "domain_set_event_hook_enabled")]
    [Description("Turn an existing event hook on or off without changing anything else about it.")]
    public static async Task<string> DomainSetEventHookEnabled(
        [Description("Event hook id (from domain_list_event_hooks)")] string id,
        [Description("true to enable, false to disable")] bool enabled,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (string.IsNullOrWhiteSpace(id)) return Invalid("id is required.");

        return await Run(async () =>
        {
            var current = await userContext.GetAsync<JsonElement>($"{Root}/event-hook/{Uri.EscapeDataString(id.Trim())}");
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty("eventHook", out var hookElement) ||
                hookElement.ValueKind != JsonValueKind.Object)
                return Fail($"No event hook with id '{id}'. Use domain_list_event_hooks.");

            var hook = (JsonObject)JsonNode.Parse(hookElement.GetRawText())!;
            hook["enabled"] = enabled;
            hook["isNew"] = false;
            return await userContext.PostAsync<JsonElement>($"{Root}/event-hook", hook);
        });
    }

    [McpServerTool(Name = "domain_delete_event_hooks", Destructive = true)]
    [Description("Delete one or more event hooks. They stop firing immediately.")]
    public static async Task<string> DomainDeleteEventHooks(
        [Description("Event hook ids to delete")] string[] ids,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        var list = (ids ?? []).Select(i => i?.Trim()).Where(i => !string.IsNullOrEmpty(i)).ToArray();
        if (list.Length == 0) return Invalid("Give at least one event hook id.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/event-hook-delete", new { input = list }));
    }

    // ================================================================ writes: domain settings

    [McpServerTool(Name = "domain_update_settings")]
    [Description("Change domain settings. Pass a JSON object of only the settings to change, using the property names domain_get_settings returns " +
                 "(e.g. {\"enableMailForwarding\":false} or {\"externalSenderOverrideSettings\":{\"enabled\":true}}). " +
                 "Nested objects are merged, arrays and plain values replace. Everything you do not mention is kept. Unknown names are refused.")]
    public static async Task<string> DomainUpdateSettings(
        [Description("JSON object string of the settings to change")] string settingsJson,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (!TryParseObject(settingsJson, out var changes, out var error)) return Invalid(error);
        if (changes.Count == 0) return Invalid("settingsJson has no settings to change.");

        // Accept either the bare settings or the { domainSettings: { … } } wrapper.
        if (changes.Count == 1 && changes["domainSettings"] is JsonObject wrapped)
            changes = (JsonObject)wrapped.DeepClone();

        return await Run(async () =>
        {
            var current = await userContext.GetAsync<JsonElement>($"{Root}/domain");
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty("domainSettings", out var settingsElement) ||
                settingsElement.ValueKind != JsonValueKind.Object)
                return Fail("Could not read the current domain settings.");

            var settings = (JsonObject)JsonNode.Parse(settingsElement.GetRawText())!;
            var unknown = changes.Select(p => p.Key).Where(k => !settings.ContainsKey(k)).ToList();
            if (unknown.Count > 0)
                return Fail($"Unknown domain setting(s): {string.Join(", ", unknown)}. Use the names domain_get_settings returns.");

            Merge(settings, changes);

            // The full object goes back, so nothing the caller did not mention can be blanked.
            return await userContext.PostAsync<JsonElement>($"{Root}/domain", new JsonObject { ["domainSettings"] = settings });
        });
    }

    // ================================================================ helpers

    private static async Task<string> Run(Func<Task<JsonElement>> call)
    {
        try
        {
            return JsonSerializer.Serialize(Redact(await call()));
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    private static string ReadOnly() => JsonSerializer.Serialize(new
    {
        success = false,
        error = "This account is signed in read-only; domain changes are not allowed.",
    });

    private static string Invalid(string message) =>
        JsonSerializer.Serialize(new { success = false, error = message });

    private static JsonElement Fail(string message) =>
        JsonSerializer.SerializeToElement(new { success = false, error = message });

    private static readonly string[] SecretKeyFragments =
        ["password", "secret", "apikey", "privatekey", "token", "credential"];

    private static bool IsSecretKey(string key) =>
        SecretKeyFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Blanks non-empty strings under password/secret/token-like keys, and the <c>value</c> of
    /// <c>{ key, value }</c> pairs (event-hook inputs) whose key looks like a secret.
    /// </summary>
    private static JsonElement Redact(JsonElement element)
    {
        var node = JsonSerializer.SerializeToNode(element);
        RedactNode(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var pairKey = obj["key"] is JsonValue k && k.GetValueKind() == JsonValueKind.String ? k.GetValue<string>() : null;
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var value = obj[key];
                    var secret = IsSecretKey(key) ||
                                 (key.Equals("value", StringComparison.OrdinalIgnoreCase) && pairKey is not null && IsSecretKey(pairKey));
                    if (secret && value is JsonValue v && v.GetValueKind() == JsonValueKind.String &&
                        !string.IsNullOrEmpty(v.GetValue<string>()))
                        obj[key] = "[redacted]";
                    else
                        RedactNode(value);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    RedactNode(item);
                break;
        }
    }

    /// <summary>Objects merge key by key; anything else (arrays, values, null) replaces.</summary>
    private static void Merge(JsonObject target, JsonObject changes)
    {
        foreach (var (key, value) in changes.ToList())
        {
            if (value is JsonObject changeObject && target[key] is JsonObject targetObject)
                Merge(targetObject, changeObject);
            else
                target[key] = value?.DeepClone();
        }
    }

    private static bool TryParseObject(string? json, out JsonObject result, out string error)
    {
        result = new JsonObject();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "A JSON object is required.";
            return false;
        }

        try
        {
            if (JsonNode.Parse(json) is JsonObject obj)
            {
                result = obj;
                return true;
            }

            error = "Expected a JSON object.";
            return false;
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }
    }

    private static int? ShareAccess(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "none" => 0,
        "availability" => 1,
        "read" or "" => 2,
        "manage" => 3,
        "owner" => 4,
        _ => null,
    };

    private static int? BookingWindow(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "three_months" => 0,
        "six_months" => 1,
        "one_year" => 2,
        "two_years" => 3,
        "no_limit" or "" => 4,
        _ => null,
    };

    private static string Lower(bool value) => value ? "true" : "false";

    /// <summary>"jane@example.com" → "jane"; "jane" → "jane".</summary>
    private static string ToLocalPart(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        var at = trimmed.IndexOf('@');
        return at >= 0 ? trimmed[..at] : trimmed;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static double? Num(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;
}
