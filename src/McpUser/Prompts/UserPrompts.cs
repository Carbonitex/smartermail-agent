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
        [Description("The file to attach: a path on your machine, or a description of a file to create")] string file,
        [Description("Recipient email address")] string to,
        [Description("Email subject")] string subject,
        [Description("Email body text")] string body) =>
    [
        new ChatMessage(ChatRole.User,
            $"Send an email to {to} with subject '{subject}' and attach {file}. Body: {body}"),
        new ChatMessage(ChatRole.Assistant,
            "I'll upload the file with upload_attachment: its contents as text for a text file, otherwise base64Content " +
            "(or filePath if this server runs on your machine, or the server's /attachments endpoint if I can run shell commands). " +
            $"Then I'll send the email to {to} with send_email_with_attachments and the attachmentGuid it returns.")
    ];

    [McpServerPrompt, Description("Sends an HTML email with an embedded inline image. The image displays within the email body rather than as a separate attachment.")]
    public static IEnumerable<ChatMessage> SendEmailWithInlineImage(
        [Description("The image: a path on your machine, or a description of an image to create")] string image,
        [Description("Recipient email address")] string to,
        [Description("Email subject")] string subject) =>
    [
        new ChatMessage(ChatRole.User,
            $"Send an HTML email to {to} with subject '{subject}' and embed {image} inline in the body."),
        new ChatMessage(ChatRole.Assistant,
            "I'll upload the image with upload_attachment and inline=true. SmarterMail assigns the content ID, so I'll put the " +
            "htmlReference it returns in the body as <img src=\"cid:...\">, then send the HTML email with " +
            $"send_email_with_attachments to {to} using the same attachmentGuid.")
    ];
}
