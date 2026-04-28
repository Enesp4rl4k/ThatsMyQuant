using MediatR;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Notifications;

namespace Listener.Api.Services;

/// <summary>
/// Detects operator-relevant state changes by polling the trade repository and
/// publishes <see cref="OperatorAlertNotification"/> via MediatR. The
/// RealtimeBroadcaster forwards them to the SignalR `OperatorAlert` channel
/// so the dashboard NotificationsBell shows them in near real time.
///
/// Producers covered here:
///   • Drawdown threshold crossings (50% of MaxDrawdown, 80%, etc.)
///   • Big-loss / big-win individual trade closures
///   • New peak equity reached
///
/// Cross-process producers (RiskSupervisor / Promotion / FundingScanner that
/// live in QuantEngine.Worker) publish their own MediatR notifications inside
/// that process — Listener.Api can also receive them once they share the bus,
/// but the bridge worker stays self-sufficient on the dashboard's own data.
/// </summary>
public sealed class OperatorAlertBridgeWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OperatorAlertBridgeWorker> _logger;
    private readonly IConfiguration _config;

    private decimal _peakEquity;
    private decimal _lastEquitySnapshot;
    private DateTime _lastTradeTimestamp = DateTime.MinValue;
    private readonly HashSet<Guid> _alertedTradeIds = new();
    private double _lastDrawdownTier;

    public OperatorAlertBridgeWorker(
        IServiceScopeFactory scopes,
        IConfiguration config,
        ILogger<OperatorAlertBridgeWorker> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "OperatorAlertBridgeWorker tick failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var trades = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var open = await trades.GetOpenAsync(500);
        var recent = await trades.GetAllAsync(500);
        var closed = recent.Where(t => t.IsClosed).ToList();

        var realized = closed.Sum(t => t.RealisedPnL);
        var unrealized = open.Sum(ComputeUnrealized);
        var equity = realized + unrealized;

        if (_peakEquity == 0m && equity != 0m)
        {
            _peakEquity = equity;
            _lastEquitySnapshot = equity;
            return;
        }

        // New peak — quietly update.
        if (equity > _peakEquity)
        {
            // Only chirp when it's a meaningful jump (>0.5%) — no spam.
            var prevPeak = _peakEquity;
            _peakEquity = equity;
            if (prevPeak > 0m && (equity - prevPeak) / Math.Abs(prevPeak) > 0.005m)
            {
                await mediator.Publish(OperatorAlertNotification.Success(
                    "Portfolio",
                    "📈 New equity peak",
                    $"Equity ${equity:N2} (+${(equity - prevPeak):N2} vs prior peak)"),
                    ct);
            }
        }

        // Drawdown tier crossings
        if (_peakEquity > 0m)
        {
            var ddPct = (double)((_peakEquity - equity) / _peakEquity) * 100.0;
            var maxDd = double.TryParse(_config["Risk:Supervisor:MaxDrawdownPercent"], out var m) ? m : 5.0;
            var ratio = ddPct / Math.Max(0.01, maxDd);
            // Tier transitions: 0.5, 0.8, 1.0
            var tier = ratio >= 1.0 ? 1.0 : ratio >= 0.8 ? 0.8 : ratio >= 0.5 ? 0.5 : 0.0;
            if (tier > _lastDrawdownTier)
            {
                var (sev, prefix) = tier switch
                {
                    >= 1.0 => ("error", "🛑 Drawdown limit hit"),
                    >= 0.8 => ("error", "⚠️ Drawdown 80% of limit"),
                    _ => ("warning", "⚠️ Drawdown 50% of limit"),
                };
                await mediator.Publish(new OperatorAlertNotification(
                    Severity: sev,
                    Source: "RiskSupervisor",
                    Title: prefix,
                    Body: $"Drawdown {ddPct:F2}% / Max {maxDd:F2}% · Equity ${equity:N0} (peak ${_peakEquity:N0})",
                    UtcTime: DateTime.UtcNow), ct);
                _lastDrawdownTier = tier;
            }
            else if (tier < _lastDrawdownTier && ratio < 0.4)
            {
                // Reset hysteresis when we recover well below 50%
                _lastDrawdownTier = 0;
            }
        }

        // Big-trade closure alerts (anything outside ±2% of basis)
        var basis = Math.Max(1m, _peakEquity);
        foreach (var t in closed.OrderByDescending(t => t.ExitTime).Take(20))
        {
            if (!t.ExitTime.HasValue) continue;
            if (_alertedTradeIds.Contains(t.Id)) continue;
            if (t.ExitTime.Value <= _lastTradeTimestamp) continue;

            var pct = Math.Abs(t.RealisedPnL) / basis;
            if (pct >= 0.02m)
            {
                var sign = t.RealisedPnL >= 0 ? "+" : "";
                await mediator.Publish(new OperatorAlertNotification(
                    Severity: t.RealisedPnL >= 0 ? "success" : "warning",
                    Source: "Trade",
                    Title: $"{(t.RealisedPnL >= 0 ? "🟢" : "🔴")} {t.Symbol} large {(t.RealisedPnL >= 0 ? "win" : "loss")}",
                    Body: $"{sign}${t.RealisedPnL:N2} · {t.StrategyTag} · {t.ExitReason ?? "n/a"}",
                    UtcTime: DateTime.UtcNow), ct);
            }
            _alertedTradeIds.Add(t.Id);
        }

        // Trim alerted set so it doesn't grow unboundedly.
        if (_alertedTradeIds.Count > 500)
        {
            _alertedTradeIds.Clear();
            foreach (var t in closed.Take(50)) _alertedTradeIds.Add(t.Id);
        }

        if (closed.Count > 0)
        {
            var newest = closed.Max(t => t.ExitTime ?? DateTime.MinValue);
            if (newest > _lastTradeTimestamp) _lastTradeTimestamp = newest;
        }

        _lastEquitySnapshot = equity;
    }

    private static decimal ComputeUnrealized(ThatsMyQuant.Contracts.Models.Trade t)
    {
        var mark = t.MarkPrice > 0m ? t.MarkPrice : t.EntryPrice;
        var qty = t.RemainingQuantity > 0m ? t.RemainingQuantity : t.Quantity;
        if (qty <= 0m) return 0m;
        var direction = t.Side == TradeSide.Sell ? -1m : 1m;
        return (mark - t.EntryPrice) * qty * direction;
    }
}
