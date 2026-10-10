using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Llm;
using SmarterMailAgent.Mcp;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;
using SmarterMailAgent.Tasks;
using SmarterMailAgent.Tasks.Approvals;
using SmarterMailAgent.Tasks.Triggers;
using SmarterMailAgent.Web;
using SmarterMailMcp.Core.Models;

// `invites …` / `access …`: the operator's commands for invite-only tasks, instead of the web host.
if (AdminCli.Handles(args))
    return AdminCli.Run(args);

SmarterMailAgent.Logging.CoreConsoleFilter.Install();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<SmarterMailAuth>();
builder.Services.AddSingleton<PendingLoginStore>();
builder.Services.AddSingleton(sp => HostLoginThrottle.FromEnvironment(
    sp.GetRequiredService<ILogger<HostLoginThrottle>>()));
builder.Services.AddSingleton(sp => ResumeSealer.FromEnvironment(
    sp.GetRequiredService<ILogger<ResumeSealer>>()));
builder.Services.AddSingleton<ToolCatalog>();
builder.Services.AddSingleton<ToolInvoker>();
builder.Services.AddSingleton<ToolDispatcher>();
builder.Services.AddSingleton(sp => new AccountRestorer(
    sp.GetRequiredService<SmarterMailAuth>(), sp.GetRequiredService<HostLoginThrottle>(),
    sp.GetRequiredService<ILogger<AccountRestorer>>()));
builder.Services.AddHostedService<SessionSweeper>();

// Server mode (the default) keeps passkey-encrypted profiles and runs scheduled tasks;
// BROWSER_ONLY_MODE=true registers none of it, so nothing is ever written to DATA_DIR.
var serverOptions = ServerOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(serverOptions);
var triggerOptions = TriggerOptions.FromConfiguration(builder.Configuration, serverOptions);
builder.Services.AddSingleton(triggerOptions);
if (serverOptions.ServerMode)
{
    builder.Services.AddSingleton<DataStore>();
    builder.Services.AddSingleton<ProfileStore>();
    builder.Services.AddSingleton<ProfileRegistry>();
    builder.Services.AddSingleton<PasskeyService>();
    builder.Services.AddHostedService<ProfileMaintenance>();
    // The stores are plain views of the database: registered whenever server mode is on, so the task,
    // approval and trigger controllers can be built and answer 404 TASKS_DISABLED themselves.
    builder.Services.AddSingleton<TaskStore>();
    builder.Services.AddSingleton<ProposalStore>();
    builder.Services.AddSingleton<TriggerStore>();
    builder.Services.AddSingleton<TaskInviteStore>();
    if (serverOptions.TasksEnabled)
    {
        builder.Services.AddHttpClient<OpenRouterClient>(http => http.Timeout = TimeSpan.FromMinutes(3));
        builder.Services.AddSingleton<AgentLoop>();
        builder.Services.AddSingleton<TaskRunner>();
        builder.Services.AddTaskRunScheduler();
        builder.Services.AddApprovals();
        if (triggerOptions.Enabled)
            builder.Services.AddTriggerProber();
    }
}
builder.Services.AddHttpContextAccessor();
builder.Services.AddControllers();

// UserContext and GlobalContext are per-account objects owned by the session's Account, not by the DI
// container. They are registered here only so that IServiceProviderIsService reports them as
// services: Microsoft.Extensions.AI's AIFunctionFactory decides at *tool creation* time whether a
// method parameter is bound from the caller's arguments or resolved from DI, and an unregistered
// type becomes a required argument ("missing a value for the required parameter 'userContext'").
// The factories below never actually run: ToolDispatcher invokes every tool on an
// AccountServiceProvider that answers these two types from the chosen account. Resolving them any
// other way is a bug, so say so loudly rather than let the container take ownership of (and
// dispose) another user's live account.
builder.Services.AddScoped<UserContext>(_ => throw new InvalidOperationException(
    "UserContext must be resolved from an AccountServiceProvider (see ToolDispatcher)."));
