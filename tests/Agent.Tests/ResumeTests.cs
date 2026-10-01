using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Controllers;
using SmarterMailAgent.Web;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Tests;

/// <summary>A stub SmarterMail: records every request and answers refresh-token by rotating.</summary>
internal sealed class StubSmarterMail : HttpMessageHandler
{
    public sealed record Seen(string Host, string Path, string? Authorization, string? Token);

    /// <summary>Per host: the status refresh-token answers (default 200, rotating the token).</summary>
    public Dictionary<string, HttpStatusCode> RefreshStatus { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<Seen> Requests { get; } = [];

    public IEnumerable<Seen> Logouts => Requests.Where(r => r.Path == "/api/v1/auth/logout-user");
    public IEnumerable<Seen> Refreshes => Requests.Where(r => r.Path == "/api/v1/auth/refresh-token");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string? token = null;
        if (request.Content is not null)
        {
            var body = await request.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("token", out var t))
                token = t.GetString();
        }

        var host = request.RequestUri!.Host;
        var path = request.RequestUri.AbsolutePath;
        lock (Requests)
            Requests.Add(new Seen(host, path, request.Headers.Authorization?.ToString(), token));

        if (path != "/api/v1/auth/refresh-token")
            return new HttpResponseMessage(HttpStatusCode.OK);

        var status = RefreshStatus.TryGetValue(host, out var s) ? s : HttpStatusCode.OK;
        if (status != HttpStatusCode.OK)
            return new HttpResponseMessage(status);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                accessToken = $"access-after-{token}",
                refreshToken = $"{token}+",
                accessTokenExpiration = DateTime.UtcNow.AddMinutes(15).ToString("O"),
                refreshTokenExpiration = DateTime.UtcNow.AddDays(60).ToString("O"),
            }),
        };
    }

    public SmarterMailAuth Auth() => new(NullLogger<SmarterMailAuth>.Instance, new HttpClient(this), TimeSpan.FromSeconds(2));
}

internal static class ResumeFixtures
{
    // Public IP literals: HostGuard accepts them without a DNS lookup.
    public const string HostA = "https://93.184.215.14";
    public const string HostB = "https://93.184.215.15";

    public static byte[] Key() => RandomNumberGenerator.GetBytes(32);

    public static Account NewAccount(SmarterMailAuth auth, string baseUrl = HostA, string login = "alice@example.com",
        string refresh = "refresh-1", string clientId = "smartermail-agent-a", string? expiration = null, bool readOnly = true,
        string? id = null)
    {
        var tokenData = new TokenData
        {
            AccessToken = "access-1",
            RefreshToken = refresh,
            BaseUrl = baseUrl,
            Username = login,
            ClientId = clientId,
            Expiration = expiration ?? DateTime.UtcNow.AddMinutes(15).ToString("O"),
            RefreshExpiration = DateTime.UtcNow.AddDays(60).ToString("O"),
        };
        var globalContext = new GlobalContext(
            Path.Combine(Path.GetTempPath(), $"sma-never-{Guid.NewGuid():N}.json"), readOnlyMode: readOnly);

        return new Account
        {
            Id = id ?? Account.NewId(),
            Role = login.Contains('@') ? AccountRole.User : AccountRole.SysAdmin,
            TokenData = tokenData,
            GlobalContext = globalContext,
            UserContext = UserContextFactory.Create(globalContext, tokenData),
            Auth = auth,
            Username = login.Split('@')[0],
            EmailAddress = login,
            Domain = login.Contains('@') ? login.Split('@')[1] : string.Empty,
            BaseUrl = baseUrl,
            ReadOnly = readOnly,
        };
    }

    public static ResumeAccount Entry(string baseUrl, string login, string refresh, string clientId,
        AccountRole role = AccountRole.User, string? refreshExpiration = null) =>
        new(baseUrl, login, role, ReadOnly: false, clientId, refresh,
            refreshExpiration ?? DateTimeOffset.UtcNow.AddDays(30).ToString("O"), role == AccountRole.SysAdmin ? "admin" : "user");

    public static ResumePayload Payload(DateTimeOffset since, params ResumeAccount[] accounts) =>
        new(1, since, DateTimeOffset.UtcNow, accounts);

