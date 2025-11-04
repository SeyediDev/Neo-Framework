using Neo.Common.Extensions;

namespace Neo.Domain.Features.Captchas.Dto;

public class Captcha
{
    public Captcha()
    {
        CaptchaId = Guid.NewGuid().ToString().Sha256();
    }

    public Captcha(string captchaId)
    {
        CaptchaId = captchaId;
    }

    public string? Challenge { get; set; }
    public string CaptchaId { get; }
    public string? Image { get; set; }
    public string? AudioUrl { get; set; }
}