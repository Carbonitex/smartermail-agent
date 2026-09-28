using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Controllers;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Tests;

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public sealed class HostLoginThrottleTests
{
    private const string Host = "https://mail.example.com";
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static (HostLoginThrottle Throttle, ManualClock Clock) Make(int limit = 3, int windowMinutes = 60, int maxHosts = 100)
    {
        var clock = new ManualClock(T0);
        return (new HostLoginThrottle(limit, TimeSpan.FromMinutes(windowMinutes), maxHosts, clock), clock);
    }

    private static void Fail(HostLoginThrottle throttle, string host = Host)
    {
        using var attempt = throttle.TryBegin(host, out _);
        Assert.NotNull(attempt);
        attempt.Finish(failed: true);
    }

    [Theory]
    [InlineData("https://Mail.Example.com", "https://mail.example.com:443")]
    [InlineData("https://mail.example.com/", "https://mail.example.com:443")]
    [InlineData("https://mail.example.com:443", "https://mail.example.com:443")]
    [InlineData("https://mail.example.com:9998", "https://mail.example.com:9998")]
    [InlineData("http://MAIL.example.com", "http://mail.example.com:80")]
    public void Hosts_normalise_to_scheme_host_and_port(string baseUrl, string expected) =>
        Assert.Equal(expected, HostLoginThrottle.NormalizeHost(baseUrl));

    [Fact]
    public void Default_cap_sits_under_every_default_SmarterMail_brute_force_by_ip_rule()
    {
        // SmarterMail GetDefaultIdsRules: delay 15/10 min, block 25/10 min, long block 35/50 min.
        // With a sliding window at least 50 minutes long, no 10- or 50-minute slice exceeds the cap.
        Assert.True(HostLoginThrottle.DefaultWindowMinutes >= 50);
        Assert.True(HostLoginThrottle.DefaultLimit < 15);
    }

    public static TheoryData<AuthOutcome, bool, bool> Outcomes() => new()
    {
        // outcome, on the two-factor step, counts
        { new AuthOutcome.Failed("USERNAME_OR_PASSWORD_INCORRECT", "x"), false, true },
        { new AuthOutcome.Failed("USER_NOT_FOUND", "x"), false, true },
        { new AuthOutcome.Failed("INVALID_TWO_FACTOR_CODE", "x"), true, true },
        { new AuthOutcome.Failed("INVALID_TWO_FACTOR_CODE", "x"), false, true },
        // Legacy inline 2FA answers a wrong code with TWO_FACTOR_REQUIRED again.
        { new AuthOutcome.TwoFactorRequired("rfc6238", "a@b.c", null), true, true },
        // ...but on the password step that is a correct password.
        { new AuthOutcome.TwoFactorRequired("rfc6238", "a@b.c", "step"), false, false },
        { new AuthOutcome.Failed("CONNECTION_FAILED", "x"), false, false },
        { new AuthOutcome.Failed("TIMEOUT", "x"), true, false },
        { new AuthOutcome.Failed("HTTP_502", "x"), false, false },
        { new AuthOutcome.Failed("HTTP_429", "x"), false, false },
        { new AuthOutcome.Failed("CHALLENGE_EXPIRED", "x"), true, false },
        { new AuthOutcome.Failed("ACCOUNT_DISABLED", "x"), false, false },
        { new AuthOutcome.ActionRequired("PASSWORD_EXPIRED", "x"), false, false },
        { new AuthOutcome.Success(new TokenData { AccessToken = "t" }), false, false },
    };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void Only_authoritative_credential_rejections_count(AuthOutcome outcome, bool twoFactorStep, bool counts)
    {
        Assert.Equal(counts, HostLoginThrottle.CountsAsFailure(outcome, twoFactorStep));

        var (throttle, _) = Make();
        using (var attempt = throttle.TryBegin(Host, out _))
            attempt!.Complete(outcome, twoFactorStep);

        Assert.Equal(counts ? 1 : 0, throttle.FailureCount(Host));
    }

    [Fact]
    public void Refuses_at_the_limit_and_counts_hosts_separately_and_case_insensitively()
    {
        var (throttle, _) = Make(limit: 3);

        Fail(throttle, "https://mail.example.com");
        Fail(throttle, "https://MAIL.example.com/");
        Assert.Null(throttle.RetryAfter(Host));
        Fail(throttle, "https://mail.example.com:443");

        Assert.Equal(3, throttle.FailureCount(Host));
        Assert.Null(throttle.TryBegin(Host, out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(60), retryAfter);
        Assert.NotNull(throttle.RetryAfter(Host));

        // Another server, another port, another scheme: separate counters.
        using var other = throttle.TryBegin("https://mail.other.com", out _);
        using var port = throttle.TryBegin("https://mail.example.com:9998", out _);
        Assert.NotNull(other);
        Assert.NotNull(port);
    }

    [Fact]
    public void Failures_age_out_of_the_sliding_window()
    {
        var (throttle, clock) = Make(limit: 3, windowMinutes: 60);

        Fail(throttle);                       // T0
        clock.Advance(TimeSpan.FromMinutes(20));
        Fail(throttle);                       // T0+20
        clock.Advance(TimeSpan.FromMinutes(20));
        Fail(throttle);                       // T0+40

        Assert.Null(throttle.TryBegin(Host, out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(20), retryAfter);   // until T0's failure is 60 min old

        clock.Advance(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1));
        Assert.Null(throttle.TryBegin(Host, out retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(1), retryAfter);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, throttle.FailureCount(Host));
        using var attempt = throttle.TryBegin(Host, out _);
        Assert.NotNull(attempt);
    }

    [Fact]
    public void A_success_does_not_reset_the_count()
    {
        var (throttle, _) = Make(limit: 3);
        Fail(throttle);
        Fail(throttle);

        using (var ok = throttle.TryBegin(Host, out _))
            ok!.Complete(new AuthOutcome.Success(new TokenData { AccessToken = "t" }));

        Assert.Equal(2, throttle.FailureCount(Host));
        Fail(throttle);
        Assert.Null(throttle.TryBegin(Host, out _));
    }

    [Fact]
    public void Attempts_in_flight_hold_a_slot_so_concurrent_sign_ins_cannot_overshoot()
    {
        var (throttle, _) = Make(limit: 3);
        Fail(throttle);

        var a = throttle.TryBegin(Host, out _);
        var b = throttle.TryBegin(Host, out _);
        Assert.NotNull(a);
        Assert.NotNull(b);

        Assert.Null(throttle.TryBegin(Host, out var retryAfter));
        Assert.Equal(HostLoginThrottle.InFlightRetry, retryAfter);

        // Disposing without completing (e.g. the request was cancelled) counts nothing.
        a.Dispose();
        a.Dispose();
        Assert.Equal(1, throttle.FailureCount(Host));

        using var c = throttle.TryBegin(Host, out _);
        Assert.NotNull(c);

        b.Finish(failed: true);
        b.Dispose();   // already finished: a no-op, not a second release
        Assert.Equal(2, throttle.FailureCount(Host));
        Assert.Null(throttle.TryBegin(Host, out _));   // 2 failures + c in flight
    }

    [Fact]
    public void Sweep_drops_hosts_whose_failures_have_expired()
    {
        var (throttle, clock) = Make(limit: 3, windowMinutes: 10);
        Fail(throttle, "https://a.example.com");
        clock.Advance(TimeSpan.FromMinutes(5));
        Fail(throttle, "https://b.example.com");
        using (throttle.TryBegin("https://c.example.com", out _)) { }   // success: never tracked after release

        Assert.Equal(2, throttle.TrackedHosts);

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, throttle.Sweep());
        Assert.Equal(1, throttle.TrackedHosts);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, throttle.Sweep());
        Assert.Equal(0, throttle.TrackedHosts);
    }

    [Fact]
    public void Tracked_hosts_are_bounded_evicting_the_least_recently_active()
    {
        var (throttle, clock) = Make(limit: 3, maxHosts: 3);
        foreach (var name in new[] { "a", "b", "c", "d", "e" })
        {
            Fail(throttle, $"https://{name}.example.com");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(3, throttle.TrackedHosts);
        Assert.Equal(0, throttle.FailureCount("https://a.example.com"));
        Assert.Equal(0, throttle.FailureCount("https://b.example.com"));
        Assert.Equal(1, throttle.FailureCount("https://e.example.com"));
    }

    [Fact]
    public void Environment_falls_back_to_defaults()
    {
        // Other tests do not set these; an unset or junk value must not disable the cap.
        var throttle = HostLoginThrottle.FromEnvironment();
        Assert.True(throttle.Limit >= 1);
        Assert.True(throttle.Window > TimeSpan.Zero);
    }

    [Fact]
    public void Rejects_nonsense_configuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostLoginThrottle(limit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostLoginThrottle(window: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostLoginThrottle(maxHosts: 0));
    }
}

