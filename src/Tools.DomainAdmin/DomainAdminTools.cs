using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Server.Tools;

/// <summary>
/// Domain-administrator tools. They need the signed-in account to be a domain admin; for a plain
/// user every call comes back 403 from SmarterMail. Every endpoint is under
/// <c>/api/v1/settings/domain/*</c> and acts on the signed-in domain admin's own domain, which
/// SmarterMail derives from the access token, so no tool takes a <c>domain</c> parameter.
///
/// Paths and shapes come from the SmarterMail API reference
/// (https://mail.smartertools.com/Documentation/api, "Domain Settings" controller).
///
/// Updates are read-modify-write: the current object is fetched, only the fields the caller passed
/// are changed, and the whole object is posted back, so an update never blanks a field the model
/// did not mention.
/// </summary>
[McpServerToolType]
public sealed class DomainAdminTools
{
    private const string Root = "/api/v1/settings/domain";

    // ---------------------------------------------------------------- reads

    [McpServerTool(Name = "domain_get_info", ReadOnly = true)]
    [Description("Get basic information about your domain: name, hostname, enabled state, user/alias/list counts and limits, and disk usage.")]
    public static async Task<string> DomainGetInfo(UserContext userContext) =>
        await Run(async () => await userContext.GetAsync<JsonElement>($"{Root}/data"));

    [McpServerTool(Name = "domain_get_settings", ReadOnly = true)]
    [Description("Get the full configuration of your domain (general settings, security options, catch-all, auto-clean, feature permissions). Large response. Password and secret values are redacted.")]
    public static async Task<string> DomainGetSettings(UserContext userContext) =>
        await Run(async () => Redact(await userContext.GetAsync<JsonElement>($"{Root}/domain")));

    [McpServerTool(Name = "domain_list_users", ReadOnly = true)]
    [Description("List the users in your domain with username, email address, full name, disabled state, domain-admin flag, mailbox size and last login.")]
    public static async Task<string> DomainListUsers(
        [Description("Optional case-insensitive filter matched against username, email address and full name")] string? search = null,
        UserContext userContext = null!)
    {
        return await Run(async () =>
        {
            var response = await userContext.GetAsync<JsonElement>($"{Root}/list-users");
            if (!response.TryGetProperty("userData", out var users) || users.ValueKind != JsonValueKind.Array)
                return response;

            var list = new List<object>();
            foreach (var user in users.EnumerateArray())
            {
                var userName = Str(user, "userName");
                var email = Str(user, "emailAddress");
                var fullName = Str(user, "fullName");

                if (!string.IsNullOrWhiteSpace(search) &&
                    !Matches(userName, search) && !Matches(email, search) && !Matches(fullName, search))
                    continue;

                var flags = user.TryGetProperty("securityFlags", out var f) ? f : default;
                list.Add(new
                {
                    userName,
                    emailAddress = email,
                    fullName,
                    isDisabled = Bool(flags, "isDisabled"),
                    isDomainAdmin = Bool(flags, "isDomainAdmin"),
                    isPrimaryDomainAdmin = Bool(user, "isPrimaryDomainAdmin"),
                    currentMailboxSize = Num(user, "currentMailboxSize"),
                    maxMailboxSize = Num(user, "maxMailboxSize"),
                    lastLoginTime = Str(user, "lastLoginTime"),
                });
            }

            return JsonSerializer.SerializeToElement(new { success = true, count = list.Count, users = list });
        });
    }

    [McpServerTool(Name = "domain_get_user", ReadOnly = true)]
    [Description("Get the full account details of one user in your domain: settings, permissions, security flags, mailbox size and last login.")]
    public static async Task<string> DomainGetUser(
        [Description("Username (local part, e.g. 'jane') or full email address in your domain")] string username,
        UserContext userContext)
    {
        return await Run(async () => await userContext.GetAsync<JsonElement>(
            $"{Root}/user/{Uri.EscapeDataString(ToEmail(username, userContext))}"));
    }

    [McpServerTool(Name = "domain_list_aliases", ReadOnly = true)]
    [Description("List the email aliases in your domain with their target addresses.")]
    public static async Task<string> DomainListAliases(
        [Description("Optional search term to filter aliases")] string? search = null,
        UserContext userContext = null!)
    {
        var path = string.IsNullOrWhiteSpace(search)
            ? $"{Root}/aliases"
            : $"{Root}/aliases/{Uri.EscapeDataString(search.Trim())}";
        return await Run(async () => await userContext.GetAsync<JsonElement>(path));
    }

