using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using NJsonSchema;
using NSwag;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace Neo.AgentOrchestration.Api;

// Extend Neo's existing NSwag generator; do not introduce a second document
// generator or an independently maintained static API specification.
public sealed class WorkspaceContractProcessor : IOperationProcessor
{
    public bool Process(OperationProcessorContext context)
    {
        var policies = context.ControllerType.GetCustomAttributes<AuthorizeAttribute>(true)
            .Concat(context.MethodInfo.GetCustomAttributes<AuthorizeAttribute>(true))
            .Select(x => x.Policy).OfType<string>().Where(x => x.StartsWith("workspace.", StringComparison.Ordinal))
            .Select(x => x["workspace.".Length..]).Distinct().ToArray();
        var operation = context.OperationDescription.Operation;
        operation.ExtensionData ??= new Dictionary<string, object?>();
        operation.ExtensionData["x-workspace-permissions"] = policies;
        if (policies.Any(x => x is "write" or "approve"))
            operation.Parameters.Add(new OpenApiParameter { Name = "X-Orchestration-Chat", Kind = OpenApiParameterKind.Header,
                IsRequired = true, Description = "Chat correlation/ownership context; not an authentication credential.",
                Schema = new JsonSchema { Type = JsonObjectType.String, MinLength = 1, MaxLength = 200 } });
        return true;
    }
}
