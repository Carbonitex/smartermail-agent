/**
 * retrieval-holdout.mjs — extra requests for `eval.mjs --retrieval-only --holdout`
 * (dev only). Written after tasks.mjs and kept apart from it, so retrieval
 * changes tuned on the task suite can be checked on prompts they were not
 * tuned on. Batch 1 was written before the retrieval changes, and some synonyms
 * were then added for its misses; batch 2 was written afterwards and is the clean
 * check. No mocks, no scoring of a model: each entry only names the tool
 * groups a correct run needs to be offered (any one tool of a group counts).
 *
 *   { id, role, prompt, groups: [[names]] }
 */

export const HOLDOUT = [
  /* ---------------------------------------------------------- User */
  { id: 'h-u-flagged', role: 'user', prompt: 'Which emails have I flagged?', groups: [['get_emails', 'search_items']] },
  { id: 'h-u-meetings-week', role: 'user', prompt: 'What meetings do I have next week?', groups: [['get_calendar_events']] },
  { id: 'h-u-mark-read', role: 'user', prompt: 'Mark every unread message in my inbox as read.', groups: [['get_emails', 'search_items'], ['set_email_properties']] },
  { id: 'h-u-block', role: 'user', prompt: 'Stop mail from spammer@example.net reaching me.', groups: [['block_senders', 'create_content_filter_simple', 'create_content_filter']] },
  { id: 'h-u-new-folder', role: 'user', prompt: 'Make a new folder called Projects.', groups: [['create_folder', 'update_folder']] },
  { id: 'h-u-phone', role: 'user', prompt: 'What is Grace Example\'s phone number?', groups: [['get_contacts', 'search_items', 'get_global_address_book', 'get_contact']] },
  { id: 'h-u-download-pdf', role: 'user', prompt: 'Get me the PDF that was attached to the invoice email from billing@example.org.', groups: [['get_emails', 'search_items'], ['get_email_attachments', 'download_email_attachment']] },
  { id: 'h-u-forward-alice', role: 'user', prompt: 'Forward Alice\'s message about the budget to frank@example.org.', groups: [['get_emails', 'search_items'], ['forward_email']] },
  { id: 'h-u-accept-invite', role: 'user', prompt: 'Accept the meeting invitation from Dave for Tuesday.', groups: [['get_emails', 'search_items', 'get_calendar_events'], ['respond_to_meeting']] },
  { id: 'h-u-todo-list', role: 'user', prompt: 'What is on my to-do list?', groups: [['get_tasks']] },
  { id: 'h-u-note', role: 'user', prompt: 'Jot down a note: call the plumber on Monday.', groups: [['create_note']] },
  { id: 'h-u-trash-promo', role: 'user', prompt: 'Throw away all the promotional mail from promo@example.net.', groups: [['get_emails', 'search_items'], ['remove_emails', 'move_emails']] },
  { id: 'h-u-trusted', role: 'user', prompt: 'Make sure mail from partner.example.org never goes to junk.', groups: [['set_trusted_senders', 'create_content_filter_simple', 'create_content_filter']] },
  { id: 'h-u-draft', role: 'user', prompt: 'Write a draft to the team saying the release slipped a week, but don\'t send it yet.', groups: [['create_draft']] },
  { id: 'h-u-rules', role: 'user', prompt: 'Which inbox rules do I have set up?', groups: [['get_content_filters']] },
  { id: 'h-u-group-members', role: 'user', prompt: 'Who is in my Family contact group?', groups: [['get_contact_groups', 'expand_contact_group', 'get_contact_group']] },

  /* ---------------------------------------------------------- DomainAdmin */
  { id: 'h-d-disable-bob', role: 'domainadmin', prompt: 'Disable bob\'s account, he left the company.', groups: [['domain_disable_users']] },
  { id: 'h-d-catch-all', role: 'domainadmin', prompt: 'Send all mail for unknown addresses to postmaster.', groups: [['domain_set_catch_all']] },
  { id: 'h-d-last-login', role: 'domainadmin', prompt: 'When did each user last log in?', groups: [['domain_get_last_login_times']] },
  { id: 'h-d-add-subscriber', role: 'domainadmin', prompt: 'Subscribe erin@example.org to the news mailing list.', groups: [['domain_list_mailing_lists', 'domain_get_mailing_list'], ['domain_add_mailing_list_subscribers']] },
  { id: 'h-d-signature', role: 'domainadmin', prompt: 'Set up a company email signature for everyone with our address and phone number.', groups: [['domain_create_signature', 'domain_update_signature', 'domain_set_signature_mapping']] },
  { id: 'h-d-rename', role: 'domainadmin', prompt: 'Rename the mailbox jsmith to john.smith.', groups: [['domain_rename_user']] },
  { id: 'h-d-activesync', role: 'domainadmin', prompt: 'Turn off ActiveSync for carol.', groups: [['domain_set_protocol_access']] },
  { id: 'h-d-mailbox-size', role: 'domainadmin', prompt: 'Give alice a 10 GB mailbox.', groups: [['domain_update_user']] },
  { id: 'h-d-who-forwards', role: 'domainadmin', prompt: 'Is dave\'s mail being forwarded anywhere?', groups: [['domain_get_mailbox_forwarding', 'domain_get_user_mail_settings']] },

  /* ---------------------------------------------------------- SysAdmin */
  { id: 'h-s-blocked-ips', role: 'sysadmin', prompt: 'Which IP addresses are blocked right now?', groups: [['get_blocked_ips']] },
  { id: 'h-s-unblock-ip', role: 'sysadmin', prompt: 'Unblock 203.0.113.7, it is our office.', groups: [['unblock_ips']] },
  { id: 'h-s-cert-expiry', role: 'sysadmin', prompt: 'Are any SSL certificates about to expire?', groups: [['get_ssl_certificates', 'get_acme_certificates']] },
  { id: 'h-s-services', role: 'sysadmin', prompt: 'Are all the mail services running?', groups: [['get_services']] },
  { id: 'h-s-new-domain', role: 'sysadmin', prompt: 'Add example.net as a new domain on the server.', groups: [['create_domain']] },
  { id: 'h-s-logs', role: 'sysadmin', prompt: 'Look in the SMTP log for anything about bounce@example.net today.', groups: [['search_log_files']] },
  { id: 'h-s-throttled', role: 'sysadmin', prompt: 'Is anyone being throttled for sending too much?', groups: [['get_throttled_users', 'get_throttled_domains', 'get_throttled_counts']] },
  { id: 'h-s-kill', role: 'sysadmin', prompt: 'Log alice@example.com out of every device.', groups: [['kill_user_sessions', 'drop_user_connections']] },

  /* ---------------------------------------------------------- Multi */
  { id: 'h-m-users-email', role: 'multi', prompt: 'Find the inactive users on example.com and email the list to user@example.com.', groups: [['get_inactive_users'], ['send_email']] },
  { id: 'h-m-version-note', role: 'multi', prompt: 'What server version are we on? Also, do I have any unread mail?', groups: [['get_server_version', 'get_dashboard_stats'], ['get_emails', 'search_items']] },

  /* ---- batch 2: written after the retrieval changes, measured once, never tuned on */
  { id: 'h2-u-yesterday', role: 'user', prompt: 'Did anyone write to me yesterday?', groups: [['get_emails', 'search_items']] },
  { id: 'h2-u-unflag', role: 'user', prompt: 'Unflag the email with uid 812 in my Inbox.', groups: [['set_email_properties']] },
  { id: 'h2-u-cancel-event', role: 'user', prompt: 'Cancel my dentist appointment on Thursday.', groups: [['get_calendar_events', 'search_items'], ['delete_calendar_event', 'new_or_update_calendar_event']] },
  { id: 'h2-u-bday', role: 'user', prompt: 'When is Henry Example\'s birthday? It should be in his contact card.', groups: [['get_contacts', 'search_items', 'get_contact']] },
  { id: 'h2-u-sent', role: 'user', prompt: 'What did I send to carol@example.org last week?', groups: [['get_emails', 'search_items']] },
  { id: 'h2-u-reply-all', role: 'user', prompt: 'Answer Bob\'s lunch email with: sounds good, see you then.', groups: [['get_emails', 'search_items'], ['reply_to_email']] },
  { id: 'h2-u-task-done', role: 'user', prompt: 'Mark the "Renew domain" task as complete.', groups: [['get_tasks'], ['new_or_update_task']] },
  { id: 'h2-u-junk-count', role: 'user', prompt: 'How much junk mail has piled up?', groups: [['list_folder_info_by_type', 'get_emails']] },
  { id: 'h2-u-delete-note', role: 'user', prompt: 'Delete my shopping list note.', groups: [['get_notes', 'search_items'], ['delete_note', 'remove_items']] },
  { id: 'h2-u-attach-send', role: 'user', prompt: 'Send the file report.pdf to dana@example.org.', groups: [['upload_attachment', 'send_email_with_attachments']] },
  { id: 'h2-d-new-group', role: 'domainadmin', prompt: 'Create a user group called Sales and put alice and bob in it.', groups: [['domain_create_user_group'], ['domain_set_user_group_membership', 'domain_update_user_group']] },
  { id: 'h2-d-quota', role: 'domainadmin', prompt: 'How many accounts does our domain have, and how many more can we add?', groups: [['domain_get_account_counts', 'domain_get_info']] },
  { id: 'h2-d-whitelist', role: 'domainadmin', prompt: 'Trust everything from partner.example.org for the whole domain.', groups: [['domain_update_whitelist']] },
  { id: 'h2-d-delete-alias', role: 'domainadmin', prompt: 'Remove the old support alias.', groups: [['domain_delete_alias']] },
  { id: 'h2-d-ips', role: 'domainadmin', prompt: 'From which IP addresses has erin been signing in?', groups: [['domain_get_user_login_ips']] },
  { id: 'h2-s-queue', role: 'sysadmin', prompt: 'Is the outgoing queue stuck? Show me what is waiting.', groups: [['get_spool_messages', 'get_spool_message_counts', 'get_spool_message_count']] },
  { id: 'h2-s-restart', role: 'sysadmin', prompt: 'Restart the IMAP service.', groups: [['stop_services', 'start_services']] },
  { id: 'h2-s-dkim-on', role: 'sysadmin', prompt: 'Turn on DKIM signing for example.org.', groups: [['enable_dkim', 'set_dkim_settings']] },
  { id: 'h2-s-connections', role: 'sysadmin', prompt: 'How many people are connected to the server right now?', groups: [['get_connections_count', 'get_connections', 'get_dashboard_stats']] },
  { id: 'h2-m-domains-users', role: 'multi', prompt: 'How many users does each domain have? Put the answer in an email to me.', groups: [['get_domains', 'get_domain_info'], ['send_email']] }
];
