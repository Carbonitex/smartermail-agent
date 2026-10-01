using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Server;

namespace SmarterMailAgent.Controllers;

/// <summary>
/// What this server offers, for the UI to decide what to show. Anonymous and cheap. The page reads
/// it at load instead of the server rewriting <c>index.html</c>, whose ETag only covers asset content.
/// </summary>
[ApiController]
[Route("api/config")]
[AllowAnonymous]
public sealed class ConfigController(ServerOptions options, ResumeSealer sealer) : ControllerBase
{
    public sealed record ResumeConfig(bool Enabled, int Days);

    public sealed record ProfilesConfig(bool Enabled);

    public sealed record TasksConfig(bool Enabled, int MinIntervalMinutes, int MaxPerProfile, int MaxToolRounds);

    /// <param name="Mode"><c>server</c> or <c>browser</c> (BROWSER_ONLY_MODE).</param>
    public sealed record ConfigResponse(string Mode, ResumeConfig Resume, ProfilesConfig Profiles, TasksConfig Tasks);

    [HttpGet]
    public IActionResult Get() => Ok(new ConfigResponse(
        options.ServerMode ? "server" : "browser",
        new ResumeConfig(sealer.Enabled, sealer.Enabled ? (int)sealer.MaxAge.TotalDays : 0),
        new ProfilesConfig(options.ServerMode),
        new TasksConfig(options.TasksEnabled, (int)options.TaskMinInterval.TotalMinutes, options.TasksPerProfile,
            options.TaskMaxToolRounds)));
}
