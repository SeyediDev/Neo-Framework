using Neo.AgentOrchestration.Contracts;
using Microsoft.AspNetCore.Authentication;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Neo.AgentOrchestration.Web;

public sealed class OrchestrationClient(HttpClient http, IHttpContextAccessor? accessor = null)
{
    public Task<ProductInfo?> GetInfoAsync(CancellationToken ct)
        => http.GetFromJsonAsync<ProductInfo>("api/orchestration/v1/system", ct);

    public async Task<T> SendAsync<T>(Guid organization, Guid workspace, string resource, CancellationToken ct,
        HttpMethod? method = null, object? body = null)
    {
        if (organization == Guid.Empty || workspace == Guid.Empty) throw new WebApiException(400);
        if (resource.StartsWith('/') || resource.Contains("://") || resource.Split('?',2)[0].Split('/').Contains("..")) throw new WebApiException(400);
        var context = accessor?.HttpContext ?? throw new WebApiException(401);
        var local = LocalDevelopmentAccess.IsAllowed(context) && http.BaseAddress is { IsLoopback: true };
        var authentication = local ? null : await context.AuthenticateAsync();
        var token = authentication?.Properties?.GetTokenValue("access_token");
        if (!local && (authentication is null || !authentication.Succeeded || string.IsNullOrWhiteSpace(token))) throw new WebApiException(401);
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get,
            $"api/orchestration/v1/organizations/{organization:D}/workspaces/{workspace:D}/{resource}");
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (local) request.Headers.Add("X-Orchestration-Local", "true");
        if (method is not null && method != HttpMethod.Get)
        {
            if (local) request.Headers.Add("X-Orchestration-Chat", "local-web");
            else if (!authentication!.Properties!.Items.TryGetValue(WebIdentity.ChatKey, out var chat) || string.IsNullOrWhiteSpace(chat))
                throw new WebApiException(401);
            else request.Headers.Add("X-Orchestration-Chat", chat);
            if (body is not null) request.Content = JsonContent.Create(body);
        }
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new WebApiException((int)response.StatusCode);
            return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new WebApiException(502);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException || error is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new WebApiException(503); }
    }
}

public sealed class WebApiException(int status) : Exception
{
    public int Status { get; } = status;
    public override string Message => Status switch {
        400 => "مقادیر فرم یا شناسه‌های محدوده معتبر نیستند.",
        401 => "نشست یا توکن API معتبر نیست؛ دوباره وارد شوید.",
        403 => "برای این عملیات یا فضای کاری دسترسی ندارید.",
        404 => "رکورد در این فضای کاری پیدا نشد.",
        409 => "رکورد تغییر کرده یا عملیات با وضعیت/مالکیت آن سازگار نیست. نتیجه را بررسی و سپس دوباره اقدام کنید.",
        503 => "سرویس یا قابلیت موردنیاز در دسترس یا پیکربندی‌شده نیست؛ نتیجه را پیش از تکرار بررسی کنید.",
        _ => "عملیات تأیید نشد. وضعیت فعلی را بررسی کنید؛ درخواست خودکار تکرار نشده است." };
}
