using MediatR;

namespace Neo.Domain.Features.Sms.Dto;

/// <summary>
/// Notification for OTP SMS - MediatR compatible
/// </summary>
public record OtpSmsNotification(string Mobile, string Message) : INotification
{
    /// <summary>
    /// Create from OtpSmsDto
    /// </summary>
    public static OtpSmsNotification FromDto(OtpSmsDto dto) => new(dto.mobile, dto.message);
    
    /// <summary>
    /// Convert to OtpSmsDto
    /// </summary>
    public OtpSmsDto ToDto() => new(Mobile, Message);
}

