using Neo.Endpoint.Features.Monitoring.Models;
using Neo.Endpoint.Features.Monitoring.Services;
using System.Text;

namespace Neo.Endpoint.Features.Monitoring.Controllers;

/// <summary>
/// API controller for trace data
/// </summary>
[ApiController]
[Route("api/monitoring/traces")]
public class TracesController : ControllerBase
{
    private readonly ITraceStore _store;
    private readonly ILogger<TracesController> _logger;

    public TracesController(ITraceStore store, ILogger<TracesController> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Query traces
    /// </summary>
    [HttpPost("query")]
    public ActionResult<IEnumerable<TraceData>> Query([FromBody] TraceQueryRequest request)
    {
        return Ok(_store.Query(request));
    }

    /// <summary>
    /// Get recent traces
    /// </summary>
    [HttpGet("recent")]
    public ActionResult<IEnumerable<TraceData>> GetRecentTraces([FromQuery] int limit = 100)
    {
        try
        {
            return Ok(_store.GetRecentTraces(limit));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recent traces");
            return StatusCode(500, new { error = "Failed to get traces", message = ex.Message });
        }
    }

    /// <summary>
    /// Get trace by ID
    /// </summary>
    [HttpGet("{traceId}")]
    public ActionResult<TraceData> GetTrace(string traceId)
    {
        var trace = _store.GetTrace(traceId);
        if (trace == null)
            return NotFound();
        return Ok(trace);
    }

    /// <summary>
    /// Get trace statistics
    /// </summary>
    [HttpGet("stats")]
    public ActionResult<TraceStats> GetStats(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        return Ok(_store.GetStats(from, to));
    }

    /// <summary>
    /// Get all service names
    /// </summary>
    [HttpGet("services")]
    public ActionResult<IEnumerable<string>> GetServiceNames()
    {
        return Ok(_store.GetServiceNames());
    }

    /// <summary>
    /// Get all operation names
    /// </summary>
    [HttpGet("operations")]
    public ActionResult<IEnumerable<string>> GetOperationNames([FromQuery] string? serviceName = null)
    {
        return Ok(_store.GetOperationNames(serviceName));
    }

    /// <summary>
    /// OTLP endpoint for receiving traces from external APIs
    /// POST /api/monitoring/traces/otlp
    /// </summary>
    [HttpPost("otlp")]
    [Consumes("application/json", "application/x-protobuf")]
    public async Task<IResult> ReceiveOtlpTraces(HttpRequest request)
    {
        try
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            
            _logger.LogDebug("Received OTLP traces data: {Length} bytes", json.Length);
            
            // Note: Full OTLP implementation would parse the JSON/protobuf and convert to TraceSpan
            // For now, ActivityListener in TraceCollector handles activities from the same process
            // For cross-process collection, implement proper OTLP deserialization here
            
            return Results.Ok(new { received = true, message = "Traces received" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving OTLP traces");
            return Results.Problem("Error receiving traces", statusCode: 500);
        }
    }
}

