using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmarterMailAgent.Auth;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Tests;

/// <summary>
/// The MCP token is a separate, revocable credential that opens <c>/mcp</c> only. The session id
/// (the HttpOnly cookie's value) is never a bearer. Hosted in-process over the real Program.
/// </summary>
public sealed class McpTokenTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string BaseUrl = "https://mail.example.com";

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private SessionStore Store => factory.Services.GetRequiredService<SessionStore>();

    private HttpClient Client() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

    /// <summary>A live session with one read-only mailbox account; revocation hits a stub, not the network.</summary>
    private Session NewSession()
    {
        var tokenData = new TokenData
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            BaseUrl = BaseUrl,
            Username = "alice@example.com",
            ClientId = "smartermail-agent-test",
        };
        var globalContext = new GlobalContext(
            Path.Combine(Path.GetTempPath(), $"sma-never-{Guid.NewGuid():N}.json"), readOnlyMode: true);

        return Store.Create(new Account
        {
            Id = Account.NewId(),
            Role = AccountRole.User,
            TokenData = tokenData,
            GlobalContext = globalContext,
            UserContext = UserContextFactory.Create(globalContext, tokenData),
            Auth = new SmarterMailAuth(NullLogger<SmarterMailAuth>.Instance, new HttpClient(new OkHandler())),
            Username = "alice",
            EmailAddress = "alice@example.com",
            Domain = "example.com",
            BaseUrl = BaseUrl,
            ReadOnly = true,
        });
    }

    private static HttpRequestMessage WithCookie(HttpRequestMessage request, Session session)
    {
        request.Headers.Add("Cookie", $"{SessionStore.CookieName}={session.Id}");
        return request;
    }

    private static HttpRequestMessage WithBearer(HttpRequestMessage request, string token)
    {
        request.Headers.Add("Authorization", $"Bearer {token}");
        return request;
    }

    private static HttpRequestMessage McpList() => new(HttpMethod.Post, "/mcp")
    {
        Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json"),
        Headers = { { "Accept", "application/json, text/event-stream" } },
    };

    private async Task<(string Token, DateTimeOffset ExpiresAt)> MintAsync(Session session)
    {
        using var response = await Client().SendAsync(WithCookie(new(HttpMethod.Post, "/api/auth/token"), session));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("token").GetString()!, body.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private async Task<HttpStatusCode> McpStatusAsync(string token)
    {
        using var response = await Client().SendAsync(WithBearer(McpList(), token));
        return response.StatusCode;
    }

    [Fact]
    public async Task Token_is_prefixed_is_not_the_session_id_and_opens_mcp()
    {
        var session = NewSession();
        var (token, expiresAt) = await MintAsync(session);

        Assert.StartsWith(SessionStore.McpTokenPrefix, token);
        Assert.DoesNotContain(session.Id, token);
        Assert.True(expiresAt <= session.ExpiresAt(SessionStore.MaxAge));

        using var response = await Client().SendAsync(WithBearer(McpList(), token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("get_emails", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cookie_still_opens_mcp()
    {
        var session = NewSession();
        using var response = await Client().SendAsync(WithCookie(McpList(), session));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/auth/session")]
    [InlineData("GET", "/api/tools")]
    [InlineData("POST", "/api/tools/call")]
    [InlineData("POST", "/api/accounts")]
    [InlineData("POST", "/api/auth/token")]
    [InlineData("DELETE", "/api/auth/token")]
    [InlineData("POST", "/api/auth/logout")]
    public async Task Token_is_refused_on_every_api_endpoint(string method, string path)
    {
        var session = NewSession();
        var (token, _) = await MintAsync(session);

        var request = WithBearer(new HttpRequestMessage(new HttpMethod(method), path), token);
        if (method == "POST")
            request.Content = JsonContent.Create(new { name = "get_emails", arguments = new { } });
        using var response = await Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // Nothing happened to the session: the token still works on /mcp.
        Assert.Equal(HttpStatusCode.OK, await McpStatusAsync(token));
        Assert.NotNull(Store.Get(session.Id));
    }

    [Theory]
    [InlineData("GET", "/api/auth/session")]
    [InlineData("POST", "/api/auth/token")]
    [InlineData("POST", "/mcp")]
    public async Task Session_id_is_not_a_bearer(string method, string path)
    {
        var session = NewSession();
        var request = path == "/mcp" ? McpList() : new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await Client().SendAsync(WithBearer(request, session.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Minting_again_rotates()
    {
        var session = NewSession();
        var (first, _) = await MintAsync(session);
        var (second, _) = await MintAsync(session);

        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(first));
        Assert.Equal(HttpStatusCode.OK, await McpStatusAsync(second));
    }

    [Fact]
    public async Task Delete_revokes_and_session_reports_the_state()
    {
        var session = NewSession();
        var client = Client();

        async Task<JsonElement> McpTokenStateAsync()
        {
            using var response = await client.SendAsync(WithCookie(new(HttpMethod.Get, "/api/auth/session"), session));
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mcpToken");
        }

        Assert.False((await McpTokenStateAsync()).GetProperty("active").GetBoolean());

        var (token, expiresAt) = await MintAsync(session);
        var state = await McpTokenStateAsync();
        Assert.True(state.GetProperty("active").GetBoolean());
        Assert.Equal(expiresAt, state.GetProperty("expiresAt").GetDateTimeOffset());

        using (var response = await client.SendAsync(WithCookie(new(HttpMethod.Delete, "/api/auth/token"), session)))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(token));
        state = await McpTokenStateAsync();
        Assert.False(state.GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("expiresAt").ValueKind);
    }

    [Fact]
    public async Task Token_dies_with_logout()
    {
        var session = NewSession();
        var (token, _) = await MintAsync(session);

        using (var response = await Client().SendAsync(WithCookie(new(HttpMethod.Post, "/api/auth/logout"), session)))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(token));
    }

    [Fact]
    public async Task Token_dies_with_the_last_account()
    {
        var session = NewSession();
        var (token, _) = await MintAsync(session);
        var account = Assert.Single(session.Accounts);

        using (var response = await Client().SendAsync(
                   WithCookie(new(HttpMethod.Delete, $"/api/accounts/{account.Id}"), session)))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(token));
    }

    [Fact]
    public async Task Mcp_requests_count_as_session_activity()
    {
        var session = NewSession();
        var (token, _) = await MintAsync(session);
        var before = session.LastSeenAt;

        await Task.Delay(20);
        Assert.Equal(HttpStatusCode.OK, await McpStatusAsync(token));
        Assert.True(session.LastSeenAt > before);
    }

    [Fact]
    public void A_lapsed_token_no_longer_matches()
    {
        var session = new Session { Id = SessionStore.NewSessionId() };
        var hash = new byte[32];

        Assert.True(session.TrySetMcpToken(hash, DateTimeOffset.UtcNow.AddMinutes(1), out _));
        Assert.True(session.McpTokenMatches(hash));
        Assert.False(session.McpTokenMatches(new byte[32].Select((_, i) => (byte)i).ToArray()));

        Assert.True(session.TrySetMcpToken(hash, DateTimeOffset.UtcNow.AddSeconds(-1), out var previous));
        Assert.NotNull(previous);
        Assert.False(session.McpTokenMatches(hash));
        Assert.Null(session.McpTokenExpiresAt);
    }
}
