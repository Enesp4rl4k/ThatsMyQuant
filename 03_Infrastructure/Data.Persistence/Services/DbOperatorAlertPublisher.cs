using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Cross-process publisher. Writes the alert as a row in <c>OperatorAlertEvents</c>;
/// Listener.Api's reader picks it up within a few seconds and broadcasts it onto the
/// SignalR <c>OperatorAlert</c> channel.
///
/// Use this in QuantEngine.Worker (RiskSupervisor / FundingScanner / Promotion etc.)
/// where the SignalR hub is not directly reachable.
/// </summary>
public sealed class DbOperatorAlertPublisher : IOperatorAlertPublisher
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DbOperatorAlertPublisher> _logger;
    private readonly string _processName;

    public DbOperatorAlertPublisher(
        IServiceScopeFactory scopes,
        ILogger<DbOperatorAlertPublisher> logger)
    {
        _scopes = scopes;
        _logger = logger;
        _processName = Truncate(Environment.GetEnvironmentVariable("TMQ_PROCESS_NAME")
            ?? System.Diagnostics.Process.GetCurrentProcess().ProcessName, 40);
    }

    public async Task PublishAsync(string severity, string source, string title, string body, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
            db.OperatorAlertEvents.Add(new OperatorAlertEvent
            {
                UtcTime = DateTime.UtcNow,
                Severity = Truncate(severity ?? "info", 16),
                Source = Truncate(source ?? "Unknown", 40),
                Title = Truncate(title ?? "", 200),
                Body = body ?? "",
                ProcessName = _processName,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DbOperatorAlertPublisher: failed to persist alert {Source}/{Title}.", source, title);
        }
    }

    private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max);
}
