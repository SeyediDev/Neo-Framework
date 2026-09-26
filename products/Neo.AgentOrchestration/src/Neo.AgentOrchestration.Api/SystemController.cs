using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Application;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Api;

[ApiController]
[Route("api/orchestration/v1/system")]
public sealed class SystemController(ISender sender) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public Task<ProductInfo> Get(CancellationToken ct) => sender.Send(new GetProductInfo(), ct);
}
