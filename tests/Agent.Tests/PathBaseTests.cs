using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Web;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Tests;

/// <summary>
/// The agent is served at the site root by default and under PATH_BASE when set. Page, assets,
/// favicon, health and the cookie path must all follow.
/// </summary>
public sealed class PathBaseTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private WebApplicationFactory<Program> With(params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        });

    private static HttpClient Client(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

    private static Session NewSession(WebApplicationFactory<Program> app)
    {
        var tokenData = new TokenData
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            BaseUrl = "https://mail.example.com",
            Username = "alice@example.com",
            ClientId = "smartermail-agent-test",
        };
        var globalContext = new GlobalContext(
            Path.Combine(Path.GetTempPath(), $"sma-never-{Guid.NewGuid():N}.json"), readOnlyMode: true);
        return app.Services.GetRequiredService<SessionStore>().Create(new Account
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
            BaseUrl = "https://mail.example.com",
            ReadOnly = true,
        });
    }

    private static async Task<string> LogoutCookiePath(HttpClient client, Session session, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Cookie", $"{SessionStore.CookieName}={session.Id}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        return setCookie.Split(';').Select(p => p.Trim())
            .Single(p => p.StartsWith("path=", StringComparison.OrdinalIgnoreCase))["path=".Length..];
    }

    [Theory]
    [InlineData("")]
    [InlineData("/mail-agent")]
    public async Task Page_assets_favicon_and_health_are_served(string pathBase)
    {
        var app = pathBase == "" ? factory : With(("PATH_BASE", pathBase));
        using var client = Client(app);

        using var page = await client.GetAsync(pathBase + "/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("href=\"./favicon.svg\"", html);
        Assert.DoesNotContain(HomeLink.Marker, html);

        var css = System.Text.RegularExpressions.Regex.Match(html, "href=\"\\./(v/[0-9a-f]{12}/css/[^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(css);
        using var asset = await client.GetAsync($"{pathBase}/{css}");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);

        using var favicon = await client.GetAsync(pathBase + "/favicon.svg");
        Assert.Equal(HttpStatusCode.OK, favicon.StatusCode);
        Assert.Equal("image/svg+xml", favicon.Content.Headers.ContentType?.MediaType);

        using var health = await client.GetAsync("/health");   // unprefixed always answers
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var prefixedHealth = await client.GetAsync(pathBase + "/health");
        Assert.Equal(HttpStatusCode.OK, prefixedHealth.StatusCode);
    }

    [Fact]
    public async Task Cookie_path_follows_the_path_base()
    {
        using (var root = Client(factory))
            Assert.Equal("/", await LogoutCookiePath(root, NewSession(factory), "/api/auth/logout"));

        var prefixed = With(("PATH_BASE", "/mail-agent"));
        using var client = Client(prefixed);
        Assert.Equal("/mail-agent/", await LogoutCookiePath(client, NewSession(prefixed), "/mail-agent/api/auth/logout"));
    }

    [Fact]
    public async Task Home_link_is_rendered_only_when_configured()
    {
        using (var plain = Client(factory))
            Assert.DoesNotContain("<a href=\"../\"", await plain.GetStringAsync("/"));

        using var client = Client(With(("HOME_LINK_URL", "../"), ("HOME_LINK_TEXT", "← example.com")));
        Assert.Contains("<p><a href=\"../\">← example.com</a></p>", await client.GetStringAsync("/"));
    }
}
