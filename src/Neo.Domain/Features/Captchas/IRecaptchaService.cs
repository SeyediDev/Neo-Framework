namespace Neo.Domain.Features.Captchas;
public interface IRecaptchaService
{
    Task<bool> CaptchaValidation(string token);
}
