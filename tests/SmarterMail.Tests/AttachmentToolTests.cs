using System.Text;
using System.Text.Json;
using SmarterMailMcp.Core.Models;
using SmarterMailMcp.Server.Tools;

namespace SmarterMail.Tests;

/// <summary>
/// upload_attachment, get_email_attachments and download_email_attachment against a fake SmarterMail
/// that answers with the shapes a live server returns.
/// </summary>
public sealed class AttachmentToolTests : IDisposable
{
    private const string RefreshOk = """{"accessToken":"t2","refreshToken":"r2"}""";
    private const string Cid = "72e8e73bcea24522bc7f1127a945c980";
    private readonly string _dir = Directory.CreateTempSubdirectory("sma-attach-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (UserContext User, GlobalContext Global) Connect(FakeSmarterMail server, bool localFiles = false, bool readOnly = false)
    {
        var global = new GlobalContext(Path.Combine(_dir, "token.json")) { LocalFileAccess = localFiles };
        Assert.True(global.WriteTokenFile(new TokenData
        {
            AccessToken = "t1", RefreshToken = "r1", BaseUrl = server.BaseUrl, Username = "user@example.com",
            ReadOnlyMode = readOnly,
        }));
        var user = new UserContext(global);
        user.InitializeFromFile(global);
        return (user, global);
    }

    private static FakeSmarterMail Server(Func<string, (int, string)> respond) =>
        new((path, _) => path.EndsWith("/refresh-token") ? (200, RefreshOk) : respond(path));

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public async Task Inline_upload_asks_SmarterMail_for_a_content_id_and_returns_the_one_it_assigned()
    {
        using var server = Server(path => path.Contains("/api/v1/mail/attachment/")
            ? (200, $$"""{"returnData":"{\"key\":\"att_0.png\"}","link":"/x","cid":"{{Cid}}","success":true,"message":"SUCCESS"}""")
            : (404, "{}"));
        var (user, global) = Connect(server);

        var result = Json(await MailTools.UploadAttachment(fileName: "banner.png",
            base64Content: Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]), inline: true,
            userContext: user, globalContext: global));

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(Cid, result.GetProperty("contentId").GetString());
        Assert.Equal($"cid:{Cid}", result.GetProperty("htmlReference").GetString());
        Assert.Equal("image/png", result.GetProperty("contentType").GetString());
        var guid = result.GetProperty("attachmentGuid").GetString();
        Assert.Contains(server.Requests, r => r.Path == $"/api/v1/mail/attachment/{guid}/cidgenerate");
    }

    [Fact]
    public async Task A_refused_upload_reports_SmarterMails_own_message()
    {
        using var server = Server(_ => (400, """{"success":false,"message":"Invalid content ID"}"""));
        var (user, global) = Connect(server);

        var result = Json(await MailTools.UploadAttachment(fileName: "banner.png", base64Content: "iVBORw==",
            inline: true, userContext: user, globalContext: global));

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("Invalid content ID", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Text_goes_up_as_utf8_through_the_resumable_upload()
    {
        using var server = Server(path => path == "/api/upload" ? (200, """{"success":true}""") : (404, "{}"));
        var (user, global) = Connect(server);

        var result = Json(await MailTools.UploadAttachment(fileName: "notes.csv", text: "a,b\n1,ü",
            userContext: user, globalContext: global));

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("text/csv", result.GetProperty("contentType").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount("a,b\n1,ü"), result.GetProperty("fileSize").GetInt64());
        Assert.False(result.TryGetProperty("htmlReference", out _));
        Assert.Contains(server.Requests, r => r.Path == "/api/upload" && r.Body.Contains("a,b\n1,ü"));
    }

    [Fact]
    public async Task A_file_path_is_refused_without_local_file_access_and_the_file_is_never_read()
    {
        var secret = Path.Combine(_dir, "secret.txt");
        await File.WriteAllTextAsync(secret, "do-not-send");
        using var server = Server(_ => (200, """{"success":true}"""));
        var (user, global) = Connect(server, localFiles: false);

        var result = Json(await MailTools.UploadAttachment(filePath: secret, userContext: user, globalContext: global));

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("base64Content", result.GetProperty("error").GetString());
        Assert.DoesNotContain(server.Requests, r => r.Body.Contains("do-not-send"));
    }

    [Fact]
    public async Task A_file_path_works_with_local_file_access()
    {
        var file = Path.Combine(_dir, "report.txt");
        await File.WriteAllTextAsync(file, "quarterly");
        using var server = Server(path => path == "/api/upload" ? (200, """{"success":true}""") : (404, "{}"));
        var (user, global) = Connect(server, localFiles: true);

        var result = Json(await MailTools.UploadAttachment(filePath: file, userContext: user, globalContext: global));

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("report.txt", result.GetProperty("fileName").GetString());
        Assert.Contains(server.Requests, r => r.Body.Contains("quarterly"));
    }

    [Theory]
    [InlineData("", "", "one of")]
    [InlineData("aGk=", "hi", "only one")]
    public async Task Exactly_one_source_is_required(string base64, string text, string expected)
    {
        using var server = Server(_ => (200, "{}"));
        var (user, global) = Connect(server);

        var result = Json(await MailTools.UploadAttachment(fileName: "a.txt", base64Content: base64, text: text,
            userContext: user, globalContext: global));

        Assert.Contains(expected, result.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", "image/png")]
    [InlineData("iVBORw0KGgo", null)]          // padding missing
    [InlineData("iVBO\nRw0K Ggo=", null)]      // wrapped
    public void Base64_content_accepts_data_urls_missing_padding_and_whitespace(string value, string? type)
    {
        var (bytes, dataUrlType, error) = MailTools.DecodeBase64Content(value);
        Assert.Null(error);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes);
        Assert.Equal(type, dataUrlType);
    }

    // What /api/v1/mail/message answers for a message with an inline PNG and a text attachment.
    private const string MessageWithAttachments = """
        {"messageData":{"uid":11,"attachments":[
          {"link":"/attachment/inline?data=a","filename":"inline.png","type":"image","partID":5,"size":39936},
          {"link":"/attachment/inline?data=b","filename":"attachment.txt","type":"document","partID":6,"size":1024}],
         "originalCidLinks":{"72e8e73bcea24522bc7f1127a945c980":"/attachment/inline?data=c"}}}
        """;

    private static (int, string) Mailbox(string path) => path switch
    {
        "/api/v1/mail/message" => (200, MessageWithAttachments),
        "/attachment/inline" => (200, "hello"),
        _ => (404, "{}"),
    };

    [Fact]
    public async Task Get_email_attachments_maps_what_SmarterMail_actually_returns()
    {
        using var server = Server(Mailbox);
        var (user, _) = Connect(server);

        var result = Json(await MailTools.GetEmailAttachments(11, "Sent Items", user));

        var atts = result.GetProperty("attachments").EnumerateArray().ToList();
        Assert.Equal(2, atts.Count);
        Assert.Equal("image/png", atts[0].GetProperty("contentType").GetString());
        Assert.Equal(5, atts[0].GetProperty("partId").GetInt32());
        Assert.Equal(39936, atts[0].GetProperty("approximateSize").GetInt64());
        Assert.Equal("text/plain", atts[1].GetProperty("contentType").GetString());
        Assert.Equal(Cid, Assert.Single(result.GetProperty("inlineContentIds").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Download_without_local_file_access_never_writes_and_returns_text_instead()
    {
        using var server = Server(Mailbox);
        var (user, global) = Connect(server, localFiles: false);
        var target = Path.Combine(_dir, "out.txt");

        var refused = Json(await MailTools.DownloadEmailAttachment(11, "Sent Items", "attachment.txt", savePath: target,
            userContext: user, globalContext: global));
        Assert.False(refused.GetProperty("success").GetBoolean());
        Assert.False(File.Exists(target));

        var inline = Json(await MailTools.DownloadEmailAttachment(11, "Sent Items", "attachment.txt",
            userContext: user, globalContext: global));
        Assert.Equal("hello", inline.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Binary_downloads_need_include_base64()
    {
        using var server = Server(Mailbox);
        var (user, global) = Connect(server);

        var plain = Json(await MailTools.DownloadEmailAttachment(11, "Sent Items", "inline.png", userContext: user, globalContext: global));
        Assert.False(plain.TryGetProperty("base64Content", out _));

        var b64 = Json(await MailTools.DownloadEmailAttachment(11, "Sent Items", "inline.png", includeBase64: true,
            userContext: user, globalContext: global));
        Assert.Equal(Convert.ToBase64String("hello"u8.ToArray()), b64.GetProperty("base64Content").GetString());
    }

    [Fact]
    public async Task Download_with_local_file_access_saves_to_the_path()
    {
        using var server = Server(Mailbox);
        var (user, global) = Connect(server, localFiles: true);

        var result = Json(await MailTools.DownloadEmailAttachment(11, "Sent Items", "attachment.txt", savePath: _dir,
            userContext: user, globalContext: global));

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_dir, "attachment.txt")));
    }
}

/// <summary>Recipients are sent to SmarterMail one entry each, not wrapped in a single display name.</summary>
public sealed class AddressListTests
{
    [Theory]
    [InlineData("a@x.com", "<a@x.com>")]
    [InlineData("a@x.com, b@y.com,c@z.com", "<a@x.com>; <b@y.com>; <c@z.com>")]
    [InlineData("a@x.com; Bee <b@y.com>", "<a@x.com>; Bee <b@y.com>")]
    [InlineData(" , ", "")]
    [InlineData(null, "")]
    public void Each_recipient_is_its_own_entry(string? input, string expected) =>
        Assert.Equal(expected, MailTools.FormatAddressList(input));
}
