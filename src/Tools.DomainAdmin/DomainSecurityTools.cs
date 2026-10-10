using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Server.Tools;

/// <summary>
/// Domain-administrator security tools: DKIM signing, spam filtering, the domain whitelist, login
/// restrictions and domain-level content filters. They need the signed-in account to be a domain
/// admin; for a plain user every call comes back 403 from SmarterMail. Every endpoint is under
/// <c>/api/v1/settings/domain/*</c> and acts on the signed-in domain admin's own domain, so no tool
/// takes a <c>domain</c> parameter.
///
/// Paths and shapes come from the SmarterMail API reference
/// (https://mail.smartertools.com/Documentation/api, "Domain Settings" controller).
///
/// Every result is passed through <see cref="Redact"/>: tool output goes on to an LLM provider, so
/// strings under password/secret/private-key-like names are blanked. DKIM private keys are never
/// returned by these endpoints, and would be redacted if they were.
/// </summary>
[McpServerToolType]
public sealed class DomainSecurityTools
{
    private const string Root = "/api/v1/settings/domain";

    // SmarterMail enums, indexed by their numeric value.
    private static readonly string[] FilterActionTypes =
    [
        "NoAction", "Delete", "Reroute", "Bounce", "MoveToFolder", "AddHeader", "PrefixSubject",
        "Forward", "Read", "SetPriority", "FollowUp",
    ];

    private static readonly string[] ConditionFieldTypes =
    [
        "FromAddress", "FromDomain", "FromTrustedSender", "WordsInSubject", "WordsInBody",
        "WordsInSubjectOrBody", "WordsInFrom", "WordsInTo", "WordsInHeaders", "WordsInMessage",
        "ToAddress", "ToDomain", "ToMe", "MyNameInTo", "MyNameNotInTo", "MyNameInToOrCC", "AttHas",
        "AttFilename", "AttExtension", "AttSize", "PriorityHigh", "PriorityNormal", "PriorityLow",
        "AutomatedMessage", "IsAuthenticated", "MessageSizeOver", "MessageSizeUnder", "DateRange",
        "SendingServerIP", "SpamWeight", "CcAddress", "CcDomain", "ToOrCcAddress", "ToOrCcDomain",
    ];

    private static readonly string[] ComparisonTypes = ["Equals", "DoesntEqual", "Contains", "DoesntContain"];

    private static readonly string[] MatchTypes = ["And", "Or"];

    private const string ActionNames =
        "NoAction, Delete, Bounce, MoveToFolder (argument = folder name), AddHeader (argument = header line), " +
        "PrefixSubject (argument = prefix text), Forward (argument = address), Read, SetPriority, FollowUp";

    // ---------------------------------------------------------------- DKIM

    [McpServerTool(Name = "domain_get_dkim", ReadOnly = true)]
    [Description("Get your domain's DKIM signing configuration: whether signing is active, the selector and public key to publish in DNS, key size, canonicalization, signed header fields, and any pending rollover key with its DNS status. Private keys are never returned.")]
    public static async Task<string> DomainGetDkim(UserContext userContext) =>
        await Run(async () =>
        {
            var settings = await GetDomainSettingsAsync(userContext);
            return new JsonObject
            {
                ["success"] = true,
                ["domainAdminCanManageDkim"] = settings?["enableDkimSigningDomainAdmin"]?.DeepClone(),
                ["dkim"] = settings?["domainKeysSettings"]?.DeepClone(),
            };
        });

    [McpServerTool(Name = "domain_test_dkim_dns", ReadOnly = true)]
    [Description("Check whether a DKIM public key is correctly published in DNS. Only performs a DNS lookup; changes nothing. By default tests the active key; set which='pending' to test the rollover key, or pass selector and publicKey to test any key.")]
    public static async Task<string> DomainTestDkimDns(
        [Description("Which of the domain's keys to test: 'active' (default) or 'pending'. Ignored when selector and publicKey are given.")] string which = "active",
        [Description("DKIM selector to test (optional; must be given together with publicKey)")] string? selector = null,
        [Description("Public key to look for in DNS (optional; must be given together with selector)")] string? publicKey = null,
        UserContext userContext = null!)
    {
        return await Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(selector) != string.IsNullOrWhiteSpace(publicKey))
                return Fail("Give both selector and publicKey, or neither.");

