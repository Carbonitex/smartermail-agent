using SmarterMailMcp.Hosting;
using SmarterMailMcp.Server.Prompts;
using SmarterMailMcp.Server.Tools;
using SmarterMailMcp.User;

// One mailbox account (plus its domain's admin tools when it is a domain admin). Everything shared
// with the admin server (configuration, sign-in, transports, auth, read-only) is in src/Mcp.Hosting.
return await McpHost.RunAsync(args, new McpHostDefinition
{
    ServerName = "smartermail-mcp-user",
    UserVariable = "SMARTERMAIL_USER",
    PasswordVariable = "SMARTERMAIL_PASSWORD",
    UserType = "user",
    DefaultTokenFile = "/tmp/smartermail_token.json",
},
async (userContext, mcp) =>
{
    mcp.WithToolsFromAssembly(typeof(MailTools).Assembly)              // src/Tools.Mailbox
       .WithPromptsFromAssembly(typeof(UserPrompts).Assembly);

    // Domain-admin tools 403 for a plain mailbox user; only register them when useful.
    if (await DomainToolsGate.ShouldEnableAsync(userContext))
        mcp.WithToolsFromAssembly(typeof(DomainAdminTools).Assembly);   // src/Tools.DomainAdmin
});
