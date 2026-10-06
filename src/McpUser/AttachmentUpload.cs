using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmarterMailMcp.Client.Services;
using SmarterMailMcp.Core.Models;
using SmarterMailMcp.Server.Tools;

namespace SmarterMailMcp.User;

/// <summary>
/// <c>POST /attachments</c> (HTTP transport): upload files as plain multipart, outside MCP. An agent
/// with a shell and the server's API key can then attach a file it has on disk without passing its
/// bytes through the model as base64:
/// <code>curl -H "X-API-Key: $KEY" -F file=@banner.png -F inline=true https://host/attachments</code>
/// The answer has the same fields as <c>upload_attachment</c>, per file, under one attachmentGuid that
/// <c>send_email_with_attachments</c> takes. The host maps it behind the same API key as <c>/mcp</c>.
/// </summary>
public static class AttachmentUpload
{
    public const string Route = "/attachments";

    public static void Map(IEndpointRouteBuilder routes, UserContext userContext) =>
        routes.MapPost(Route, (HttpRequest request) => HandleAsync(request, userContext))
            .DisableAntiforgery();

    /// <summary>Instructions for MCP clients: how to get a local file to this server.</summary>
    public const string Instructions =
        "Attachments: this server cannot read files on your machine. To attach a file, either pass its bytes to " +
        "upload_attachment as base64Content (or its contents as text), or, if you can run shell commands, POST it to " +
        "this server's /attachments endpoint (the /mcp URL with /attachments in place of /mcp, same API key): " +
        "curl -H \"X-API-Key: <key>\" -F file=@report.pdf [-F file=@more.csv] [-F inline=true] [-F attachmentGuid=<guid>] <base>/attachments. " +
        "It returns the same attachmentGuid / htmlReference fields as upload_attachment. Prefer it for binary or large files.";

    internal static async Task<IResult> HandleAsync(HttpRequest request, UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return Error(StatusCodes.Status403Forbidden, "Read-only mode is enabled. Cannot upload attachments.");
        if (!request.HasFormContentType)
            return Error(StatusCodes.Status400BadRequest,
                "Send multipart/form-data with one or more 'file' fields, e.g. curl -F file=@banner.png -F inline=true.");

        var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
        if (form.Files.Count == 0)
            return Error(StatusCodes.Status400BadRequest, "No files: add one or more 'file' fields (curl -F file=@path).");

        var inlineValue = (form["inline"].FirstOrDefault() ?? request.Query["inline"].FirstOrDefault())?.Trim().ToLowerInvariant();
        var inline = inlineValue is "true" or "1" or "yes";

        var guid = (form["attachmentGuid"].FirstOrDefault() ?? request.Query["attachmentGuid"].FirstOrDefault())?.Trim();
        if (string.IsNullOrEmpty(guid))
            guid = Guid.NewGuid().ToString();
        else if (!Guid.TryParse(guid, out _))
            return Error(StatusCodes.Status400BadRequest, $"attachmentGuid '{guid}' is not a GUID.");

        var files = new JsonArray();
        var allSucceeded = true;
        foreach (var file in form.Files)
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, request.HttpContext.RequestAborted);
            // curl sends application/octet-stream for anything it does not recognise; the extension knows better.
            var contentType = file.ContentType is null or "" or "application/octet-stream" ? null : file.ContentType;
            var fileName = Path.GetFileName(file.FileName);

            var result = await UploadService.UploadAttachmentAsync(userContext, buffer.ToArray(), fileName, contentType, guid, inline);
            allSucceeded &= result.Success;
            var entry = JsonNode.Parse(MailTools.UploadResultJson(result, inline))!.AsObject();
            entry.Remove("next");
            if (!result.Success)
                entry["fileName"] = fileName;
            files.Add(entry);
        }

        var body = new JsonObject
        {
            ["success"] = allSucceeded,
            ["attachmentGuid"] = guid,
            ["files"] = files,
            ["next"] = inline
                ? $"Put <img src=\"cid:<contentId>\"> in the HTML body and call send_email_with_attachments with attachmentGuid {guid}."
                : $"Call send_email_with_attachments with attachmentGuid {guid}.",
        };
        return Results.Content(body.ToJsonString(), "application/json",
            statusCode: allSucceeded ? StatusCodes.Status200OK : StatusCodes.Status502BadGateway);
    }

    private static IResult Error(int status, string message) =>
        Results.Content(JsonSerializer.Serialize(new { success = false, error = message }), "application/json", statusCode: status);
}
