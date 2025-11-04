using Neo.Domain.Features.Sms.Dto;

namespace Neo.Domain.Features.Sms;

public interface ISmsProvidorService
{
    public Task<bool> SendOtpAsync(OtpSmsDto model);
    public Task<bool> SendAsync(SmsDto model);
}
