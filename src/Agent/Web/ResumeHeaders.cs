using System.Globalization;
using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Web;

/// <summary>
/// Keeps the browser's resume bundle current. SmarterMail rotates the refresh token on every
/// refresh, so the bundle the browser saved goes stale each time a remembered session refreshes
/// (a tool call's lazy refresh, a 401 retry, an MCP caller) or its accounts change.
/// <para>
/// The browser sends the version it holds in <c>X-Resume-Version</c> (0 for none) on every API
/// request. When the session is remembered, the request came with the <b>cookie</b> (never a
/// bearer), and the session's <see cref="Session.ResumeVersion"/> is newer, the response carries
/// <c>X-Resume-Version: &lt;newer&gt;</c> and the browser fetches <c>GET /api/auth/resume</c>.
/// Only the number travels in a header: a sealed bundle is ~1.5 KB per account, and five of them
/// in a response header would overflow a proxy's default header buffer (nginx: 4–8 KB) and turn
/// an ordinary tool call into a 502.
/// </para>
/// Also marks every <c>/api</c> response <c>Cache-Control: no-store</c>: they carry session state,
/// and one of them (<c>GET /api/auth/resume</c>) carries the bundle itself.
/// </summary>
public static class ResumeHeaders
{
    public const string VersionHeader = "X-Resume-Version";

    /// <summary>Middleware body: runs after authentication, decides when the response starts.</summary>
    public static Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var api = context.Request.Path.StartsWithSegments("/api");
        var clientVersion = ClientVersion(context.Request);

        if (api || clientVersion is not null)
        {
            context.Response.OnStarting(() =>
            {
                if (api && !context.Response.Headers.ContainsKey("Cache-Control"))
                    context.Response.Headers.CacheControl = "no-store";
                if (clientVersion is { } known)
                    Apply(context, known);
                return Task.CompletedTask;
            });
        }

        return next();
    }

    /// <summary>The version the browser says it holds; null when it sent none (or garbage).</summary>
    public static long? ClientVersion(HttpRequest request) =>
        long.TryParse(request.Headers[VersionHeader].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    /// <summary>
    /// Adds <c>X-Resume-Version</c> when this cookie-authenticated request's remembered session has
    /// moved past <paramref name="clientVersion"/>. Evaluated as the response starts, so a refresh
    /// made by this very request is announced by its own response.
    /// </summary>
    public static void Apply(HttpContext context, long clientVersion)
    {
        var session = context.GetSession();
        if (session is null || session.IsDisposed || !session.IsRemembered || !context.IsCookieAuthenticated())
            return;

        var version = session.ResumeVersion;
        if (version <= clientVersion)
            return;

        context.Response.Headers[VersionHeader] = version.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";
    }
}
