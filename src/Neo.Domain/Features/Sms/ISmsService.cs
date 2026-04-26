using Neo.Domain.Features.Sms.Dto;

namespace Neo.Domain.Features.Sms;

public interface ISmsService
{
    Task SendAsync(SmsDto model, CancellationToken cancellationToken=default);
    Task SendOtpAsync(OtpSmsDto model, CancellationToken cancellationToken = default);
}
