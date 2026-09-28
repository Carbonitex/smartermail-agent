using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Client.Services;

/// <summary>
/// Service for handling file uploads to SmarterMail using the resumable upload protocol.
/// Supports email attachments, file storage, contact images, and other upload contexts.
/// </summary>
public class UploadService
{
    private const int DefaultChunkSize = 2 * 1024 * 1024; // 2MB chunks (matches SmarterMail's default)
    private const string UploadEndpoint = "/api/upload";

    /// <summary>
    /// Upload context types supported by SmarterMail
    /// </summary>
    public static class ContextType
    {
        public const string Attachment = "attachment";
        public const string FileStorage = "file-storage";
        public const string WorkspaceStorage = "workspace-storage";
        public const string ContactImage = "contact-image";
        public const string ProfileImage = "profile-image";
        public const string CompanyLogo = "company-logo";
        public const string CompanyIcon = "company-icon";
        public const string ContactImport = "contact-import";
        public const string UsersImport = "users-import";
        public const string CalendarIcs = "calendar-ics";
        public const string TaskIcs = "task-ics";
        public const string AttachedFile = "attached-file";
    }

    /// <summary>
    /// Result of an upload operation
    /// </summary>
    public class UploadResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? Guid { get; set; }
        public string? FileName { get; set; }
        public long FileSize { get; set; }
        public string? ContentType { get; set; }
        public string? ContentId { get; set; }
        public JsonElement? ServerResponse { get; set; }
    }

    /// <summary>
    /// Upload a file as an email attachment.
    /// </summary>
    /// <param name="userContext">The authenticated user context</param>
    /// <param name="filePath">Full path to the file to upload</param>
    /// <param name="attachmentGuid">Optional existing attachment session GUID. If not provided, a new one is generated.</param>
    /// <param name="contentId">Optional Content-ID for inline images. Use "cidgenerate" to auto-generate, or provide a specific ID to use in HTML as src="cid:yourId"</param>
    /// <returns>Upload result containing the attachment GUID to use when sending the email</returns>
    public static async Task<UploadResult> UploadAttachmentAsync(
        UserContext userContext,
        string filePath,
        string? attachmentGuid = null,
        string? contentId = null)
    {
        if (string.IsNullOrEmpty(attachmentGuid))
            attachmentGuid = Guid.NewGuid().ToString();

        // If contentId is provided, use the /api/v1/mail/attachment endpoint which properly handles inline images
        if (!string.IsNullOrEmpty(contentId))
        {
            return await UploadAttachmentWithContentIdAsync(userContext, filePath, attachmentGuid, contentId);
        }

        // Otherwise use the resumable /api/upload endpoint for regular attachments
        var contextData = JsonSerializer.Serialize(new { guid = attachmentGuid });
        var result = await UploadFileAsync(userContext, filePath, ContextType.Attachment, contextData);
        result.Guid = attachmentGuid;
        return result;
    }

    /// <summary>
    /// Upload an inline image attachment using the /api/v1/mail/attachment endpoint.
    /// </summary>
    private static async Task<UploadResult> UploadAttachmentWithContentIdAsync(
        UserContext userContext,
        string filePath,
        string attachmentGuid,
        string contentId)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new UploadResult
                {
                    Success = false,
                    Error = $"File not found: {filePath}"
                };
            }

            var fileBytes = await File.ReadAllBytesAsync(filePath);
            var fileName = Path.GetFileName(filePath);
            var contentType = GetContentType(filePath);

            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(fileContent, "file", fileName);

            // Use the /api/v1/mail/attachment/{attachguid}/{contentid} endpoint
            var endpoint = $"/api/v1/mail/attachment/{attachmentGuid}/{contentId}";
            var response = await userContext.PostMultipartAsync<JsonElement>(endpoint, content);

            // Extract the generated contentId if "cidgenerate" was used
            string? actualContentId = contentId;
            try
            {
                if (response.ValueKind == JsonValueKind.Object &&
                    response.TryGetProperty("contentId", out var cidProp))
                {
                    actualContentId = cidProp.GetString();
                }
            }
            catch { /* Keep original contentId */ }

            return new UploadResult
            {
                Success = true,
                Guid = attachmentGuid,
                FileName = fileName,
                FileSize = fileBytes.Length,
                ContentType = contentType,
                ContentId = actualContentId,
                ServerResponse = response
            };
        }
        catch (Exception ex)
        {
            return new UploadResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Upload a file as an email attachment from a byte array.
    /// </summary>
    public static async Task<UploadResult> UploadAttachmentAsync(
        UserContext userContext,
        byte[] fileBytes,
        string fileName,
        string? contentType = null,
        string? attachmentGuid = null)
    {
        if (string.IsNullOrEmpty(attachmentGuid))
            attachmentGuid = Guid.NewGuid().ToString();

        var contextData = JsonSerializer.Serialize(new { guid = attachmentGuid });

        var result = await UploadBytesAsync(userContext, fileBytes, fileName, contentType, ContextType.Attachment, contextData);
        result.Guid = attachmentGuid;
        return result;
    }

    /// <summary>
    /// Upload a file to file storage.
    /// </summary>
    /// <param name="userContext">The authenticated user context</param>
    /// <param name="filePath">Full path to the file to upload</param>
    /// <param name="folderPath">Target folder path in file storage</param>
    /// <returns>Upload result</returns>
    public static async Task<UploadResult> UploadToFileStorageAsync(
        UserContext userContext,
        string filePath,
        string folderPath)
    {
        var contextData = JsonSerializer.Serialize(new { folder = folderPath });
        return await UploadFileAsync(userContext, filePath, ContextType.FileStorage, contextData);
    }

    /// <summary>
    /// Upload a contact image.
    /// </summary>
    public static async Task<UploadResult> UploadContactImageAsync(
        UserContext userContext,
        string filePath,
        string sourceId,
        string contactId)
    {
        var contextData = JsonSerializer.Serialize(new { sourceId, contactId });
        return await UploadFileAsync(userContext, filePath, ContextType.ContactImage, contextData);
    }

    /// <summary>
    /// Upload a profile image for the current user.
    /// </summary>
    public static async Task<UploadResult> UploadProfileImageAsync(
        UserContext userContext,
        string filePath)
    {
        return await UploadFileAsync(userContext, filePath, ContextType.ProfileImage, "{}");
    }

    /// <summary>
    /// Upload a calendar ICS file for import.
    /// </summary>
    public static async Task<UploadResult> UploadCalendarIcsAsync(
        UserContext userContext,
        string filePath,
        string owner,
        string calendarId)
    {
        var contextData = JsonSerializer.Serialize(new { owner, calId = calendarId });
        return await UploadFileAsync(userContext, filePath, ContextType.CalendarIcs, contextData);
    }

    /// <summary>
    /// Generic file upload with custom context.
    /// </summary>
    /// <param name="userContext">The authenticated user context</param>
    /// <param name="filePath">Full path to the file to upload</param>
    /// <param name="context">Upload context type (use ContextType constants)</param>
    /// <param name="contextData">JSON string with context-specific data</param>
    /// <returns>Upload result</returns>
    public static async Task<UploadResult> UploadFileAsync(
        UserContext userContext,
        string filePath,
        string context,
        string contextData)
    {
        try
        {
            // Validate file exists
            if (!File.Exists(filePath))
            {
                return new UploadResult
                {
                    Success = false,
                    Error = $"File not found: {filePath}"
                };
            }

            // Read file
            var fileBytes = await File.ReadAllBytesAsync(filePath);
            var fileName = Path.GetFileName(filePath);
            var contentType = GetContentType(filePath);

            return await UploadBytesAsync(userContext, fileBytes, fileName, contentType, context, contextData);
        }
        catch (Exception ex)
        {
            return new UploadResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Generic upload from byte array with custom context.
    /// </summary>
    public static async Task<UploadResult> UploadBytesAsync(
        UserContext userContext,
        byte[] fileBytes,
        string fileName,
        string? contentType,
        string context,
        string contextData)
    {
        try
        {
            contentType ??= "application/octet-stream";
            var fileSize = fileBytes.Length;

            // Build resumable identifier
            var sanitizedFileName = Regex.Replace(fileName, @"[^a-zA-Z0-9]", "");
            var resumableIdentifier = $"{fileSize}-{sanitizedFileName}";

            // Calculate chunks
            var totalChunks = (int)Math.Ceiling((double)fileSize / DefaultChunkSize);
            if (totalChunks == 0) totalChunks = 1;

            // For now, we upload as a single chunk if file is small enough
            // TODO: Implement multi-chunk upload for large files
            if (totalChunks == 1)
            {
                return await UploadSingleChunkAsync(
                    userContext,
                    fileBytes,
                    fileName,
                    contentType,
                    fileSize,
                    resumableIdentifier,
                    context,
                    contextData);
            }
            else
            {
                return await UploadMultipleChunksAsync(
                    userContext,
                    fileBytes,
                    fileName,
                    contentType,
                    fileSize,
                    resumableIdentifier,
                    totalChunks,
                    context,
                    contextData);
            }
        }
        catch (Exception ex)
        {
            return new UploadResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Upload a file as a single chunk (for files smaller than chunk size).
    /// </summary>
    private static async Task<UploadResult> UploadSingleChunkAsync(
        UserContext userContext,
        byte[] fileBytes,
        string fileName,
        string contentType,
        long fileSize,
        string resumableIdentifier,
        string context,
        string contextData)
    {
        using var content = new MultipartFormDataContent();

        // Add resumable upload fields
        content.Add(new StringContent("1"), "resumableChunkNumber");
        content.Add(new StringContent(DefaultChunkSize.ToString()), "resumableChunkSize");
        content.Add(new StringContent(fileSize.ToString()), "resumableCurrentChunkSize");
        content.Add(new StringContent(fileSize.ToString()), "resumableTotalSize");
        content.Add(new StringContent(contentType), "resumableType");
        content.Add(new StringContent(resumableIdentifier), "resumableIdentifier");
        content.Add(new StringContent(fileName), "resumableFilename");
        content.Add(new StringContent(fileName), "resumableRelativePath");
        content.Add(new StringContent("1"), "resumableTotalChunks");

        // Add context fields
        content.Add(new StringContent(context), "context");
        content.Add(new StringContent(contextData), "contextData");

        // Add file content
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);

        // Upload to /api/upload
        var response = await userContext.PostMultipartAsync<JsonElement>(UploadEndpoint, content);

        return new UploadResult
        {
            Success = true,
            FileName = fileName,
            FileSize = fileSize,
            ContentType = contentType,
            ServerResponse = response
        };
    }

    /// <summary>
    /// Upload a file in multiple chunks (for files larger than chunk size).
    /// </summary>
    private static async Task<UploadResult> UploadMultipleChunksAsync(
        UserContext userContext,
        byte[] fileBytes,
        string fileName,
        string contentType,
        long fileSize,
        string resumableIdentifier,
        int totalChunks,
        string context,
        string contextData)
    {
        for (int chunkNumber = 1; chunkNumber <= totalChunks; chunkNumber++)
        {
            var offset = (chunkNumber - 1) * DefaultChunkSize;
            var currentChunkSize = Math.Min(DefaultChunkSize, (int)(fileSize - offset));
            var chunkBytes = new byte[currentChunkSize];
            Array.Copy(fileBytes, offset, chunkBytes, 0, currentChunkSize);

            using var content = new MultipartFormDataContent();

            // Add resumable upload fields
            content.Add(new StringContent(chunkNumber.ToString()), "resumableChunkNumber");
            content.Add(new StringContent(DefaultChunkSize.ToString()), "resumableChunkSize");
            content.Add(new StringContent(currentChunkSize.ToString()), "resumableCurrentChunkSize");
            content.Add(new StringContent(fileSize.ToString()), "resumableTotalSize");
            content.Add(new StringContent(contentType), "resumableType");
            content.Add(new StringContent(resumableIdentifier), "resumableIdentifier");
            content.Add(new StringContent(fileName), "resumableFilename");
            content.Add(new StringContent(fileName), "resumableRelativePath");
            content.Add(new StringContent(totalChunks.ToString()), "resumableTotalChunks");

            // Add context fields
            content.Add(new StringContent(context), "context");
            content.Add(new StringContent(contextData), "contextData");

            // Add chunk content
            var fileContent = new ByteArrayContent(chunkBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(fileContent, "file", fileName);

            // Upload chunk
            var response = await userContext.PostMultipartAsync<JsonElement>(UploadEndpoint, content);

            // If this is the last chunk, return the result
            if (chunkNumber == totalChunks)
            {
                return new UploadResult
                {
                    Success = true,
                    FileName = fileName,
                    FileSize = fileSize,
                    ContentType = contentType,
                    ServerResponse = response
                };
            }
        }

        // Should never reach here
        return new UploadResult
        {
            Success = false,
            Error = "Unexpected end of chunk upload"
        };
    }

    /// <summary>
    /// Get MIME content type from file extension.
    /// </summary>
    public static string GetContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            // Images
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".tiff" or ".tif" => "image/tiff",

            // Documents
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".odt" => "application/vnd.oasis.opendocument.text",
            ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
            ".odp" => "application/vnd.oasis.opendocument.presentation",
            ".rtf" => "application/rtf",

            // Text
            ".txt" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".csv" => "text/csv",
            ".md" => "text/markdown",

            // Code
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".yaml" or ".yml" => "application/x-yaml",

            // Archives
            ".zip" => "application/zip",
            ".rar" => "application/x-rar-compressed",
            ".7z" => "application/x-7z-compressed",
            ".tar" => "application/x-tar",
            ".gz" => "application/gzip",

            // Audio
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",

            // Video
            ".mp4" => "video/mp4",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".wmv" => "video/x-ms-wmv",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",

            // Calendar
            ".ics" => "text/calendar",
            ".vcf" => "text/vcard",

            // Email
            ".eml" => "message/rfc822",
            ".msg" => "application/vnd.ms-outlook",

            // Default
            _ => "application/octet-stream"
        };
    }
}
