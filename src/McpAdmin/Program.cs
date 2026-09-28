using SmarterMailMcp.Hosting;
using SmarterMailMcp.SystemAdmin.Tools;

// One system-admin account. Everything shared with the user server (configuration, sign-in,
// transports, auth, read-only) is in src/Mcp.Hosting.
return await McpHost.RunAsync(args, new McpHostDefinition
{
    ServerName = "smartermail-mcp-admin",
    UserVariable = "SMARTERMAIL_ADMIN_USER",
    PasswordVariable = "SMARTERMAIL_ADMIN_PASSWORD",
    UserType = "admin",
    DefaultTokenFile = "/tmp/smartermail_sysadmin_token.json",
},
(_, mcp) =>
{
    mcp.WithToolsFromAssembly(typeof(ServerTools).Assembly);   // src/Tools.SysAdmin
    return Task.CompletedTask;
});
