using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Server;

namespace SmarterMailAgent.Tests;

public sealed class ServerOptionsTests
{
    private static ServerOptions Parse(params (string Key, string? Value)[] values) =>
        ServerOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build());

    [Fact]
    public void Defaults_are_server_mode_without_tasks()
    {
        var options = Parse();
        Assert.True(options.ServerMode);
        Assert.Null(options.DataKey);
        Assert.False(options.TasksEnabled);
        Assert.Equal(TimeSpan.FromMinutes(15), options.TaskMinInterval);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("YES", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    public void Browser_only_mode_parses(string value, bool expected) =>
        Assert.Equal(expected, Parse(("BROWSER_ONLY_MODE", value)).BrowserOnly);

    [Fact]
    public void Tasks_need_the_data_key()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Assert.True(Parse(("DATA_KEY", key)).TasksEnabled);
        Assert.False(Parse(("DATA_KEY", key), ("TASKS_ENABLED", "false")).TasksEnabled);
        Assert.False(Parse(("DATA_KEY", key), ("BROWSER_ONLY_MODE", "true")).TasksEnabled);
    }

    [Theory]
    [InlineData("BROWSER_ONLY_MODE", "maybe")]
    [InlineData("DATA_KEY", "too-short")]
    [InlineData("PUBLIC_ORIGIN", "https://example.com/path")]
    [InlineData("PUBLIC_ORIGIN", "ftp://example.com")]
    [InlineData("TASK_CONCURRENCY", "0")]
    public void Malformed_values_fail_startup(string key, string value) =>
        Assert.Throws<InvalidOperationException>(() => Parse((key, value)));

    [Fact]
    public void Profile_idle_maximum()
    {
        Assert.Equal(480, Parse().ProfileMaxIdleMinutes);
        Assert.Equal(120, Parse(("PROFILE_MAX_IDLE_MINUTES", "120")).ProfileMaxIdleMinutes);
        Assert.Equal(ServerOptions.ProfileMinIdleMinutes, Parse(("PROFILE_MAX_IDLE_MINUTES", "1")).ProfileMaxIdleMinutes);
    }

    [Fact]
    public void Profile_hosts_allowlist()
    {
        var options = Parse(("PROFILE_MAIL_HOSTS", "Mail.Example.com, other.example.org"));
        Assert.True(options.AllowsProfileHost("https://mail.example.com"));
        Assert.True(options.AllowsProfileHost("https://MAIL.example.com:8443/"));
        Assert.False(options.AllowsProfileHost("https://evil.example.net"));
        Assert.True(Parse().AllowsProfileHost("https://anything.example"));
    }
}

