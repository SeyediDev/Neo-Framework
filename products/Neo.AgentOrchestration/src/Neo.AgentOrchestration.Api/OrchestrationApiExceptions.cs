using System.Data.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Application.Workspace;

namespace Neo.AgentOrchestration.Api;

public sealed class OrchestrationApiExceptions : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception error, CancellationToken ct)
    {
        var (status, code, title) = error switch
        {
            SimulationDisabledException => (503, "simulation-disabled", "Simulation is not enabled on this installation."),
            OrchestrationUnavailableException => (503, "storage-unconfigured", "Configure the independent orchestration database."),
            KeyNotFoundException => (404, "not-found", "The resource was not found in this workspace."),
            ArgumentException => (400, "invalid-input", "The request contains an invalid value."),
            UnauthorizedAccessException => (403, "forbidden", "The operation is not authorized."),
            InvalidOperationException => (409, "conflict", "The resource or operation conflicts with its current state. Reload before retrying."),
            DbException => (503, "storage-unavailable", "Orchestration storage is temporarily unavailable."),
            _ => (500, "unexpected-error", "The request could not be completed.")
        };
        context.Response.StatusCode = status;
        // Never return exception text: it can contain SQL or provider details.
        await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title,
            Extensions = { ["code"] = code, ["traceId"] = context.TraceIdentifier } },
            options: null, contentType: "application/problem+json", cancellationToken: ct);
        return true;
    }
}
