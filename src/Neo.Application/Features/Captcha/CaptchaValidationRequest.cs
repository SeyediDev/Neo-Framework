namespace Neo.Application.Features.Captcha;

public class CaptchaValidationRequest
{
    public string? Challenge { get; set; }
    public string? CaptchaId { get; set; }
}