/// <summary>The agent in-process, in both modes, through HTTP.</summary>
public sealed class ServerModeHttpTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private readonly StubSmarterMail _stub = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
    }

    private WebApplicationFactory<Program> App(params (string Key, string Value)[] settings)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            builder.ConfigureTestServices(services => services.AddSingleton(_stub.Auth()));
        });
        _disposables.Add(factory);
        return factory;
    }

    private static HttpClient Client(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

    private static HttpRequestMessage Req(HttpMethod method, string path, string? cookie = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (cookie is not null)
            request.Headers.Add("Cookie", $"{SessionStore.CookieName}={cookie}");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private static string? CookieOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith(SessionStore.CookieName + "=", StringComparison.Ordinal))
                ?[(SessionStore.CookieName.Length + 1)..]
            : null;

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Browser_only_mode_stores_nothing_and_refuses_profile_endpoints()
    {
        var dir = TestEnvironment.NewDataDir();
        var app = App(("BROWSER_ONLY_MODE", "true"), ("DATA_DIR", dir),
            ("DATA_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        using var client = Client(app);

        var config = await Json(await client.GetAsync("/api/config"));
        Assert.Equal("browser", config.GetProperty("mode").GetString());
        Assert.False(config.GetProperty("profiles").GetProperty("enabled").GetBoolean());
        Assert.False(config.GetProperty("tasks").GetProperty("enabled").GetBoolean());

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, "/api/profile/login/options"),
                     (HttpMethod.Post, "/api/profile/login"),
                     (HttpMethod.Get, "/api/profile"),
                     (HttpMethod.Get, "/api/tasks"),
                 })
        {
            using var response = await client.SendAsync(Req(method, path, body: new { }));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("SERVER_MODE_DISABLED", (await Json(response)).GetProperty("code").GetString());
        }

        Assert.Null(app.Services.GetService<SmarterMailAgent.Storage.DataStore>());
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Server_mode_reports_itself_and_tasks_follow_the_data_key()
    {
        using var plain = Client(App(("DATA_DIR", TestEnvironment.NewDataDir())));
        var config = await Json(await plain.GetAsync("/api/config"));
        Assert.Equal("server", config.GetProperty("mode").GetString());
        Assert.True(config.GetProperty("profiles").GetProperty("enabled").GetBoolean());
        Assert.False(config.GetProperty("tasks").GetProperty("enabled").GetBoolean());

        using var keyed = Client(App(("DATA_DIR", TestEnvironment.NewDataDir()),
            ("DATA_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))));
        config = await Json(await keyed.GetAsync("/api/config"));
        Assert.True(config.GetProperty("tasks").GetProperty("enabled").GetBoolean());
    }

    /// <summary>
    /// The whole profile life cycle against a stub SmarterMail and a software passkey: create from a
    /// signed-in chat, lock, sign in again with the passkey, unlock, delegate, add a task.
    /// </summary>
    [Fact]
    public async Task Profile_round_trip_with_a_passkey()
    {
        var dir = TestEnvironment.NewDataDir();
        var app = App(("DATA_DIR", dir), ("DATA_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        using var client = Client(app);

        var store = app.Services.GetRequiredService<SessionStore>();
        var account = ResumeFixtures.NewAccount(_stub.Auth(), refresh: "refresh-1");
        var first = store.Create(account);

        // 1. Create the profile from the signed-in chat.
        var begin = await Json(await client.SendAsync(Req(HttpMethod.Post, "/api/profile/register/options", first.Id, new { })));
        var authenticator = new SoftAuthenticator();
        var credential = authenticator.Create(begin.GetProperty("options"), "http://localhost",
            // A PRF output that slipped into the response must be ignored, not stored.
            extensionResults: new JsonObject { ["prf"] = new JsonObject { ["results"] = new JsonObject { ["first"] = "c2VjcmV0" } } });

        var accountsKey = RandomNumberGenerator.GetBytes(32);
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Base64Url.Encode(ecdh.ExportSubjectPublicKeyInfo());

        using var created = await client.SendAsync(Req(HttpMethod.Post, "/api/profile", first.Id, new
        {
            ceremonyId = begin.GetProperty("ceremonyId").GetString(),
            credential,
            wrappedKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(60)),
            accountsKey = Base64Url.Encode(accountsKey),
            publicKey,
            encryptedPrivateKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(200)),
            settings = Base64Url.Encode(RandomNumberGenerator.GetBytes(100)),
            label = "Test laptop",
        }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var profileCookie = CookieOf(created)!;
        Assert.NotEqual(first.Id, profileCookie);
        Assert.Null(store.Get(first.Id));                       // the old session is gone
        Assert.False(account.IsDisposed);                       // but its account lives on in the profile
        var body = await Json(created);
        Assert.True(body.GetProperty("profile").GetProperty("unlocked").GetBoolean());

        // The database holds no refresh token or login in the clear.
        var bytes = await File.ReadAllBytesAsync(Path.Combine(dir, "smartermail-agent.db"));
        var walPath = Path.Combine(dir, "smartermail-agent.db-wal");
        if (File.Exists(walPath))
            bytes = bytes.Concat(await File.ReadAllBytesAsync(walPath)).ToArray();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("refresh-1", text);
        Assert.DoesNotContain("alice@example.com", text);
        Assert.DoesNotContain("c2VjcmV0", text);

        var view = await Json(await client.SendAsync(Req(HttpMethod.Get, "/api/profile", profileCookie)));
        Assert.Single(view.GetProperty("passkeys").EnumerateArray());
        Assert.True(view.GetProperty("accounts")[0].GetProperty("live").GetBoolean());

        // 2. Lock (log out): the account is forgotten, not revoked.
        using (var logout = await client.SendAsync(Req(HttpMethod.Post, "/api/auth/logout", profileCookie)))
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.True(account.IsDisposed);
        Assert.Empty(_stub.Logouts);
        Assert.Equal(0, app.Services.GetRequiredService<ProfileRegistry>().Count);

        // 3. Another browser: passkey sign-in, then unlock.
        var loginBegin = await Json(await client.SendAsync(Req(HttpMethod.Post, "/api/profile/login/options", body: new { })));
        var assertion = authenticator.Get(loginBegin.GetProperty("options"), "http://localhost");
        using var login = await client.SendAsync(Req(HttpMethod.Post, "/api/profile/login", body: new
        {
            ceremonyId = loginBegin.GetProperty("ceremonyId").GetString(),
            credential = assertion,
        }));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var signIn = await Json(login);
        var cookie = CookieOf(login)!;
        Assert.False(signIn.GetProperty("session").GetProperty("profile").GetProperty("unlocked").GetBoolean());
        Assert.Empty(signIn.GetProperty("session").GetProperty("accounts").EnumerateArray());

        // The same ceremony cannot be used twice.
        using (var replay = await client.SendAsync(Req(HttpMethod.Post, "/api/profile/login", body: new
               {
                   ceremonyId = loginBegin.GetProperty("ceremonyId").GetString(),
                   credential = assertion,
               })))
            Assert.NotEqual(HttpStatusCode.OK, replay.StatusCode);

        using (var wrong = await client.SendAsync(Req(HttpMethod.Post, "/api/profile/unlock", cookie,
                   new { accountsKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(32)) })))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using var unlock = await client.SendAsync(Req(HttpMethod.Post, "/api/profile/unlock", cookie,
            new { accountsKey = Base64Url.Encode(accountsKey) }));
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var unlocked = await Json(unlock);
        var restored = unlocked.GetProperty("session").GetProperty("accounts");
        Assert.Single(restored.EnumerateArray());
        Assert.Equal("alice@example.com", restored[0].GetProperty("emailAddress").GetString());
        Assert.Contains(_stub.Refreshes, r => r.Token == "refresh-1");

        var tools = await Json(await client.SendAsync(Req(HttpMethod.Get, "/api/tools", cookie)));
        Assert.True(tools.GetArrayLength() > 0);

        // 4. Delegate the account, store a task key, add a task.
        var accountId = restored[0].GetProperty("id").GetString()!;
        using (var delegation = await client.SendAsync(Req(HttpMethod.Put, $"/api/profile/accounts/{accountId}/delegation", cookie,
                   new { enabled = true })))
        {
            Assert.Equal(HttpStatusCode.OK, delegation.StatusCode);
            Assert.True((await Json(delegation)).GetProperty("accounts")[0].GetProperty("delegated").GetBoolean());
        }

        using (var key = await client.SendAsync(Req(HttpMethod.Put, "/api/profile/task-key", cookie, new { key = "sk-or-test" })))
            Assert.True((await Json(key)).GetProperty("hasTaskKey").GetBoolean());

        using (var tooOften = await client.SendAsync(Req(HttpMethod.Post, "/api/tasks", cookie, new
               {
                   name = "Too often", prompt = "x", cron = "*/5 * * * *", timeZone = "UTC", accountIds = new[] { accountId },
                   model = "test/model",
               })))
            Assert.Equal(HttpStatusCode.BadRequest, tooOften.StatusCode);

        using var task = await client.SendAsync(Req(HttpMethod.Post, "/api/tasks", cookie, new
        {
            name = "Morning summary",
            prompt = "Summarise unread mail in the Inbox.",
            cron = "0 7 * * 1-5",
            timeZone = "America/Phoenix",
            accountIds = new[] { accountId },
            allowedWrites = Array.Empty<string>(),
            model = "test/model",
        }));
        Assert.Equal(HttpStatusCode.OK, task.StatusCode);
        var taskView = await Json(task);
        Assert.Equal("Morning summary", taskView.GetProperty("definition").GetProperty("name").GetString());
        Assert.True(taskView.GetProperty("nextRunAt").ValueKind == JsonValueKind.String);

        var list = await Json(await client.SendAsync(Req(HttpMethod.Get, "/api/tasks", cookie)));
        Assert.Single(list.GetProperty("tasks").EnumerateArray());

        // 5. Delete the profile: accounts revoked, cookie cleared, rows gone.
        using (var deleted = await client.SendAsync(Req(HttpMethod.Delete, "/api/profile", cookie)))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.NotEmpty(_stub.Logouts);
        Assert.Null(store.Get(cookie));
    }

    [Fact]
    public async Task Profile_chooses_its_session_idle_timeout()
    {
        var app = App(("DATA_DIR", TestEnvironment.NewDataDir()), ("PROFILE_MAX_IDLE_MINUTES", "120"));
        using var client = Client(app);
        var store = app.Services.GetRequiredService<SessionStore>();
        var first = store.Create(ResumeFixtures.NewAccount(_stub.Auth()));

        var begin = await Json(await client.SendAsync(Req(HttpMethod.Post, "/api/profile/register/options", first.Id, new { })));
        var credential = new SoftAuthenticator().Create(begin.GetProperty("options"), "http://localhost");
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var created = await client.SendAsync(Req(HttpMethod.Post, "/api/profile", first.Id, new
        {
            ceremonyId = begin.GetProperty("ceremonyId").GetString(),
            credential,
            wrappedKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(60)),
            accountsKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(32)),
            publicKey = Base64Url.Encode(ecdh.ExportSubjectPublicKeyInfo()),
            encryptedPrivateKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(200)),
        }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var cookie = CookieOf(created)!;
        var session = store.Get(cookie)!;
        var defaultMinutes = (int)SessionStore.IdleTimeout.TotalMinutes;

        var idle = (await Json(await client.SendAsync(Req(HttpMethod.Get, "/api/profile", cookie)))).GetProperty("idle");
        Assert.Equal(JsonValueKind.Null, idle.GetProperty("minutes").ValueKind);
        Assert.Equal(defaultMinutes, idle.GetProperty("defaultMinutes").GetInt32());
        Assert.Equal(120, idle.GetProperty("maxMinutes").GetInt32());

        foreach (var outOfRange in new[] { 4, 121 })
        {
            using var refused = await client.SendAsync(Req(HttpMethod.Put, "/api/profile/idle", cookie, new { minutes = outOfRange }));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("IDLE_OUT_OF_RANGE", (await Json(refused)).GetProperty("code").GetString());
        }

        using (var set = await client.SendAsync(Req(HttpMethod.Put, "/api/profile/idle", cookie, new { minutes = 90 })))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            Assert.Equal(90, (await Json(set)).GetProperty("idle").GetProperty("minutes").GetInt32());
        }

        // The open session follows it at once, and the browser is told (it keeps the keys as long).
        Assert.Equal(TimeSpan.FromMinutes(90), session.IdleTimeout(SessionStore.IdleTimeout));
        var state = await Json(await client.SendAsync(Req(HttpMethod.Get, "/api/auth/session", cookie)));
        Assert.Equal(90, state.GetProperty("profile").GetProperty("idleMinutes").GetInt32());

        // A later runtime (every session closed, then a new sign-in) reads it back from the database.
        var profileId = session.Profile!.ProfileId;
        var registry = app.Services.GetRequiredService<ProfileRegistry>();
        Assert.Equal(TimeSpan.FromMinutes(90), registry.AcquireSession(profileId).IdleTimeout);

        using (var reset = await client.SendAsync(Req(HttpMethod.Put, "/api/profile/idle", cookie, new { minutes = (int?)null })))
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(SessionStore.IdleTimeout, session.IdleTimeout(SessionStore.IdleTimeout));
    }

    [Fact]
    public async Task Passkey_from_another_origin_is_refused()
    {
        var app = App(("DATA_DIR", TestEnvironment.NewDataDir()));
        using var client = Client(app);
        var session = app.Services.GetRequiredService<SessionStore>().Create(ResumeFixtures.NewAccount(_stub.Auth()));

        var begin = await Json(await client.SendAsync(Req(HttpMethod.Post, "/api/profile/register/options", session.Id, new { })));
        var credential = new SoftAuthenticator().Create(begin.GetProperty("options"), "http://localhost", overrideOrigin: "https://evil.example");
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        using var response = await client.SendAsync(Req(HttpMethod.Post, "/api/profile", session.Id, new
        {
            ceremonyId = begin.GetProperty("ceremonyId").GetString(),
            credential,
            wrappedKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(60)),
            accountsKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(32)),
            publicKey = Base64Url.Encode(ecdh.ExportSubjectPublicKeyInfo()),
            encryptedPrivateKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(200)),
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("PASSKEY_INVALID", (await Json(response)).GetProperty("code").GetString());
        Assert.NotNull(app.Services.GetRequiredService<SessionStore>().Get(session.Id));   // untouched
    }

    [Fact]
    public async Task Device_remember_me_is_refused_in_a_profile_session()
    {
        var app = App(("DATA_DIR", TestEnvironment.NewDataDir()), ("RESUME_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        var runtime = app.Services.GetRequiredService<ProfileRegistry>().AcquireSession(ProfileCrypto.NewId());
        var session = app.Services.GetRequiredService<SessionStore>().CreateForProfile(runtime);
        using var client = Client(app);

        using var response = await client.SendAsync(Req(HttpMethod.Put, "/api/auth/resume", session.Id));
        // RESUME_KEY may be read from the process environment only (ResumeSealer.FromEnvironment):
        // either way a profile session never gets a device bundle.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
