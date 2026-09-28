using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace SmarterMailMcp.Server.Prompts;

[McpServerPromptType]
public static class UserPrompts
{
    [McpServerPrompt, Description("Provides a daily briefing summarizing unread emails, calendar events, and tasks for today.")]
    public static IEnumerable<ChatMessage> DailyBriefing() =>
    [
        new ChatMessage(ChatRole.User, 
            "Can you give me my daily briefing? Please check for unread emails, today's calendar events, and tasks. Get folder ids for emails, calendar, and tasks first."),
        new ChatMessage(ChatRole.Assistant, 
            "Okay, I will fetch your unread emails from your Inbox, today's events from your primary calendar, and today's tasks. Then I'll give you a summary.")
    ];

    [McpServerPrompt, Description("Searches for a query across emails, contacts, notes, tasks, and calendar events.")]
    public static IEnumerable<ChatMessage> SearchAll([Description("The search query")] string query) =>
    [
        new ChatMessage(ChatRole.User, 
            $"Search for '{query}' everywhere."),
        new ChatMessage(ChatRole.Assistant, 
            $"Okay, I will search for '{query}' in your emails (Inbox, Sent), contacts, global address book, and calendar events for the next month.")
    ];

    [McpServerPrompt, Description("Helps draft a follow-up email based on an existing email.")]
    public static IEnumerable<ChatMessage> FollowUpEmail(
        [Description("The UID of the email to follow up on")] int emailUid,
        [Description("The folder ID containing the email")] string folderId) =>
    [
        new ChatMessage(ChatRole.User,
            $"Help me draft a follow-up to email UID {emailUid} in folder {folderId}."),
        new ChatMessage(ChatRole.Assistant,
            "Alright, let me fetch that email first so we have the context. Then I can help you write the follow-up.")
    ];

    [McpServerPrompt, Description("Sends an email with a file attachment. First uploads the file, then sends the email using the attachment GUID.")]
    public static IEnumerable<ChatMessage> SendEmailWithAttachment(
        [Description("Full path to the file to attach")] string filePath,
        [Description("Recipient email address")] string to,
        [Description("Email subject")] string subject,
        [Description("Email body text")] string body) =>
    [
        new ChatMessage(ChatRole.User,
            $"Send an email to {to} with subject '{subject}' and attach the file at {filePath}. Body: {body}"),
        new ChatMessage(ChatRole.Assistant,
            $"I'll first upload the file '{filePath}' using upload_attachment to get an attachment GUID, then send the email to {to} using send_email_with_attachments with that GUID.")
    ];

    [McpServerPrompt, Description("Sends an HTML email with an embedded inline image. The image displays within the email body rather than as a separate attachment.")]
    public static IEnumerable<ChatMessage> SendEmailWithInlineImage(
        [Description("Full path to the image file")] string imagePath,
        [Description("Recipient email address")] string to,
        [Description("Email subject")] string subject,
        [Description("Content ID for the image (used in HTML as src='cid:yourId')")] string contentId = "inlineimg") =>
    [
        new ChatMessage(ChatRole.User,
            $"Send an HTML email to {to} with subject '{subject}' and embed the image at {imagePath} inline in the body."),
        new ChatMessage(ChatRole.Assistant,
            $"I'll upload the image using upload_attachment with contentId='{contentId}' to get an attachment GUID. Then I'll send an HTML email using send_email_with_attachments with <img src='cid:{contentId}' /> in the body to embed the image inline.")
    ];
}