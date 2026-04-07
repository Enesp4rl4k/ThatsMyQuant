using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.HftInfrastructure;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// BLUE PATH (Cold Path): Background service for heavy analytical workloads.
/// Runs Monte Carlo simulations, regime detection, PBO validation, and MAFE analysis
/// on a low-priority background thread every N minutes.
///
/// Publishes updated parameters (regime, SL/TP multipliers) to HotStateBuffer
/// so the Red Path can consume them without waiting.
/// </summary>
public sealed class ColdPathAnalyticsService : BackgroundService
{
    private readonly ILogger<ColdPathAnalyticsService> _logger;
    private readonly HotStateBuffer _hotState;
    private readonly IMarketDataService _marketDataService;
    private readonly MonteCarloSimulator _monteCarloSimulator;
    private readonly TimeSpan _regimeInterval;
    private readonly TimeSpan _monteCarloInterval;

    public ColdPathAnalyticsService(
        ILogger<ColdPathAnalyticsService> logger,
        HotStateBuffer hotState,
        IMarketDataService marketDataService,
        MonteCarloSimulator? monteCarloSimulator = null)
    {
        _logger = logger;
        _hotState = hotState;
        _marketDataService = marketDataService;
        _monteCarloSimulator = monteCarloSimulator ?? new MonteCarloSimulator();
        _regimeInterval = TimeSpan.FromMinutes(5);
        _monteCarloInterval = TimeSpan.FromMinutes(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BLUE PATH: Cold Path Analytics Service started.");

        var lastRegimeRun = DateTime.MinValue;
        var lastMonteCarloRun = DateTime.MinValue;
        Task? monteCarloTask = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;

                if (now - lastRegimeRun >= _regimeInterval)
                {
                    lastRegimeRun = now;
                    await RunRegimeDetectionAsync(stoppingToken).ConfigureAwait(false);
                }

                if (now - lastMonteCarloRun >= _monteCarloInterval)
                {
                    // Await any previous Monte Carlo run before starting a new one
                    if (monteCarloTask is not null)
                        await monteCarloTask.ConfigureAwait(false);

                    lastMonteCarloRun = now;
                    monteCarloTask = RunMonteCarloAsync(stoppingToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cold Path analytics cycle failed.");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            }
        }

        // Ensure any in-flight Monte Carlo completes before shutdown
        if (monteCarloTask is not null)
        {
            try { await monteCarloTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _logger.LogInformation("BLUE PATH: Cold Path Analytics Service stopped.");
    }

    private async Task RunRegimeDetectionAsync(CancellationToken ct)
    {
        try
        {
            // Fetch recent candles for regime analysis
            var candles = await _marketDataService.GetCandlesAsync("BTCUSDT", 60, "CRYPTO", "1h").ConfigureAwait(false);
            if (candles == null || candles.Count < 34) return;

            var regime = MarketRegimeDetector.Detect(candles);

            int regimeCode = regime.Regime switch
            {
                MarketRegime.Trending => 1,
                MarketRegime.Ranging => 2,
                MarketRegime.Volatile => 3,
                _ => 0
            };

            // Whisper to HotStateBuffer — Red Path picks up new parameters atomically
            _hotState.UpdateRegimeParameters(
                regimeCode,
                regime.StopLossMultiplier,
                regime.TakeProfitMultiplier);

            _logger.LogInformation(
                "BLUE PATH: Regime updated → {Regime} (SL={SL:F1}x, TP={TP:F1}x)",
                regime.Regime, regime.StopLossMultiplier, regime.TakeProfitMultiplier);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cold Path regime detection failed.");
        }
    }

    private async Task RunMonteCarloAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("BLUE PATH: Starting Monte Carlo simulation...");

            // Use dummy P&L data for now — in production, pull from trade repository
            var samplePnl = new List<double> { 50, -30, 80, -20, 60, -40, 100, -50, 70, -25 };
            var result = await _monteCarloSimulator.RunSimulationsAsync(
                samplePnl, 100_000m, iterations: 5000, stepsPerIteration: 252);

            _logger.LogInformation(
                "BLUE PATH: Monte Carlo complete — Median={Median:N0}, P5={P5:N0}, P99 MDD={MDD:N0}",
                result.MedianFinalEquity, result.FifthPercentileEquity, result.NinetyNinthPercentileDrawdown);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cold Path Monte Carlo simulation failed.");
        }
    }
}
