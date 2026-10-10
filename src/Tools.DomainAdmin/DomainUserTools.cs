using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Server.Tools;

/// <summary>
/// Domain-administrator user management beyond <see cref="DomainAdminTools"/>: user groups, account
/// status and connections, protocol access, password policy, per-user mail settings and forwarding,
/// new-user defaults, and mailbox maintenance (reindex, resync, size recalculation). They need the
/// signed-in account to be a domain admin; for a plain user every call comes back 403 from
/// SmarterMail. Every endpoint is under <c>/api/v1/settings/domain/*</c> and acts on the signed-in
/// domain admin's own domain, so no tool takes a <c>domain</c> parameter.
///
/// Paths and shapes come from the SmarterMail API reference
/// (https://mail.smartertools.com/Documentation/api, "Domain Settings" controller).
///
/// Every response is passed through <see cref="Redact"/>: some of these endpoints return temporary
/// passwords or fresh access tokens, and a tool result is sent on to the caller's LLM provider.
///
/// Deliberately not wrapped: impersonate-user, show-password, reset-app-password (returns a new
/// secret), user-detach, propagate-settings, resync-users/{domain} (returns tokens), and every
/// export-/import-/download- endpoint.
/// </summary>
[McpServerToolType]
public sealed class DomainUserTools
{
    private const string Root = "/api/v1/settings/domain";

    // ================================================================ reads

    [McpServerTool(Name = "domain_list_user_groups", ReadOnly = true)]
    [Description("List the user groups in your domain (id, name, enabled, member usernames). Pass a username to list only the groups that user belongs to.")]
    public static async Task<string> DomainListUserGroups(
        [Description("Optional username (local part) or email address: only return this user's groups")] string? username = null,
        UserContext userContext = null!)
    {
        var path = string.IsNullOrWhiteSpace(username)
            ? $"{Root}/all-user-groups"
            : $"{Root}/all-user-groups/{Uri.EscapeDataString(ToLocalPart(username))}";
        return await Run(async () => await userContext.GetAsync<JsonElement>(path));
    }

    [McpServerTool(Name = "domain_get_user_group", ReadOnly = true)]
    [Description("Get one user group in your domain by id: name, enabled state and member usernames.")]
    public static async Task<string> DomainGetUserGroup(
        [Description("User group id (from domain_list_user_groups)")] string groupId,
        UserContext userContext)
    {
        return await Run(async () => await userContext.GetAsync<JsonElement>(
            $"{Root}/user-group/{Uri.EscapeDataString(groupId.Trim())}"));
    }

    [McpServerTool(Name = "domain_get_account_counts", ReadOnly = true)]
    [Description("Get a quick overview of your domain's size: number of users, aliases, administrators, groups, ActiveSync and MAPI/EWS mailboxes and mailing lists, with the domain's limits.")]
    public static async Task<string> DomainGetAccountCounts(UserContext userContext) =>
        await Run(async () => await userContext.GetAsync<JsonElement>($"{Root}/account-list-counts"));

    [McpServerTool(Name = "domain_search_accounts", ReadOnly = true)]
    [Description("Search the users and aliases of your domain together, with status (enabled / disabled / errored), account type, authentication type and ActiveSync / MAPI-EWS flags. Paginated.")]
    public static async Task<string> DomainSearchAccounts(
        [Description("Search text matched against account names (empty for all)")] string? search = null,
        [Description("Number of results to skip (default 0)")] int skip = 0,
        [Description("Maximum results to return (default 100)")] int take = 100,
        [Description("Field to sort by, e.g. 'userName' or 'displayName' (optional)")] string? sortField = null,
        [Description("Sort descending (default false)")] bool sortDescending = false,
        UserContext userContext = null!)
    {
        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/account-list-search", Page(search, skip, take, sortField, sortDescending)));
    }

    [McpServerTool(Name = "domain_get_user_statuses", ReadOnly = true)]
    [Description("Get the account health of users in your domain: enabled, two-factor enabled, expired password, password-policy violations, indexing / migrating state, authentication type and critical errors. Paginated.")]
    public static async Task<string> DomainGetUserStatuses(
        [Description("Search text matched against usernames (empty for all)")] string? search = null,
        [Description("Number of results to skip (default 0)")] int skip = 0,
        [Description("Maximum results to return (default 100)")] int take = 100,
        UserContext userContext = null!)
    {
        return await Run(async () =>
        {
            var body = Page(search, skip, take, null, false);
            body["domain"] = userContext.Domain;
            return await userContext.PostAsync<JsonElement>($"{Root}/users-statuses", body);
        });
    }

