using Neo.Domain.Features.Cache;
using Neo.Domain.Features.Client;
using Neo.Domain.Features.Sms;
using Neo.Domain.Features.Sms.Dto;
using Microsoft.Extensions.Caching.Distributed;
using OtpNet;

namespace Neo.Infrastructure.Features.Sms;

public class OtpService(
    IRequesterUser user, ICacheService cacheService, ISmsService smsService, TimeProvider timeProvider
    ) : IOtpService
{
    public byte[] GetNewOtpSeed()
    {
        return Guid.NewGuid().ToByteArray();
    }

    public async Task<DateTimeOffset> SendAsync(string mobile, byte[] seedBytes, string messageTemplate)
    {
        var key = GetCacheKey(mobile);
        var existKey = await cacheService.GetAsync<OtpData>(key);
        if (existKey != null)
        {
            return existKey.Expire;
        }
        var otp = GenerateOtp(seedBytes);
        DateTimeOffset expire = timeProvider.GetLocalNow().AddMinutes(2);
        await cacheService.SetAsync(key, new OtpData() { Expire = expire },
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2) });
        //var message = messageTemplate.Replace("{opt}", otp);
        await smsService.SendOtpAsync(new OtpSmsDto(mobile, otp));
        return expire;
    }
    class OtpData
    {
        public DateTimeOffset Expire { get; set; }
    }

    public async Task<TimeSpan?> OtpTimeout(string mobile)
    {
        var key = GetCacheKey(mobile);
        var otpData = await cacheService.GetAsync<OtpData>(key);
        return otpData is not null ? timeProvider.GetLocalNow() - otpData.Expire : null;
    }

    public bool Verify(byte[] seedBytes, string otp)
    {
        var totp = new Totp(seedBytes, 120, OtpHashMode.Sha1, 6);
        // verify otp.
        var otpVerified = totp.VerifyTotp(otp, out long _/*timeWindowUsed*/, VerificationWindow.RfcSpecifiedNetworkDelay);
        return otpVerified;
        //return true;
    }

    private static string GenerateOtp(byte[] seedBytes)
    {
        var totp = new Totp(seedBytes, 120, OtpHashMode.Sha1, 6);
        var dateTime = DateTime.UtcNow;
        var totpCode = totp.ComputeTotp(dateTime);
        return totpCode;
    }
    private string GetCacheKey(string mobile)
    {
        return $"{user.AppName}.{mobile}";
    }
}
