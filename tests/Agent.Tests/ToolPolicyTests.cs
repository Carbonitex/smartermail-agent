using System.Text.Json;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Mcp;

namespace SmarterMailAgent.Tests;

public sealed class ToolPolicyTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private ToolCatalog Catalog => fixture.Catalog;

    private static readonly string[] MailboxWrites =
    [
        "block_senders", "create_contact", "create_contact_group", "create_content_filter",
        "create_content_filter_simple", "create_draft", "create_folder", "create_note",
        "delete_calendar_event", "delete_contact_group", "delete_contacts", "delete_content_filters",
        "delete_note", "delete_task", "forward_email", "move_emails", "new_or_update_calendar_event",
        "new_or_update_task", "remove_emails", "remove_folder", "remove_items", "reply_to_email",
        "respond_to_meeting", "run_content_filters", "send_draft", "send_email",
        "send_email_with_attachments", "set_email_properties", "set_trusted_senders", "unblock_senders",
        "update_contact", "update_contact_group", "update_content_filter", "update_folder",
        "update_note", "upload_attachment",
    ];

    private static readonly string[] MailboxReads =
    [
        "check_sender_blocked", "check_sender_trusted", "download_email_attachment",
        "expand_contact_group", "get_calendar_event", "get_calendar_events", "get_contact",
        "get_contact_group", "get_contact_groups", "get_contacts", "get_content_filters",
        "get_email_attachments", "get_email_message", "get_emails", "get_global_address_book",
        "get_note", "get_notes", "get_task_details", "get_tasks", "get_user_data",
        "list_folder_info_by_type", "read_email_part", "search_items",
    ];

    private static readonly string[] SysAdminReads =
    [
        "get_acme_certificates", "get_blocked_ips", "get_connections", "get_connections_count",
        "get_dashboard_stats", "get_dkim_settings", "get_domain_info", "get_domain_settings",
        "get_domains", "get_inactive_users", "get_ip_access_rules", "get_rspamd_servers",
        "get_server_version", "get_services", "get_smtp_auth_bypass", "get_smtp_block_rules",
        "get_spam_assassin_servers", "get_spool_message_count", "get_spool_message_counts",
        "get_spool_messages", "get_ssl_certificate_counts", "get_ssl_certificates",
        "get_throttled_counts", "get_throttled_domains", "get_throttled_users",
        "get_troubleshooting_counts", "list_users", "search_log_files", "search_users",
    ];

    private static readonly string[] SysAdminWrites =
    [
        "add_ip_access_rule", "create_dkim_rollover_key", "create_domain", "delete_dkim_rollover_key",
        "delete_domain", "delete_ip_access_rule", "delete_spool_message", "delete_spool_messages",
        "delete_ssl_certificate", "delete_users", "disable_dkim", "disable_users",
        "drop_ip_connections", "drop_user_connections", "enable_dkim", "kill_user_sessions",
        "refresh_acme_certificates", "reload_domain", "rename_domain", "renew_certificates_now",
        "reset_all_spool_messages", "reset_spool_messages", "reset_throttled_domain",
        "reset_throttled_user", "set_dkim_settings", "start_services", "stop_services",
        "unblock_ips", "upload_ssl_certificate",
    ];

    private static readonly string[] DomainReads =
    [
        "domain_check_address_available", "domain_get_account_counts", "domain_get_alias",
        "domain_get_dkim", "domain_get_event_catalog", "domain_get_forward_blacklist",
        "domain_get_info", "domain_get_last_login_times", "domain_get_mailbox_forwarding",
        "domain_get_mailing_list", "domain_get_mailing_list_messages",
        "domain_get_mailing_list_subscriber", "domain_get_password_policy", "domain_get_permissions",
        "domain_get_security_settings", "domain_get_settings", "domain_get_signature_mappings",
        "domain_get_spam_settings", "domain_get_user", "domain_get_user_connections",
        "domain_get_user_defaults", "domain_get_user_group", "domain_get_user_indexing_status",
        "domain_get_user_login_ips", "domain_get_user_mail_settings", "domain_get_user_statuses",
        "domain_list_aliases", "domain_list_content_filters", "domain_list_domain_aliases",
        "domain_list_event_hooks", "domain_list_mailing_list_posters", "domain_list_mailing_lists",
        "domain_list_password_issues", "domain_list_protocol_mailboxes",
        "domain_list_shared_resources", "domain_list_signatures", "domain_list_spam_checks",
        "domain_list_subscriber_fields", "domain_list_user_groups", "domain_list_users",
        "domain_search_accounts", "domain_search_mailing_list_subscribers", "domain_test_dkim_dns",
        "domain_verify_domain_alias_mx",
    ];

    private static readonly string[] DomainWrites =
    [
        "domain_add_content_filter", "domain_add_domain_alias", "domain_add_mailing_list_posters",
        "domain_add_mailing_list_subscribers", "domain_clear_subscriber_bounces",
        "domain_create_alias", "domain_create_dkim_rollover", "domain_create_mailing_list",
        "domain_create_shared_resource", "domain_create_signature", "domain_create_user",
        "domain_create_user_group", "domain_delete_alias", "domain_delete_content_filters",
        "domain_delete_dkim_rollover", "domain_delete_domain_aliases", "domain_delete_event_hooks",
        "domain_delete_mailing_lists", "domain_delete_shared_resource", "domain_delete_signature",
        "domain_delete_subscriber_fields", "domain_delete_user_groups", "domain_delete_users",
        "domain_disable_dkim", "domain_disable_two_factor", "domain_disable_users",
        "domain_disconnect_users", "domain_enable_dkim", "domain_expire_user_passwords",
        "domain_move_content_filter", "domain_recalculate_mailbox_sizes", "domain_reindex_users",
        "domain_remove_mailing_list_posters", "domain_remove_mailing_list_subscribers",
        "domain_rename_domain_alias", "domain_rename_user", "domain_resync_user_devices",
        "domain_save_event_hook", "domain_save_subscriber_field", "domain_send_mailing_list_digest",
        "domain_set_catch_all", "domain_set_event_hook_enabled", "domain_set_mailbox_forwarding",
        "domain_set_protocol_access", "domain_set_signature_mapping",
        "domain_set_user_group_membership", "domain_update_alias", "domain_update_content_filter",
        "domain_update_dkim_settings", "domain_update_mailing_list",
        "domain_update_mailing_list_message", "domain_update_mailing_list_subscriber",
        "domain_update_password_policy", "domain_update_settings", "domain_update_shared_resource",
        "domain_update_signature", "domain_update_spam_settings", "domain_update_user",
        "domain_update_user_defaults", "domain_update_user_group", "domain_update_user_mail_settings",
        "domain_update_whitelist", "domain_verify_dkim",
    ];

    private string[] Names(ToolScope scope, bool write) => Catalog.Entries
        .Where(e => e.Scope == scope && e.Write == write)
        .Select(e => e.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();

    [Fact]
    public void Every_registered_tool_has_a_scope_and_category()
    {
        Assert.Equal(59 + 107 + 58, Catalog.Count);   // mailbox + domain admin + sysadmin
        Assert.All(Catalog.Entries, e =>
        {
            Assert.Contains(e.Group, ToolPolicy.Groups);
            Assert.False(string.IsNullOrWhiteSpace(e.Category));
        });
    }

    [Fact]
    public void Every_tool_class_in_the_assembly_is_listed_in_the_policy()
    {
        var toolTypes = typeof(ToolPolicy).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), false).Length > 0);
        Assert.All(toolTypes, t => Assert.Contains(ToolPolicy.Groups, g => g.Type == t));
    }

    [Fact]
    public void Excluded_log_analysis_tools_are_not_registered()
    {
        Assert.False(Catalog.TryGet("analyze_logs", out _));
        Assert.False(Catalog.TryGet("get_analysis_status", out _));
        Assert.True(Catalog.TryGet("search_log_files", out var search));
        Assert.Equal(ToolScope.SysAdmin, search.Scope);
    }

    [Fact]
    public void Mailbox_read_and_write_sets_are_pinned()
    {
        Assert.Equal(MailboxWrites.Order(StringComparer.Ordinal), Names(ToolScope.Mailbox, write: true));
        Assert.Equal(MailboxReads.Order(StringComparer.Ordinal), Names(ToolScope.Mailbox, write: false));
    }

    [Fact]
    public void SysAdmin_read_and_write_sets_are_pinned()
    {
        Assert.Equal(SysAdminWrites.Order(StringComparer.Ordinal), Names(ToolScope.SysAdmin, write: true));
        Assert.Equal(SysAdminReads.Order(StringComparer.Ordinal), Names(ToolScope.SysAdmin, write: false));
    }

    [Fact]
    public void DomainAdmin_read_and_write_sets_are_pinned()
    {
        Assert.Equal(DomainWrites.Order(StringComparer.Ordinal), Names(ToolScope.DomainAdmin, write: true));
        Assert.Equal(DomainReads.Order(StringComparer.Ordinal), Names(ToolScope.DomainAdmin, write: false));
    }

    /// <summary>
    /// The hand-written <c>ToolPolicy.AdminReadNames</c> as it stood before the read/write split moved
    /// onto the tools' own <c>[McpServerTool(ReadOnly = true)]</c> annotations. Kept verbatim so the
    /// move provably lost and gained nothing; change it only together with an annotation.
    /// </summary>
    private static readonly string[] LegacyAdminReadNames =
    [
        // SysAdmin: Server
        "get_server_version", "get_services", "get_dashboard_stats", "get_troubleshooting_counts",
        // SysAdmin: Domains
        "get_domains", "get_domain_info", "get_domain_settings",
        // SysAdmin: Users
        "list_users", "search_users", "get_inactive_users",
        // SysAdmin: Security
        "get_blocked_ips", "get_ip_access_rules", "get_smtp_block_rules", "get_smtp_auth_bypass",
        "get_spam_assassin_servers", "get_rspamd_servers",
        // SysAdmin: Spool
        "get_spool_message_count", "get_spool_message_counts", "get_spool_messages",
        // SysAdmin: Certificates
        "get_ssl_certificates", "get_ssl_certificate_counts", "get_acme_certificates",
        // SysAdmin: DKIM
        "get_dkim_settings",
        // SysAdmin: Monitoring
        "get_throttled_users", "get_throttled_domains", "get_throttled_counts",
        "get_connections", "get_connections_count", "search_log_files",

        // DomainAdmin
        "domain_get_info", "domain_get_settings", "domain_list_users", "domain_get_user",
        "domain_list_aliases", "domain_get_alias",

        // DomainAdmin: Domain users
        "domain_list_user_groups", "domain_get_user_group", "domain_get_account_counts",
        "domain_search_accounts", "domain_get_user_statuses", "domain_get_last_login_times",
        "domain_get_user_connections", "domain_list_protocol_mailboxes", "domain_get_password_policy",
        "domain_list_password_issues", "domain_get_user_mail_settings",
        "domain_get_mailbox_forwarding", "domain_get_user_defaults", "domain_get_user_indexing_status",

        // DomainAdmin: Domain routing
        "domain_list_domain_aliases", "domain_verify_domain_alias_mx",
        "domain_check_address_available", "domain_get_forward_blacklist", "domain_list_signatures",
        "domain_get_signature_mappings", "domain_list_shared_resources", "domain_list_event_hooks",
        "domain_get_event_catalog", "domain_get_permissions",

        // DomainAdmin: Domain security
        "domain_get_dkim", "domain_test_dkim_dns", "domain_get_spam_settings",
        "domain_list_spam_checks", "domain_get_security_settings", "domain_get_user_login_ips",
        "domain_list_content_filters",

        // DomainAdmin: Mailing lists
        "domain_list_mailing_lists", "domain_get_mailing_list",
        "domain_search_mailing_list_subscribers", "domain_get_mailing_list_subscriber",
        "domain_list_mailing_list_posters", "domain_get_mailing_list_messages",
        "domain_list_subscriber_fields",
    ];

    [Fact]
    public void Admin_read_set_from_annotations_equals_the_legacy_list()
    {
        var annotated = Catalog.Entries
            .Where(e => e.Scope != ToolScope.Mailbox && ToolPolicy.IsMarkedReadOnly(e.Tool))
            .Select(e => e.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(LegacyAdminReadNames.Order(StringComparer.Ordinal), annotated);
    }

    [Fact]
    public void Mailbox_annotations_agree_with_the_name_rule()
    {
        var mailbox = Catalog.Entries.Where(e => e.Scope == ToolScope.Mailbox).ToList();
        Assert.Equal(59, mailbox.Count);
        Assert.All(mailbox, e => Assert.True(
            ToolPolicy.IsMarkedReadOnly(e.Tool) != ToolPolicy.MailboxNameSaysWrite(e.Name),
            $"'{e.Name}': ReadOnly annotation and the mailbox name rule disagree"));
        Assert.Equal(23, mailbox.Count(e => ToolPolicy.IsMarkedReadOnly(e.Tool)));
    }

    [Theory]
    [InlineData("get_something_new", ToolScope.SysAdmin)]
    [InlineData("list_everything", ToolScope.SysAdmin)]
    [InlineData("domain_get_new_thing", ToolScope.DomainAdmin)]
    [InlineData("get_something_new", ToolScope.Mailbox)]
    public void Unannotated_tools_fail_closed(string name, ToolScope scope) =>
        Assert.True(ToolPolicy.IsWrite(name, scope, markedReadOnly: false));

    [Fact]
    public void Mailbox_name_rule_backs_up_the_annotation()
    {
        Assert.False(ToolPolicy.IsWrite("get_something_new", ToolScope.Mailbox, markedReadOnly: true));
        Assert.True(ToolPolicy.IsWrite("send_something", ToolScope.Mailbox, markedReadOnly: true));
        Assert.True(ToolPolicy.IsWrite("block_senders", ToolScope.Mailbox, markedReadOnly: true));
    }

    [Fact]
    public void Admin_scopes_go_by_the_annotation_alone()
    {
        Assert.False(ToolPolicy.IsWrite("send_something", ToolScope.SysAdmin, markedReadOnly: true));
        Assert.False(ToolPolicy.IsWrite("domain_set_thing", ToolScope.DomainAdmin, markedReadOnly: true));
    }

    [Fact]
    public void No_tool_schema_has_an_account_or_userContext_property()
    {
        Assert.All(Catalog.Entries, e =>
        {
            var schema = e.Tool.ProtocolTool.InputSchema;
            if (schema.TryGetProperty("properties", out var properties))
            {
                Assert.False(properties.TryGetProperty("account", out _), $"{e.Name} has an 'account' parameter");
                Assert.False(properties.TryGetProperty("userContext", out _), $"{e.Name} exposes userContext");
            }
        });
    }

    [Theory]
    [InlineData(AccountRole.User, ToolScope.Mailbox, true)]
    [InlineData(AccountRole.User, ToolScope.DomainAdmin, false)]
    [InlineData(AccountRole.User, ToolScope.SysAdmin, false)]
    [InlineData(AccountRole.DomainAdmin, ToolScope.Mailbox, true)]
    [InlineData(AccountRole.DomainAdmin, ToolScope.DomainAdmin, true)]
    [InlineData(AccountRole.DomainAdmin, ToolScope.SysAdmin, false)]
    [InlineData(AccountRole.SysAdmin, ToolScope.Mailbox, false)]
    [InlineData(AccountRole.SysAdmin, ToolScope.DomainAdmin, false)]
    [InlineData(AccountRole.SysAdmin, ToolScope.SysAdmin, true)]
    public void Roles_reach_their_scopes(AccountRole role, ToolScope scope, bool allowed) =>
        Assert.Equal(allowed, ToolPolicy.RoleAllows(role, scope));

    [Fact]
    public void Session_views_list_only_eligible_tools()
    {
        var user = new FakeAccount("me@example.com", AccountRole.User, ReadOnly: true);
        var sys = new FakeAccount("sysadmin:admin@mail.example.com", AccountRole.SysAdmin, ReadOnly: true);
        var domain = new FakeAccount("boss@example.com", AccountRole.DomainAdmin, ReadOnly: false);

        Assert.Equal(23, Catalog.List([user]).Count);
        Assert.Equal(59, Catalog.List([new FakeAccount("me@example.com", AccountRole.User, false)]).Count);
        Assert.Equal(29, Catalog.List([sys]).Count);
        Assert.Equal(59 + 107, Catalog.List([domain]).Count);

        var all = Catalog.List([user, sys, domain]);
        Assert.Equal(59 + 107 + 29, all.Count);

        // get_emails: user and domain admin can both read → required, two handles.
        var getEmails = all.Single(t => t.Name == "get_emails");
        Assert.Equal(["me@example.com", "boss@example.com"], Enum(getEmails.InputSchema.ToJsonString()));
        Assert.Contains("account", Required(getEmails.InputSchema.ToJsonString()));

        // send_email: only the read-write domain admin → optional, one handle.
        var sendEmail = all.Single(t => t.Name == "send_email");
        Assert.Equal(["boss@example.com"], Enum(sendEmail.InputSchema.ToJsonString()));
        Assert.DoesNotContain("account", Required(sendEmail.InputSchema.ToJsonString()));
        Assert.True(sendEmail.Write);
        Assert.Equal("Mail", sendEmail.Category);
        Assert.Equal("Mailbox", sendEmail.Scope);

        // delete_domain: the only sysadmin is read-only → hidden.
        Assert.DoesNotContain(all, t => t.Name == "delete_domain");

        Assert.Equal(all.Select(t => t.Name), Catalog.ListProtocolTools([user, sys, domain]).Select(t => t.Name));
    }

    private static string[] Enum(string schema) =>
        JsonDocument.Parse(schema).RootElement.GetProperty("properties").GetProperty("account")
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string[] Required(string schema) =>
        JsonDocument.Parse(schema).RootElement.TryGetProperty("required", out var r)
            ? r.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : [];
}
