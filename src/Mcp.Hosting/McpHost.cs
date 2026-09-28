using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using SmarterMailMcp.Core.Auth;
using SmarterMailMcp.Core.Models;
using SmAuthService = SmarterMailMcp.Core.Auth.AuthenticationService;

namespace SmarterMailMcp.Hosting;

/// <summary>
/// Runs one fixed-account MCP server: validate the environment, sign in to SmarterMail (with
/// <see cref="StartupSignIn"/>'s retry rules), then serve MCP over stdio or stateless HTTP.
/// </summary>
public static class McpHost
{
    /// <summary>Process exit code for a configuration error.</summary>
    public const int ConfigurationErrorExitCode = 1;

    /// <param name="addPrimitives">
    /// Registers the server's tools and prompts once signed in (it may probe SmarterMail with the
    /// signed-in context). The read-only filter is applied afterwards.
    /// </param>
    public static async Task<int> RunAsync(
        string[] args,
        McpHostDefinition definition,
        Func<UserContext, IMcpServerBuilder, Task> addPrimitives)
    {
        var (settings, errors) = McpHostSettings.Parse(definition, args, Environment.GetEnvironmentVariable);
        if (settings is null)
        {
            foreach (var error in errors)
                Console.Error.WriteLine($"Configuration error: {error}");
            return ConfigurationErrorExitCode;
        }

        var version = Version;
        Console.Error.WriteLine($"{definition.ServerName} {version} ({settings.Transport.ToString().ToLowerInvariant()}, " +
                                $"{(settings.ReadOnly ? "read-only" : "read-write")})");

        var globalContext = new GlobalContext(settings.TokenFile, readOnlyMode: settings.ReadOnly);
        var authService = new SmAuthService(globalContext);

        Console.Error.WriteLine($"Authenticating with SmarterMail at {settings.SmarterMailUrl} as {settings.Username} ({definition.UserType})...");
        // Transient failures retry with capped backoff (2s, 4s, 8s ... 60s); a credential rejection exits
        // with code 2 without retrying. The server (and /health) only starts once this succeeds.
        StartupSignInResult signIn;
        using (var shutdown = new StartupShutdownSignal())
        {
            signIn = await StartupSignIn.SignInWithRetryAsync(
                globalContext, authService, settings.SmarterMailUrl, settings.Username, settings.Password,
                readOnlyMode: settings.ReadOnly, userType: definition.UserType, shutdown.Token);
        }
        if (signIn.UserContext is not { } userContext)
            return signIn.ExitCode;   // 2 = credentials rejected (not retried), 0 = shutdown

        Console.Error.WriteLine("Authenticated successfully.");

        // Remove the transport switch so the configuration binder never sees it.
        var hostArgs = args.Where(a => !a.Equals(McpHostSettings.StdioArgument, StringComparison.OrdinalIgnoreCase)).ToArray();
        var serverInfo = new Implementation { Name = definition.ServerName, Version = version };

        if (settings.Transport == McpTransport.Stdio)
        {
            var builder = Host.CreateApplicationBuilder(hostArgs);
            // stdout carries JSON-RPC only; every log line goes to stderr.
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            AddContexts(builder.Services, globalContext, userContext, authService);

            var mcp = builder.Services.AddMcpServer(o => o.ServerInfo = serverInfo).WithStdioServerTransport();
            await addPrimitives(userContext, mcp);
            mcp.WithReadOnlyFilter(settings.ReadOnly);

            await builder.Build().RunAsync();
            return 0;
        }
        else
        {
            var builder = WebApplication.CreateBuilder(hostArgs);
            AddContexts(builder.Services, globalContext, userContext, authService);

            builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
                .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                    ApiKeyAuthenticationHandler.SchemeName, o => o.ApiKey = settings.ApiKey ?? "");
            builder.Services.AddAuthorization(options =>
                options.AddPolicy("ApiAccess", policy => policy
                    .RequireAuthenticatedUser()
                    .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)));

            // Stateless HTTP: no Mcp-Session-Id. Dual-stack via ModelContextProtocol.AspNetCore 2.1.0:
            // - initialize 2025-11-25 (Cursor Streamable HTTP) echoes that version over SSE
            // - 2026-07-28 is JSON-only: server/discover + per-request params._meta (no initialize, no sessions)
            // - GET/DELETE /mcp return 405 (no SSE GET stream). Never default initialize to 2026-07-28.
            var mcp = builder.Services.AddMcpServer(o => o.ServerInfo = serverInfo)
                .WithHttpTransport(options => options.Stateless = true);
            await addPrimitives(userContext, mcp);
            mcp.WithReadOnlyFilter(settings.ReadOnly);

            var app = builder.Build();
            app.MapMcp("/mcp").RequireAuthorization("ApiAccess");
            app.MapGet("/health", () => Results.Text($"{definition.ServerName} {version} ok")).AllowAnonymous();   // liveness only
            await app.RunAsync();
            return 0;
        }
    }

    /// <summary>The release version (<c>-p:Version=…</c> at build time), without the source-link suffix.</summary>
    public static string Version { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(McpHost).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    private static void AddContexts(IServiceCollection services, GlobalContext globalContext, UserContext userContext, SmAuthService authService)
    {
        services.AddSingleton(globalContext);
        services.AddSingleton(userContext);
        services.AddSingleton(authService);
    }
}
