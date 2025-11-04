using System.ComponentModel.DataAnnotations;

namespace Neo.Domain.Entities.Common;

public class OutboxMessage : BaseCoreLogAuditableEntity<long>
{
    /// <summary>
    /// CSharp Message Name : typeof(TMessage).Name
    /// </summary>
    [MaxLength(60)]
    public string MessageName { get; set; } = null!;
    /// <summary>
    /// CSharp Message Type : typeof(TMessage).FullName
    /// </summary>
    [MaxLength(1024)]
    public string MessageType { get; set; } = null!;

    /// <summary>
    /// Json Serialized of TMessage
    /// </summary>
    public string MessageContent { get; set; } = null!;

    /// <summary>
    /// Json Serialized of TMessageResponse
    /// </summary>
    public string? MessageResponse { get; set; }

    /// <summary>
    /// Error In Publish (Queueing)
    /// </summary>
    [MaxLength(500)]
    public string? PublishError { get; set; }
    /// <summary>
    /// Publishing (Queueing) Try Count
    /// </summary>
    public int? PublishTryCount { get; set; }
    
    [MaxLength(500)]
    /// <summary>
    /// Process Method Error
    /// </summary>
    public string? ProcessError { get; set; }
    /// <summary>
    /// Process Method Try Count
    /// </summary>
    public int? ProcessTryCount { get; set; }

    public OutboxState OutboxState { get; set; } = OutboxState.Requested;
    
    [MaxLength(40)]
    public string? IdempotencyKey { get; set; }
    [MaxLength(40)]
    public string? JobId { get; set; }
}

/// <summary>
/// Lifecycle states of the outbox message.
/// </summary>
public enum OutboxState
{
    Requested,   // در Outbox ثبت شده، هنوز به صف نرفته
    Queued,      // به صف (مثل Hangfire) ارسال شده
    Processing,  // در حال اجرا
    Retrying,    // در حال تلاش مجدد
    Processed,   // اجرا موفق
    Failed,      // اجرا شکست خورده
    Expired,     // منقضی شده (Timeout/TTL)
    Canceled,    // لغو شده توسط کاربر/سیستم
    DuplicateIdempotencyKey
}