    [McpServerTool(Name = "domain_get_last_login_times", ReadOnly = true)]
    [Description("Get the last login time of every user in your domain, with enabled and domain-admin flags. Useful for finding inactive accounts.")]
    public static async Task<string> DomainGetLastLoginTimes(
        [Description("Reference date in yyyy-MM-dd format that the server evaluates login times against (default: today)")] string? referenceDate = null,
        UserContext userContext = null!)
    {
        var date = DateTime.TryParse(referenceDate, out var parsed) ? parsed : DateTime.UtcNow;
        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/last-login-times", new { dateTime = date.ToString("o") }));
    }

    [McpServerTool(Name = "domain_get_user_connections", ReadOnly = true)]
    [Description("Get who is connected in your domain: per-user connection counts, last login (overall and webmail), last authenticating IP and protocol, plus domain-wide counts per protocol (webmail, IMAP, POP, SMTP, ActiveSync, MAPI/EWS, XMPP, WebDAV).")]
    public static async Task<string> DomainGetUserConnections(
        [Description("Protocol filter: all (default), webmail, imap, pop, smtp, eas, mapiEws, xmpp or webdav")] string protocol = "all",
        [Description("Search text matched against usernames (empty for all)")] string? search = null,
        [Description("Number of users to skip (default 0)")] int skip = 0,
        [Description("Maximum users to return (default 100)")] int take = 100,
        UserContext userContext = null!)
    {
        return await Run(async () =>
        {
            var body = Page(search, skip, take, null, false);
            body["type"] = string.IsNullOrWhiteSpace(protocol) ? "all" : protocol.Trim();
            body["domain"] = userContext.Domain;

            var summary = await userContext.PostAsync<JsonElement>($"{Root}/users-connections-summary", body);
            var counts = await userContext.PostAsync<JsonElement>($"{Root}/users-connections-counts", body);
            return JsonSerializer.SerializeToElement(new { success = true, counts, users = summary });
        });
    }

    [McpServerTool(Name = "domain_list_protocol_mailboxes", ReadOnly = true)]
    [Description("List the mailboxes in your domain that have ActiveSync or MAPI/EWS (Outlook) access enabled, with display name and last sync time. These are licensed per mailbox.")]
    public static async Task<string> DomainListProtocolMailboxes(
        [Description("Which protocol: 'activesync' or 'mapiews'")] string protocol,
        UserContext userContext)
    {
        var path = NormalizeProtocol(protocol) switch
        {
            "activesync" => $"{Root}/active-sync-mailboxes",
            "mapiews" => $"{Root}/mapi-ews-mailboxes",
            _ => null,
        };
        if (path is null) return Invalid("protocol must be 'activesync' or 'mapiews'.");
        return await Run(async () => await userContext.GetAsync<JsonElement>(path));
    }

    [McpServerTool(Name = "domain_get_password_policy", ReadOnly = true)]
    [Description("Get your domain's password policy: the server defaults and the domain's overrides (minimum length, required character classes, common-password and username checks, password history, expiration and grace period).")]
    public static async Task<string> DomainGetPasswordPolicy(UserContext userContext) =>
        await Run(async () => await userContext.GetAsync<JsonElement>($"{Root}/password-settings"));

