using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SmarterMailAgent.Auth;

/// <summary>
/// Authenticates a request from the HttpOnly <c>sma_session</c> cookie (the browser). The default
/// scheme and the only one <c>/api/*</c> accepts. A bearer header is ignored here: the session id
/// is never a bearer credential (MCP clients use <see cref="McpTokenAuthenticationHandler"/>).
/// </summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    SessionStore store)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "Session";

    /// <summary>HttpContext item set when the session came from the browser's cookie, not an MCP token.</summary>
    public const string ViaCookieItem = "session.viaCookie";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(SessionStore.CookieName, out var id) || string.IsNullOrEmpty(id))
            return Task.FromResult(AuthenticateResult.NoResult());

        var session = store.Get(id);
        if (session is null)
            return Task.FromResult(AuthenticateResult.Fail("Session expired"));

        Context.Items[ViaCookieItem] = true;
        return Task.FromResult(Attach(Context, session, Scheme.Name));
    }

    /// <summary>
    /// Binds <paramref name="session"/> to the request, for either scheme. Counts as activity for
    /// the idle timeout, so an MCP client on its own keeps its session alive.
    /// </summary>
    internal static AuthenticateResult Attach(HttpContext context, Session session, string scheme)
    {
        session.Touch();

        // Session resolves from DI for the rest of this request; tool calls pick an account in ToolDispatcher.
        context.Items["session"] = session;
        context.RequestServices = new SessionServiceProvider(context.RequestServices, session);

        var identity = new ClaimsIdentity(
        [
            new Claim("sid", session.Id),
        ], scheme);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), scheme));
    }
}

/// <summary>
/// Authenticates <c>Authorization: Bearer sma_mcp_…</c> (Cursor / Claude Code) as the session that
/// minted the token. Only the <c>McpAccess</c> policy on <c>/mcp</c> uses this scheme, so the token
/// is worthless on <c>/api/*</c>.
/// </summary>
public sealed class McpTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    SessionStore store)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "McpToken";

    /// <summary>
    /// Policy scheme for <c>/mcp</c>: a request with a bearer header goes to <see cref="SchemeName"/>,
    /// anything else to the cookie, so the browser keeps working there too.
    /// </summary>
    public const string CookieOrTokenScheme = "SessionOrMcpToken";

    public static bool HasBearer(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!HasBearer(Request))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        var session = store.FindByMcpToken(token);
        if (session is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired MCP token"));

        return Task.FromResult(SessionAuthenticationHandler.Attach(Context, session, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return base.HandleChallengeAsync(properties);
    }
}

public static class SessionHttpContextExtensions
{
    public static Session? GetSession(this HttpContext context) =>
        context.Items.TryGetValue("session", out var value) ? value as Session : null;

    public static Session RequireSession(this HttpContext context) =>
        context.GetSession() ?? throw new UnauthorizedAccessException("No session on this request.");

    /// <summary>
    /// True when this request's session came from the <c>sma_session</c> cookie, i.e. the browser.
    /// Resume bundles (and the headers that announce them) go only to such requests, never to a
    /// bearer caller.
    /// </summary>
    public static bool IsCookieAuthenticated(this HttpContext context) =>
        context.GetSession() is not null && context.Items.ContainsKey(SessionAuthenticationHandler.ViaCookieItem);
}