/// <summary>
/// The 429 HOST_THROTTLED contract, through the real controllers. Every request here is refused
/// before SmarterMail would be called, so nothing touches the network.
/// </summary>
public sealed class HostThrottledContractTests
{
    // A public IP literal: HostGuard accepts it without a DNS lookup.
    private const string PublicHost = "https://93.184.215.14";

    private static HostLoginThrottle SaturatedThrottle(string baseUrl, int limit = 2)
    {
        var throttle = new HostLoginThrottle(limit, TimeSpan.FromMinutes(60));
        for (var i = 0; i < limit; i++)
        {
            using var attempt = throttle.TryBegin(baseUrl, out _);
            attempt!.Complete(new AuthOutcome.Failed("USERNAME_OR_PASSWORD_INCORRECT", "x"));
        }
        return throttle;
    }

    private static AuthController AuthControllerWith(HostLoginThrottle throttle, PendingLoginStore pending) =>
        new(new SessionStore(NullLogger<SessionStore>.Instance), pending,
            new SmarterMailAuth(NullLogger<SmarterMailAuth>.Instance), throttle,
            NullLogger<AuthController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static void AssertHostThrottled(IActionResult result, HttpResponse response)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, obj.StatusCode);

        var json = JsonSerializer.SerializeToElement(obj.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("HOST_THROTTLED", json.GetProperty("code").GetString());

        var seconds = json.GetProperty("retryAfterSeconds").GetInt32();
        Assert.InRange(seconds, 3500, 3600);
        Assert.Equal(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), response.Headers.RetryAfter.ToString());