    [McpServerTool(Name = "domain_list_password_issues", ReadOnly = true)]
    [Description("List users in your domain with password problems: 'noncompliant' (password breaks the current policy; shows which rules), 'expired' (must change before signing in) or 'aged' (approaching or past the maximum age). Paginated.")]
    public static async Task<string> DomainListPasswordIssues(
        [Description("Which list: 'noncompliant', 'expired' or 'aged'")] string kind,
        [Description("Search text matched against usernames (empty for all)")] string? search = null,
        [Description("Number of results to skip (default 0)")] int skip = 0,
        [Description("Maximum results to return (default 100)")] int take = 100,
        [Description("Leave out Active Directory-authenticated accounts, whose passwords SmarterMail does not manage (default false; ignored for 'expired')")] bool excludeActiveDirectory = false,
        UserContext userContext = null!)
    {
        var suffix = excludeActiveDirectory ? "-no-activedirectory" : string.Empty;
        var path = (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "noncompliant" or "non-compliant" or "compliance" => $"{Root}/password-compliance-users{suffix}",
            "aged" => $"{Root}/aged-password-list{suffix}",
            "expired" => $"{Root}/expired-password-list",
            _ => null,
        };
        if (path is null) return Invalid("kind must be 'noncompliant', 'expired' or 'aged'.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            path, Page(search, skip, take, null, false)));
    }

    [McpServerTool(Name = "domain_get_user_mail_settings", ReadOnly = true)]
    [Description("Get one user's mail settings: can receive mail, forwarding enabled, ActiveSync / MAPI-EWS enabled, mailbox size limit, sending throttles, auto-clean rules, blocked and trusted senders, spam and calendar options.")]
    public static async Task<string> DomainGetUserMailSettings(
        [Description("Username (local part) or full email address in your domain")] string username,
        UserContext userContext)
    {
        return await Run(async () => await userContext.GetAsync<JsonElement>(
            $"{Root}/user-mail/{Uri.EscapeDataString(ToEmail(username, userContext))}"));
    }

    [McpServerTool(Name = "domain_get_mailbox_forwarding", ReadOnly = true)]
    [Description("Get where one user's incoming mail is forwarded: the forward-to addresses, whether a copy is kept, whether original recipients are kept, and how spam is handled.")]
    public static async Task<string> DomainGetMailboxForwarding(
        [Description("Username (local part) or full email address in your domain")] string username,
        UserContext userContext)
    {
        return await Run(async () => await userContext.GetAsync<JsonElement>(
            $"{Root}/mailbox-forward-list/{Uri.EscapeDataString(ToEmail(username, userContext))}"));
    }

    [McpServerTool(Name = "domain_get_user_defaults", ReadOnly = true)]
    [Description("Get the default settings applied to NEW users created in your domain (mailbox size, protocol access, feature permissions, GAL visibility, password lock, auto-clean and more).")]
    public static async Task<string> DomainGetUserDefaults(UserContext userContext) =>
        await Run(async () => await userContext.GetAsync<JsonElement>($"{Root}/user-defaults"));

    [McpServerTool(Name = "domain_get_user_indexing_status", ReadOnly = true)]
    [Description("Get the search-index status of one user's mailbox: whether it is indexing, items indexed and still to index, last indexed time and current status.")]
    public static async Task<string> DomainGetUserIndexingStatus(
        [Description("Username (local part) or full email address in your domain")] string username,
        UserContext userContext)
    {
        return await Run(async () => await userContext.GetAsync<JsonElement>(
            $"{Root}/indexing-user/{Uri.EscapeDataString(ToEmail(username, userContext))}"));
    }

    // ================================================================ writes: user groups

    [McpServerTool(Name = "domain_create_user_group")]
    [Description("Create a user group in your domain. Groups are used to assign permissions, signatures and shared resources to several users at once.")]
    public static async Task<string> DomainCreateUserGroup(
        [Description("Group name")] string name,
        [Description("Initial members: usernames (local parts) or email addresses in your domain (optional)")] string[]? members = null,
        [Description("Whether the group is active (default true)")] bool enabled = true,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (string.IsNullOrWhiteSpace(name)) return Invalid("A group name is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>($"{Root}/user-group-put", new
        {
            userGroup = new
            {
                name = name.Trim(),
                userNames = (members ?? []).Select(ToLocalPart).ToArray(),
                enabled,
            },
        }));
    }

    [McpServerTool(Name = "domain_update_user_group")]
    [Description("Rename, enable/disable, or replace the member list of a user group in your domain. Only what you pass is changed.")]
    public static async Task<string> DomainUpdateUserGroup(
        [Description("User group id (from domain_list_user_groups)")] string groupId,
        [Description("New group name (optional)")] string? name = null,
        [Description("New COMPLETE member list, replacing the current one: usernames (local parts) or email addresses (optional)")] string[]? members = null,
        [Description("Enable (true) or disable (false) the group (optional)")] bool? enabled = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        return await Run(async () =>
        {
            var current = await userContext.GetAsync<JsonElement>(
                $"{Root}/user-group/{Uri.EscapeDataString(groupId.Trim())}");
            if (!current.TryGetProperty("userGroup", out var existing) || existing.ValueKind != JsonValueKind.Object)
                return current;

            var group = JsonNode.Parse(existing.GetRawText())!.AsObject();
            if (name is not null) group["name"] = name.Trim();
            if (enabled is not null) group["enabled"] = enabled.Value;
            if (members is not null)
                group["userNames"] = new JsonArray(members.Select(m => (JsonNode)JsonValue.Create(ToLocalPart(m))!).ToArray());

            return await userContext.PostAsync<JsonElement>($"{Root}/user-group", new JsonObject { ["userGroup"] = group });
        });
    }

