using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Lightweight HTTP health check endpoint for worker services that don't have ASP.NET hosting.
/// Listens on a configurable port and responds to GET /health with 200 OK.
/// </summary>
public sealed class HealthCheckHostedService : BackgroundService
{
    private readonly ILogger<HealthCheckHostedService> _logger;
    private readonly int _port;
    private HttpListener? _listener;

    public HealthCheckHostedService(ILogger<HealthCheckHostedService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _port = int.TryParse(configuration["HealthCheck:Port"], out var p) ? p : 8080;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{_port}/");

        try
        {
            _listener.Start();
            _logger.LogInformation("Health check endpoint listening on port {Port}", _port);
        }
        catch (HttpListenerException ex)
        {
            _logger.LogWarning(ex, "Health check listener failed to bind on port {Port} — health endpoint unavailable", _port);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(stoppingToken);

                if (context.Request.Url?.AbsolutePath == "/health")
                {
                    var response = Encoding.UTF8.GetBytes("Healthy");
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/plain";
                    context.Response.ContentLength64 = response.Length;
                    await context.Response.OutputStream.WriteAsync(response, stoppingToken);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }

                context.Response.Close();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check request error");
            }
        }
    }

    public override void Dispose()
    {
        _listener?.Close();
        base.Dispose();
    }
}