    public static JsonElement Json(object? value) =>
        JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

/// <summary>The sealed bundle itself: format, keys, tampering, age.</summary>
public sealed class ResumeSealerTests
{
    [Fact]
    public void A_remembered_session_round_trips_through_seal_and_unseal()
    {
        var sealer = new ResumeSealer(ResumeFixtures.Key());
        var stub = new StubSmarterMail();
        var session = new Session { Id = SessionStore.NewSessionId() };
        session.Add(ResumeFixtures.NewAccount(stub.Auth()), 5, out _);
        session.Add(ResumeFixtures.NewAccount(stub.Auth(), ResumeFixtures.HostB, "admin", "refresh-2", "smartermail-agent-b"), 5, out _);
        var since = DateTimeOffset.UtcNow.AddDays(-3);
        session.Remember(since, sealer.UntilFor(since));

        var (bundle, version) = sealer.Seal(session)!.Value;
        Assert.Equal(session.ResumeVersion, version);
        Assert.DoesNotContain("refresh-1", bundle);   // sealed, not merely encoded

        var opened = sealer.Unseal(bundle);
        Assert.True(opened.Ok, opened.Failure?.ToString());
        Assert.Equal(since, opened.Payload!.RememberedSince);
        Assert.Collection(opened.Payload.Accounts,
            a =>
            {
                Assert.Equal(ResumeFixtures.HostA, a.BaseUrl);
                Assert.Equal("alice@example.com", a.Login);
                Assert.Equal(AccountRole.User, a.Role);
                Assert.Equal("refresh-1", a.RefreshToken);
                Assert.Equal("smartermail-agent-a", a.ClientId);
                Assert.True(a.ReadOnly);
            },
            a =>
            {
                Assert.Equal("admin", a.Login);
                Assert.Equal(AccountRole.SysAdmin, a.Role);
                Assert.Equal("refresh-2", a.RefreshToken);
            });
    }

    [Fact]
    public void A_session_that_is_not_remembered_gets_no_bundle()
    {
        var sealer = new ResumeSealer(ResumeFixtures.Key());
        var session = new Session { Id = SessionStore.NewSessionId() };
        session.Add(ResumeFixtures.NewAccount(new StubSmarterMail().Auth()), 5, out _);

        Assert.Null(sealer.Seal(session));

        session.Remember(DateTimeOffset.UtcNow, sealer.UntilFor(DateTimeOffset.UtcNow));
        session.StopRemembering();
        Assert.Null(sealer.Seal(session));
        Assert.NotNull(session.RememberedSince);   // kept, so switching back on cannot restart the chain
    }

    [Fact]
    public void Tampering_anywhere_is_rejected()
    {
        var sealer = new ResumeSealer(ResumeFixtures.Key());
        var bundle = sealer.SealPayload(ResumeFixtures.Payload(DateTimeOffset.UtcNow,
            ResumeFixtures.Entry(ResumeFixtures.HostA, "alice@example.com", "r", "c")));
        var raw = Convert.FromBase64String(Pad(bundle.Replace('-', '+').Replace('_', '/')));

        foreach (var index in new[] { 5, raw.Length / 2, raw.Length - 1 })
        {
            var copy = (byte[])raw.Clone();
            copy[index] ^= 0x01;
            Assert.Equal(ResumeSealer.UnsealFailure.Tampered, sealer.Unseal(Encode(copy)).Failure);
        }

        var badKeyId = (byte[])raw.Clone();
        badKeyId[1] ^= 0xFF;
        Assert.Equal(ResumeSealer.UnsealFailure.UnknownKey, sealer.Unseal(Encode(badKeyId)).Failure);

        var badFormat = (byte[])raw.Clone();
        badFormat[0] = 0x02;
        Assert.Equal(ResumeSealer.UnsealFailure.Malformed, sealer.Unseal(Encode(badFormat)).Failure);

        Assert.Equal(ResumeSealer.UnsealFailure.Malformed, sealer.Unseal("not a bundle!").Failure);
        Assert.Equal(ResumeSealer.UnsealFailure.Malformed, sealer.Unseal(bundle[..20]).Failure);
        Assert.Equal(ResumeSealer.UnsealFailure.Malformed, sealer.Unseal(null).Failure);
        Assert.Equal(ResumeSealer.UnsealFailure.Malformed,
            sealer.Unseal(new string('A', ResumeSealer.MaxBundleLength + 1)).Failure);
    }

