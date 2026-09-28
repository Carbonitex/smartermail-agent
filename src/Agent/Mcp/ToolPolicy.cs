using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using SmarterMailAgent.Auth;
using SmarterMailMcp.Server.Tools;
using SmarterMailMcp.SystemAdmin.Tools;

namespace SmarterMailAgent.Mcp;

/// <summary>Which kind of account a tool is for.</summary>
public enum ToolScope
{
    /// <summary>The user's own mailbox (the copied smartermail-mcp-client tools).</summary>
    Mailbox,

    /// <summary>The signed-in domain admin's own domain (the <c>domain_*</c> tools from smartermail-mcp-client).</summary>
    DomainAdmin,

    /// <summary>The whole server (the copied smartermail-mcp-admin tools).</summary>
    SysAdmin,
}

/// <summary>One registered tool class: its scope and the category the UI filters by.</summary>
public sealed record ToolGroup(Type Type, ToolScope Scope, string Category);

/// <summary>
/// The whole tool policy in one place: which classes are registered, what scope and category each
/// has, which roles may use a scope, which tools write, and how the <c>account</c> argument is
/// resolved. Everything here is pure so it can be tested without a SmarterMail server.
/// </summary>
public static class ToolPolicy
{
    /// <summary>
    /// Every tool class the server registers. Nothing is picked up from the assembly by scanning:
    /// a class that is not listed here is not a tool, and a tool whose class is not listed here
    /// fails startup (see <see cref="ToolCatalog"/>).
    /// </summary>
    public static readonly IReadOnlyList<ToolGroup> Groups =
    [
        new(typeof(MailTools), ToolScope.Mailbox, "Mail"),
        new(typeof(UserTools), ToolScope.Mailbox, "Mail"),
        new(typeof(CalendarTools), ToolScope.Mailbox, "Calendar"),
        new(typeof(ContactTools), ToolScope.Mailbox, "Contacts"),
        new(typeof(ContactGroupTools), ToolScope.Mailbox, "Contacts"),
        new(typeof(TaskTools), ToolScope.Mailbox, "Tasks"),
        new(typeof(NoteTools), ToolScope.Mailbox, "Notes"),
        new(typeof(FolderTools), ToolScope.Mailbox, "Folders"),
        new(typeof(SettingsTools), ToolScope.Mailbox, "Settings"),

        new(typeof(DomainAdminTools), ToolScope.DomainAdmin, "Domain"),
        new(typeof(DomainUserTools), ToolScope.DomainAdmin, "Domain users"),
        new(typeof(DomainRoutingTools), ToolScope.DomainAdmin, "Domain routing"),
        new(typeof(DomainSecurityTools), ToolScope.DomainAdmin, "Domain security"),
        new(typeof(DomainMailingListTools), ToolScope.DomainAdmin, "Mailing lists"),

        new(typeof(ServerTools), ToolScope.SysAdmin, "Server"),
        new(typeof(DomainTools), ToolScope.SysAdmin, "Domains"),
        new(typeof(UserAdminTools), ToolScope.SysAdmin, "Users"),
        new(typeof(SecurityTools), ToolScope.SysAdmin, "Security"),
        new(typeof(SpoolTools), ToolScope.SysAdmin, "Spool"),
        new(typeof(CertificateTools), ToolScope.SysAdmin, "Certificates"),
        new(typeof(DkimTools), ToolScope.SysAdmin, "DKIM"),
        new(typeof(MonitoringTools), ToolScope.SysAdmin, "Monitoring"),
        new(typeof(LogSearchTools), ToolScope.SysAdmin, "Monitoring"),
    ];

    /// <summary>Which account roles a scope is open to. A sysadmin has no mailbox.</summary>
    public static bool RoleAllows(AccountRole role, ToolScope scope) => scope switch
    {
        ToolScope.Mailbox => role is AccountRole.User or AccountRole.DomainAdmin,
        ToolScope.DomainAdmin => role is AccountRole.DomainAdmin,
        ToolScope.SysAdmin => role is AccountRole.SysAdmin,
        _ => false,
    };

    // ------------------------------------------------------------------ write classification

