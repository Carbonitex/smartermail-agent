using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Mcp;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Tests;

public sealed record FakeAccount(string Handle, AccountRole Role, bool ReadOnly) : IToolAccount;

/// <summary>The real tool registration, with the same DI placeholders Program.cs registers.</summary>
public sealed class CatalogFixture : IDisposable
{
    private readonly ServiceProvider _services;

    public CatalogFixture()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<UserContext>(_ => throw new InvalidOperationException("placeholder"));
        services.AddScoped<GlobalContext>(_ => throw new InvalidOperationException("placeholder"));
        services.AddMcpServer().WithAgentTools();
        _services = services.BuildServiceProvider();

        Catalog = new ToolCatalog(
            _services.GetRequiredService<IOptions<McpServerOptions>>(),
            _services.GetRequiredService<ToolOrigins>());
        Invoker = new ToolInvoker(
            _services.GetRequiredService<IOptions<McpServerOptions>>(),
            _services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
    }

    public ToolCatalog Catalog { get; }

    public ToolInvoker Invoker { get; }

    /// <summary>The root provider, for invoking tools the way the dispatcher does.</summary>
    public IServiceProvider Services => _services;

    public void Dispose() => _services.Dispose();
}
