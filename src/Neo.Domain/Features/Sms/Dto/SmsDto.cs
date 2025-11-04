namespace Neo.Domain.Features.Sms.Dto;
public record SmsDto(string mobile, string message)
{
}
public record OtpSmsDto(string mobile, string message)
{
}
