using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Auth;

/// <summary>
/// The provider one tool call runs on: <see cref="UserContext"/>, <see cref="GlobalContext"/> and
/// <see cref="Account"/> resolve to the account <c>ToolDispatcher</c> chose, and everything else is
/// delegated to the request's provider.
///
/// Same rules as <see cref="SessionServiceProvider"/>: it hands out the account's objects but never
/// owns or disposes them (the account does), and scopes created from it stay wrapped.
/// </summary>
public sealed class AccountServiceProvider(IServiceProvider inner, Account account)
    : IServiceProvider, IServiceScopeFactory, IServiceProviderIsService
{
    public Account Account { get; } = account;

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(UserContext)) return Account.UserContext;
        if (serviceType == typeof(GlobalContext)) return Account.GlobalContext;
        if (serviceType == typeof(Account)) return Account;
        if (serviceType == typeof(IServiceScopeFactory)) return this;
        if (serviceType == typeof(IServiceProvider)) return this;
        if (serviceType == typeof(IServiceProviderIsService)) return this;
        return inner.GetService(serviceType);
    }

    public bool IsService(Type serviceType) =>
        serviceType == typeof(UserContext) ||
        serviceType == typeof(GlobalContext) ||
        serviceType == typeof(Account) ||
        (inner.GetService<IServiceProviderIsService>()?.IsService(serviceType) ?? false);

    public IServiceScope CreateScope() =>
        new WrappedScope(inner.GetRequiredService<IServiceScopeFactory>().CreateScope(), Account);

    private sealed class WrappedScope(IServiceScope innerScope, Account account) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } =
            new AccountServiceProvider(innerScope.ServiceProvider, account);

        public void Dispose() => innerScope.Dispose();

        public ValueTask DisposeAsync() => innerScope is IAsyncDisposable a
            ? a.DisposeAsync()
            : ValueTask.CompletedTask;
    }
}