    /// <summary>
    /// Mailbox tools only: a name starting with any of these mutates the user's mailbox. The tool's
    /// own <c>ReadOnly</c> annotation decides first; this rule is belt and braces on top of it, so
    /// a mailbox tool mis-annotated as read-only but named like a write still counts as a write.
    /// </summary>
    public static readonly string[] MailboxWritePrefixes =
    [
        "send_", "delete_", "create_", "update_", "move_", "upload_", "add_",
        "remove_", "set_", "mark_", "respond_", "reply_", "forward_",
    ];

    /// <summary>
    /// Mailbox write tools whose names do not match <see cref="MailboxWritePrefixes"/>: every
    /// method in the mailbox tools that guards itself with <c>if (userContext.ReadOnlyMode)</c>
    /// and is not already caught by a prefix.
    /// </summary>
    public static readonly HashSet<string> MailboxWriteNames = new(StringComparer.Ordinal)
    {
        "new_or_update_calendar_event",
        "new_or_update_task",
        "block_senders",
        "unblock_senders",
        "run_content_filters",
    };

    /// <summary>
    /// Whether the tool declares itself read-only: <c>[McpServerTool(ReadOnly = true)]</c>, which the
    /// SDK surfaces as <c>ProtocolTool.Annotations.ReadOnlyHint</c>. The annotation lives on the tool
    /// in its shared library (src/Tools.*), so every host that registers the tool classifies it the
    /// same way.
    /// </summary>
    public static bool IsMarkedReadOnly(McpServerTool tool) =>
        tool.ProtocolTool.Annotations?.ReadOnlyHint == true;

    /// <summary>The mailbox name rule on its own (prefixes plus <see cref="MailboxWriteNames"/>).</summary>
    public static bool MailboxNameSaysWrite(string name) =>
        MailboxWriteNames.Contains(name) ||
        MailboxWritePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    public static bool IsWrite(McpServerTool tool, ToolScope scope) =>
        IsWrite(tool.ProtocolTool.Name, scope, IsMarkedReadOnly(tool));

    /// <summary>
    /// Fails closed in every scope: a tool is a read only if it is annotated <c>ReadOnly = true</c>.
    /// A tool newly added to a shared library is therefore a write until someone marks it read-only
    /// on purpose. Admin scopes go by the annotation alone: none of the sysadmin tools check
    /// <c>ReadOnlyMode</c> themselves, and many writes (<c>enable_dkim</c>, <c>stop_services</c>,
    /// <c>kill_user_sessions</c>, …) match no write prefix, so a name rule would not be safe there.
    /// Mailbox tools must also pass the name rule (<see cref="MailboxNameSaysWrite"/>).
    /// </summary>
    public static bool IsWrite(string name, ToolScope scope, bool markedReadOnly) => scope switch
    {
        ToolScope.Mailbox => !markedReadOnly || MailboxNameSaysWrite(name),
        _ => !markedReadOnly,
    };

    // ------------------------------------------------------------------ eligibility

    /// <summary>
    /// The accounts that may run a tool: the role allows its scope, and the account is read-write
    /// or the tool is a read. A tool with none is hidden.
    /// </summary>
    public static List<T> Eligible<T>(ToolScope scope, bool write, IEnumerable<T> accounts)
        where T : IToolAccount =>
        accounts.Where(a => RoleAllows(a.Role, scope) && (!write || !a.ReadOnly)).ToList();

    public const string AccountProperty = "account";

    /// <summary>
    /// Adds the <c>account</c> property to a clone of <paramref name="inputSchema"/>:
    /// <c>{ type: "string", enum: [eligible handles], description }</c>, required when more than one
    /// account is eligible. A single-account session gets the schema back untouched, so it behaves
    /// exactly as a one-mailbox session always has.
    /// </summary>
    public static JsonElement InjectAccount(
        JsonElement inputSchema, IReadOnlyList<string> eligibleHandles, int sessionAccountCount)
    {
        if (sessionAccountCount <= 1 || eligibleHandles.Count == 0)
            return inputSchema;

        var schema = JsonNode.Parse(inputSchema.GetRawText()) as JsonObject ?? new JsonObject();
        schema["type"] ??= "object";

        if (schema["properties"] is not JsonObject properties)
        {
            properties = new JsonObject();
            schema["properties"] = properties;
        }

        var description = eligibleHandles.Count == 1
            ? $"Which signed-in account to run this as. Only '{eligibleHandles[0]}' can; it is used if omitted."
            : "Which signed-in account to run this as. Required: more than one account can run this tool.";

        properties[AccountProperty] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(eligibleHandles.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()),
            ["description"] = description,
        };

