using Neo.Domain.Features.PubSub;
using Neo.Domain.Features.Sms;
using Neo.Domain.Features.Sms.Dto;

namespace Neo.Infrastructure.Features.Sms;

public class SmsService(ICandoPublisher candoPublish) : ISmsService
{
    public async Task SendAsync(SmsDto model)
    {
        await candoPublish.Publish(model);
    }

    public async Task SendOtpAsync(OtpSmsDto model)
    {
        // تبدیل DTO به Notification
        var notification = OtpSmsNotification.FromDto(model);
        await candoPublish.Publish(notification);
    }
}
