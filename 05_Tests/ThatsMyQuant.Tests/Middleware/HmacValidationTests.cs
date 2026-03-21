using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Listener.Api.Middleware;
using ThatsMyQuant.Infrastructure.Persistence;
using Xunit;

namespace ThatsMyQuant.Tests.Middleware;

public class HmacValidationTests
{
    private const string TestSecret = "test-secret-key-123";

    private static IHost CreateTestHost()
    {
        return new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureAppConfiguration((context, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "WebhookSecret", TestSecret }
                    });
                });
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddMemoryCache();

                    var connection = new SqliteConnection("Data Source=:memory:");
                    connection.Open();
                    services.AddSingleton(connection);
                    services.AddDbContext<QuantDbContext>(options => options.UseSqlite(connection));
                });
                webBuilder.Configure(app =>
                {
                    using var scope = app.ApplicationServices.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
                    db.Database.EnsureCreated();

                    app.UseMiddleware<HmacValidationMiddleware>();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/webhook", async context =>
                        {
                            context.Response.StatusCode = StatusCodes.Status202Accepted;
                            await context.Response.WriteAsync("OK");
                        });
                    });
                });
            })
            .Build();
    }

    private static string ComputeHmac(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    [Fact]
    public async Task Webhook_WithValidSignature_ShouldReturn202()
    {
        // Arrange
        using var host = CreateTestHost();
        await host.StartAsync();
        var client = host.GetTestClient();

        var body = "{\"Symbol\":\"THYAO\",\"Side\":\"Buy\"}";
        var signature = ComputeHmac(body, TestSecret);

        var request = new HttpRequestMessage(HttpMethod.Post, "/webhook");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("X-Signature-256", signature);
        request.Headers.Add("X-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        request.Headers.Add("X-Nonce", Guid.NewGuid().ToString("N"));
        request.Headers.Add("X-Idempotency-Key", Guid.NewGuid().ToString("N"));

        // Act
        var response = await client.SendAsync(request);

        // Assert
        ((int)response.StatusCode).Should().Be(202);
    }

    [Fact]
    public async Task Webhook_WithInvalidSignature_ShouldReturn401()
    {
        // Arrange
        using var host = CreateTestHost();
        await host.StartAsync();
        var client = host.GetTestClient();

        var body = "{\"Symbol\":\"THYAO\",\"Side\":\"Buy\"}";

        var request = new HttpRequestMessage(HttpMethod.Post, "/webhook");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("X-Signature-256", "sha256=invalid-garbage-signature");
        request.Headers.Add("X-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        request.Headers.Add("X-Nonce", Guid.NewGuid().ToString("N"));
        request.Headers.Add("X-Idempotency-Key", Guid.NewGuid().ToString("N"));

        // Act
        var response = await client.SendAsync(request);

        // Assert
        ((int)response.StatusCode).Should().Be(401);
    }

    [Fact]
    public async Task Webhook_WithMissingSignatureHeader_ShouldReturn401()
    {
        // Arrange
        using var host = CreateTestHost();
        await host.StartAsync();
        var client = host.GetTestClient();

        var body = "{\"Symbol\":\"THYAO\",\"Side\":\"Buy\"}";

        var request = new HttpRequestMessage(HttpMethod.Post, "/webhook");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("X-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        request.Headers.Add("X-Nonce", Guid.NewGuid().ToString("N"));
        request.Headers.Add("X-Idempotency-Key", Guid.NewGuid().ToString("N"));

        // Act
        var response = await client.SendAsync(request);

        // Assert
        ((int)response.StatusCode).Should().Be(401);
    }
}