    [McpServerTool(Name = "domain_delete_user_groups", Destructive = true)]
    [Description("Delete one or more user groups in your domain. The member users are not deleted, but lose whatever the group granted them.")]
    public static async Task<string> DomainDeleteUserGroups(
        [Description("User group ids (from domain_list_user_groups)")] string[] groupIds,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (groupIds is not { Length: > 0 }) return Invalid("At least one group id is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/remove-user-groups", new { iDs = groupIds.Select(g => g.Trim()).ToArray() }));
    }

    [McpServerTool(Name = "domain_set_user_group_membership")]
    [Description("Add one user to, and/or remove them from, user groups in your domain.")]
    public static async Task<string> DomainSetUserGroupMembership(
        [Description("Username (local part) or email address in your domain")] string username,
        [Description("Group ids to add the user to (optional)")] string[]? addToGroupIds = null,
        [Description("Group ids to remove the user from (optional)")] string[]? removeFromGroupIds = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (addToGroupIds is not { Length: > 0 } && removeFromGroupIds is not { Length: > 0 })
            return Invalid("Pass at least one group id to add or remove.");

        return await Run(async () =>
        {
            var user = Uri.EscapeDataString(ToLocalPart(username));
            JsonElement? added = null, removed = null;
            if (addToGroupIds is { Length: > 0 })
            {
                added = await userContext.PostAsync<JsonElement>(
                    $"{Root}/add-to-user-groups/{user}", new { iDs = addToGroupIds.Select(g => g.Trim()).ToArray() });
            }

            if (removeFromGroupIds is { Length: > 0 })
            {
                removed = await userContext.PostAsync<JsonElement>(
                    $"{Root}/remove-from-user-groups/{user}", new { iDs = removeFromGroupIds.Select(g => g.Trim()).ToArray() });
            }

            return JsonSerializer.SerializeToElement(new
            {
                success = Succeeded(added) && Succeeded(removed),
                added,
                removed,
            });
        });
    }

    // ================================================================ writes: sessions and protocols

    [McpServerTool(Name = "domain_disconnect_users", Destructive = true)]
    [Description("Force users in your domain to sign in again: ends their webmail sessions and/or drops their open protocol connections (IMAP, POP, ActiveSync, …). Useful after a password change or a suspected compromise.")]
    public static async Task<string> DomainDisconnectUsers(
        [Description("Usernames (local parts) or email addresses in your domain")] string[] usernames,
        [Description("End webmail sessions (default true)")] bool webmailSessions = true,
        [Description("Drop protocol connections such as IMAP, POP and ActiveSync (default true)")] bool protocolConnections = true,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (usernames is not { Length: > 0 }) return Invalid("At least one username is required.");
        if (!webmailSessions && !protocolConnections) return Invalid("Nothing to do: both options are false.");

        return await Run(async () =>
        {
            var emails = usernames.Select(u => ToEmail(u, userContext)).ToArray();
            JsonElement? sessions = null;
            var connections = new List<JsonElement>();

            if (webmailSessions)
            {
                sessions = await userContext.PostAsync<JsonElement>($"{Root}/kill-user-sessions", new
                {
                    sessions = emails.Select(e => new { email = e, ip = "" }).ToArray(),
                });
            }

            if (protocolConnections)
            {
                foreach (var email in emails)
                {
                    connections.Add(await userContext.PostAsync<JsonElement>($"{Root}/drop-user-connections", new
                    {
                        email,
                        dropInfo = new[] { email },
                    }));
                }
            }

            return JsonSerializer.SerializeToElement(new
            {
                success = Succeeded(sessions) && connections.All(c => Succeeded(c)),
                webmailSessions = sessions,
                protocolConnections = connections,
            });
        });
    }

    [McpServerTool(Name = "domain_set_protocol_access")]
    [Description("Turn protocols on or off for one or more users in your domain. Only the protocols you pass are changed. ActiveSync and MAPI/EWS are licensed per mailbox and count against the domain's limits.")]
    public static async Task<string> DomainSetProtocolAccess(
        [Description("Usernames (local parts) or email addresses in your domain")] string[] usernames,
        [Description("Webmail access (optional)")] bool? webmail = null,
        [Description("SMTP (sending from mail clients) (optional)")] bool? smtp = null,
        [Description("IMAP (optional)")] bool? imap = null,
        [Description("POP (optional)")] bool? pop = null,
        [Description("XMPP chat (optional)")] bool? xmpp = null,
        [Description("WebDAV (optional)")] bool? webdav = null,
        [Description("Exchange ActiveSync (mobile devices) (optional)")] bool? activeSync = null,
        [Description("MAPI/EWS (Outlook / Exchange clients) (optional)")] bool? mapiEws = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (usernames is not { Length: > 0 }) return Invalid("At least one username is required.");
        if (webmail is null && smtp is null && imap is null && pop is null && xmpp is null &&
            webdav is null && activeSync is null && mapiEws is null)
            return Invalid("Pass at least one protocol to change.");

        return await Run(async () =>
        {
            var emails = usernames.Select(u => ToEmail(u, userContext)).ToArray();
            var results = new JsonObject();

            // The per-mailbox licensed protocols have explicit patch endpoints.
            if (activeSync is not null)
            {
                results["activeSync"] = Node(await userContext.PostAsync<JsonElement>($"{Root}/active-sync-mailboxes-patch",
                    emails.Select(e => new { emailAddress = e, isActive = activeSync.Value }).ToArray()));
            }

            if (mapiEws is not null)
            {
                results["mapiEws"] = Node(await userContext.PostAsync<JsonElement>($"{Root}/mapi-ews-mailboxes-patch",
                    emails.Select(e => new { emailAddress = e, isActive = mapiEws.Value }).ToArray()));
            }

            // The rest go through modify-protocol-access, sending only the flags that were given.
            var flags = new JsonObject();
            if (webmail is not null) flags["webmailEnabled"] = webmail.Value;
            if (smtp is not null) flags["smtpEnabled"] = smtp.Value;
            if (imap is not null) flags["imapEnabled"] = imap.Value;
            if (pop is not null) flags["popEnabled"] = pop.Value;
            if (xmpp is not null) flags["xmppEnabled"] = xmpp.Value;
            if (webdav is not null) flags["webdavEnabled"] = webdav.Value;
            if (flags.Count > 0)
            {
                flags["items"] = new JsonArray(emails.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
                results["otherProtocols"] = Node(await userContext.PostAsync<JsonElement>($"{Root}/modify-protocol-access", flags));
            }

            var success = results.All(r => r.Value is JsonObject o && o["success"]?.GetValueKind() != JsonValueKind.False);
            results["success"] = success;
            return JsonSerializer.SerializeToElement(results);
        });
    }

    // ================================================================ writes: passwords and sign-in

    [McpServerTool(Name = "domain_update_password_policy")]
    [Description("Change your domain's password policy. Only the rules you pass are changed; everything else is kept. Stricter rules affect existing users at their next password change and show up in domain_list_password_issues.")]
    public static async Task<string> DomainUpdatePasswordPolicy(
        [Description("Minimum password length; 0 turns the length rule off (optional)")] int? minLength = null,
        [Description("Require an uppercase letter (optional)")] bool? requireCapital = null,
        [Description("Require a lowercase letter (optional)")] bool? requireLower = null,
        [Description("Require a number (optional)")] bool? requireNumber = null,
        [Description("Require a symbol (optional)")] bool? requireSymbol = null,
        [Description("Refuse passwords that contain the username (optional)")] bool? notUsername = null,
        [Description("Refuse common, easily guessed passwords (optional)")] bool? preventCommonPasswords = null,
        [Description("Number of previous passwords that may not be reused; 0 turns the history rule off (optional)")] int? passwordHistoryCount = null,
        [Description("Password expiration in months; 0 turns expiration off (optional)")] int? expirationMonths = null,
        [Description("Grace period in days after expiry before outgoing SMTP is blocked (optional)")] int? expirationGraceDays = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        return await Run(async () =>
        {
            var current = await userContext.GetAsync<JsonElement>($"{Root}/password-settings");
            var policy = current.TryGetProperty("overrides", out var existing) && existing.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(existing.GetRawText())!.AsObject()
                : new JsonObject();

            if (requireCapital is not null) policy["requireCapital"] = requireCapital.Value;
            if (requireLower is not null) policy["requireLower"] = requireLower.Value;
            if (requireNumber is not null) policy["requireNumber"] = requireNumber.Value;
            if (requireSymbol is not null) policy["requireSymbol"] = requireSymbol.Value;
            if (notUsername is not null) policy["notUsername"] = notUsername.Value;
            if (preventCommonPasswords is not null) policy["preventCommonPasswords"] = preventCommonPasswords.Value;
            if (minLength is not null) SetRule(policy, "minLength", minLength.Value);
            if (passwordHistoryCount is not null) SetRule(policy, "preventPreviousPasswords", passwordHistoryCount.Value);
            if (expirationMonths is not null) SetRule(policy, "expiration", expirationMonths.Value);
            if (expirationGraceDays is not null)
            {
                if (policy["expiration"] is not JsonObject expiration)
                    policy["expiration"] = expiration = new JsonObject();
                expiration["gracePeriodDays"] = expirationGraceDays.Value;
            }

            return await userContext.PostAsync<JsonElement>($"{Root}/password-settings", policy);
        });
    }

    [McpServerTool(Name = "domain_expire_user_passwords", Destructive = true)]
    [Description("Mark the passwords of one or more users in your domain as expired, so they must choose a new one at their next sign-in.")]
    public static async Task<string> DomainExpireUserPasswords(
        [Description("Usernames (local parts) or email addresses in your domain")] string[] usernames,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (usernames is not { Length: > 0 }) return Invalid("At least one username is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/expire-users-passwords", new { input = usernames.Select(ToLocalPart).ToArray() }));
    }

    [McpServerTool(Name = "domain_disable_two_factor", Destructive = true)]
    [Description("Turn off two-step authentication for one user in your domain, e.g. when they lost their authenticator device. They can sign in with just their password until they set it up again.")]
    public static async Task<string> DomainDisableTwoFactor(
        [Description("Username (local part) or email address in your domain")] string username,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/disable-two-factor/{Uri.EscapeDataString(ToLocalPart(username))}"));
    }

    [McpServerTool(Name = "domain_rename_user")]
    [Description("Change a user's username (and so their email address) in your domain. Mail, settings and folders are kept. The old address stops working unless you add an alias for it. You cannot rename yourself with this tool.")]
    public static async Task<string> DomainRenameUser(
        [Description("Current username (local part) or email address")] string username,
        [Description("New username (local part only, e.g. 'jane.doe')")] string newUsername,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        var from = ToLocalPart(username);
        var to = ToLocalPart(newUsername);
        if (from.Length == 0 || to.Length == 0) return Invalid("Both the current and the new username are required.");
        if (string.Equals(from, userContext.Username, StringComparison.OrdinalIgnoreCase))
            return Invalid("Renaming the signed-in account would invalidate this session. Rename it from SmarterMail webmail instead.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/rename-user/{Uri.EscapeDataString(from)}/{Uri.EscapeDataString(to)}", new { }));
    }

    // ================================================================ writes: mail settings, forwarding, defaults

    [McpServerTool(Name = "domain_update_user_mail_settings")]
    [Description("Change one user's mail settings (see domain_get_user_mail_settings for the field names), e.g. {\"canReceiveMail\":false}, {\"maxSize\":0}, {\"throttleSettings\":{\"messagesPerHour\":500}} or {\"blockedSenders\":[\"spam@example.com\"]}. The fields you pass are merged into the current settings; lists you pass replace the current list.")]
    public static async Task<string> DomainUpdateUserMailSettings(
        [Description("Username (local part) or full email address in your domain")] string username,
        [Description("JSON object with the settings to change, using the field names from domain_get_user_mail_settings")] string settingsJson,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (ParseObject(settingsJson) is not { } changes)
            return Invalid("settingsJson must be a JSON object, e.g. {\"canReceiveMail\":false}.");

        return await Run(async () =>
        {
            var email = ToEmail(username, userContext);
            var current = await userContext.GetAsync<JsonElement>($"{Root}/user-mail/{Uri.EscapeDataString(email)}");
            if (!current.TryGetProperty("userMailSettings", out var existing) || existing.ValueKind != JsonValueKind.Object)
                return current;

            var settings = JsonNode.Parse(existing.GetRawText())!.AsObject();
            Merge(settings, changes);

            return await userContext.PostAsync<JsonElement>($"{Root}/post-user-mail",
                new JsonObject { ["email"] = email, ["userMailSettings"] = settings });
        });
    }

    [McpServerTool(Name = "domain_set_mailbox_forwarding")]
    [Description("Set where one user's incoming mail is forwarded. Only what you pass is changed. Pass an empty forwardTo list to stop forwarding. The user's mail settings must also allow forwarding (enableMailForwarding, see domain_update_user_mail_settings).")]
    public static async Task<string> DomainSetMailboxForwarding(
        [Description("Username (local part) or full email address in your domain")] string username,
        [Description("COMPLETE list of addresses to forward to, replacing the current list (optional; empty list stops forwarding)")] string[]? forwardTo = null,
        [Description("True to delete the message from this mailbox after forwarding; false keeps a copy (optional)")] bool? deleteOnForward = null,
        [Description("True to keep the original recipients on the forwarded message (optional)")] bool? keepRecipients = null,
        [Description("How spam is treated when forwarding, as SmarterMail names it (see domain_get_mailbox_forwarding for the current value) (optional)")] string? spamForwardOption = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        return await Run(async () =>
        {
            var email = ToEmail(username, userContext);
            var current = await userContext.GetAsync<JsonElement>(
                $"{Root}/mailbox-forward-list/{Uri.EscapeDataString(email)}");
            var list = current.TryGetProperty("mailboxForwardList", out var existing) && existing.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(existing.GetRawText())!.AsObject()
                : new JsonObject();

            if (forwardTo is not null)
            {
                list["forwardList"] = new JsonArray(forwardTo
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => (JsonNode)JsonValue.Create(a.Trim())!).ToArray());
            }
            if (deleteOnForward is not null) list["deleteOnForward"] = deleteOnForward.Value;
            if (keepRecipients is not null) list["keepRecipients"] = keepRecipients.Value;
            if (spamForwardOption is not null) list["spamForwardOption"] = spamForwardOption;

            return await userContext.PostAsync<JsonElement>($"{Root}/post-mailbox-forward-list",
                new JsonObject { ["email"] = email, ["mailboxForwardList"] = list });
        });
    }

    [McpServerTool(Name = "domain_update_user_defaults")]
    [Description("Change the default settings for NEW users in your domain (see domain_get_user_defaults for the field names), e.g. {\"hideFromGal\":true}. Only the fields you pass are changed. Existing users are not affected.")]
    public static async Task<string> DomainUpdateUserDefaults(
        [Description("JSON object with the defaults to change, using the field names inside 'settings' from domain_get_user_defaults")] string settingsJson,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (ParseObject(settingsJson) is not { } changes)
            return Invalid("settingsJson must be a JSON object, e.g. {\"hideFromGal\":true}.");

        // The API documents that unspecified settings remain unchanged, so only the changes are sent.
        return await Run(async () => await userContext.PostAsync<JsonElement>($"{Root}/user-defaults", changes));
    }

    // ================================================================ writes: maintenance

    [McpServerTool(Name = "domain_reindex_users")]
    [Description("Rebuild the search index of one or more mailboxes in your domain. Use when search results are missing or wrong. Runs in the background and can take a while for large mailboxes.")]
    public static async Task<string> DomainReindexUsers(
        [Description("Usernames (local parts) or email addresses in your domain")] string[] usernames,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (usernames is not { Length: > 0 }) return Invalid("At least one username is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/reindex-users", new { input = usernames.Select(ToLocalPart).ToArray() }));
    }

    [McpServerTool(Name = "domain_resync_user_devices")]
    [Description("Force the connected devices (ActiveSync and other sync clients) of one or more users in your domain to resynchronise. Use to fix devices that stopped syncing.")]
    public static async Task<string> DomainResyncUserDevices(
        [Description("Usernames (local parts) or email addresses in your domain")] string[] usernames,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (usernames is not { Length: > 0 }) return Invalid("At least one username is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/resync-users-devices", new { input = usernames.Select(ToLocalPart).ToArray() }));
    }

    [McpServerTool(Name = "domain_recalculate_mailbox_sizes")]
    [Description("Recount the disk usage of one or more mailboxes in your domain, for when the reported size is out of sync with what is actually stored.")]
    public static async Task<string> DomainRecalculateMailboxSizes(
        [Description("Usernames (local parts) or email addresses in your domain")] string[] usernames,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (usernames is not { Length: > 0 }) return Invalid("At least one username is required.");

        try
        {
            // This endpoint answers with a bare HTTP status, not JSON.
            var ok = await userContext.PostWithoutResponseAsync(
                $"{Root}/recalculate-users", new { input = usernames.Select(ToLocalPart).ToArray() });
            return JsonSerializer.Serialize(new { success = ok, count = usernames.Length });
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

    /// <summary>The paging body most list endpoints here take.</summary>
    private static JsonObject Page(string? search, int skip, int take, string? sortField, bool sortDescending)
    {
        var body = new JsonObject
        {
            ["skip"] = Math.Max(0, skip),
            ["take"] = take <= 0 ? 100 : Math.Min(take, 1000),
            ["search"] = search?.Trim() ?? string.Empty,
            ["sortDescending"] = sortDescending,
        };
        if (!string.IsNullOrWhiteSpace(sortField)) body["sortField"] = sortField.Trim();
        return body;
    }

    /// <summary>Sets a <c>{ ruleValue, enabled }</c> password rule; 0 or less turns it off.</summary>
    private static void SetRule(JsonObject policy, string name, int value)
    {
        if (policy[name] is not JsonObject rule)
            policy[name] = rule = new JsonObject();
        rule["enabled"] = value > 0;
        if (value > 0) rule["ruleValue"] = value;
    }

    /// <summary>Deep-merges <paramref name="changes"/> into <paramref name="target"/>; arrays and scalars replace.</summary>
    private static void Merge(JsonObject target, JsonObject changes)
    {
        foreach (var (key, value) in changes.ToList())
        {
            if (value is JsonObject child && target[key] is JsonObject existing)
            {
                Merge(existing, child);
                continue;
            }

            target[key] = value?.DeepClone();
        }
    }

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? Node(JsonElement element) => JsonNode.Parse(element.GetRawText());

    private static bool Succeeded(JsonElement? element) =>
        element is not { } e || e.ValueKind != JsonValueKind.Object ||
        !e.TryGetProperty("success", out var s) || s.ValueKind != JsonValueKind.False;

    private static string NormalizeProtocol(string? protocol) =>
        new string((protocol ?? string.Empty).Where(char.IsLetter).ToArray()).ToLowerInvariant() switch
        {
            "activesync" or "eas" => "activesync",
            "mapiews" or "mapi" or "ews" or "outlook" => "mapiews",
            var other => other,
        };

    /// <summary>
    /// Some of these endpoints return temporary passwords (<c>tempPassword</c>), fresh access and
    /// refresh tokens (rename-user) or other credentials. A tool result is sent on to the caller's
    /// LLM provider, so any non-empty string under a secret-like key is blanked.
    /// </summary>
    private static JsonElement Redact(JsonElement element)
    {
        if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return element;

        var node = JsonNode.Parse(element.GetRawText());
        RedactNode(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static readonly string[] SecretKeyFragments = SmarterMailMcp.Core.SecretRedactor.DefaultFragments;

    private static void RedactNode(JsonNode? node) =>
        SmarterMailMcp.Core.SecretRedactor.RedactNode(node, SecretKeyFragments);

    private static string ReadOnly() => JsonSerializer.Serialize(new
    {
        success = false,
        error = "This account is signed in read-only; domain changes are not allowed.",
    });

    private static string Invalid(string message) =>
        JsonSerializer.Serialize(new { success = false, error = message });

    /// <summary>"jane@example.com" → "jane"; "jane" → "jane".</summary>
    private static string ToLocalPart(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        var at = trimmed.IndexOf('@');
        return at >= 0 ? trimmed[..at] : trimmed;
    }

    /// <summary>"jane" → "jane@&lt;token's domain&gt;"; a full address is passed through.</summary>
    private static string ToEmail(string value, UserContext userContext)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Contains('@') || string.IsNullOrEmpty(userContext.Domain)
            ? trimmed
            : $"{trimmed}@{userContext.Domain}";
    }
}
