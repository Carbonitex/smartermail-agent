using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Auth;

/// <summary>
/// Wraps the request's IServiceProvider so that <see cref="Session"/> resolves to the authenticated
/// browser session. A session can hold several SmarterMail accounts, so this provider refuses to
/// answer <see cref="UserContext"/> or <see cref="GlobalContext"/>: which account a tool runs as is
/// decided per call by <c>ToolDispatcher</c>, which invokes the tool through an
/// <see cref="AccountServiceProvider"/>. Resolving either type here means a tool ran without going
/// through the dispatcher, and the right answer is a loud failure, not a guess.
///
/// Why a wrapper and not <c>AddScoped&lt;UserContext&gt;(...)</c>: the DI container disposes
/// IDisposable instances returned by a scoped factory when the scope ends, and UserContext owns the
/// account's HttpClient. Container-owned disposal would kill the account after its first request.
///
/// Scopes created from this provider (the MCP SDK creates one per request when
/// <c>McpServerOptions.ScopeRequests</c> is on) stay wrapped, so the override survives.
/// </summary>
public sealed class SessionServiceProvider(IServiceProvider inner, Session session)
    : IServiceProvider, IServiceScopeFactory, IServiceProviderIsService
{
    public Session Session { get; } = session;

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(UserContext) || serviceType == typeof(GlobalContext))
        {
            throw new InvalidOperationException(
                $"{serviceType.Name} cannot be resolved from the session: a session holds " +
                $"{Session.Count} account(s). Invoke tools through ToolDispatcher, which resolves " +
                "them from an AccountServiceProvider for the chosen account.");
        }

        if (serviceType == typeof(Session)) return Session;
        if (serviceType == typeof(IServiceScopeFactory)) return this;
        if (serviceType == typeof(IServiceProvider)) return this;
        if (serviceType == typeof(IServiceProviderIsService)) return this;
        return inner.GetService(serviceType);
    }

    public bool IsService(Type serviceType) =>
        serviceType == typeof(UserContext) ||
        serviceType == typeof(GlobalContext) ||
        serviceType == typeof(Session) ||
        (inner.GetService<IServiceProviderIsService>()?.IsService(serviceType) ?? false);

    public IServiceScope CreateScope() =>
        new WrappedScope(inner.GetRequiredService<IServiceScopeFactory>().CreateScope(), Session);

    private sealed class WrappedScope(IServiceScope innerScope, Session session) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } =
            new SessionServiceProvider(innerScope.ServiceProvider, session);

        public void Dispose() => innerScope.Dispose();

        public ValueTask DisposeAsync() => innerScope is IAsyncDisposable a
            ? a.DisposeAsync()
            : ValueTask.CompletedTask;
    }
}
