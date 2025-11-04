namespace Neo.Domain.Dto;
public class HttpLog
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Request Info
    public string? Method { get; set; }
    public string? Url { get; set; }
    public Dictionary<string, string>? RequestHeaders { get; set; }
    public string? RequestBody { get; set; }

    // Response Info
    public int StatusCode { get; set; }
    public Dictionary<string, string>? ResponseHeaders { get; set; }
    public string? ResponseBody { get; set; }

    public long ElapsedMilliseconds { get; set; }
}
