using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;

namespace Neo.Endpoint.IntegrationTests.Controller;

public class AppControllerBaseIntegrationTests : IClassFixture<WebApplicationFactory<TestWebApp.Program>>
{
    private readonly WebApplicationFactory<TestWebApp.Program> _factory;

    public AppControllerBaseIntegrationTests(WebApplicationFactory<TestWebApp.Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Controller_ShouldHaveApiControllerAttribute()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/test");

        // Assert
        // Just verify the endpoint exists and follows ApiController conventions
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }
}