        var error = json.GetProperty("error").GetString()!;
        Assert.Contains("Too many failed sign-ins to this mail server", error);
        Assert.Contains("Try again in 60 minutes.", error);
    }

    [Fact]
    public async Task Login_to_a_throttled_host_is_refused_before_SmarterMail()
    {
        var throttle = SaturatedThrottle(PublicHost);
        var controller = AuthControllerWith(throttle, new PendingLoginStore());

        var result = await controller.Login(
            new LoginRequest(PublicHost, "someone@example.com", "wrong"), CancellationToken.None);

        AssertHostThrottled(result, controller.Response);
        Assert.Equal(2, throttle.FailureCount(PublicHost));   // the refusal itself counts nothing
    }

    [Fact]
    public async Task Add_account_to_a_throttled_host_is_refused_before_SmarterMail()
    {
        var throttle = SaturatedThrottle(PublicHost);
        var controller = new AccountsController(
            new SessionStore(NullLogger<SessionStore>.Instance), new PendingLoginStore(),
            new SmarterMailAuth(NullLogger<SmarterMailAuth>.Instance), throttle,
            NullLogger<AccountsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Items["session"] = new Session { Id = SessionStore.NewSessionId() };

        var result = await controller.Add(
            new LoginRequest(PublicHost, "someone@example.com", "wrong"), CancellationToken.None);

        AssertHostThrottled(result, controller.Response);
    }

    [Fact]
    public async Task Two_factor_to_a_throttled_host_is_refused_and_the_challenge_survives()
    {
        const string baseUrl = "https://mail.example.com";
        var pending = new PendingLoginStore();
        var challenge = pending.Create(baseUrl, "someone@example.com", readOnly: true, "client",
            "rfc6238", "someone@example.com", stepToken: "step", password: null);

        // Throttled under a differently spelled but equivalent base URL.
        var throttle = SaturatedThrottle("https://MAIL.example.com:443/");
        var controller = AuthControllerWith(throttle, pending);

        var result = await controller.TwoFactor(
            new AuthController.TwoFactorRequest(challenge.Id, "123456"), CancellationToken.None);

        AssertHostThrottled(result, controller.Response);
        var survivor = pending.Get(challenge.Id);
        Assert.NotNull(survivor);
        Assert.Equal(0, survivor.Attempts);   // a throttled try does not burn a code attempt
    }
}
