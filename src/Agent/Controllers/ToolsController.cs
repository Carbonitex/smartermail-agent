using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Mcp;

namespace SmarterMailAgent.Controllers;

[ApiController]
[Route("api/tools")]
[Authorize(Policy = "SessionAccess")]
public sealed class ToolsController(ToolCatalog catalog, ToolDispatcher dispatcher) : ControllerBase
{
    public sealed record CallRequest(string? Name, Dictionary<string, JsonElement>? Arguments);

    /// <param name="Account">The handle the call ran as; null when it failed before resolving one.</param>
    public sealed record CallResponse(bool IsError, string Content, string? Account);

    [HttpGet]
    public IActionResult List()
    {
        var session = HttpContext.RequireSession();
        return Ok(catalog.List(session.Accounts));
    }

    [HttpPost("call")]
    [EnableRateLimiting("api")]
    public async Task<IActionResult> Call([FromBody] CallRequest request, CancellationToken ct)
    {
        var session = HttpContext.RequireSession();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Tool name is required." });

        var outcome = await dispatcher.DispatchAsync(
            session, request.Name, request.Arguments, HttpContext.RequestServices, ct);

        return outcome.Status switch
        {
            ToolDispatcher.Status.UnknownTool => NotFound(new { error = outcome.Message }),
            ToolDispatcher.Status.ReadOnly => StatusCode(StatusCodes.Status403Forbidden, new { error = outcome.Message }),
            ToolDispatcher.Status.InvalidAccount => Ok(new CallResponse(true, outcome.Message, null)),
            _ => Ok(new CallResponse(
                (outcome.Result.IsError ?? false) ||
                ToolInvoker.PayloadIndicatesFailure(ToolInvoker.Flatten(outcome.Result)),
                ToolInvoker.Flatten(outcome.Result),
                outcome.Account)),
        };
    }
}
