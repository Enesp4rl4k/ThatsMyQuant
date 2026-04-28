using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Services;

public static class ObservabilityEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapObservabilityEndpoints(this IEndpointRouteBuilder endpoints, IServiceProvider rootProvider)
    {
        endpoints.MapGet("/health", (ServiceRuntimeState runtimeState) =>
        {
            var snapshot = runtimeState.GetSnapshot();
            return Results.Json(new
            {
                status = "healthy",
                service = snapshot.ServiceName,
                startedUtc = snapshot.StartedUtc,
                ready = snapshot.IsReady,
                lastUpdatedUtc = snapshot.LastUpdatedUtc
            });
        });

        endpoints.MapGet("/ready", async (ServiceRuntimeState runtimeState, CancellationToken cancellationToken) =>
        {
            var snapshot = runtimeState.GetSnapshot();
            var databaseReady = await CanConnectDatabaseAsync(rootProvider, cancellationToken);
            var isReady = snapshot.IsReady && databaseReady;

            return Results.Json(new
            {
                status = isReady ? "ready" : "not-ready",
                service = snapshot.ServiceName,
                ready = isReady,
                checks = new
                {
                    runtime = snapshot.IsReady,
                    database = databaseReady
                },
                reasons = snapshot.Reasons,
                readySinceUtc = snapshot.ReadySinceUtc,
                lastUpdatedUtc = snapshot.LastUpdatedUtc
            }, statusCode: isReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });

        endpoints.MapGet("/metrics", (ServiceRuntimeState runtimeState, OperationalTelemetry telemetry) =>
        {
            var circuitBreaker = rootProvider.GetService<CircuitBreakerService>()?.GetStatus();
            var portfolio = rootProvider.GetService<IPortfolioManager>();
            var payload = PrometheusMetricsFormatter.Format(runtimeState, telemetry, circuitBreaker, portfolio);
            return Results.Text(payload, "text/plain; version=0.0.4; charset=utf-8");
        });

        endpoints.MapGet("/metrics.json", (OperationalTelemetry telemetry) =>
            Results.Json(telemetry.GetSnapshot()));

        endpoints.MapGet("/exchange/health", () =>
        {
            var health = rootProvider.GetService<IExchangeHealthProvider>();
            if (health is null)
            {
                return Results.Json(new
                {
                    venue = "unknown",
                    credentialsConfigured = false,
                    isConnected = false,
                    lastError = "IExchangeHealthProvider not registered."
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var snap = health.Current;
            var status = snap.IsConnected
                ? StatusCodes.Status200OK
                : snap.CredentialsConfigured
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status424FailedDependency;
            return Results.Json(snap, statusCode: status);
        });

        endpoints.MapGet("/ops", (ServiceRuntimeState runtimeState, OperationalTelemetry telemetry) =>
        {
            var circuitBreaker = rootProvider.GetService<CircuitBreakerService>()?.GetStatus();
            return Results.Json(new
            {
                runtime = runtimeState.GetSnapshot(),
                telemetry = telemetry.GetSnapshot(),
                circuitBreaker
            });
        });

        return endpoints;
    }

    private static async Task<bool> CanConnectDatabaseAsync(IServiceProvider rootProvider, CancellationToken cancellationToken)
    {
        using var scope = rootProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetService<QuantDbContext>();
        if (dbContext is null)
        {
            return true;
        }

        try
        {
            return await dbContext.Database.CanConnectAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
    }
}
