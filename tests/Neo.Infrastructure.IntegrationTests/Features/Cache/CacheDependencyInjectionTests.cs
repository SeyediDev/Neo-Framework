using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Neo.Domain.Features.Cache;
using Neo.Infrastructure.Features.Cache;
using Xunit;

namespace Neo.Infrastructure.IntegrationTests.Features.Cache;

public sealed class CacheDependencyInjectionTests
{
    [Fact]
    public async Task AddNeoMemoryCacheServices_Should_Use_MemoryCacheService()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddNeoMemoryCacheServices(configuration);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<ICacheService>();

        cache.Should().BeOfType<MemoryCacheService>();

        await cache.SetStringAsync(
            "otp:test", "123456", TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);

        var value = await cache.GetStringAsync("otp:test", TestContext.Current.CancellationToken);
        value.Should().Be("123456");
    }
}
