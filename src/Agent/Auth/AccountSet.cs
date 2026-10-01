namespace SmarterMailAgent.Auth;

/// <summary>
/// The SmarterMail accounts a chat can use, with handles unique within the set. A browser-only
/// session owns its own; in server mode every session of one profile, and that profile's scheduled
/// tasks, share the profile's set (<see cref="Profiles.ProfileRuntime"/>). They must: SmarterMail
/// keeps one token per (user, clientId), so two live copies of one account would rotate each other's
/// refresh token dead.
/// </summary>
public sealed class AccountSet
{
    private readonly object _lock = new();
    private readonly List<Account> _accounts = [];

    /// <summary>
    /// Raised on every token rotation and every add, replace or remove. A session turns it into its
    /// resume version (<see cref="Session.ResumeVersion"/>).
    /// </summary>
    public event Action? Changed;

    /// <summary>A snapshot, in the order the accounts were added.</summary>
    public IReadOnlyList<Account> Accounts
    {
        get { lock (_lock) return _accounts.ToArray(); }
    }

    public int Count
    {
        get { lock (_lock) return _accounts.Count; }
    }

    public enum AddStatus { Added, Replaced, LimitReached }

    /// <summary>
    /// Adds <paramref name="account"/> and gives it a handle unique within this set. An existing
    /// account with the same (baseUrl, email) is replaced in place and returned in
    /// <paramref name="replaced"/>; the caller disposes it. At <paramref name="maxAccounts"/> nothing
    /// changes and <see cref="AddStatus.LimitReached"/> comes back.
    /// </summary>
    public AddStatus Add(Account account, int maxAccounts, out Account? replaced)
    {
        AddStatus status;
        lock (_lock)
        {
            var index = _accounts.FindIndex(a => SameLogin(a, account.BaseUrl, account.EmailAddress));
            var existing = index >= 0 ? _accounts[index] : null;
            replaced = existing;

            if (existing is null && _accounts.Count >= maxAccounts)
                return AddStatus.LimitReached;

            var others = _accounts.Where(a => !ReferenceEquals(a, existing)).Select(a => a.Handle);
            account.Handle = UniqueHandle(
                Account.BaseHandle(account.Role, account.EmailAddress, account.BaseUrl),
                Account.HostOf(account.BaseUrl), others);
            account.Rotated = RaiseChanged;

            if (existing is not null)
            {
                _accounts[index] = account;
                status = AddStatus.Replaced;
            }
            else
            {
                _accounts.Add(account);
                status = AddStatus.Added;
            }
        }

        RaiseChanged();
        return status;
    }

    /// <summary>Whether adding this login would need a free slot (false when it would replace one).</summary>
    public bool IsFullFor(string baseUrl, string emailAddress, int maxAccounts)
    {
        lock (_lock)
            return _accounts.Count >= maxAccounts && !_accounts.Any(a => SameLogin(a, baseUrl, emailAddress));
    }

    /// <summary>Detaches the account; the caller disposes it.</summary>
    public Account? Remove(string accountId)
    {
        Account account;
        lock (_lock)
        {
            var index = _accounts.FindIndex(a => string.Equals(a.Id, accountId, StringComparison.Ordinal));
            if (index < 0) return null;
            account = _accounts[index];
            _accounts.RemoveAt(index);
        }

        RaiseChanged();
        return account;
    }

    public Account? Find(string? handle)
    {
        if (string.IsNullOrEmpty(handle)) return null;
        lock (_lock)
            return _accounts.FirstOrDefault(a => string.Equals(a.Handle, handle, StringComparison.OrdinalIgnoreCase));
    }

    public Account? FindById(string id)
    {
        lock (_lock)
            return _accounts.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
    }

    /// <summary>Empties the set and returns what it held; the caller disposes or forgets them.</summary>
    public Account[] TakeAll()
    {
        Account[] accounts;
        lock (_lock)
        {
            accounts = _accounts.ToArray();
            _accounts.Clear();
        }

        if (accounts.Length > 0)
            RaiseChanged();
        return accounts;
    }

    /// <summary>Removes and returns the accounts <paramref name="match"/> picks.</summary>
    public Account[] TakeWhere(Func<Account, bool> match)
    {
        Account[] taken;
        lock (_lock)
        {
            taken = _accounts.Where(match).ToArray();
            _accounts.RemoveAll(a => taken.Contains(a));
        }

        if (taken.Length > 0)
            RaiseChanged();
        return taken;
    }

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>
    /// <paramref name="baseHandle"/>, or <c>baseHandle#host</c> on a clash, then a counter. Handles
    /// compare case-insensitively because they are mostly email addresses.
    /// </summary>
    public static string UniqueHandle(string baseHandle, string host, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseHandle))
            return baseHandle;

        var withHost = $"{baseHandle}#{host}";
        if (!taken.Contains(withHost))
            return withHost;

        for (var n = 2; ; n++)
        {
            var candidate = $"{withHost}-{n}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    public static bool SameLogin(Account account, string baseUrl, string emailAddress) =>
        string.Equals(account.BaseUrl.TrimEnd('/'), baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(account.EmailAddress, emailAddress, StringComparison.OrdinalIgnoreCase);
}
