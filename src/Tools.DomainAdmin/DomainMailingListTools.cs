using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Server.Tools;

/// <summary>
/// Mailing-list tools for a domain administrator: the lists themselves, their subscribers and
/// digest subscribers, the approved-poster and banned-user lists, custom subscriber fields and the
/// lists' system message templates. Everything is under
/// <c>/api/v1/settings/domain/mailing-lists/*</c> (plus the domain's subscriber field definitions)
/// and acts on the signed-in account's own domain, so no tool takes a <c>domain</c> parameter.
///
/// A domain admin account is expected. Creating and deleting lists needs the domainadmin role; most
/// other endpoints are documented without a role (a list moderator may be allowed some of them),
/// and SmarterMail decides per call.
///
/// Paths and shapes come from the SmarterMail API reference
/// (https://mail.smartertools.com/Documentation/api, "Mailing Lists" controller).
///
/// List settings are read-modify-write: the current settings are fetched, only the fields the
/// caller passed are changed, and the whole object is posted back. Results are sent on to the
/// caller's LLM provider, so password/secret-like string fields (e.g. a list's posting password)
/// are redacted from everything returned.
/// </summary>
[McpServerToolType]
public sealed class DomainMailingListTools
{
    private const string Root = "/api/v1/settings/domain/mailing-lists";

    private const string DomainAdminNote = " Requires a domain admin account.";

    // ---------------------------------------------------------------- reads

    [McpServerTool(Name = "domain_list_mailing_lists", ReadOnly = true)]
    [Description("List the mailing lists in your domain: id, list address, description, moderator, status, posting permissions, digest/double opt-in flags and subscriber, digest, poster and banned-user counts. Use the id with the other mailing list tools." + DomainAdminNote)]
    public static async Task<string> DomainListMailingLists(UserContext userContext) =>
        await Run(async () =>
        {
            var response = await userContext.GetAsync<JsonElement>($"{Root}/list");
            if (!response.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return Redact(response);

            var lists = items.EnumerateArray().Select(l => new
            {
                id = Num(l, "id"),
                listAddress = Str(l, "listAddress"),
                description = Str(l, "description"),
                moderatorAddress = Str(l, "moderatorAddress"),
                status = Raw(l, "status"),
                postingPermissions = Raw(l, "postingPermissions"),
                enableDigest = Bool(l, "enableDigest"),
                doubleOptIn = Bool(l, "doubleOptIn"),
                disableSubscriptions = Bool(l, "disableSubscriptions"),
                listSubscriberCount = Num(l, "listSubscriberCount"),
                digestSubscriberCount = Num(l, "digestSubscriberCount"),
                posterCount = Num(l, "posterCount"),
                bannedUserCount = Num(l, "bannedUserCount"),
                listCommandAddress = Str(l, "listCommandAddress"),
            }).ToList();

            return JsonSerializer.SerializeToElement(new { success = true, count = lists.Count, mailingLists = lists });
        });

    [McpServerTool(Name = "domain_get_mailing_list", ReadOnly = true)]
    [Description("Get the full settings of one mailing list (posting permissions, digest, double opt-in, subject prefix, throttling, custom To/From/Reply-To, unsubscribe text, spam filtering, GAL visibility…) together with its counts of subscribers, digest subscribers, posters, banned users, custom fields and system messages. The posting password, if any, is redacted." + DomainAdminNote)]
    public static async Task<string> DomainGetMailingList(
        [Description("Mailing list id (from domain_list_mailing_lists)")] int mailingListId,
        UserContext userContext)
    {
        return await Run(async () =>
        {
            var settings = await userContext.GetAsync<JsonElement>($"{Root}/{mailingListId}/settings");
            var counts = await userContext.GetAsync<JsonElement>($"{Root}/{mailingListId}/counts");

            var item = settings.TryGetProperty("item", out var i) ? i : settings;
            return Redact(JsonSerializer.SerializeToElement(new
            {
                success = true,
                settings = item,
                counts = new
                {
                    listSubscribers = Num(counts, "listSubscribers"),
                    digestSubscribers = Num(counts, "digestSubscribers"),
                    posters = Num(counts, "posters"),
                    bannedUsers = Num(counts, "bannedUsers"),
                    customFields = Num(counts, "customFields"),
                    messages = Num(counts, "messages"),
                },
            }));
        });
    }

    [McpServerTool(Name = "domain_search_mailing_list_subscribers", ReadOnly = true)]
    [Description("Search the subscribers of a mailing list, with paging. Returns the total match count and the page of subscribers (address, bounce count, last edit)." + DomainAdminNote)]
    public static async Task<string> DomainSearchMailingListSubscribers(
        [Description("Mailing list id (from domain_list_mailing_lists)")] int mailingListId,
        [Description("Only return subscribers whose address contains this text (optional)")] string? search = null,
        [Description("Which subscribers: 'regular' (default), 'digest', or 'all'")] string subscriberType = "regular",
        [Description("Sort by 'emailaddress' (default), 'editedon' or 'bouncecount'")] string sortField = "emailaddress",
        [Description("True to sort descending (default false)")] bool sortDescending = false,
        [Description("Number of results to skip, for paging (default 0)")] int skip = 0,
        [Description("Maximum number of results to return (default 100)")] int take = 100,
        UserContext userContext = null!)
    {
        var type = SubscriberType(subscriberType);
        if (type is null) return Invalid("subscriberType must be 'regular', 'digest' or 'all'.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/{mailingListId}/subscriber-search", new
            {
                skip = Math.Max(0, skip),
                take = Math.Clamp(take, 1, 1000),
                search = search ?? string.Empty,
                subscriberType = type.Value,
                sortField = string.IsNullOrWhiteSpace(sortField) ? "emailaddress" : sortField.Trim().ToLowerInvariant(),
                sortDescending,
            }));
    }

