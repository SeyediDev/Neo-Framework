using Neo.Endpoint.Features.Monitoring.Models;
using Neo.Endpoint.Features.Monitoring.Services;
using System.Text;

namespace Neo.Endpoint.Features.Monitoring.Controllers;

/// <summary>
/// API controller for metrics data
/// </summary>
[ApiController]
[Route("api/monitoring/metrics")]
public class MetricsController : ControllerBase
{
    private readonly IMetricsStore _store;
    private readonly ILogger<MetricsController> _logger;

    public MetricsController(IMetricsStore store, ILogger<MetricsController> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Get all metric definitions
    /// </summary>
    [HttpGet("definitions")]
    public ActionResult<IEnumerable<MetricDefinition>> GetDefinitions()
    {
        return Ok(_store.GetMetricDefinitions());
    }

    /// <summary>
    /// Get metric definition by name
    /// </summary>
    [HttpGet("definitions/{name}")]
    public ActionResult<MetricDefinition> GetDefinition(string name)
    {
        var definition = _store.GetMetricDefinition(name);
        if (definition == null)
            return NotFound();
        return Ok(definition);
    }

    /// <summary>
    /// Query metric data points
    /// </summary>
    [HttpPost("query")]
    public ActionResult<IEnumerable<MetricDataPoint>> Query([FromBody] MetricsQueryRequest request)
    {
        return Ok(_store.Query(request));
    }

    /// <summary>
    /// Get time series data for a metric
    /// </summary>
    [HttpGet("{name}/timeseries")]
    public ActionResult<MetricTimeSeries> GetTimeSeries(
        string name,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int? aggregationIntervalSeconds = null)
    {
        return Ok(_store.GetTimeSeries(name, from, to, null, aggregationIntervalSeconds));
    }

    /// <summary>
    /// Get statistics for a metric
    /// </summary>
    [HttpGet("{name}/stats")]
    public ActionResult<MetricStats> GetStats(
        string name,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        var stats = _store.GetStats(name, from, to);
        if (stats == null)
            return NotFound();
        return Ok(stats);
    }

    /// <summary>
    /// Get current system metrics
    /// </summary>
    [HttpGet("system")]
    public ActionResult<SystemMetrics> GetSystemMetrics()
    {
        return Ok(_store.GetSystemMetrics());
    }

    /// <summary>
    /// Get application metrics
    /// </summary>
    [HttpGet("application")]
    public ActionResult<ApplicationMetrics> GetApplicationMetrics()
    {
        return Ok(_store.GetApplicationMetrics());
    }

    /// <summary>
    /// Get all collected metrics with current values - useful for debugging
    /// </summary>
    [HttpGet("all")]
    public ActionResult<object> GetAllMetrics()
    {
        try
        {
            var definitions = _store.GetMetricDefinitions().ToList();
            var result = definitions.Select(d => new 
            {
                Name = d.Name,
                Type = d.Type,
                Unit = d.Unit,
                Description = d.Description,
                Stats = _store.GetStats(d.Name)
            }).OrderByDescending(m => m.Stats?.Count ?? 0).ToList();
            
            return Ok(new 
            { 
                TotalMetrics = result.Count,
                Metrics = result 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all metrics");
            return StatusCode(500, new { error = "Failed to get metrics", message = ex.Message });
        }
    }

    /// <summary>
    /// OTLP endpoint for receiving metrics from external APIs
    /// POST /api/monitoring/metrics/otlp
    /// </summary>
    [HttpPost("otlp")]
    [Consumes("application/json", "application/x-protobuf")]
    public async Task<IResult> ReceiveOtlpMetrics(HttpRequest request)
    {
        try
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            
            _logger.LogDebug("Received OTLP metrics data: {Length} bytes", json.Length);
            
            // Note: Full OTLP implementation would parse the JSON/protobuf and convert to MetricDataPoint
            // For now, MeterListener in MetricsCollector handles metrics from the same process
            // For cross-process collection, implement proper OTLP deserialization here
            
            return Results.Ok(new { received = true, message = "Metrics received" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving OTLP metrics");
            return Results.Problem("Error receiving metrics", statusCode: 500);
        }
    }
}

