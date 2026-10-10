using Microsoft.AspNetCore.Mvc;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Controllers;

/// <summary>The refusal every task endpoint gives a profile without task access (<c>TASKS_ACCESS=invite</c>).</summary>
internal static class TaskAccess
{
    /// <summary>Null when <paramref name="profileId"/> may use scheduled tasks here, else <c>403 TASKS_NOT_INVITED</c>.</summary>
    public static IActionResult? Refusal(ControllerBase controller, ServerOptions options, ProfileStore profiles, string profileId) =>
        profiles.GetProfile(profileId) is { } profile && options.AllowsTasks(profile) ? null : NotInvited(controller);

    public static ObjectResult NotInvited(ControllerBase controller) => controller.StatusCode(StatusCodes.Status403Forbidden, new
    {
        error = "Scheduled tasks on this server are by invitation. Enter an invite code in the Profile menu first.",
        code = TaskInviteStore.NotInvitedCode,
    });
}
