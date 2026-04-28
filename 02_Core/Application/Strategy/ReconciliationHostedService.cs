using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Periodic ledger-drift guard: every N seconds compares the in-memory
/// <see cref="IPortfolioManager"/> balance against the exchange's REST account
/// balance. On mismatch beyond tolerance it arms the <see cref="RiskAgent"/>
/// soft pause and (best-effort) alerts the operator so trading halts until
/// manual intervention.
///
/// Multi-symbol position-level reconciliation (open orders, individual fills)
/// is a Phase 4 item; for now this is a balance-level tripwire. The hosted
/// service is configurable via <c>Reconciliation:IntervalSeconds</c>
/// (default 300 = 5 min) and <c>Reconciliation:Enabled</c> (default false —
/// requires OKX keys and real account state).
/// </summary>
public sealed class ReconciliationHostedService : BackgroundService
{
    private readonly ILogger<ReconciliationHostedService> _logger;
    private readonly IServiceProvider _services;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;
    private readonly decimal _tolerancePercent;

    private readonly bool _isPaperMode;

    public ReconciliationHostedService(
        ILogger<ReconciliationHostedService> logger,
        IServiceProvider services,
        IConfiguration configuration)
    {
        _logger = logger;
        _services = services;

        _isPaperMode = string.Equals(
            (configuration["TMQ_MODE"] ?? "").Trim(),
            "paper",
            StringComparison.OrdinalIgnoreCase);

        _enabled = bool.TryParse(configuration["Reconciliation:Enabled"], out var e) && e;

        var seconds = 300;
        if (int.TryParse(configuration["Reconciliation:IntervalSeconds"], out var s) && s > 10)
            seconds = s;
        _interval = TimeSpan.FromSeconds(seconds);

        var tol = 0.5m;
        if (decimal.TryParse(configuration["Reconciliation:TolerancePercent"],
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var t) && t > 0m)
            tol = t;
        _tolerancePercent = tol;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_isPaperMode)
        {
            _logger.LogInformation(
                "ReconciliationHostedService skipped: TMQ_MODE=paper. " +
                "Paper portfolio is simulated locally and intentionally independent from the live/demo exchange balance.");
            return;
        }

        if (!_enabled)
        {
            _logger.LogInformation("ReconciliationHostedService disabled (Reconciliation:Enabled=false).");
            return;
        }

        _logger.LogInformation(
            "ReconciliationHostedService started: interval={Interval}s tolerance={Tol:F2}%.",
            _interval.TotalSeconds, _tolerancePercent);

        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformReconciliationAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconciliation tick failed — will retry on next interval.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PerformReconciliationAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var exchange = scope.ServiceProvider.GetService<IExchangeService>();
        var portfolio = scope.ServiceProvider.GetService<IPortfolioManager>();
        var risk = scope.ServiceProvider.GetService<RiskAgent>();

        if (exchange is null || portfolio is null)
        {
            _logger.LogDebug("Reconciliation skipped: missing IExchangeService or IPortfolioManager.");
            return;
        }

        decimal localEquity = portfolio.GetAvailableBalance("USDT");

        var account = await exchange.GetAccountStatusAsync().WaitAsync(ct).ConfigureAwait(false);
        if (account is null || account.Balance <= 0m)
        {
            _logger.LogWarning("Reconciliation: exchange returned null/zero balance — treating as transient.");
            return;
        }

        var remoteEquity = account.Balance;
        var drift = Math.Abs(localEquity - remoteEquity);
        var baseline = Math.Max(1m, Math.Max(localEquity, remoteEquity));
        var driftPercent = drift / baseline * 100m;

        if (driftPercent > _tolerancePercent)
        {
            var reason =
                $"Ledger drift {driftPercent:F2}% (local={localEquity:N2}, remote={remoteEquity:N2}) " +
                $"exceeds tolerance {_tolerancePercent:F2}%.";
            _logger.LogCritical("[RECONCILIATION HALT] {Reason}", reason);
            risk?.ArmSoftPause(reason);
        }
        else
        {
            _logger.LogInformation(
                "Reconciliation OK: drift {Drift:F3}% (local={Local:N2} remote={Remote:N2}).",
                driftPercent, localEquity, remoteEquity);
        }

        // Position-level drift check — only if the exchange exposes IExchangePositionReader.
        if (exchange is IExchangePositionReader positionReader)
        {
            await ReconcilePositionsAsync(positionReader, portfolio, risk, ct).ConfigureAwait(false);
        }
    }

    private async Task ReconcilePositionsAsync(
        IExchangePositionReader positionReader,
        IPortfolioManager portfolio,
        RiskAgent? risk,
        CancellationToken ct)
    {
        IReadOnlyList<ExchangePositionSnapshot> remote;
        try
        {
            remote = await positionReader.GetOpenPositionsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconciliation: position fetch failed — skipping position check.");
            return;
        }

        var local = portfolio.GetAllPositions();
        var drifts = new List<string>();

        // 1. Local has but remote doesn't (or quantity mismatch).
        foreach (var (symbol, pos) in local)
        {
            var match = remote.FirstOrDefault(r =>
                string.Equals(NormalizeSymbol(r.Symbol), NormalizeSymbol(symbol), StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                drifts.Add($"{symbol}: local open (qty={pos.Quantity:F8} long={pos.IsLong}) but not on exchange");
                continue;
            }

            var qtyDiff = Math.Abs(pos.Quantity - match.Quantity);
            var qtyBase = Math.Max(0.00000001m, Math.Max(pos.Quantity, match.Quantity));
            var qtyDriftPct = qtyDiff / qtyBase * 100m;
            if (qtyDriftPct > _tolerancePercent)
            {
                drifts.Add(
                    $"{symbol}: qty drift {qtyDriftPct:F2}% (local={pos.Quantity:F8} remote={match.Quantity:F8})");
            }
            if (pos.IsLong != match.IsLong)
            {
                drifts.Add($"{symbol}: side mismatch (local={(pos.IsLong ? "LONG" : "SHORT")} remote={(match.IsLong ? "LONG" : "SHORT")})");
            }
        }

        // 2. Remote has but local doesn't (ghost position).
        foreach (var r in remote)
        {
            var normalized = NormalizeSymbol(r.Symbol);
            var matched = local.Keys.Any(k => string.Equals(NormalizeSymbol(k), normalized, StringComparison.OrdinalIgnoreCase));
            if (!matched)
            {
                drifts.Add($"{r.Symbol}: exchange open (qty={r.Quantity:F8} long={r.IsLong}) but no local record");
            }
        }

        if (drifts.Count > 0)
        {
            var reason = $"Position reconciliation drift ({drifts.Count} issue(s)): " + string.Join(" | ", drifts);
            _logger.LogCritical("[RECONCILIATION HALT] {Reason}", reason);
            risk?.ArmSoftPause(reason);
        }
        else
        {
            _logger.LogInformation(
                "Position reconciliation OK: {LocalCount} local / {RemoteCount} remote positions matched.",
                local.Count, remote.Count);
        }
    }

    private static string NormalizeSymbol(string s) =>
        (s ?? string.Empty).Replace("-", string.Empty).Replace("_", string.Empty).ToUpperInvariant();
}