    [Fact]
    public void A_bundle_from_another_key_is_rejected()
    {
        var bundle = new ResumeSealer(ResumeFixtures.Key()).SealPayload(ResumeFixtures.Payload(DateTimeOffset.UtcNow,
            ResumeFixtures.Entry(ResumeFixtures.HostA, "alice@example.com", "r", "c")));

        var result = new ResumeSealer(ResumeFixtures.Key()).Unseal(bundle);

        Assert.False(result.Ok);
        // Usually UnknownKey; Tampered on the 1-in-256 key-id collision. Never opened.
        Assert.Contains(result.Failure, new ResumeSealer.UnsealFailure?[]
            { ResumeSealer.UnsealFailure.UnknownKey, ResumeSealer.UnsealFailure.Tampered });
    }

    [Fact]
    public void The_previous_key_still_opens_bundles_but_new_ones_use_the_current_key()
    {
        var oldKey = ResumeFixtures.Key();
        var newKey = ResumeFixtures.Key();
        var payload = ResumeFixtures.Payload(DateTimeOffset.UtcNow,
            ResumeFixtures.Entry(ResumeFixtures.HostA, "alice@example.com", "r", "c"));
        var oldBundle = new ResumeSealer(oldKey).SealPayload(payload);

        var rotated = new ResumeSealer(newKey, previousKey: oldKey);
        Assert.True(rotated.Unseal(oldBundle).Ok);

        var newBundle = rotated.SealPayload(payload);
        Assert.True(new ResumeSealer(newKey).Unseal(newBundle).Ok);
        Assert.False(new ResumeSealer(oldKey).Unseal(newBundle).Ok);
        Assert.False(new ResumeSealer(newKey).Unseal(oldBundle).Ok);   // once the previous key is dropped
    }

    [Theory]
    [InlineData(-29, true)]
    [InlineData(-31, false)]
    [InlineData(1, false)]      // a chain that claims to start tomorrow
    public void The_chain_is_limited_to_RESUME_DAYS_from_the_original_sign_in(int sinceDays, bool ok)
    {
        var sealer = new ResumeSealer(ResumeFixtures.Key(), days: 30);
        var bundle = sealer.SealPayload(ResumeFixtures.Payload(DateTimeOffset.UtcNow.AddDays(sinceDays),
            ResumeFixtures.Entry(ResumeFixtures.HostA, "alice@example.com", "r", "c")));

        var result = sealer.Unseal(bundle);

        Assert.Equal(ok, result.Ok);
        if (!ok)
            Assert.Equal(ResumeSealer.UnsealFailure.Expired, result.Failure);
    }

    [Fact]
    public void RESUME_DAYS_cannot_exceed_the_refresh_token_lifetime()
    {
        Assert.Equal(TimeSpan.FromDays(60), new ResumeSealer(ResumeFixtures.Key(), days: 365).MaxAge);
        Assert.Equal(TimeSpan.FromDays(30), new ResumeSealer(ResumeFixtures.Key()).MaxAge);
    }

