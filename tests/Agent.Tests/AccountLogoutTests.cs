using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmarterMailAgent.Auth;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Tests;

/// <summary>
/// Disposing an account revokes its tokens on SmarterMail (<c>POST /api/v1/auth/logout-user</c>),
/// best effort and bounded, before dropping them from memory.
/// </summary>
public sealed class AccountLogoutTests
{
    private const string BaseUrl = "https://mail.example.com";

    private sealed record Seen(HttpMethod Method, string Path, string? Authorization);

    /// <summary>Records every request (as seen at send time) and answers with <c>respond</c>.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests)
                Requests.Add(new Seen(request.Method, request.RequestUri!.AbsolutePath,
                    request.Headers.Authorization?.ToString()));
            return respond(request, ct);
        }
    }

    private static SmarterMailAuth Auth(StubHandler handler, TimeSpan? logoutTimeout = null) =>
        new(NullLogger<SmarterMailAuth>.Instance, new HttpClient(handler), logoutTimeout);

    private static Account NewAccount(SmarterMailAuth auth)
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

        return new Account
        {
            Id = Account.NewId(),
            Role = AccountRole.User,
            TokenData = tokenData,
            GlobalContext = globalContext,
            UserContext = UserContextFactory.Create(globalContext, tokenData),
            Auth = auth,
            Username = "alice",
            EmailAddress = "alice@example.com",
            Domain = "example.com",
            BaseUrl = BaseUrl,
            ReadOnly = true,
        };
    }

    private static Task<HttpResponseMessage> Status(HttpStatusCode status) =>
        Task.FromResult(new HttpResponseMessage(status));

    [Fact]
    public async Task Dispose_posts_logout_with_the_bearer_token_then_clears_tokens()
    {
        var handler = new StubHandler((_, _) => Status(HttpStatusCode.OK));
        var account = NewAccount(Auth(handler));

        await account.DisposeAsync();
        await account.DisposeAsync(); // idempotent: one revocation only

        var seen = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("/api/v1/auth/logout-user", seen.Path);
        Assert.Equal("Bearer access-1", seen.Authorization);
        Assert.Null(account.TokenData.AccessToken);
        Assert.Null(account.TokenData.RefreshToken);
        Assert.True(account.IsDisposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dispose_swallows_logout_failures(bool connectionFails)
    {
        var handler = new StubHandler((_, _) => connectionFails
            ? throw new HttpRequestException("connection refused")
            : Status(HttpStatusCode.Unauthorized));
        var account = NewAccount(Auth(handler));

        await account.DisposeAsync();

        Assert.Single(handler.Requests);
        Assert.Null(account.TokenData.AccessToken);
        Assert.Null(account.TokenData.RefreshToken);
    }

    [Fact]
    public async Task Dispose_gives_up_on_a_mail_server_that_never_answers()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var account = NewAccount(Auth(handler, TimeSpan.FromMilliseconds(200)));

        var clock = Stopwatch.StartNew();
        await account.DisposeAsync();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"dispose took {clock.Elapsed}");
        Assert.Single(handler.Requests);
        Assert.Null(account.TokenData.AccessToken);
        Assert.Null(account.TokenData.RefreshToken);
    }

    [Fact]
    public async Task Dispose_waits_for_an_in_flight_refresh_and_revokes_the_new_token()
    {
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/refresh-token", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK);

            refreshStarted.SetResult();
            await releaseRefresh.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { accessToken = "access-2", refreshToken = "refresh-2" }),
            };
        });
        var account = NewAccount(Auth(handler));

        var refresh = account.RefreshTokenAsync();
        await refreshStarted.Task;
        var dispose = account.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);

        releaseRefresh.SetResult();
        Assert.False(await refresh); // the account is gone; the refresh does not count
        await dispose;

        // Exactly one revocation, carrying the token the refresh just minted (not the stale one).
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/v1/auth/logout-user", handler.Requests[1].Path);
        Assert.Equal("Bearer access-2", handler.Requests[1].Authorization);
        Assert.Null(account.TokenData.AccessToken);
        Assert.Null(account.TokenData.RefreshToken);
        Assert.False(await account.RefreshTokenAsync());
    }

    [Fact]
    public async Task Logout_without_an_access_token_sends_nothing()
    {
        var handler = new StubHandler((_, _) => Status(HttpStatusCode.OK));

        await Auth(handler).LogoutAsync(new TokenData { BaseUrl = BaseUrl, RefreshToken = "refresh-1" });

        Assert.Empty(handler.Requests);
    }
}