builder.Services.AddScoped<GlobalContext>(_ => throw new InvalidOperationException(
    "GlobalContext must be resolved from an AccountServiceProvider (see ToolDispatcher)."));

// Two credentials, two scopes. The cookie (default scheme) opens everything; an MCP token
// (Bearer sma_mcp_…) opens /mcp only, because only the McpAccess policy names its scheme. The
// session id itself is never accepted as a bearer.
builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
        SessionAuthenticationHandler.SchemeName, _ => { })
    .AddScheme<AuthenticationSchemeOptions, McpTokenAuthenticationHandler>(
        McpTokenAuthenticationHandler.SchemeName, _ => { })
    .AddPolicyScheme(McpTokenAuthenticationHandler.CookieOrTokenScheme, null, options =>
        options.ForwardDefaultSelector = context => McpTokenAuthenticationHandler.HasBearer(context.Request)
            ? McpTokenAuthenticationHandler.SchemeName
            : SessionAuthenticationHandler.SchemeName);

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SessionAccess", policy => policy
        .RequireAuthenticatedUser()
        .AddAuthenticationSchemes(SessionAuthenticationHandler.SchemeName));
    options.AddPolicy("McpAccess", policy => policy
        .RequireAuthenticatedUser()
        .AddAuthenticationSchemes(McpTokenAuthenticationHandler.CookieOrTokenScheme));
});

// Forwarded headers and CF-Connecting-IP are honoured only from TRUSTED_PROXIES (see Web/ProxyTrust.cs).
var proxyTrust = ProxyTrust.Parse(builder.Configuration["TRUSTED_PROXIES"], builder.Configuration["TRUST_CF_CONNECTING_IP"]);
builder.Services.AddSingleton(proxyTrust);
builder.Services.Configure<ForwardedHeadersOptions>(proxyTrust.Configure);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        proxyTrust.ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
        }));
    // Code entry is per-challenge capped at 5 tries; the IP limiter here only needs to stop
    // scripted guessing across many challenges without tripping a human who mistypes twice.
    options.AddPolicy("two-factor", context => RateLimitPartition.GetFixedWindowLimiter(
        proxyTrust.ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 15,
            Window = TimeSpan.FromMinutes(1),
        }));
    options.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
        proxyTrust.ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
        }));
});

builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithAgentTools()
    .WithRequestFilters(filters =>
    {
        // /mcp sees exactly what /api/tools does: the session's tools, with `account` injected.
        filters.AddListToolsFilter(_ => (context, _) =>
        {
            var session = SessionOf(context.Services ?? context.Server?.Services);
            var catalog = context.Server!.Services!.GetRequiredService<ToolCatalog>();
            return ValueTask.FromResult(new ListToolsResult
            {
                Tools = session is null ? [] : catalog.ListProtocolTools(session.Accounts),
            });
        });

        // Every call goes through the same dispatcher as /api/tools/call. The tool's own result is
        // passed through unchanged; account and read-only refusals come back as isError.
        filters.AddCallToolFilter(_ => async (context, ct) =>
        {
            var services = context.Services ?? context.Server?.Services;
            var session = SessionOf(services);
            var name = context.Params?.Name;
            if (session is null || services is null || string.IsNullOrEmpty(name))
                return ToolInvoker.ErrorResult("No session on this request.");

            var dispatcher = services.GetRequiredService<ToolDispatcher>();
            var arguments = context.Params?.Arguments is { } args
                ? new Dictionary<string, System.Text.Json.JsonElement>(args, StringComparer.Ordinal)
                : null;
            var outcome = await dispatcher.DispatchAsync(session, name, arguments, services, ct);
            return outcome.Result;
        });
    });

var app = builder.Build();

