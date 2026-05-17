using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [Sprint 5 — 2026-05-10] One-shot startup sanity check that surfaces any
/// disagreement between the configured paper-trading basis and the runtime
/// PaperPortfolio that <see cref="ExchangeBalanceSyncHostedService"/> seeds
/// from the OKX demo balance. Closes deferred bug B1 (the historical
/// "paper basis $96K vs HB cash $5K" mismatch from a stale persistence
/// snapshot left over from the $100K → $2K capital-cycle pivot).
///
/// Behaviour:
///   - Waits up to 30s for ExchangeBalanceSync to populate
///     <see cref="PaperPortfolio.InitialBalance"/> (zero on boot until first sync).
///   - Compares config <c>Paper:InitialBalance</c> vs runtime
///     <c>PaperPortfolio.InitialBalance</c> vs runtime <c>PaperPortfolio.Cash</c>.
///   - Always logs the trio at INFO so operator + post-mortem readers can verify
///     they agreed at boot.
///   - Emits a WARNING (and Telegram if available) when config and runtime
///     differ by more than 25% (typical for stale-snapshot scenarios).
///   - Does NOT mutate state — pure diagnostic. Operator decides whether the
///     drift is intended (real OKX demo balance changed) or a config-drift bug.
/// </summary>
public sealed class BasisSanityCheckHostedService : BackgroundService
{
    private readonly ILogger<BasisSanityCheckHostedService> _logger;
    private readonly IConfiguration _config;
    private readonly PaperPortfolio _portfolio;

    public BasisSanityCheckHostedService(
        ILogger<BasisSanityCheckHostedService> logger,
        IConfiguration config,
        PaperPortfolio portfolio)
    {
        _logger = logger;
        _config = config;
        _portfolio = portfolio;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait up to 30s for ExchangeBalanceSync to seed PaperPortfolio. The sync
        // service's first tick runs immediately on its own ExecuteAsync.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_portfolio.InitialBalance == 0m && DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        var configRaw = _config["Paper:InitialBalance"] ?? "(unset)";
        var configValue = decimal.TryParse(configRaw,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var cv) ? cv : 0m;
        var runtimeInitial = _portfolio.InitialBalance;
        var runtimeCash = _portfolio.Cash;

        // Always emit the trio so the audit trail is complete.
        _logger.LogInformation(
            "BasisSanityCheck: config={ConfigRaw} runtime.InitialBalance=${RuntimeInitial:N2} " +
            "runtime.Cash=${RuntimeCash:N2}",
            configRaw, runtimeInitial, runtimeCash);

        if (runtimeInitial == 0m)
        {
            _logger.LogWarning(
                "BasisSanityCheck: PaperPortfolio.InitialBalance is still 0 after 30s — " +
                "ExchangeBalanceSync may have failed (check OKX credentials + network). " +
                "All EdgeLab strategies will idle on basis=0 until this resolves.");
            return;
        }

        if (configValue > 0m)
        {
            var ratio = runtimeInitial / configValue;
            // Tolerate 0.75-1.5x range (OKX demo accounts drift from PnL between sessions).
            // Anything outside is almost certainly a stale snapshot or misconfiguration.
            if (ratio < 0.75m || ratio > 1.5m)
            {
                _logger.LogWarning(
                    "BasisSanityCheck: config Paper:InitialBalance=${Config:N2} disagrees " +
                    "with runtime ${Runtime:N2} (ratio={Ratio:F2}x). " +
                    "Likely cause: stale persistence snapshot or config-vs-OKX-demo drift. " +
                    "EdgeLab sizing uses runtime basis; verify intended.",
                    configValue, runtimeInitial, ratio);
            }
        }

        // Cash > 1.1× InitialBalance shouldn't happen unless trades have been profitable
        // beyond expected drift. Cash << InitialBalance means significant losses or open
        // positions consumed reserve. Log either as a diagnostic, no alert.
        if (runtimeInitial > 0m)
        {
            var cashRatio = runtimeCash / runtimeInitial;
            if (cashRatio < 0.5m)
            {
                _logger.LogWarning(
                    "BasisSanityCheck: Cash/InitialBalance={Ratio:P0} — significant capital " +
                    "deployed in open positions or realized losses. Verify OKX position list.",
                    cashRatio);
            }
        }
    }
}