    [McpServerTool(Name = "domain_get_alias", ReadOnly = true)]
    [Description("Get the full configuration of one alias in your domain: targets, display name, GAL visibility, internal-only, sending permission and more.")]
    public static async Task<string> DomainGetAlias(
        [Description("Alias name (local part, e.g. 'sales') or full alias address")] string name,
        UserContext userContext)
    {
        return await Run(async () => await userContext.GetAsync<JsonElement>(
            $"{Root}/alias/{Uri.EscapeDataString(ToLocalPart(name))}"));
    }

    // ---------------------------------------------------------------- writes

    [McpServerTool(Name = "domain_create_user")]
    [Description("Create a new user in your domain. Domain default settings apply to anything not given here.")]
    public static async Task<string> DomainCreateUser(
        [Description("Username (local part only, e.g. 'jane'); the address becomes jane@<your domain>")] string username,
        [Description("Initial password; must meet the domain's password requirements")] string password,
        [Description("Full display name (optional)")] string? fullName = null,
        [Description("Maximum mailbox size in MB (optional; 0 means no limit; omit to use the domain default)")] long? maxMailboxSizeMb = null,
        [Description("True to make the new user a domain administrator (default false)")] bool isDomainAdmin = false,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        return await Run(async () =>
        {
            var userData = new JsonObject
            {
                ["userName"] = ToLocalPart(username),
                ["password"] = password,
                ["securityFlags"] = new JsonObject { ["isDomainAdmin"] = isDomainAdmin },
            };
            if (fullName is not null) userData["fullName"] = fullName;
            if (maxMailboxSizeMb is not null) userData["maxMailboxSize"] = maxMailboxSizeMb.Value * 1024 * 1024;

            return await userContext.PostAsync<JsonElement>($"{Root}/user-put", new JsonObject { ["userData"] = userData });
        });
    }

    [McpServerTool(Name = "domain_update_user")]
    [Description("Update an existing user in your domain. Only the fields you pass are changed; everything else is kept.")]
    public static async Task<string> DomainUpdateUser(
        [Description("Username (local part) or full email address of the user to change")] string username,
        [Description("New full display name (optional)")] string? fullName = null,
        [Description("New password (optional); must meet the domain's password requirements")] string? password = null,
        [Description("New maximum mailbox size in MB (optional; 0 means no limit)")] long? maxMailboxSizeMb = null,
        [Description("Grant (true) or revoke (false) domain administrator rights (optional)")] bool? isDomainAdmin = null,
        [Description("New account description (optional)")] string? description = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        return await Run(async () =>
        {
            var email = ToEmail(username, userContext);
            var current = await userContext.GetAsync<JsonElement>($"{Root}/user/{Uri.EscapeDataString(email)}");
            if (!current.TryGetProperty("userData", out var existing) || existing.ValueKind != JsonValueKind.Object)
                return current;

            var userData = JsonNode.Parse(existing.GetRawText())!.AsObject();
            if (fullName is not null) userData["fullName"] = fullName;
            if (password is not null) userData["password"] = password;
            if (description is not null) userData["description"] = description;
            if (maxMailboxSizeMb is not null) userData["maxMailboxSize"] = maxMailboxSizeMb.Value * 1024 * 1024;
            if (isDomainAdmin is not null)
            {
                if (userData["securityFlags"] is not JsonObject flags)
                    userData["securityFlags"] = flags = new JsonObject();
                flags["isDomainAdmin"] = isDomainAdmin.Value;
            }

            return await userContext.PostAsync<JsonElement>($"{Root}/post-user",
                new JsonObject { ["email"] = email, ["userData"] = userData });
        });
    }