    [Fact]
    public void Without_a_key_the_feature_is_off()
    {
        var sealer = new ResumeSealer(null);
        var session = new Session { Id = SessionStore.NewSessionId() };
        session.Add(ResumeFixtures.NewAccount(new StubSmarterMail().Auth()), 5, out _);
        session.Remember(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        Assert.False(sealer.Enabled);
        Assert.Null(sealer.Seal(session));
        Assert.False(sealer.Unseal("AQ" + new string('A', 60)).Ok);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("too-short", false)]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=", true)]   // base64
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8", true)]    // base64url, unpadded
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwd", false)]      // 30 bytes
    public void Keys_must_be_32_bytes_of_base64(string? value, bool valid) =>
        Assert.Equal(valid, ResumeSealer.DecodeKey(value) is not null);

    private static string Pad(string s) => s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

/// <summary>Revoke vs forget, the resume version, lazy refresh and the sweeper.</summary>
public sealed class RememberedSessionTests
{
    private static (SessionStore Store, Session Session, StubSmarterMail Stub) Remembered(bool remembered)
    {
        var stub = new StubSmarterMail();
        var store = new SessionStore(NullLogger<SessionStore>.Instance);
        var session = store.Create(ResumeFixtures.NewAccount(stub.Auth()));
        if (remembered)
            session.Remember(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
        return (store, session, stub);
    }

    [Fact]
    public async Task A_remembered_session_that_expires_is_forgotten_without_revoking()
    {
        var (store, session, stub) = Remembered(remembered: true);
        var account = session.Accounts[0];

        await store.ExpireAsync(session.Id);

        Assert.Empty(stub.Logouts);
        Assert.Null(store.Get(session.Id));
        Assert.True(account.IsDisposed);
        Assert.Null(account.TokenData.RefreshToken);   // dropped from memory all the same
    }

    [Fact]
    public async Task An_ordinary_session_that_expires_is_still_revoked()
    {
        var (store, session, stub) = Remembered(remembered: false);

        await store.ExpireAsync(session.Id);

        Assert.Equal("Bearer access-1", Assert.Single(stub.Logouts).Authorization);
    }

    [Fact]
    public async Task Explicit_logout_and_account_removal_revoke_a_remembered_session()
    {
        var (store, session, stub) = Remembered(remembered: true);
        session.Add(ResumeFixtures.NewAccount(stub.Auth(), ResumeFixtures.HostB, "bob@example.com", "r-b", "c-b"), 5, out _);

        await store.RemoveAccountAsync(session, session.Accounts[1].Id);   // DELETE /api/accounts/{id}
        Assert.Single(stub.Logouts);

        await store.RemoveAsync(session.Id);                                // POST /api/auth/logout
        Assert.Equal(2, stub.Logouts.Count());
    }

    [Fact]
    public void The_resume_version_moves_on_every_account_change_and_rotation()
    {
        var (_, session, stub) = Remembered(remembered: true);
        var seen = new List<long> { session.ResumeVersion };

        session.Add(ResumeFixtures.NewAccount(stub.Auth(), ResumeFixtures.HostB, "bob@example.com", "r-b", "c-b"), 5, out _);
        seen.Add(session.ResumeVersion);
        session.Remove(session.Accounts[1].Id);
        seen.Add(session.ResumeVersion);
        session.Accounts[0].Rotated!();
        seen.Add(session.ResumeVersion);

        Assert.Equal(seen.Order().Distinct(), seen);   // strictly increasing
        Assert.True(seen[0] >= DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task A_refresh_rotates_and_bumps_the_version()
    {
        var (_, session, stub) = Remembered(remembered: true);
        var before = session.ResumeVersion;

        Assert.True(await session.Accounts[0].RefreshTokenAsync());

        Assert.Equal("refresh-1+", session.Accounts[0].TokenData.RefreshToken);
        Assert.True(session.ResumeVersion > before);
        Assert.Single(stub.Refreshes);
    }

    [Fact]
    public async Task Lazy_refresh_only_calls_SmarterMail_near_expiry_and_once_for_concurrent_calls()
    {
        var stub = new StubSmarterMail();
        var fresh = ResumeFixtures.NewAccount(stub.Auth());
        fresh.LastTokenRefresh = DateTimeOffset.UtcNow.AddMinutes(-12);   // past the sweeper's 8 minutes

        Assert.True(fresh.NeedsTokenRefresh());
        Assert.False(fresh.NeedsTokenRefresh(lazy: true));
        Assert.Equal(RefreshResult.Refreshed, await fresh.EnsureFreshAsync());
        Assert.Empty(stub.Refreshes);

        var stale = ResumeFixtures.NewAccount(stub.Auth(), expiration: DateTime.UtcNow.AddMinutes(1).ToString("O"));
        var results = await Task.WhenAll(stale.EnsureFreshAsync(), stale.EnsureFreshAsync(), stale.EnsureFreshAsync());

        Assert.All(results, r => Assert.Equal(RefreshResult.Refreshed, r));
        Assert.Single(stub.Refreshes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RefreshResult.Rejected)]
    [InlineData(HttpStatusCode.BadRequest, RefreshResult.Rejected)]
    [InlineData(HttpStatusCode.BadGateway, RefreshResult.Unavailable)]
    public async Task Refresh_failures_say_whether_the_token_is_dead(HttpStatusCode status, RefreshResult expected)
    {
        var stub = new StubSmarterMail();
        stub.RefreshStatus["93.184.215.14"] = status;
        var account = ResumeFixtures.NewAccount(stub.Auth(), expiration: DateTime.UtcNow.ToString("O"));

        Assert.Equal(expected, await account.EnsureFreshAsync());
    }

    [Fact]
    public async Task The_sweeper_leaves_remembered_sessions_to_refresh_lazily()
    {
        var stub = new StubSmarterMail();
        var store = new SessionStore(NullLogger<SessionStore>.Instance);
        var remembered = store.Create(ResumeFixtures.NewAccount(stub.Auth(), refresh: "remembered"));
        remembered.Remember(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
        var ordinary = store.Create(ResumeFixtures.NewAccount(stub.Auth(), refresh: "ordinary"));
        foreach (var account in remembered.Accounts.Concat(ordinary.Accounts))
            account.LastTokenRefresh = DateTimeOffset.UtcNow.AddMinutes(-9);

        var sweeper = new SessionSweeper(store, new PendingLoginStore(), new HostLoginThrottle(),
            NullLogger<SessionSweeper>.Instance);
        await sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.Equal("ordinary", Assert.Single(stub.Refreshes).Token);
        Assert.Equal("remembered", remembered.Accounts[0].TokenData.RefreshToken);
    }

    [Fact]
    public async Task A_remembered_session_past_its_chain_is_treated_as_ordinary()
    {
        var (store, session, stub) = Remembered(remembered: false);
        session.Remember(DateTimeOffset.UtcNow.AddDays(-31), DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.False(session.IsRemembered);
        await store.ExpireAsync(session.Id);
        Assert.Single(stub.Logouts);
    }
}

/// <summary>X-Resume-Version: only to the cookie, only when newer.</summary>
public sealed class ResumeHeaderTests
{
    private static (DefaultHttpContext Context, Session Session) Request(bool cookie, bool remembered)
    {
        var session = new Session { Id = SessionStore.NewSessionId() };
        session.Add(ResumeFixtures.NewAccount(new StubSmarterMail().Auth()), 5, out _);
        if (remembered)
            session.Remember(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));

        var context = new DefaultHttpContext();
        context.Items["session"] = session;
        if (cookie)
            context.Items[SessionAuthenticationHandler.ViaCookieItem] = true;
        return (context, session);
    }

    [Fact]
    public void A_newer_version_is_announced_to_the_cookie()
    {
        var (context, session) = Request(cookie: true, remembered: true);

        ResumeHeaders.Apply(context, session.ResumeVersion - 1);

        Assert.Equal(session.ResumeVersion.ToString(), context.Response.Headers[ResumeHeaders.VersionHeader].ToString());
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void Nothing_is_announced_when_the_browser_is_current()
    {
        var (context, session) = Request(cookie: true, remembered: true);

        ResumeHeaders.Apply(context, session.ResumeVersion);

        Assert.False(context.Response.Headers.ContainsKey(ResumeHeaders.VersionHeader));
    }

    [Fact]
    public void A_bearer_caller_never_hears_about_the_bundle()
    {
        var (context, _) = Request(cookie: false, remembered: true);

        ResumeHeaders.Apply(context, 0);

        Assert.False(context.Response.Headers.ContainsKey(ResumeHeaders.VersionHeader));
    }

    [Fact]
    public void An_ordinary_session_announces_nothing()
    {
        var (context, _) = Request(cookie: true, remembered: false);

        ResumeHeaders.Apply(context, 0);

        Assert.False(context.Response.Headers.ContainsKey(ResumeHeaders.VersionHeader));
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1758800000000", 1758800000000L)]
    [InlineData("-1", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void The_client_version_header_is_parsed_strictly(string value, long? expected)
    {
        var context = new DefaultHttpContext();
        if (value.Length > 0)
            context.Request.Headers[ResumeHeaders.VersionHeader] = value;

        Assert.Equal(expected, ResumeHeaders.ClientVersion(context.Request));
    }
}

/// <summary>POST/GET/PUT/DELETE /api/auth/resume through the real controller, against a stub SmarterMail.</summary>
public sealed class ResumeControllerTests
{
    private sealed class Rig
    {
        public StubSmarterMail Stub { get; } = new();
        public SessionStore Store { get; } = new(NullLogger<SessionStore>.Instance);
        public HostLoginThrottle Throttle { get; } = new();
        public ResumeSealer Sealer { get; init; } = new(ResumeFixtures.Key());

        public ResumeController Controller(Session? session = null, bool cookie = true, string? cookieHeader = null)
        {
            var controller = new ResumeController(Store, new PendingLoginStore(), Stub.Auth(), Throttle, Sealer,
                NullLogger<ResumeController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
            if (session is not null)
            {
                controller.HttpContext.Items["session"] = session;
                if (cookie)
                    controller.HttpContext.Items[SessionAuthenticationHandler.ViaCookieItem] = true;
            }
            if (cookieHeader is not null)
                controller.HttpContext.Request.Headers.Cookie = cookieHeader;
            return controller;
        }

        public string Bundle(params ResumeAccount[] accounts) =>
            Sealer.SealPayload(ResumeFixtures.Payload(DateTimeOffset.UtcNow.AddDays(-2), accounts));
    }

    private static (int Status, JsonElement Body) Unpack(IActionResult result) => result switch
    {
        OkObjectResult ok => (200, ResumeFixtures.Json(ok.Value)),
        ObjectResult obj => (obj.StatusCode ?? 0, ResumeFixtures.Json(obj.Value)),
        StatusCodeResult code => (code.StatusCode, default),
        _ => throw new InvalidOperationException(result.GetType().Name),
    };

    [Fact]
    public async Task Resume_refreshes_every_account_opens_a_session_and_returns_the_rotated_bundle()
    {
        var rig = new Rig();
        var bundle = rig.Bundle(
            ResumeFixtures.Entry(ResumeFixtures.HostA, "alice@example.com", "r-a", "c-a"),
            ResumeFixtures.Entry(ResumeFixtures.HostB, "admin", "r-b", "c-b", AccountRole.SysAdmin));
        var controller = rig.Controller();

        var (status, body) = Unpack(await controller.Resume(new ResumeController.ResumeRequest(bundle), CancellationToken.None));

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("remembered").GetBoolean());
        Assert.Equal(0, body.GetProperty("skipped").GetArrayLength());
        Assert.Equal(new[] { "alice@example.com", "sysadmin:admin@93.184.215.15" },
            body.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("handle").GetString()));
        Assert.Equal(new[] { "r-a", "r-b" }, rig.Stub.Refreshes.Select(r => r.Token).Order());
        Assert.Empty(rig.Stub.Logouts);

        var cookie = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{SessionStore.CookieName}=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        var session = Assert.Single(rig.Store.Snapshot());
        Assert.True(session.IsRemembered);
        Assert.Equal(body.GetProperty("version").GetInt64(), session.ResumeVersion);
        Assert.Equal(AccountRole.SysAdmin, session.Accounts[1].Role);
        Assert.Equal("access-after-r-a", session.Accounts[0].TokenData.AccessToken);

        // The new bundle carries the rotated tokens and the ORIGINAL chain start.
        var next = rig.Sealer.Unseal(body.GetProperty("bundle").GetString());
        Assert.True(next.Ok);
        Assert.Equal(new[] { "r-a+", "r-b+" }, next.Payload!.Accounts.Select(a => a.RefreshToken));
        Assert.Equal(rig.Sealer.Unseal(bundle).Payload!.RememberedSince, next.Payload.RememberedSince);
        Assert.Equal(new[] { "c-a", "c-b" }, next.Payload.Accounts.Select(a => a.ClientId));
    }

    [Fact]
    public async Task Accounts_that_fail_are_skipped_and_counted_against_their_server()
    {
        var rig = new Rig();
        rig.Stub.RefreshStatus["93.184.215.15"] = HttpStatusCode.Unauthorized;
        var bundle = rig.Bundle(
            ResumeFixtures.Entry(ResumeFixtures.HostA, "alice@example.com", "r-a", "c-a"),
            ResumeFixtures.Entry(ResumeFixtures.HostB, "bob@example.com", "r-b", "c-b"),
            ResumeFixtures.Entry(ResumeFixtures.HostA, "old@example.com", "r-c", "c-c",
                refreshExpiration: DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")));

        var (status, body) = Unpack(await rig.Controller().Resume(new ResumeController.ResumeRequest(bundle), CancellationToken.None));

        Assert.Equal(200, status);
        Assert.Equal("alice@example.com", Assert.Single(body.GetProperty("accounts").EnumerateArray()).GetProperty("handle").GetString());
        var skipped = body.GetProperty("skipped").EnumerateArray()
            .Select(s => (s.GetProperty("login").GetString(), s.GetProperty("reason").GetString())).ToList();
        Assert.Contains(("bob@example.com", "REJECTED"), skipped);
        Assert.Contains(("old@example.com", "EXPIRED"), skipped);
        Assert.DoesNotContain(rig.Stub.Refreshes, r => r.Token == "r-c");   // lapsed: SmarterMail not asked
        Assert.Equal(1, rig.Throttle.FailureCount(ResumeFixtures.HostB));
        Assert.Equal(0, rig.Throttle.FailureCount(ResumeFixtures.HostA));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401, "RESUME_EXPIRED")]
    [InlineData(HttpStatusCode.InternalServerError, 503, "RESUME_UNAVAILABLE")]
    public async Task With_no_account_left_the_answer_says_whether_to_keep_the_bundle(
        HttpStatusCode refresh, int expectedStatus, string code)
    {
        var rig = new Rig();
        rig.Stub.RefreshStatus["93.184.215.14"] = refresh;
        var bundle = rig.Bundle(ResumeFixtures.Entry(ResumeFixtures.HostA, "alice@example.com", "r-a", "c-a"));
        var controller = rig.Controller();

        var (status, body) = Unpack(await controller.Resume(new ResumeController.ResumeRequest(bundle), CancellationToken.None));

        Assert.Equal(expectedStatus, status);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Empty(rig.Store.Snapshot());
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task Tampered_and_expired_bundles_are_refused_without_calling_SmarterMail()
    {
        var rig = new Rig();
        var good = rig.Bundle(ResumeFixtures.Entry(ResumeFixtures.HostA, "alice@example.com", "r-a", "c-a"));
        var tampered = good[..^4] + (good[^4] == 'A' ? "B" : "A") + good[^3..];
        var old = rig.Sealer.SealPayload(ResumeFixtures.Payload(DateTimeOffset.UtcNow.AddDays(-31),
            ResumeFixtures.Entry(ResumeFixtures.HostA, "alice@example.com", "r-a", "c-a")));

        var (status1, body1) = Unpack(await rig.Controller().Resume(new(tampered), CancellationToken.None));
        var (status2, body2) = Unpack(await rig.Controller().Resume(new(old), CancellationToken.None));
        var (status3, _) = Unpack(await rig.Controller().Resume(new(null), CancellationToken.None));

        Assert.Equal((400, "RESUME_INVALID"), (status1, body1.GetProperty("code").GetString()));
        Assert.Equal((401, "RESUME_EXPIRED"), (status2, body2.GetProperty("code").GetString()));
        Assert.Equal(400, status3);
        Assert.Empty(rig.Stub.Requests);
    }

    [Fact]
    public async Task A_throttled_server_refuses_the_resume_before_any_token_rotates()
    {
        var rig = new Rig();
        for (var i = 0; i < HostLoginThrottle.DefaultLimit; i++)
        {
            using var attempt = rig.Throttle.TryBegin(ResumeFixtures.HostB, out _);
            attempt!.Finish(failed: true);
        }
        var bundle = rig.Bundle(
            ResumeFixtures.Entry(ResumeFixtures.HostA, "alice@example.com", "r-a", "c-a"),
            ResumeFixtures.Entry(ResumeFixtures.HostB, "bob@example.com", "r-b", "c-b"));

        var (status, body) = Unpack(await rig.Controller().Resume(new(bundle), CancellationToken.None));

        Assert.Equal((429, "HOST_THROTTLED"), (status, body.GetProperty("code").GetString()));
        Assert.Empty(rig.Stub.Requests);
    }

    [Fact]
    public async Task A_bundle_whose_host_now_fails_the_SSRF_guard_is_not_contacted()
    {
        var rig = new Rig();
        var bundle = rig.Bundle(ResumeFixtures.Entry("https://169.254.169.254", "alice@example.com", "r-a", "c-a"));

        var (status, body) = Unpack(await rig.Controller().Resume(new(bundle), CancellationToken.None));

        Assert.Equal(401, status);
        Assert.Equal("BLOCKED_HOST", body.GetProperty("skipped")[0].GetProperty("reason").GetString());
        Assert.Empty(rig.Stub.Requests);
    }

    [Fact]
    public async Task A_live_session_on_the_cookie_is_replaced_but_the_resumed_clientId_is_not_revoked()
    {
        var rig = new Rig();
        var shared = ResumeFixtures.NewAccount(rig.Stub.Auth(), refresh: "r-a", clientId: "c-a");
        var unrelated = ResumeFixtures.NewAccount(rig.Stub.Auth(), ResumeFixtures.HostB, "bob@example.com", "r-x", "c-x");
        var previous = rig.Store.Create(shared);
        previous.Add(unrelated, 5, out _);
        var bundle = rig.Bundle(ResumeFixtures.Entry(ResumeFixtures.HostA, "alice@example.com", "r-a", "c-a"));

        var (status, _) = Unpack(await rig.Controller(cookieHeader: $"{SessionStore.CookieName}={previous.Id}")
            .Resume(new(bundle), CancellationToken.None));

        Assert.Equal(200, status);
        Assert.Null(rig.Store.Get(previous.Id));
        // Only the unrelated login is revoked: revoking c-a would kill the pair the resume just minted.
        Assert.Equal("93.184.215.15", Assert.Single(rig.Stub.Logouts).Host);
        Assert.True(shared.IsDisposed);
    }

    [Fact]
    public void Enable_get_and_disable_work_for_the_cookie_only()
    {
        var rig = new Rig();
        var session = rig.Store.Create(ResumeFixtures.NewAccount(rig.Stub.Auth()));

        Assert.Equal(404, Unpack(rig.Controller(session).Get()).Status);                 // not remembered yet
        Assert.Equal(403, Unpack(rig.Controller(session, cookie: false).Enable()).Status); // bearer

        var (status, body) = Unpack(rig.Controller(session).Enable());
        Assert.Equal(200, status);
        Assert.True(session.IsRemembered);
        Assert.Equal(session.ResumeVersion, body.GetProperty("version").GetInt64());
        Assert.True(rig.Sealer.Unseal(body.GetProperty("bundle").GetString()).Ok);

        Assert.Equal(200, Unpack(rig.Controller(session).Get()).Status);
        Assert.Equal(403, Unpack(rig.Controller(session, cookie: false).Get()).Status);

        Assert.IsType<NoContentResult>(rig.Controller(session).Disable());
        Assert.False(session.IsRemembered);
        Assert.Equal(404, Unpack(rig.Controller(session).Get()).Status);
    }

    [Fact]
    public void Re_enabling_keeps_the_original_chain_start()
    {
        var rig = new Rig();
        var session = rig.Store.Create(ResumeFixtures.NewAccount(rig.Stub.Auth()));
        var since = DateTimeOffset.UtcNow.AddDays(-10);
        session.Remember(since, rig.Sealer.UntilFor(since));
        session.StopRemembering();

        var (_, body) = Unpack(rig.Controller(session).Enable());

        Assert.Equal(since, session.RememberedSince);
        Assert.Equal(since, rig.Sealer.Unseal(body.GetProperty("bundle").GetString()).Payload!.RememberedSince);

        session.Remember(DateTimeOffset.UtcNow.AddDays(-40), DateTimeOffset.UtcNow.AddDays(-10));
        Assert.Equal(410, Unpack(rig.Controller(session).Enable()).Status);   // chain over: sign in again
    }

    [Fact]
    public async Task With_no_RESUME_KEY_every_endpoint_says_disabled()
    {
        var rig = new Rig { Sealer = new ResumeSealer(null) };
        var session = rig.Store.Create(ResumeFixtures.NewAccount(rig.Stub.Auth()));

        var config = Unpack(rig.Controller().Config());
        Assert.False(config.Body.GetProperty("enabled").GetBoolean());

        foreach (var result in new[]
                 {
                     await rig.Controller().Resume(new("x"), CancellationToken.None),
                     rig.Controller(session).Get(),
                     rig.Controller(session).Enable(),
                     rig.Controller(session).Disable(),
                 })
        {
            var (status, body) = Unpack(result);
            Assert.Equal((404, "RESUME_DISABLED"), (status, body.GetProperty("code").GetString()));
        }
        Assert.False(session.IsRemembered);
    }
}
