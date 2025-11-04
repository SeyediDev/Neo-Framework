using Asp.Versioning;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;

namespace Neo.Endpoint.Controller;

[ApiController]
[VersionRoute("[controller]")]
[ApiVersion("1")]
public abstract class AppControllerBase : ControllerBase
{
    private ISender _sender = null!;
    protected ISender Sender => _sender ??= HttpContext.RequestServices.GetRequiredService<ISender>();

    private ILogger _logger = null!;
    protected ILogger Logger => _logger ??= HttpContext.RequestServices.GetRequiredService<ILogger>();
}
