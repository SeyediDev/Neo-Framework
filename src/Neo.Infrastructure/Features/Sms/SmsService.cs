using Neo.Domain.Features.PubSub;
using Neo.Domain.Features.Sms;
using Neo.Domain.Features.Sms.Dto;

namespace Neo.Infrastructure.Features.Sms;

public class SmsService(INeoPublisher neoPublish) : ISmsService
{
    public async Task SendAsync(SmsDto model, CancellationToken cancellationToken = default)
    {
        await neoPublish.Publish(model, cancellationToken);
    }

    public async Task SendOtpAsync(OtpSmsDto model, CancellationToken cancellationToken = default)
    {
        // تبدیل DTO به Notification
        var notification = OtpSmsNotification.FromDto(model);
        await neoPublish.Publish(notification, cancellationToken);
    }
}