    [McpServerTool(Name = "domain_get_mailing_list_subscriber", ReadOnly = true)]
    [Description("Get one subscriber's details across your domain's mailing lists: which lists and digests they are subscribed to, custom field values, bounce address, bounce count and history, and the log of list messages sent to them." + DomainAdminNote)]
    public static async Task<string> DomainGetMailingListSubscriber(
        [Description("The subscriber's email address")] string emailAddress,
        [Description("Limit the details to one mailing list id (optional)")] int? mailingListId = null,
        UserContext userContext = null!)
    {
        if (string.IsNullOrWhiteSpace(emailAddress)) return Invalid("An email address is required.");

        var path = $"{Root}/subscriber-details/{Uri.EscapeDataString(emailAddress.Trim())}";
        if (mailingListId is not null) path += $"/{mailingListId.Value}";

        return await Run(async () => await userContext.GetAsync<JsonElement>(path));
    }

    [McpServerTool(Name = "domain_list_mailing_list_posters", ReadOnly = true)]
    [Description("Search a mailing list's approved posters (addresses allowed to post) or its banned users (addresses that may neither post nor subscribe), with paging." + DomainAdminNote)]
    public static async Task<string> DomainListMailingListPosters(
        [Description("Mailing list id (from domain_list_mailing_lists)")] int mailingListId,
        [Description("Which list: 'approved' (default) for approved posters, or 'banned' for banned users")] string listType = "approved",
        [Description("Only return addresses containing this text (optional)")] string? search = null,
        [Description("Number of results to skip, for paging (default 0)")] int skip = 0,
        [Description("Maximum number of results to return (default 100)")] int take = 100,
        UserContext userContext = null!)
    {
        var segment = PosterSegment(listType);
        if (segment is null) return Invalid("listType must be 'approved' or 'banned'.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/{mailingListId}/{segment}-search", new
            {
                skip = Math.Max(0, skip),
                take = Math.Clamp(take, 1, 1000),
                search = search ?? string.Empty,
            }));
    }

    [McpServerTool(Name = "domain_get_mailing_list_messages", ReadOnly = true)]
    [Description("Get a mailing list's system message templates (welcome, goodbye, opt-in confirmation, error replies…): name, subject, body and last-modified date. Use the name with domain_update_mailing_list_message." + DomainAdminNote)]
    public static async Task<string> DomainGetMailingListMessages(
        [Description("Mailing list id (from domain_list_mailing_lists)")] int mailingListId,
        UserContext userContext)
    {
        return await Run(async () => await userContext.GetAsync<JsonElement>($"{Root}/{mailingListId}/system-messages"));
    }

    [McpServerTool(Name = "domain_list_subscriber_fields", ReadOnly = true)]
    [Description("List the custom subscriber fields defined for your domain's mailing lists: id, name and default value." + DomainAdminNote)]
    public static async Task<string> DomainListSubscriberFields(UserContext userContext) =>
        await Run(async () => await userContext.GetAsync<JsonElement>("/api/v1/settings/domain/subscriber-field-definitions"));

    // ---------------------------------------------------------------- writes

    [McpServerTool(Name = "domain_create_mailing_list")]
    [Description("Create a new mailing list in your domain. Anything not given here uses SmarterMail's defaults; change more with domain_update_mailing_list." + DomainAdminNote)]
    public static async Task<string> DomainCreateMailingList(
        [Description("List address, local part only (e.g. 'announce' becomes announce@<your domain>)")] string listAddress,
        [Description("Email address of the list moderator")] string moderatorAddress,
        [Description("Description of the list's purpose (optional)")] string? description = null,
        [Description("Who may post: 'anyone', 'subscribers' or 'moderator' (optional)")] string? postingPermissions = null,
        [Description("True to enable digest delivery (optional)")] bool? enableDigest = null,
        [Description("True to require subscribers to confirm by email (double opt-in) (optional)")] bool? doubleOptIn = null,
        [Description("True to stop new subscriptions (optional)")] bool? disableSubscriptions = null,
        [Description("Text to prefix every list message's subject with, e.g. '[announce]' (optional; turns on the prefix)")] string? subjectPrefix = null,
        [Description("True to show the list in the Global Address List (optional)")] bool? showInGal = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (string.IsNullOrWhiteSpace(listAddress)) return Invalid("A list address is required.");
        if (string.IsNullOrWhiteSpace(moderatorAddress)) return Invalid("A moderator address is required.");

        var body = new JsonObject
        {
            ["listAddress"] = ToLocalPart(listAddress),
            ["moderatorAddress"] = moderatorAddress.Trim(),
        };

        var error = ApplyCommonFields(body, description, postingPermissions, enableDigest, doubleOptIn,
            disableSubscriptions, subjectPrefix, showInGal);
        if (error is not null) return Invalid(error);

        return await Run(async () => Redact(await userContext.PostAsync<JsonElement>($"{Root}/add", body)));
    }

    [McpServerTool(Name = "domain_update_mailing_list")]
    [Description("Change a mailing list's settings. Only the fields you pass are changed; everything else is kept. For settings without their own parameter, pass them in settingsJson using the field names domain_get_mailing_list returns." + DomainAdminNote)]
    public static async Task<string> DomainUpdateMailingList(
        [Description("Mailing list id (from domain_list_mailing_lists)")] int mailingListId,
        [Description("New moderator email address (optional)")] string? moderatorAddress = null,
        [Description("New description (optional)")] string? description = null,
        [Description("Who may post: 'anyone', 'subscribers' or 'moderator' (optional)")] string? postingPermissions = null,
        [Description("Enable (true) or disable (false) digest delivery (optional)")] bool? enableDigest = null,
        [Description("Require (true) or stop requiring (false) double opt-in (optional)")] bool? doubleOptIn = null,
        [Description("Stop (true) or allow (false) new subscriptions (optional)")] bool? disableSubscriptions = null,
        [Description("Subject prefix text (optional; an empty string turns the prefix off)")] string? subjectPrefix = null,
        [Description("Show (true) or hide (false) the list in the Global Address List (optional)")] bool? showInGal = null,
        [Description("Disable (true) or re-enable (false) the whole list (optional)")] bool? disabled = null,
        [Description("Other settings as a JSON object string, e.g. {\"listReplyToAddress\":\"help@example.com\",\"maxMessagesSentPerHour\":500} (optional)")] string? settingsJson = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        JsonObject? extra = null;
        if (!string.IsNullOrWhiteSpace(settingsJson))
        {
            try
            {
                extra = JsonNode.Parse(settingsJson) as JsonObject;
            }
            catch (JsonException)
            {
            }

            if (extra is null) return Invalid("settingsJson must be a JSON object.");
        }

        return await Run(async () =>
        {
            var current = await userContext.GetAsync<JsonElement>($"{Root}/{mailingListId}/settings");
            if (!current.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                return Redact(current);

            // Merge into the raw object (not a redacted copy), so an existing posting password is
            // posted back unchanged rather than overwritten with the redaction marker.
            var settings = JsonNode.Parse(item.GetRawText())!.AsObject();

            if (extra is not null)
            {
                foreach (var (key, value) in extra)
                    settings[key] = value?.DeepClone();
            }

            if (moderatorAddress is not null) settings["moderatorAddress"] = moderatorAddress.Trim();
            if (disabled is not null) settings["disabled"] = disabled.Value;

            var error = ApplyCommonFields(settings, description, postingPermissions, enableDigest, doubleOptIn,
                disableSubscriptions, subjectPrefix, showInGal);
            if (error is not null)
                return JsonSerializer.SerializeToElement(new { success = false, error });

            return await userContext.PostAsync<JsonElement>($"{Root}/{mailingListId}/settings", settings);
        });
    }

    [McpServerTool(Name = "domain_delete_mailing_lists", Destructive = true)]
    [Description("Permanently delete one or more mailing lists, with all their subscribers, posters and settings. This cannot be undone." + DomainAdminNote)]
    public static async Task<string> DomainDeleteMailingLists(
        [Description("Mailing list ids (from domain_list_mailing_lists)")] int[] mailingListIds,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (mailingListIds is not { Length: > 0 }) return Invalid("At least one mailing list id is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/delete-bulk", new { ints = mailingListIds }));
    }

    [McpServerTool(Name = "domain_add_mailing_list_subscribers")]
    [Description("Subscribe one or more email addresses to a mailing list, as regular subscribers or as digest subscribers. If the list uses double opt-in, SmarterMail may email each address a confirmation first." + DomainAdminNote)]
    public static async Task<string> DomainAddMailingListSubscribers(
        [Description("Mailing list id (from domain_list_mailing_lists)")] int mailingListId,
        [Description("Email addresses to subscribe")] string[] emailAddresses,
        [Description("True to add them as digest subscribers instead of regular subscribers (default false)")] bool digest = false,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        var addresses = Clean(emailAddresses);
        if (addresses.Length == 0) return Invalid("At least one email address is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/{mailingListId}/{(digest ? "digest-subscriber-add" : "subscriber-add")}", addresses));
    }

    [McpServerTool(Name = "domain_remove_mailing_list_subscribers", Destructive = true)]
    [Description("Unsubscribe one or more email addresses from a mailing list (regular or digest subscribers)." + DomainAdminNote)]
    public static async Task<string> DomainRemoveMailingListSubscribers(
        [Description("Mailing list id (from domain_list_mailing_lists)")] int mailingListId,
        [Description("Email addresses to remove")] string[] emailAddresses,
        [Description("True to remove them from the digest subscribers instead of the regular subscribers (default false)")] bool digest = false,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        var addresses = Clean(emailAddresses);
        if (addresses.Length == 0) return Invalid("At least one email address is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/{mailingListId}/{(digest ? "digest-subscriber-remove" : "subscriber-remove")}", addresses));
    }

    [McpServerTool(Name = "domain_update_mailing_list_subscriber")]
    [Description("Change one subscriber: set custom field values, and/or replace which of your domain's lists and digests they are subscribed to. Only what you pass is changed." + DomainAdminNote)]
    public static async Task<string> DomainUpdateMailingListSubscriber(
        [Description("The subscriber's email address")] string emailAddress,
        [Description("Custom field values as a JSON object string of field name → value, e.g. {\"FirstName\":\"Jane\"} (optional; other fields are kept)")] string? fieldValuesJson = null,
        [Description("The complete set of mailing list ids they should be subscribed to (optional; replaces the current set)")] int[]? subscribedListIds = null,
        [Description("The complete set of mailing list ids they should receive digests from (optional; replaces the current set)")] int[]? subscribedDigestIds = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (string.IsNullOrWhiteSpace(emailAddress)) return Invalid("An email address is required.");

        JsonObject? values = null;
        if (!string.IsNullOrWhiteSpace(fieldValuesJson))
        {
            try
            {
                values = JsonNode.Parse(fieldValuesJson) as JsonObject;
            }
            catch (JsonException)
            {
            }

            if (values is null) return Invalid("fieldValuesJson must be a JSON object of field name to value.");
        }

        var email = emailAddress.Trim();
        var escaped = Uri.EscapeDataString(email);

        return await Run(async () =>
        {
            var current = await userContext.GetAsync<JsonElement>($"{Root}/subscribers/{escaped}");
            if (current.ValueKind != JsonValueKind.Object)
                return current;
            if (Bool(current, "success") == false)
                return current;

            var body = new JsonObject
            {
                ["emailAddress"] = Str(current, "emailAddress") ?? email,
                ["fieldValues"] = current.TryGetProperty("fieldValues", out var fv) && fv.ValueKind == JsonValueKind.Array
                    ? JsonNode.Parse(fv.GetRawText())
                    : new JsonArray(),
                ["subscribedLists"] = subscribedListIds is not null
                    ? new JsonArray(subscribedListIds.Select(id => (JsonNode)id).ToArray())
                    : Node(current, "subscribedLists") ?? new JsonArray(),
                ["subscribedDigests"] = subscribedDigestIds is not null
                    ? new JsonArray(subscribedDigestIds.Select(id => (JsonNode)id).ToArray())
                    : Node(current, "subscribedDigests") ?? new JsonArray(),
            };

            if (values is not null)
            {
                var fields = body["fieldValues"] as JsonArray ?? new JsonArray();
                foreach (var (name, value) in values)
                {
                    var text = value is null ? string.Empty
                        : value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>()
                        : value.ToJsonString();
                    var existing = fields.OfType<JsonObject>().FirstOrDefault(f =>
                        string.Equals(f["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null)
                        existing["value"] = text;
                    else
                        fields.Add(new JsonObject { ["name"] = name, ["value"] = text });
                }

                body["fieldValues"] = fields;
            }

            return await userContext.PostAsync<JsonElement>($"{Root}/subscribers/{escaped}/edit", body);
        });
    }

    [McpServerTool(Name = "domain_clear_subscriber_bounces")]
    [Description("Reset a subscriber's bounce count to zero, e.g. after their mailbox problem was fixed, so the list stops treating them as bouncing." + DomainAdminNote)]
    public static async Task<string> DomainClearSubscriberBounces(
        [Description("The subscriber's email address")] string emailAddress,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (string.IsNullOrWhiteSpace(emailAddress)) return Invalid("An email address is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/subscriber-clear-bounce-count", new { input = emailAddress.Trim() }));
    }

    [McpServerTool(Name = "domain_add_mailing_list_posters")]
    [Description("Add email addresses to a mailing list's approved posters (allowed to post) or to its banned users (may neither post nor subscribe)." + DomainAdminNote)]
    public static async Task<string> DomainAddMailingListPosters(
        [Description("Mailing list id (from domain_list_mailing_lists)")] int mailingListId,
        [Description("Email addresses to add")] string[] emailAddresses,
        [Description("Which list: 'approved' (default) for approved posters, or 'banned' for banned users")] string listType = "approved",
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        var segment = PosterSegment(listType);
        if (segment is null) return Invalid("listType must be 'approved' or 'banned'.");
        var addresses = Clean(emailAddresses);
        if (addresses.Length == 0) return Invalid("At least one email address is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/{mailingListId}/{segment}-add", addresses));
    }

    [McpServerTool(Name = "domain_remove_mailing_list_posters", Destructive = true)]
    [Description("Remove email addresses from a mailing list's approved posters or from its banned users." + DomainAdminNote)]
    public static async Task<string> DomainRemoveMailingListPosters(
        [Description("Mailing list id (from domain_list_mailing_lists)")] int mailingListId,
        [Description("Email addresses to remove")] string[] emailAddresses,
        [Description("Which list: 'approved' (default) for approved posters, or 'banned' for banned users")] string listType = "approved",
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        var segment = PosterSegment(listType);
        if (segment is null) return Invalid("listType must be 'approved' or 'banned'.");
        var addresses = Clean(emailAddresses);
        if (addresses.Length == 0) return Invalid("At least one email address is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/{mailingListId}/{segment}-remove", addresses));
    }

    [McpServerTool(Name = "domain_update_mailing_list_message")]
    [Description("Change one of a mailing list's system message templates (see domain_get_mailing_list_messages for the names). Some messages have no subject; leave subject out for those." + DomainAdminNote)]
    public static async Task<string> DomainUpdateMailingListMessage(
        [Description("Mailing list id (from domain_list_mailing_lists)")] int mailingListId,
        [Description("Template name exactly as domain_get_mailing_list_messages returns it")] string name,
        [Description("New message body")] string body,
        [Description("New subject (optional; only for messages that have one)")] string? subject = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (string.IsNullOrWhiteSpace(name)) return Invalid("A template name is required.");

        var message = new JsonObject { ["name"] = name, ["body"] = body ?? string.Empty };
        if (subject is not null) message["subject"] = subject;

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/{mailingListId}/system-message", message));
    }

    [McpServerTool(Name = "domain_save_subscriber_field")]
    [Description("Create a custom subscriber field for your domain's mailing lists, or rename one / change its default value." + DomainAdminNote)]
    public static async Task<string> DomainSaveSubscriberField(
        [Description("Field name, e.g. 'FirstName'")] string name,
        [Description("Default value for subscribers who have none (optional)")] string? defaultValue = null,
        [Description("Id of an existing field to change (from domain_list_subscriber_fields); omit to create a new field")] int? fieldId = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (string.IsNullOrWhiteSpace(name)) return Invalid("A field name is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            "/api/v1/settings/domain/subscriber-field-definition", new
            {
                subscriberFieldDefinition = new
                {
                    id = fieldId ?? 0,
                    name = name.Trim(),
                    defaultValue = defaultValue ?? string.Empty,
                },
            }));
    }

    [McpServerTool(Name = "domain_delete_subscriber_fields", Destructive = true)]
    [Description("Delete one or more custom subscriber fields, and every subscriber's value for them. This cannot be undone." + DomainAdminNote)]
    public static async Task<string> DomainDeleteSubscriberFields(
        [Description("Field ids (from domain_list_subscriber_fields)")] int[] fieldIds,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (fieldIds is not { Length: > 0 }) return Invalid("At least one field id is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/subscriber-field-definitions/delete-bulk", new { ints = fieldIds }));
    }

    [McpServerTool(Name = "domain_send_mailing_list_digest")]
    [Description("Send a mailing list's pending digest right now, emailing every digest subscriber, instead of waiting for its schedule." + DomainAdminNote)]
    public static async Task<string> DomainSendMailingListDigest(
        [Description("Mailing list id (from domain_list_mailing_lists)")] int mailingListId,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        return await Run(async () => await userContext.PostAsync<JsonElement>($"{Root}/{mailingListId}/send-digest"));
    }

    // ---------------------------------------------------------------- helpers

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

    /// <summary>Shared by create and update. Returns an error message, or null.</summary>
    private static string? ApplyCommonFields(
        JsonObject target, string? description, string? postingPermissions, bool? enableDigest,
        bool? doubleOptIn, bool? disableSubscriptions, string? subjectPrefix, bool? showInGal)
    {
        if (postingPermissions is not null)
        {
            var permission = postingPermissions.Trim().ToLowerInvariant() switch
            {
                "anyone" => 0,
                "subscribers" => 1,
                "moderator" or "moderators" => 2,
                _ => -1,
            };
            if (permission < 0) return "postingPermissions must be 'anyone', 'subscribers' or 'moderator'.";
            target["postingPermissions"] = permission;
        }

        if (description is not null) target["description"] = description;
        if (enableDigest is not null) target["enableDigest"] = enableDigest.Value;
        if (doubleOptIn is not null) target["doubleOptIn"] = doubleOptIn.Value;
        if (disableSubscriptions is not null) target["disableSubscriptions"] = disableSubscriptions.Value;
        if (showInGal is not null) target["showInGal"] = showInGal.Value;
        if (subjectPrefix is not null)
        {
            target["prependSubject"] = subjectPrefix.Length > 0;
            target["subject"] = subjectPrefix;
        }

        return null;
    }

    /// <summary>'regular' → 0 (Subscriber), 'digest' → 1, 'all' → 999.</summary>
    private static int? SubscriberType(string? value) => (value ?? "regular").Trim().ToLowerInvariant() switch
    {
        "" or "regular" or "subscriber" or "subscribers" => 0,
        "digest" => 1,
        "all" => 999,
        _ => null,
    };

    /// <summary>'approved' → poster-*, 'banned' → banned-user-*.</summary>
    private static string? PosterSegment(string? value) => (value ?? "approved").Trim().ToLowerInvariant() switch
    {
        "" or "approved" or "poster" or "posters" or "allowed" => "poster",
        "banned" or "ban" or "blocked" => "banned-user",
        _ => null,
    };

    private static string[] Clean(string[]? addresses) =>
        (addresses ?? [])
        .Select(a => (a ?? string.Empty).Trim())
        .Where(a => a.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static readonly string[] SecretKeyFragments = ["password", "secret", "apikey", "privatekey"];

    /// <summary>Blanks any non-empty string under a password/secret-like key, at any depth.</summary>
    private static JsonElement Redact(JsonElement element)
    {
        if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return element;

        var node = JsonSerializer.SerializeToNode(element);
        RedactNode(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var value = obj[key];
                    if (value is JsonValue v && v.GetValueKind() == JsonValueKind.String &&
                        SecretKeyFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase)) &&
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

    private static string ReadOnly() => JsonSerializer.Serialize(new
    {
        success = false,
        error = "This account is signed in read-only; mailing list changes are not allowed.",
    });

    private static string Invalid(string message) =>
        JsonSerializer.Serialize(new { success = false, error = message });

    /// <summary>"announce@example.com" → "announce"; "announce" → "announce".</summary>
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

    /// <summary>The property as-is (enums may arrive as a number or a name), or null.</summary>
    private static JsonElement? Raw(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.Clone()
            : null;

    private static JsonNode? Node(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? JsonNode.Parse(v.GetRawText())
            : null;
}
