using Neo.Domain.Features.Sms.Dto;

namespace Neo.Domain.Features.Sms;
public interface ISmsService
{
    Task SendAsync(SmsDto model);
    Task SendOtpAsync(OtpSmsDto model);
}
