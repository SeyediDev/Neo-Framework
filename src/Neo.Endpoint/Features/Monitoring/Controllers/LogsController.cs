using Microsoft.AspNetCore.Authorization;
using Neo.Endpoint.Features.Monitoring.Models;
using Neo.Endpoint.Features.Monitoring.Services;
using System.Text;
using System.Text.Json;

namespace Neo.Endpoint.Features.Monitoring.Controllers;

/// <summary>
/// API controller for log data
/// </summary>
[ApiController]
[Route("api/monitoring/logs")]
[AllowAnonymous] // Allow access without authentication for monitoring
public class LogsController : ControllerBase
{
    private readonly ILogStore _store;
    private readonly ILogger<LogsController> _logger;

    public LogsController(ILogStore store, ILogger<LogsController> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Query logs
    /// </summary>
    [HttpPost("query")]
    public ActionResult<IEnumerable<LogEntry>> Query([FromBody] LogQueryRequest request)
    {
        return Ok(_store.Query(request));
    }

    /// <summary>
    /// Get recent logs
    /// </summary>
    [HttpGet("recent")]
    public ActionResult<IEnumerable<LogEntry>> GetRecentLogs(
        [FromQuery] int limit = 100,
        [FromQuery] string? minLevel = null)
    {
        try
        {
            Models.LogLevel? level = null;
            
            if (!string.IsNullOrEmpty(minLevel))
            {
                // Try parsing as numeric string first (frontend sends numeric values)
                if (int.TryParse(minLevel, out var numericValue) && 
                    Enum.IsDefined(typeof(Models.LogLevel), numericValue))
                {
                    level = (Models.LogLevel)numericValue;
                }
                // Try parsing as enum name
                else if (Enum.TryParse<Models.LogLevel>(minLevel, true, out var parsed))
                {
                    level = parsed;
                }
            }
            
            return Ok(_store.GetRecentLogs(limit, level));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recent logs");
            return StatusCode(500, new { error = "Failed to get logs", message = ex.Message });
        }
    }

    /// <summary>
    /// Get log statistics
    /// </summary>
    [HttpGet("stats")]
    public ActionResult<LogStats> GetStats(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        return Ok(_store.GetStats(from, to));
    }

    /// <summary>
    /// Get log timeline for charts
    /// </summary>
    [HttpGet("timeline")]
    public ActionResult<LogTimeline> GetTimeline(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int intervalSeconds = 60)
    {
        var fromDate = from ?? DateTime.UtcNow.AddHours(-1);
        var toDate = to ?? DateTime.UtcNow;
        return Ok(_store.GetTimeline(fromDate, toDate, intervalSeconds));
    }

    /// <summary>
    /// Get logs by trace ID
    /// </summary>
    [HttpGet("trace/{traceId}")]
    public ActionResult<IEnumerable<LogEntry>> GetByTraceId(string traceId)
    {
        return Ok(_store.GetByTraceId(traceId));
    }

    /// <summary>
    /// Get distinct source contexts
    /// </summary>
    [HttpGet("sources")]
    public ActionResult<IEnumerable<string>> GetSourceContexts()
    {
        return Ok(_store.GetSourceContexts());
    }

    /// <summary>
    /// OTLP endpoint for receiving logs from external APIs
    /// POST /api/monitoring/logs/otlp
    /// </summary>
    [HttpPost("otlp")]
    [Consumes("application/json")]
    public async Task<IResult> ReceiveOtlpLogs()
    {
        try
        {
			var request = HttpContext.Request;
			using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            
            _logger.LogDebug("Received OTLP logs data: {Length} bytes", json.Length);
            
            // Parse OTLP JSON format and convert to LogEntry
            // For now, we'll accept a simple JSON format that matches our LogEntry model
            try
            {
                var logEntries = System.Text.Json.JsonSerializer.Deserialize<List<LogEntry>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (logEntries != null)
                {
                    foreach (var entry in logEntries)
                    {
                        _store.Record(entry);
                    }
                    _logger.LogInformation("Received and stored {Count} log entries from external API", logEntries.Count);
                    return Results.Ok(new { received = true, count = logEntries.Count, message = "Logs received and stored" });
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse OTLP logs JSON, trying alternative format");
                
                // Try to parse as single LogEntry
                try
                {
                    var singleEntry = System.Text.Json.JsonSerializer.Deserialize<LogEntry>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    
                    if (singleEntry != null)
                    {
                        _store.Record(singleEntry);
                        _logger.LogInformation("Received and stored 1 log entry from external API");
                        return Results.Ok(new { received = true, count = 1, message = "Log received and stored" });
                    }
                }
                catch (JsonException)
                {
                    // Ignore and return error
                }
            }
            
            return Results.BadRequest(new { error = "Invalid log entry format" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving OTLP logs");
            return Results.Problem("Error receiving logs", statusCode: 500);
        }
    }

    /// <summary>
    /// Clear all logs
    /// </summary>
    [HttpDelete("clear")]
    public IActionResult Clear()
    {
        try
        {
            _store.Clear();
            _logger.LogInformation("All logs cleared");
            return Ok(new { message = "All logs cleared successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing logs");
            return StatusCode(500, new { error = "Failed to clear logs", message = ex.Message });
        }
    }
}

