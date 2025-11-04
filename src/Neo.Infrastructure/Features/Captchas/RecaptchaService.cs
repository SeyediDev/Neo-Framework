using Neo.Common.Extensions;
using Neo.Domain.Features.Captchas;
using Neo.Domain.Features.Captchas.Dto;
using Microsoft.Extensions.Configuration;

namespace Neo.Infrastructure.Features.Captchas;

public class RecaptchaService(IConfiguration configuration, HttpClient httpClient) : IRecaptchaService
{
    public async Task<bool> CaptchaValidation(string token)
    {
        var response = await httpClient.GetAsync($"recaptcha/api/siteverify?secret={configuration["Recaptcha:SecretKey"]}&response={token}");
        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync();
            var model = responseContent.FromJson<RecaptchaResponseDto>();
            return model.Success;
        }
        return false;
    }
}
