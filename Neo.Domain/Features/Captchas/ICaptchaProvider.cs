using Neo.Domain.Features.Captchas.Dto;

namespace Neo.Domain.Features.Captchas;

public interface ICaptchaProvider
{
    Task<Captcha> GetCaptcha();
    Task<Captcha> Reload(string captchaId);
    Task<bool> Validate(string captchaId, string receivedChallenge);
}