            if (string.IsNullOrWhiteSpace(selector))
            {
                var dkim = (await GetDomainSettingsAsync(userContext))?["domainKeysSettings"];
                var source = string.Equals(which, "pending", StringComparison.OrdinalIgnoreCase)
                    ? dkim?["pending"]
                    : dkim;
                selector = Str(source?["selector"]);
                publicKey = Str(source?["publicKey"]);
                if (string.IsNullOrWhiteSpace(selector) || string.IsNullOrWhiteSpace(publicKey))
                    return Fail($"The domain has no {(source == dkim ? "active" : "pending")} DKIM key to test.");
            }

            var response = await userContext.GetAsync<JsonElement>(
                $"{Root}/test-domain-key-dns/{Uri.EscapeDataString(selector!)}/{Uri.EscapeDataString(publicKey!.Trim())}");
            return new JsonObject
            {
                ["selector"] = selector,
                ["result"] = JsonSerializer.SerializeToNode(response),
            };
        });
    }

    [McpServerTool(Name = "domain_enable_dkim")]
    [Description("Turn on DKIM signing for your domain. If no DKIM key exists yet, a pending key is created; publish its selector and public key in DNS (see domain_get_dkim), then run domain_verify_dkim. Without forceActivation an active key that fails DNS validation is moved back to pending.")]
    public static async Task<string> DomainEnableDkim(
        [Description("Force the key active even if DNS validation fails (default false; not recommended — receivers will fail DKIM checks until DNS is right)")] bool forceActivation = false,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        return await Run(async () => Node(await userContext.PostAsync<JsonElement>(
            $"{Root}/dkim-enable/{Bool(forceActivation)}")));
    }

    [McpServerTool(Name = "domain_disable_dkim", Destructive = true)]
    [Description("Turn off DKIM signing for your domain. Any pending DKIM key is deleted. Outgoing mail stops being signed immediately, which can hurt deliverability where receivers expect DKIM.")]
    public static async Task<string> DomainDisableDkim(UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        return await Run(async () => Node(await userContext.PostAsync<JsonElement>($"{Root}/dkim-disable")));
    }

    [McpServerTool(Name = "domain_update_dkim_settings")]
    [Description("Change how your domain signs mail with DKIM (does not change the key). Only the options you pass change; the rest keep their current values.")]
    public static async Task<string> DomainUpdateDkimSettings(
        [Description("Body canonicalization: 'simple' or 'relaxed' (optional)")] string? canonicalizationBody = null,
        [Description("Header canonicalization: 'simple' or 'relaxed' (optional)")] string? canonicalizationHeader = null,
        [Description("How 'fields' is used: 'include' (sign only these headers) or 'exclude' (sign all but these) (optional)")] string? fieldOption = null,
        [Description("Header field names to include/exclude from signing, e.g. ['From','To','Subject','Date'] (optional; replaces the current list)")] string[]? fields = null,
        [Description("Messages larger than this many bytes are not signed (optional)")] int? maxMessageSign = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        return await Run(async () =>
        {
            var current = (await GetDomainSettingsAsync(userContext))?["domainKeysSettings"];
            var payload = new JsonObject
            {
                ["canonicalizationBody"] = canonicalizationBody ?? Str(current?["dkimCanonicalizationAlgorithmBody"]),
                ["canonicalizationHeader"] = canonicalizationHeader ?? Str(current?["dkimCanonicalizationAlgorithmHeader"]),
                ["fieldOption"] = fieldOption ?? Str(current?["dkimHeaderFieldOption"]),
                ["fields"] = fields is not null
                    ? new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray())
                    : current?["dkimHeaderFields"]?.DeepClone() ?? new JsonArray(),
                ["maxMessageSign"] = maxMessageSign is { } max
                    ? JsonValue.Create(max)
                    : current?["maxMessageSign"]?.DeepClone(),
            };

            return Node(await userContext.PostAsync<JsonElement>($"{Root}/dkim-settings-set", payload));
        });
    }

    [McpServerTool(Name = "domain_create_dkim_rollover")]
    [Description("Start a DKIM key rotation: generates a new key pair that stays pending (the current key keeps signing) until its DNS record is published and verified with domain_verify_dkim(rollover=true). Returns the new selector and public key to publish.")]
    public static async Task<string> DomainCreateDkimRollover(
        [Description("Key size in bits: 1024, 2048 (default) or 4096")] int keySize = 2048,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (keySize is not (1024 or 2048 or 4096)) return Serialize(Fail("keySize must be 1024, 2048 or 4096."));
        return await Run(async () => Node(await userContext.PostAsync<JsonElement>(
            $"{Root}/dkim-create-rollover/{keySize}")));
    }

    [McpServerTool(Name = "domain_delete_dkim_rollover", Destructive = true)]
    [Description("Cancel a pending DKIM key rotation: deletes the pending rollover key without activating it. The active key is unaffected.")]
    public static async Task<string> DomainDeleteDkimRollover(UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        return await Run(async () => Node(await userContext.PostAsync<JsonElement>($"{Root}/dkim-delete-rollover")));
    }

    [McpServerTool(Name = "domain_verify_dkim")]
    [Description("Have SmarterMail validate a DKIM key against DNS and update its status. rollover=false checks the active key; rollover=true checks the pending rollover key and, if DNS matches, makes it the active signing key. Can change which key signs mail. For a lookup that changes nothing, use domain_test_dkim_dns.")]
    public static async Task<string> DomainVerifyDkim(
        [Description("false (default): verify the active key. true: verify the pending rollover key and activate it on success.")] bool rollover = false,
        [Description("Rollover only: activate the pending key even if DNS validation fails (default false)")] bool forceActivation = false,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        return await Run(async () => Node(await userContext.PostAsync<JsonElement>(rollover
            ? $"{Root}/dkim-verify-rollover/{Bool(forceActivation)}"
            : $"{Root}/dkim-verify-active")));
    }

    // ---------------------------------------------------------------- spam

    [McpServerTool(Name = "domain_get_spam_settings", ReadOnly = true)]
    [Description("Get your domain's spam filtering: whether the domain overrides the server defaults, the low/medium/high spam weight thresholds and the action taken at each level, the server defaults, and the relay/forward spam options.")]
    public static async Task<string> DomainGetSpamSettings(UserContext userContext) =>
        await Run(async () =>
        {
            var spam = Node(await userContext.GetAsync<JsonElement>($"{Root}/spam-settings"));
            var settings = await GetDomainSettingsAsync(userContext);
            spam["relayAndForward"] = Pick(settings,
                "spamRelayOption", "spamRelayOverrideActive", "spamForwardOption", "spamForwardOverrideActive",
                "allowSpamActionOverride", "bypassGreyListing");
            return spam;
        });

    [McpServerTool(Name = "domain_list_spam_checks", ReadOnly = true)]
    [Description("List the DNS blocklists (RBL / URIBL) used for spam checking: name, lookup domain, whether each is enabled for incoming and outgoing SMTP, and how results are weighted.")]
    public static async Task<string> DomainListSpamChecks(UserContext userContext) =>
        await Run(async () => Node(await userContext.GetAsync<JsonElement>($"{Root}/ip4r-lookup")));

    [McpServerTool(Name = "domain_update_spam_settings")]
    [Description("Change your domain's spam thresholds and actions. Only the values you pass change. Changing weights or actions turns on the domain override of the server defaults unless overrideActive=false is passed. The server administrator may not allow domains to override spam actions. Actions: " + ActionNames + ".")]
    public static async Task<string> DomainUpdateSpamSettings(
        [Description("true: use this domain's own spam settings; false: fall back to the server defaults (optional)")] bool? overrideActive = null,
        [Description("Spam weight at which a message counts as low-probability spam (optional)")] int? lowWeight = null,
        [Description("Action for low-probability spam (optional), e.g. 'PrefixSubject'")] string? lowAction = null,
        [Description("Argument for lowAction, e.g. the subject prefix or folder name (optional)")] string? lowActionArgument = null,
        [Description("Spam weight for medium-probability spam (optional)")] int? mediumWeight = null,
        [Description("Action for medium-probability spam (optional), e.g. 'MoveToFolder'")] string? mediumAction = null,
        [Description("Argument for mediumAction (optional), e.g. 'Junk E-Mail'")] string? mediumActionArgument = null,
        [Description("Spam weight for high-probability spam (optional)")] int? highWeight = null,
        [Description("Action for high-probability spam (optional), e.g. 'Delete'")] string? highAction = null,
        [Description("Argument for highAction (optional)")] string? highActionArgument = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        return await Run(async () =>
        {
            var current = await GetDomainSettingsAsync(userContext);
            var changes = new JsonObject();

            string? error = null;
            void Level(string name, int? weight, string? action, string? argument)
            {
                if (weight is { } w)
                    changes[$"spamLevel{name}Weight"] = w;

                if (action is null && argument is null)
                    return;

                var merged = current?[$"spamLevel{name}Action"]?.DeepClone() as JsonObject ?? new JsonObject();
                if (action is not null)
                {
                    var type = EnumValue(action, FilterActionTypes);
                    if (type is null)
                    {
                        error = $"Unknown action '{action}'. Use one of: {ActionNames}.";
                        return;
                    }
                    merged["actionType"] = type.Value;
                }
                if (argument is not null)
                    merged["argument"] = argument;
                changes[$"spamLevel{name}Action"] = merged;
            }

            Level("Low", lowWeight, lowAction, lowActionArgument);
            Level("Med", mediumWeight, mediumAction, mediumActionArgument);
            Level("High", highWeight, highAction, highActionArgument);
            if (error is not null)
                return Fail(error);

            if (overrideActive is { } active)
                changes["spamCheckOverrideActive"] = active;
            else if (changes.Count > 0)
                changes["spamCheckOverrideActive"] = true;

            if (changes.Count == 0)
                return Fail("Nothing to change: pass at least one weight, action or overrideActive.");

            var response = await userContext.PostAsync<JsonElement>($"{Root}/domain",
                new JsonObject { ["domainSettings"] = changes.DeepClone() });
            var result = Node(response);
            result["changed"] = changes;
            return result;
        });
    }

    // ---------------------------------------------------------------- whitelist & login restrictions

    [McpServerTool(Name = "domain_get_security_settings", ReadOnly = true)]
    [Description("Get your domain's sender and login security: the domain whitelist (addresses and domains that bypass spam filtering), the forwarding blocklist, login country restrictions, the IP access list, SMTP authentication / SSL / HSTS / TLS / SRS requirements, sending throttles and the external-sender warning settings.")]
    public static async Task<string> DomainGetSecuritySettings(UserContext userContext) =>
        await Run(async () =>
        {
            var settings = await GetDomainSettingsAsync(userContext);
            var result = new JsonObject
            {
                ["success"] = true,
                ["whitelist"] = new JsonObject
                {
                    ["emails"] = Values(settings?["whitelistAddresses"]),
                    ["domains"] = Values(settings?["whitelistDomains"]),
                },
                ["loginRestrictions"] = Pick(settings,
                    "authBlockedCountrySettings", "systemBlockedCountries", "iPAccessList"),
                ["transport"] = Pick(settings,
                    "requireSmtpAuthentication", "sslRequired", "hstsEnabled", "enableTlsIfAvailable",
                    "srsEnabled", "bypassGreyListing"),
                ["throttleSettings"] = settings?["throttleSettings"]?.DeepClone(),
                ["externalSenderWarning"] = settings?["externalSenderOverrideSettings"]?.DeepClone(),
            };

            try
            {
                var blacklist = await userContext.GetAsync<JsonElement>($"{Root}/forward-blacklist");
                result["forwardBlocklist"] = blacklist.TryGetProperty("forwardBlacklists", out var list)
                    ? JsonSerializer.SerializeToNode(list)
                    : JsonSerializer.SerializeToNode(blacklist);
            }
            catch (SmarterMailApiException apiEx)
            {
                result["forwardBlocklist"] = $"unavailable: {apiEx.Message}";
            }

            return result;
        });

    [McpServerTool(Name = "domain_update_whitelist", Destructive = true)]
    [Description("Change the domain whitelist: senders (email addresses) and sending domains that bypass spam filtering for every user in your domain. mode 'add' (default) and 'remove' edit the current list; 'replace' sets it to exactly what you pass. Whitelisting a sender lets its spam through, so add only senders you trust.")]
    public static async Task<string> DomainUpdateWhitelist(
        [Description("Email addresses, e.g. ['alerts@vendor.com'] (optional)")] string[]? emails = null,
        [Description("Domains, e.g. ['vendor.com'] (optional)")] string[]? domains = null,
        [Description("'add' (default), 'remove', or 'replace'")] string mode = "add",
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        return await Run(async () =>
        {
            mode = (mode ?? "add").Trim().ToLowerInvariant();
            if (mode is not ("add" or "remove" or "replace"))
                return Fail("mode must be 'add', 'remove' or 'replace'.");
            if (mode != "replace" && (emails?.Length ?? 0) + (domains?.Length ?? 0) == 0)
                return Fail("Pass at least one email address or domain.");

            var settings = await GetDomainSettingsAsync(userContext);
            var currentEmails = StringList(Values(settings?["whitelistAddresses"]));
            var currentDomains = StringList(Values(settings?["whitelistDomains"]));

            List<string> Apply(List<string> current, string[]? given)
            {
                var items = (given ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
                return mode switch
                {
                    "replace" => items.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    "remove" => current.Where(c => !items.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList(),
                    _ => current.Concat(items).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                };
            }

            var newEmails = Apply(currentEmails, emails);
            var newDomains = Apply(currentDomains, domains);

            var result = Node(await userContext.PostAsync<JsonElement>($"{Root}/whitelist",
                new { emails = newEmails, domains = newDomains }));
            result["whitelist"] = new JsonObject
            {
                ["emails"] = JsonSerializer.SerializeToNode(newEmails),
                ["domains"] = JsonSerializer.SerializeToNode(newDomains),
            };
            return result;
        });
    }

    [McpServerTool(Name = "domain_get_user_login_ips", ReadOnly = true)]
    [Description("List the IP addresses a user in your domain has signed in from, with location and last-login time, grouped by protocol/category. Useful when investigating a compromised account.")]
    public static async Task<string> DomainGetUserLoginIps(
        [Description("The user: local part (e.g. 'jane') or full address")] string username,
        UserContext userContext = null!)
    {
        if (string.IsNullOrWhiteSpace(username)) return Serialize(Fail("username is required."));
        return await Run(async () => Node(await userContext.PostAsync<JsonElement>($"{Root}/authenticated-ips",
            new { emailAddress = ToEmail(username, userContext) })));
    }

    // ---------------------------------------------------------------- content filters

    [McpServerTool(Name = "domain_list_content_filters", ReadOnly = true)]
    [Description("List the domain-level content filters, which apply to every user in your domain: id, title, order, enabled state, match type, conditions and actions (enum values are numeric; see domain_add_content_filter for the names).")]
    public static async Task<string> DomainListContentFilters(UserContext userContext) =>
        await Run(async () => Node(await userContext.GetAsync<JsonElement>($"{Root}/content-filter-groups")));

    [McpServerTool(Name = "domain_add_content_filter")]
    [Description("Add a content filter that applies to every user in your domain. " +
                 "conditions: JSON array of {fieldType, filterType, searchArguments, applyLogicalNot?, isWildcard?}; " +
                 "fieldType is a name such as FromAddress, FromDomain, WordsInSubject, WordsInBody, WordsInSubjectOrBody, WordsInFrom, WordsInTo, WordsInHeaders, ToAddress, ToDomain, CcAddress, AttHas, AttFilename, AttExtension, MessageSizeOver, MessageSizeUnder, SendingServerIP, SpamWeight, IsAuthenticated, AutomatedMessage; " +
                 "filterType is Equals, DoesntEqual, Contains or DoesntContain; searchArguments is a list of strings. " +
                 "actions: JSON array of {actionType, argument?}; actionType is one of " + ActionNames + ". " +
                 "Example: conditions=[{\"fieldType\":\"FromDomain\",\"filterType\":\"Equals\",\"searchArguments\":[\"spammy.example\"]}], actions=[{\"actionType\":\"Delete\"}].")]
    public static async Task<string> DomainAddContentFilter(
        [Description("Filter name")] string title,
        [Description("JSON array of conditions (see tool description)")] string conditions,
        [Description("JSON array of actions (see tool description)")] string actions,
        [Description("'And' (all conditions must match, default) or 'Or' (any condition)")] string matchType = "And",
        [Description("Whether the filter is active (default true)")] bool enabled = true,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        return await Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(title))
                return Fail("title is required.");

            var match = EnumValue(matchType, MatchTypes);
            if (match is null) return Fail("matchType must be 'And' or 'Or'.");

            var filters = NormalizeConditions(conditions, out var conditionError);
            if (filters is null) return Fail(conditionError!);
            var actionList = NormalizeActions(actions, out var actionError);
            if (actionList is null) return Fail(actionError!);

            var group = new JsonObject
            {
                ["title"] = title.Trim(),
                ["matchType"] = match.Value,
                ["isEnabled"] = enabled,
                ["filters"] = filters,
                ["actions"] = actionList,
            };

            return Node(await userContext.PostAsync<JsonElement>($"{Root}/content-filter-groups",
                new JsonObject { ["toAdd"] = new JsonArray(group) }));
        });
    }

    [McpServerTool(Name = "domain_update_content_filter")]
    [Description("Change a domain-level content filter. Only what you pass changes; conditions and actions, when given, replace the filter's whole list (same format as domain_add_content_filter). Use domain_list_content_filters to find the id.")]
    public static async Task<string> DomainUpdateContentFilter(
        [Description("Filter id")] int id,
        [Description("New name (optional)")] string? title = null,
        [Description("Enable or disable the filter (optional)")] bool? enabled = null,
        [Description("'And' or 'Or' (optional)")] string? matchType = null,
        [Description("Replacement JSON array of conditions (optional)")] string? conditions = null,
        [Description("Replacement JSON array of actions (optional)")] string? actions = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        return await Run(async () =>
        {
            var list = Node(await userContext.GetAsync<JsonElement>($"{Root}/content-filter-groups"));
            var existing = (list["contentFilterGroups"] as JsonArray)?
                .OfType<JsonObject>()
                .FirstOrDefault(g => g["id"] is JsonValue v && v.TryGetValue<int>(out var gid) && gid == id);
            if (existing is null)
                return Fail($"No domain content filter with id {id}.");

            var updated = (JsonObject)existing.DeepClone();
            if (title is not null) updated["title"] = title.Trim();
            if (enabled is { } on) updated["isEnabled"] = on;
            if (matchType is not null)
            {
                var match = EnumValue(matchType, MatchTypes);
                if (match is null) return Fail("matchType must be 'And' or 'Or'.");
                updated["matchType"] = match.Value;
            }
            if (conditions is not null)
            {
                var filters = NormalizeConditions(conditions, out var conditionError);
                if (filters is null) return Fail(conditionError!);
                updated["filters"] = filters;
            }
            if (actions is not null)
            {
                var actionList = NormalizeActions(actions, out var actionError);
                if (actionList is null) return Fail(actionError!);
                updated["actions"] = actionList;
            }

            return Node(await userContext.PostAsync<JsonElement>($"{Root}/content-filter-groups",
                new JsonObject { ["contentFilterGroups"] = new JsonArray(updated) }));
        });
    }

    [McpServerTool(Name = "domain_delete_content_filters", Destructive = true)]
    [Description("Permanently delete domain-level content filters by id. They stop applying to every user in your domain.")]
    public static async Task<string> DomainDeleteContentFilters(
        [Description("Filter ids to delete")] int[] ids,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (ids is not { Length: > 0 }) return Serialize(Fail("Pass at least one filter id."));
        return await Run(async () => Node(await userContext.PostAsync<JsonElement>($"{Root}/content-filter-groups",
            new JsonObject { ["toRemove"] = JsonSerializer.SerializeToNode(ids) })));
    }

    [McpServerTool(Name = "domain_move_content_filter")]
    [Description("Move a domain-level content filter one place up (evaluated earlier) or down in the processing order.")]
    public static async Task<string> DomainMoveContentFilter(
        [Description("Filter id")] int id,
        [Description("true: move up (higher priority); false: move down")] bool up,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        return await Run(async () => Node(await userContext.PostAsync<JsonElement>(
            $"{Root}/content-filter-groups/order/{id}/{Bool(up)}")));
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<JsonNode?> GetDomainSettingsAsync(UserContext userContext)
    {
        var response = await userContext.GetAsync<JsonElement>($"{Root}/domain");
        return response.ValueKind == JsonValueKind.Object && response.TryGetProperty("domainSettings", out var settings)
            ? JsonSerializer.SerializeToNode(settings)
            : null;
    }

    private static async Task<string> Run(Func<Task<JsonNode>> call)
    {
        try
        {
            var node = await call();
            RedactNode(node);
            return node.ToJsonString();
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

    private static JsonObject Node(JsonElement element) =>
        JsonSerializer.SerializeToNode(element) as JsonObject ?? new JsonObject { ["result"] = JsonSerializer.SerializeToNode(element) };

    private static JsonObject Fail(string message) => new() { ["success"] = false, ["error"] = message };

    private static string Serialize(JsonObject node) => node.ToJsonString();

    private static string ReadOnly() => JsonSerializer.Serialize(new
    {
        success = false,
        error = "This account is signed in read-only; domain changes are not allowed.",
    });

    private static string Bool(bool value) => value ? "true" : "false";

    private static JsonObject Pick(JsonNode? source, params string[] names)
    {
        var result = new JsonObject();
        foreach (var name in names)
            result[name] = source?[name]?.DeepClone();
        return result;
    }

    /// <summary>The <c>value</c> of each <c>{ id, value, type, description }</c> whitelist entry.</summary>
    private static JsonArray Values(JsonNode? entries) =>
        new((entries as JsonArray ?? [])
            .Select(e => e is JsonObject o ? o["value"]?.DeepClone() : e?.DeepClone())
            .Where(v => v is not null)
            .ToArray());

    private static List<string> StringList(JsonArray array) =>
        array.Select(Str).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>An enum given as a name (case-insensitive) or a number, as its numeric value.</summary>
    private static int? EnumValue(JsonNode? value, string[] names)
    {
        if (value is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var number))
            return number >= 0 && number < names.Length ? number : null;
        return v.TryGetValue<string>(out var s) ? EnumValue(s, names) : null;
    }

    private static int? EnumValue(string? value, string[] names)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (int.TryParse(trimmed, out var number))
            return number >= 0 && number < names.Length ? number : null;
        var index = Array.FindIndex(names, n => n.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : null;
    }

    private static JsonArray? NormalizeConditions(string? json, out string? error)
    {
        error = null;
        if (!TryParseArray(json, out var items))
        {
            error = "conditions must be a JSON array, e.g. [{\"fieldType\":\"FromDomain\",\"filterType\":\"Equals\",\"searchArguments\":[\"example.com\"]}].";
            return null;
        }
        if (items.Count == 0)
        {
            error = "At least one condition is required.";
            return null;
        }

        var result = new JsonArray();
        foreach (var item in items.OfType<JsonObject>())
        {
            var field = EnumValue(item["fieldType"] ?? item["field"], ConditionFieldTypes);
            if (field is null)
            {
                error = $"Unknown condition fieldType. Use one of: {string.Join(", ", ConditionFieldTypes)}.";
                return null;
            }

            var comparison = EnumValue(item["filterType"] ?? item["comparison"] ?? JsonValue.Create("Contains"), ComparisonTypes);
            if (comparison is null)
            {
                error = "Unknown condition filterType. Use Equals, DoesntEqual, Contains or DoesntContain.";
                return null;
            }

            var arguments = item["searchArguments"] ?? item["values"] ?? item["value"];
            var argumentArray = arguments switch
            {
                JsonArray a => (JsonArray)a.DeepClone(),
                JsonValue single => new JsonArray(single.DeepClone()),
                _ => new JsonArray(),
            };

            result.Add(new JsonObject
            {
                ["fieldType"] = field.Value,
                ["filterType"] = comparison.Value,
                ["searchArguments"] = argumentArray,
                ["applyLogicalNot"] = item["applyLogicalNot"]?.DeepClone() ?? item["not"]?.DeepClone() ?? false,
                ["isWildcard"] = item["isWildcard"]?.DeepClone() ?? item["wildcard"]?.DeepClone() ?? false,
            });
        }

        if (result.Count != items.Count)
        {
            error = "Each condition must be a JSON object.";
            return null;
        }

        return result;
    }

    private static JsonArray? NormalizeActions(string? json, out string? error)
    {
        error = null;
        if (!TryParseArray(json, out var items))
        {
            error = "actions must be a JSON array, e.g. [{\"actionType\":\"MoveToFolder\",\"argument\":\"Junk E-Mail\"}].";
            return null;
        }
        if (items.Count == 0)
        {
            error = "At least one action is required.";
            return null;
        }

        var result = new JsonArray();
        var order = 0;
        foreach (var item in items)
        {
            if (item is not JsonObject action)
            {
                error = "Each action must be a JSON object.";
                return null;
            }

            var type = EnumValue(action["actionType"] ?? action["type"], FilterActionTypes);
            if (type is null)
            {
                error = $"Unknown actionType. Use one of: {ActionNames}.";
                return null;
            }

            result.Add(new JsonObject
            {
                ["actionType"] = type.Value,
                ["argument"] = action["argument"]?.DeepClone() ?? "",
                ["boolOption1"] = action["boolOption1"]?.DeepClone() ?? false,
                ["boolOption2"] = action["boolOption2"]?.DeepClone() ?? false,
                ["isDefaultAction"] = false,
                ["executionOrder"] = order++,
            });
        }

        return result;
    }

    private static bool TryParseArray(string? json, out JsonArray items)
    {
        items = [];
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) is JsonArray array)
            {
                items = array;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    /// <summary>"jane" → "jane@&lt;token's domain&gt;"; a full address is passed through.</summary>
    private static string ToEmail(string value, UserContext userContext)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Contains('@') || string.IsNullOrEmpty(userContext.Domain)
            ? trimmed
            : $"{trimmed}@{userContext.Domain}";
    }

    private static readonly string[] SecretKeyFragments = SmarterMailMcp.Core.SecretRedactor.DefaultFragments;

    /// <summary>Blanks non-empty strings under password/secret/private-key-like property names.</summary>
    private static void RedactNode(JsonNode? node) =>
        SmarterMailMcp.Core.SecretRedactor.RedactNode(node, SecretKeyFragments);
}