        if (eligibleHandles.Count > 1)
        {
            if (schema["required"] is not JsonArray required)
            {
                required = new JsonArray();
                schema["required"] = required;
            }

            if (!required.Any(n => n?.GetValue<string>() == AccountProperty))
                required.Add(AccountProperty);
        }

        return JsonSerializer.SerializeToElement(schema);
    }

    // ------------------------------------------------------------------ account resolution

    public abstract record Resolution<T> where T : IToolAccount
    {
        public sealed record Ok(T Account) : Resolution<T>;

        /// <summary>A mistake the model can fix: the message lists the valid handles.</summary>
        public sealed record Invalid(string Message) : Resolution<T>;

        /// <summary>A write tool on a read-only account. REST answers 403, MCP isError.</summary>
        public sealed record ReadOnly(string Message) : Resolution<T>;
    }

    /// <summary>
    /// Picks the account a call runs as from the <c>account</c> argument. Omitting it is fine when
    /// exactly one account can run the tool.
    /// </summary>
    public static Resolution<T> Resolve<T>(
        string toolName, ToolScope scope, bool write, IReadOnlyList<T> accounts, string? requested)
        where T : IToolAccount
    {
        var byRole = accounts.Where(a => RoleAllows(a.Role, scope)).ToList();
        var eligible = byRole.Where(a => !write || !a.ReadOnly).ToList();
        var valid = eligible.Count == 0 ? "none" : string.Join(", ", eligible.Select(a => $"'{a.Handle}'"));

        if (string.IsNullOrEmpty(requested))
        {
            if (eligible.Count == 1)
                return new Resolution<T>.Ok(eligible[0]);

            if (eligible.Count > 1)
            {
                return new Resolution<T>.Invalid(
                    $"'{toolName}' needs an '{AccountProperty}' argument: more than one signed-in account " +
                    $"can run it. Valid accounts: {valid}.");
            }

            if (byRole.Count > 0)
                return new Resolution<T>.ReadOnly(ReadOnlyMessage(toolName, scope, byRole[0].Handle, byRole.Count));

            return new Resolution<T>.Invalid(
                $"No signed-in account can use '{toolName}': it needs {ScopeNeeds(scope)}.");
        }

        var account = accounts.FirstOrDefault(a =>
            string.Equals(a.Handle, requested, StringComparison.OrdinalIgnoreCase));

        if (account is null)
        {
            return new Resolution<T>.Invalid(
                $"Unknown account '{requested}'. Valid accounts for '{toolName}': {valid}.");
        }

        if (!RoleAllows(account.Role, scope))
        {
            return new Resolution<T>.Invalid(
                $"Account '{account.Handle}' is {RoleName(account.Role)} and cannot use '{toolName}', " +
                $"which needs {ScopeNeeds(scope)}. Valid accounts for '{toolName}': {valid}.");
        }

        if (write && account.ReadOnly)
            return new Resolution<T>.ReadOnly(ReadOnlyMessage(toolName, scope, account.Handle, 1));

        return new Resolution<T>.Ok(account);
    }

    public static string ReadOnlyMessage(string toolName, ToolScope scope, string handle, int readOnlyAccounts)
    {
        var what = scope switch
        {
            ToolScope.Mailbox => "the mailbox",
            ToolScope.DomainAdmin => "the domain",
            _ => "the server",
        };

        return readOnlyAccounts > 1
            ? $"'{toolName}' changes {what} and every account that could run it is read-only."
            : $"'{toolName}' changes {what} and account '{handle}' is read-only.";
    }

    private static string ScopeNeeds(ToolScope scope) => scope switch
    {
        ToolScope.Mailbox => "a mailbox account (a user or domain admin)",
        ToolScope.DomainAdmin => "a domain admin account",
        _ => "a system admin account",
    };

    private static string RoleName(AccountRole role) => role switch
    {
        AccountRole.DomainAdmin => "a domain admin account",
        AccountRole.SysAdmin => "a system admin account",
        _ => "a user account",
    };
}
