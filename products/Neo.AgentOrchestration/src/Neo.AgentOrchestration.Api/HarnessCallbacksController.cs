using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Neo.AgentOrchestration.Application.Runs;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Infrastructure.Runs;
using NSwag;
using NSwag.Annotations;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace Neo.AgentOrchestration.Api;

public sealed class HarnessAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, ConfiguredRunProviders providers, IHarnessSecrets secrets)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Name = "HarnessKey";
    public const string Header = "X-Neo-Harness-Key";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        try
        {
            if (!Guid.TryParse(Request.RouteValues["organizationId"]?.ToString(), out var org) ||
                !Guid.TryParse(Request.RouteValues["workspaceId"]?.ToString(), out var workspace))
                return Task.FromResult(AuthenticateResult.Fail("Invalid harness scope."));
            var provider = "http." + Request.RouteValues["connection"]?.ToString();
            var target = providers.Resolve(provider, new(org, workspace));
            var values = Request.Headers[Header];
            if (values.Count != 1 || values[0] is not { Length: >= 32 and <= 1024 } supplied ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(secrets.Resolve(target.CallbackSecretRef)))))
                return Task.FromResult(AuthenticateResult.Fail("Invalid harness credential."));
            var user = new ClaimsPrincipal(new ClaimsIdentity([new("harness.provider", provider)], Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, Name)));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { return Task.FromResult(AuthenticateResult.Fail("Harness connection unavailable.")); }
    }
}

[ApiController]
[Route("api/orchestration/v1/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/harness/{connection}/runs/{runId:guid}/result")]
[Authorize(AuthenticationSchemes = HarnessAuthentication.Name)]
[RequestSizeLimit(262144)]
[OpenApiOperationProcessor(typeof(HarnessContractProcessor))]
public sealed class HarnessCallbacksController(ISender sender, ConfiguredRunProviders providers, IHarnessSecrets secrets) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<HarnessReceipt>(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    public Task<HarnessReceipt> Receive(Guid organizationId, Guid workspaceId, Guid runId, HarnessResult body, CancellationToken ct)
    {
        if (body.Evidence?.Count > 100 || body.Evidence?.Any(x => x is null) == true)
            throw new ArgumentException("Invalid result evidence.");
        var scope = new WorkspaceScope(organizationId, workspaceId);
        var provider = User.FindFirstValue("harness.provider") ?? throw new UnauthorizedAccessException();
        var target = providers.Resolve(provider, scope);
        // Reject known transport credentials if a misconfigured gateway puts
        // them in a result. Never store response bodies or secrets as log data.
        var text = new[] { body.Summary }.Concat((body.Evidence ?? []).SelectMany(x => new[] { x.Reference, x.Details, x.CommitSha }));
        foreach (var reference in new[] { target.DispatchSecretRef, target.CallbackSecretRef })
        {
            var secret = secrets.Resolve(reference);
            if (text.Any(value => value?.Contains(secret, StringComparison.Ordinal) == true))
                throw new ArgumentException("Result contains a transport credential.");
        }
        return sender.Send(new ReceiveHarnessResult(scope, runId, provider, body), ct);
    }
}
public sealed class HarnessContractProcessor : IOperationProcessor
{
    public bool Process(OperationProcessorContext context)
    {
        context.Document.SecurityDefinitions[HarnessAuthentication.Name] = new OpenApiSecurityScheme
        { Type = OpenApiSecuritySchemeType.ApiKey, Name = HarnessAuthentication.Header, In = OpenApiSecurityApiKeyLocation.Header };
        context.OperationDescription.Operation.Security = [new OpenApiSecurityRequirement { [HarnessAuthentication.Name] = [] }];
        context.OperationDescription.Operation.Description = "Connection-scoped authenticated result; this key cannot access workspace CRUD. Duplicate result is idempotent; changed result conflicts.";
        return true;
    }
}