// Remember the TCP peer before forwarded headers overwrite it: CF-Connecting-IP is only believed
// when that peer is a trusted proxy.
app.Use((context, next) =>
{
    context.Items[ProxyTrust.PeerItem] = context.Connection.RemoteIpAddress;
    return next(context);
});
app.UseForwardedHeaders();
// Default "/" (the app at the site root). Behind a shared host set e.g. PATH_BASE=/mail-agent.
app.UsePathBase(app.Configuration["PATH_BASE"] ?? "/");
// index.html and the ES modules it imports must move together: a browser holding yesterday's
// chat.js against today's API breaks in confusing ways. Cloudflare overrides the origin's
// no-cache with a day-long browser TTL on .js/.css, so revalidation alone is not enough: assets
// are served under a content-hash directory (see Web/AssetVersioning.cs) and cached immutably,
// while index.html, which names that directory, is always revalidated.
var webRoot = app.Environment.WebRootPath;
var assetVersion = AssetVersioning.ComputeVersion(webRoot);
var indexHtml = AssetVersioning.RewriteIndex(File.ReadAllText(Path.Combine(webRoot, "index.html")), assetVersion,
    HomeLink.Create(app.Configuration["HOME_LINK_URL"], app.Configuration["HOME_LINK_TEXT"]));
var indexEtag = $"\"{assetVersion}\"";
app.Logger.LogInformation("Asset version {Version}", assetVersion);

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "/";
    if (path is "/" or "/index.html" && HttpMethods.IsGet(context.Request.Method))
    {
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.ETag = indexEtag;
        if (context.Request.Headers.IfNoneMatch == indexEtag)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(indexHtml);
        return;
    }
    if (AssetVersioning.TryStripVersion(path, out var rest))
    {
        context.Request.Path = rest;
        context.Items[AssetVersioning.Prefix] = true;
    }
    await next();
});
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
        context.Context.Response.Headers.CacheControl =
            context.Context.Items.ContainsKey(AssetVersioning.Prefix)
                ? "public, max-age=31536000, immutable"
                : "no-cache",
});
// Browser-only mode: profile and task endpoints do not exist, whoever asks (ahead of authentication,
// so an anonymous caller sees the same 404 as a signed-in one).
if (serverOptions.BrowserOnly)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/profile") || context.Request.Path.StartsWithSegments("/api/tasks"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "This server runs in browser-only mode: nothing is stored here.",
                code = "SERVER_MODE_DISABLED",
            });
            return;
        }
        await next(context);
    });
}
app.UseRateLimiter();
app.UseAuthentication();
// After authentication, so it can tell a cookie from a bearer: announces a newer resume bundle to
// the browser (X-Resume-Version) and marks /api responses no-store.
app.Use(ResumeHeaders.InvokeAsync);
app.UseAuthorization();

app.MapControllers();
app.MapMcp("/mcp").RequireAuthorization("McpAccess");
app.MapGet("/health", () => Results.Text("smartermail-agent ok"));

// Startup breadcrumb: tool counts only, no configuration values. Resolving the catalog here also
// fails startup if any registered tool has no scope.
var catalog = app.Services.GetRequiredService<ToolCatalog>();
app.Services.GetRequiredService<ResumeSealer>();   // logs whether remember-me is on (never the key)
ServerOptions.FromConfiguration(app.Configuration, app.Logger);   // logs the mode (never a key)
if (serverOptions.ServerMode)
    app.Services.GetRequiredService<DataStore>();   // opens / migrates the database, or fails startup
var counts = catalog.Entries
    .GroupBy(e => e.Scope)
    .Select(g => $"{g.Key} {g.Count()} ({g.Count(e => !e.Write)} read)");
app.Logger.LogInformation("smartermail-agent ready. {Total} tools: {Scopes}.",
    catalog.Count, string.Join(", ", counts));

app.Run();
return 0;

static Session? SessionOf(IServiceProvider? services) => services?.GetService(typeof(Session)) as Session;
