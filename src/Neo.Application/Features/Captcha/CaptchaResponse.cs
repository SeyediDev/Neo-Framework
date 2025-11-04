namespace Neo.Application.Features.Captcha;

public class CaptchaResponse
{
    public string? CaptchaId { get; set; }
    public string? Image { get; set; }
    public string? AudioUrl { get; set; }
}