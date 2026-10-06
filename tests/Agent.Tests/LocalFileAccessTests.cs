using Microsoft.Extensions.Logging.Abstractions;
using SmarterMailAgent.Auth;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Tests;

/// <summary>
/// The agent is a remote server: a path in upload_attachment / download_email_attachment would name
/// the agent's own files (its data directory, /proc, …), not the user's. Its accounts never get
/// filesystem access.
/// </summary>
public sealed class LocalFileAccessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Agent_accounts_have_no_local_file_access(bool readOnly)
    {
        var auth = new SmarterMailAuth(NullLogger<SmarterMailAuth>.Instance, new HttpClient());
        var account = AccountBuilder.Build(auth,
            new AuthOutcome.Success(new TokenData { AccessToken = "t", RefreshToken = "r", Username = "user@example.com" }),
            "https://mail.example.com", readOnly);

        Assert.False(account.GlobalContext.LocalFileAccess);
    }
}