    [McpServerTool(Name = "domain_delete_users", Destructive = true)]
    [Description("Permanently delete one or more users in your domain and all of their mail data. Cannot delete the primary domain admin or yourself. This cannot be undone.")]
    public static async Task<string> DomainDeleteUsers(
        [Description("Usernames (local parts, e.g. 'jane') or full email addresses in your domain")] string[] usernames,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (usernames is not { Length: > 0 }) return Invalid("At least one username is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/users-delete", new { input = usernames.Select(ToLocalPart).ToArray() }));
    }

    [McpServerTool(Name = "domain_disable_users", Destructive = true)]
    [Description("Disable (or re-enable) one or more users in your domain. A disabled user cannot sign in.")]
    public static async Task<string> DomainDisableUsers(
        [Description("Usernames (local parts, e.g. 'jane') or full email addresses in your domain")] string[] usernames,
        [Description("True to disable the users (default), false to enable them again")] bool disable = true,
        [Description("When disabling: true to keep delivering incoming mail to the disabled mailboxes (default false)")] bool allowMail = false,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (usernames is not { Length: > 0 }) return Invalid("At least one username is required.");

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/users-disable/{(disable ? "true" : "false")}/{(allowMail ? "true" : "false")}",
            new { input = usernames.Select(ToLocalPart).ToArray() }));
    }

    [McpServerTool(Name = "domain_create_alias")]
    [Description("Create an email alias in your domain that delivers mail to one or more target addresses.")]
    public static async Task<string> DomainCreateAlias(
        [Description("Alias name (local part only, e.g. 'sales')")] string name,
        [Description("Email addresses that receive mail sent to the alias")] string[] targets,
        [Description("Display name (optional)")] string? displayName = null,
        [Description("Description of the alias purpose (optional)")] string? description = null,
        [Description("True to only accept mail from inside the domain (default false)")] bool internalOnly = false,
        [Description("True to let target users send mail as this alias (default false)")] bool allowSending = false,
        [Description("True to hide the alias from the Global Address List (default false)")] bool hideFromGAL = false,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();
        if (targets is not { Length: > 0 }) return Invalid("At least one target address is required.");

        return await Run(async () =>
        {
            var alias = new JsonObject
            {
                ["name"] = ToLocalPart(name),
                ["aliasTargetList"] = new JsonArray(targets.Select(t => (JsonNode)t.Trim()).ToArray()),
                ["internalOnly"] = internalOnly,
                ["allowSending"] = allowSending,
                ["hideFromGAL"] = hideFromGAL,
                ["isEnabled"] = true,
            };
            if (displayName is not null) alias["displayName"] = displayName;
            if (description is not null) alias["description"] = description;

            return await userContext.PostAsync<JsonElement>($"{Root}/alias-put",
                new JsonObject { ["alias"] = alias, ["oldName"] = "" });
        });
    }

    [McpServerTool(Name = "domain_update_alias")]
    [Description("Update an existing alias in your domain. Only the fields you pass are changed. Passing targets replaces the whole target list.")]
    public static async Task<string> DomainUpdateAlias(
        [Description("Current alias name (local part, e.g. 'sales')")] string name,
        [Description("New alias name to rename it (optional)")] string? newName = null,
        [Description("Replacement list of target addresses (optional; replaces the existing list)")] string[]? targets = null,
        [Description("New display name (optional)")] string? displayName = null,
        [Description("New description (optional)")] string? description = null,
        [Description("Only accept mail from inside the domain (optional)")] bool? internalOnly = null,
        [Description("Let target users send mail as this alias (optional)")] bool? allowSending = null,
        [Description("Hide the alias from the Global Address List (optional)")] bool? hideFromGAL = null,
        [Description("Enable (true) or disable (false) the alias; a disabled alias bounces mail (optional)")] bool? isEnabled = null,
        UserContext userContext = null!)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        return await Run(async () =>
        {
            var oldName = ToLocalPart(name);
            var current = await userContext.GetAsync<JsonElement>($"{Root}/alias/{Uri.EscapeDataString(oldName)}");
            if (!current.TryGetProperty("alias", out var existing) || existing.ValueKind != JsonValueKind.Object)
                return current;

            var alias = JsonNode.Parse(existing.GetRawText())!.AsObject();
            if (newName is not null) alias["name"] = ToLocalPart(newName);
            if (targets is not null) alias["aliasTargetList"] = new JsonArray(targets.Select(t => (JsonNode)t.Trim()).ToArray());
            if (displayName is not null) alias["displayName"] = displayName;
            if (description is not null) alias["description"] = description;
            if (internalOnly is not null) alias["internalOnly"] = internalOnly.Value;
            if (allowSending is not null) alias["allowSending"] = allowSending.Value;
            if (hideFromGAL is not null) alias["hideFromGAL"] = hideFromGAL.Value;
            if (isEnabled is not null) alias["isEnabled"] = isEnabled.Value;

            return await userContext.PostAsync<JsonElement>($"{Root}/alias",
                new JsonObject { ["alias"] = alias, ["oldName"] = oldName });
        });
    }

    [McpServerTool(Name = "domain_delete_alias", Destructive = true)]
    [Description("Permanently delete an alias from your domain. Mail sent to it will no longer be delivered.")]
    public static async Task<string> DomainDeleteAlias(
        [Description("Alias name (local part, e.g. 'sales')")] string name,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode) return ReadOnly();

        return await Run(async () => await userContext.PostAsync<JsonElement>(
            $"{Root}/alias-delete/{Uri.EscapeDataString(ToLocalPart(name))}"));
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<string> Run(Func<Task<JsonElement>> call)
    {
        try
        {
            return JsonSerializer.Serialize(await call());
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

    /// <summary>
    /// The domain settings object carries credentials (e.g. <c>ldapPassword</c>). A tool result is
    /// sent on to the user's LLM provider, so any string under a password/secret-like key is blanked.
    /// </summary>
    private static JsonElement Redact(JsonElement element)
    {
        var node = JsonSerializer.SerializeToNode(element);
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

    private static bool Matches(string? value, string search) =>
        value is not null && value.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);

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
