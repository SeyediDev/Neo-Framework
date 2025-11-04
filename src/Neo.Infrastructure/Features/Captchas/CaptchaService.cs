using Neo.Common.Extensions;
using Neo.Domain.Features.Cache;
using Neo.Domain.Features.Captchas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.InteropServices;
using Neo.Domain.Features.Captchas.Dto;

namespace Neo.Infrastructure.Features.Captchas;

public class CaptchaService(IConfiguration configuration, ICacheService redisCache, ILogger<CaptchaService> logger)
    : ICaptchaProvider
{
    private readonly Random _random = new();

    public async Task<Captcha> GetCaptcha()
    {
        return await GenerateImageCaptcha();
    }
    public async Task<Captcha> Reload(string captchaId)
    {
        Captcha captcha = await GenerateImageCaptcha(captchaId: captchaId);
        return captcha;
    }
    private async Task<Captcha> GenerateImageCaptcha([Optional] string challenge, [Optional] string captchaId)
    {
        Captcha captcha = captchaId.HasValue() ? new(captchaId) : new();
        try
        {
            CaptchaGenerator.MkCaptcha c = new(CaptchaGenerator.CharStyle.NumbersOnly, 5, false, 35, 50, 160, 50,
                CaptchaGenerator.ComplexityLevel.Medium);

            (System.Drawing.Bitmap image, string value) = c.GenerateImageCaptcha();
            captcha.Image = image.ToBase64();
            captcha.Challenge = value;
            captcha.AudioUrl =
                $"{configuration["Captcha:Samples:Storage:AudioPath"]}/" +
                $"{captcha.Challenge.Sha256()}.{configuration["Captcha:Samples:Extensions:Audio"]}";

            _ = await BindChallenge(captcha);
            logger.LogDebug("Challenge binded {@Captcha}", captcha);
            return captcha;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Captcha.GenerateImageCaptcha threw an exception");
            if (string.IsNullOrEmpty(captcha.Challenge))
            {
                throw new Exception("CaptchaChallengeNullException");
            }
            throw;
        }
    }

    public async Task<bool> Validate(string captchaId, string receivedChallenge)
    {
        string? captchaValue = await redisCache.GetStringAsync($"{configuration["Captcha:CachePrefix"]}{captchaId}");
        if (string.IsNullOrEmpty(captchaValue))
        {
            throw new Exception("InvalidCaptchaException");
        }
        if (configuration["Captcha:AutomaticInvalidation"].ToBoolean())
        {
            await redisCache.RemoveAsync($"{configuration["Captcha:CachePrefix"]}{captchaId}");
        }

        return captchaValue.IsEqualCaseInsensitive(receivedChallenge);
    }

    private string? PickChallenge([Optional] string current)
    {
        try
        {
            string assemblyLocation = Assembly.GetExecutingAssembly().Location;
            string? rootDir = Path.GetDirectoryName(assemblyLocation);
            string samplesDir = $"{rootDir}{configuration["Captcha:Samples:Storage:ImagePath"]}".Replace("\\", "/");

            logger.LogDebug("Picking challenge from {@Path}",
                new
                {
                    AssebmblyLocation = assemblyLocation,
                    RootPath = rootDir,
                    SamplesPath = samplesDir,
                });

            string[] challenges = Directory.GetFiles($"{samplesDir}",
                $"*.{configuration["Captcha:Samples:Extensions:Image"]}", SearchOption.TopDirectoryOnly);

            if (!challenges.Any())
            {
                logger.LogWarning("No challenge pic found in {@Path}", samplesDir);
            }

            bool duplicatedSamplePicked = true;
            string challenge = string.Empty;

            while (duplicatedSamplePicked)
            {
                int rand = _random.Next(0, challenges.Length);
                if (challenges[rand] == current || challenges[rand].IsNullOrEmpty())
                {
                    continue;
                }

                challenge = challenges[rand];
                duplicatedSamplePicked = false;
            }

            string captchaValue = Path.GetFileName(challenge)
                .Replace($".{configuration["Captcha:Samples:Extensions:Image"]}", "");

            return captchaValue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed To Pick Challenge");
            return default;
        }
    }

    private async Task<bool> BindChallenge(Captcha captcha)
    {
        await redisCache.SetStringAsync($"{configuration["Captcha:CachePrefix"]}{captcha.CaptchaId}",
            captcha.Challenge!, TimeSpan.FromSeconds(configuration["Captcha:Timeout"].ToInt16()));
        return true;
    }
}