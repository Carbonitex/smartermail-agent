using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmarterMailAgent.Auth;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Tests;

/// <summary>
/// SmarterMail refuses a refresh token it cannot verify with 200 + success:false and empty tokens. That
/// must read as Rejected and leave the TokenData alone, not as Refreshed with the tokens blanked.
/// </summary>
public class RefreshAnswerTests
{
    private sealed class FixedAnswer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static TokenData Token() => new()
    {
        BaseUrl = ResumeFixtures.HostA,
        AccessToken = "access-1",
        RefreshToken = "refresh-1",
        ClientId = "smartermail-agent-a",
        Expiration = "2026-01-01T00:00:00Z",
        RefreshExpiration = "2026-03-01T00:00:00Z",
    };

    private static Task<RefreshResult> RefreshAsync(TokenData token, HttpStatusCode status, string body) =>
        new SmarterMailAuth(NullLogger<SmarterMailAuth>.Instance, new HttpClient(new FixedAnswer(status, body)))
            .TryRefreshAsync(token);

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"accessToken":"","refreshToken":"","accessTokenExpiration":null,"refreshTokenExpiration":null,"success":false,"resultCode":200,"message":"IDX10517: Signature validation failed."}""", RefreshResult.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, """{"accessToken":"","refreshToken":"","success":false,"resultCode":401,"message":"Token not found in settings"}""", RefreshResult.Rejected)]
    [InlineData(HttpStatusCode.OK, """{"success":true,"accessToken":"","refreshToken":""}""", RefreshResult.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, "<html>Bad Gateway</html>", RefreshResult.Unavailable)]
    public async Task A_refused_or_empty_answer_keeps_the_stored_tokens(HttpStatusCode status, string body, RefreshResult expected)
    {
        var token = Token();

        Assert.Equal(expected, await RefreshAsync(token, status, body));
        Assert.Equal("access-1", token.AccessToken);
        Assert.Equal("refresh-1", token.RefreshToken);
        Assert.Equal("2026-03-01T00:00:00Z", token.RefreshExpiration);
    }

    [Fact]
    public async Task A_good_answer_rotates_and_keeps_fields_it_did_not_send()
    {
        var token = Token();

        var result = await RefreshAsync(token, HttpStatusCode.OK,
            """{"success":true,"accessToken":"access-2","refreshToken":"refresh-2","accessTokenExpiration":"2026-01-02T00:00:00Z","refreshTokenExpiration":""}""");

        Assert.Equal(RefreshResult.Refreshed, result);
        Assert.Equal("access-2", token.AccessToken);
        Assert.Equal("refresh-2", token.RefreshToken);
        Assert.Equal("2026-01-02T00:00:00Z", token.Expiration);
        Assert.Equal("2026-03-01T00:00:00Z", token.RefreshExpiration);
    }
}
