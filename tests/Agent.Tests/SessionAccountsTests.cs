using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Tests;

public sealed class SessionAccountsTests
{
    [Fact]
    public void Handles_are_email_or_sysadmin_prefixed()
    {
        Assert.Equal("me@example.com", Account.BaseHandle(AccountRole.User, "me@example.com", "https://mail.example.com"));
        Assert.Equal("boss@example.com", Account.BaseHandle(AccountRole.DomainAdmin, "boss@example.com", "https://mail.example.com"));
        Assert.Equal("sysadmin:admin@mail.example.com",
            Account.BaseHandle(AccountRole.SysAdmin, "admin", "https://mail.example.com"));
        Assert.Equal("sysadmin:admin@mail.example.com:8101",
            Account.BaseHandle(AccountRole.SysAdmin, "admin", "http://mail.example.com:8101"));
        Assert.Equal("mail.example.com", Account.HostOf("https://mail.example.com:443/"));
    }

    [Fact]
    public void Clashing_handles_get_the_host_then_a_counter()
    {
        Assert.Equal("me@example.com", Session.UniqueHandle("me@example.com", "a.com", []));
        Assert.Equal("me@example.com#b.com", Session.UniqueHandle("me@example.com", "b.com", ["ME@example.com"]));
        Assert.Equal("me@example.com#b.com-2",
            Session.UniqueHandle("me@example.com", "b.com", ["me@example.com", "me@example.com#b.com"]));
    }
}
