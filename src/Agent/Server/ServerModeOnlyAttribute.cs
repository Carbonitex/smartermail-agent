using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SmarterMailAgent.Server;

/// <summary>
/// Profile and task endpoints in browser-only mode: <c>404 { code: "SERVER_MODE_DISABLED" }</c>,
/// answered as a resource filter, i.e. before the controller (and the database it would need) is
/// ever constructed.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ServerModeOnlyAttribute : Attribute, IResourceFilter, IOrderedFilter
{
    // Ahead of everything else that might touch server-mode services.
    public int Order => int.MinValue;

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<ServerOptions>();
        if (options.ServerMode)
            return;

        context.Result = new NotFoundObjectResult(new
        {
            error = "This server runs in browser-only mode: nothing is stored here.",
            code = "SERVER_MODE_DISABLED",
        });
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
