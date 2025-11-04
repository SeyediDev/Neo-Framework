namespace Neo.Application.Features.Captcha;

public class CaptchaValidationResponse
{
    public bool IsMatched { get; set; } = false;
    public string? CaptchaId { get; set; }
}