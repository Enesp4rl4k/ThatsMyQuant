using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Listener.Api.Middleware;
using Xunit;

namespace ThatsMyQuant.Tests.Middleware;

public class ApiKeyValidationMiddlewareTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/ready")]
    [InlineData("/metrics")]
    [InlineData("/webhook")]
    public async Task InvokeAsync_ExemptPaths_ShouldBypassApiKeyValidation(string path)
    {
        var nextCalled = false;
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:InternalApiKey"] = "secret"
            })
            .Build();

        var sut = new ApiKeyValidationMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            configuration,
            Substitute.For<ILogger<ApiKeyValidationMiddleware>>());

        await sut.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_NonExemptPath_WithoutApiKey_ShouldReturnUnauthorized()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/internal";
        context.Response.Body = new MemoryStream();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:InternalApiKey"] = "secret"
            })
            .Build();

        var sut = new ApiKeyValidationMiddleware(
            _ => Task.CompletedTask,
            configuration,
            Substitute.For<ILogger<ApiKeyValidationMiddleware>>());

        await sut.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }
}
