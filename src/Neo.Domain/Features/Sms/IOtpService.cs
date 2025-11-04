namespace Neo.Domain.Features.Sms;

public interface IOtpService
{
    byte[] GetNewOtpSeed();
    Task<DateTimeOffset> SendAsync(string mobile, byte[] seedBytes, string messageTemplate);
    bool Verify(byte[] seedBytes, string otp);
    Task<TimeSpan?> OtpTimeout(string mobile);
}
