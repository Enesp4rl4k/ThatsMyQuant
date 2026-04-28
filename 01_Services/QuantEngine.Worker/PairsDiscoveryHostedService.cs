using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Standalone discovery loop — every <c>Pairs:Discovery:IntervalHours</c> (default 6h),
/// pulls aligned 4h close-price series for the configured candidate universe and runs
/// <see cref="PairsDiscoveryService"/> to surface fresh cointegrating pair candidates.
/// Top-N results are persisted to a human-reviewable JSON file (LocalAppData/ThatsMyQuant)
/// and the top 5 are logged. The operator promotes interesting candidates manually into
/// <c>Strategy:PairsTrading:AllowedPairsCsv</c> — service NEVER auto-promotes.
///
/// Disabled when <c>Pairs:Discovery:Enabled</c> is false (default true). Enables
/// portfolio breadth growth without overfitting risk.
/// </summary>
public sealed class PairsDiscoveryHostedService : BackgroundService
{
    private readonly ILogger<PairsDiscoveryHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _initialDelay;
    private readonly string _timeframe;
    private readonly int _candleLookback;
    private readonly int _topN;
    private readonly string[] _universe;
    private readonly string _outputPath;

    public PairsDiscoveryHostedService(
        ILogger<PairsDiscoveryHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _enabled = !bool.TryParse(configuration["Pairs:Discovery:Enabled"], out var en) || en;
        var hours = int.TryParse(configuration["Pairs:Discovery:IntervalHours"], out var h) && h > 0 ? h : 6;
        _interval = TimeSpan.FromHours(hours);
        _initialDelay = TimeSpan.FromMinutes(int.TryParse(configuration["Pairs:Discovery:InitialDelayMinutes"], out var d) && d > 0 ? d : 5);
        _timeframe = configuration["Pairs:Discovery:Timeframe"] ?? "4h";
        _candleLookback = int.TryParse(configuration["Pairs:Discovery:CandleLookback"], out var cl) && cl > 0 ? cl : 250;
        _topN = int.TryParse(configuration["Pairs:Discovery:TopN"], out var tn) && tn > 0 ? tn : 15;

        var universeCsv = configuration["Pairs:Discovery:UniverseCsv"]
            ?? "BTCUSDT,ETHUSDT,SOLUSDT,AVAXUSDT,LINKUSDT,LTCUSDT,DOGEUSDT,XRPUSDT,ADAUSDT,AAVEUSDT,ATOMUSDT,DOTUSDT,NEARUSDT,APTUSDT,SUIUSDT,ARBUSDT,OPUSDT,INJUSDT,FILUSDT,TRXUSDT,XLMUSDT,BCHUSDT,UNIUSDT,ETCUSDT,MATICUSDT,ALGOUSDT,EGLDUSDT,FTMUSDT,SANDUSDT,GALAUSDT";
        _universe = universeCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThatsMyQuant");
        Directory.CreateDirectory(stateDir);
        _outputPath = Path.Combine(stateDir, "pairs_discovery_candidates.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("PairsDiscoveryHostedService disabled (Pairs:Discovery:Enabled=false).");
            return;
        }

        _logger.LogWarning(
            "PairsDiscoveryHostedService starting. Interval={Interval}h Timeframe={TF} Lookback={LB} Universe={N} symbols TopN={TopN} InitialDelay={Delay}m",
            _interval.TotalHours, _timeframe, _candleLookback, _universe.Length, _topN, _initialDelay.TotalMinutes);

        try { await Task.Delay(_initialDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try
                {
                    await RunDiscoveryAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PairsDiscovery cycle failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunDiscoveryAsync(CancellationToken ct)
    {
        var startUtc = DateTime.UtcNow;
        using var scope = _serviceProvider.CreateScope();
        var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataService>();
        var discovery = scope.ServiceProvider.GetRequiredService<PairsDiscoveryService>();

        // Fetch close-price series for each symbol; tolerate per-symbol failures.
        var series = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sym in _universe)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var candles = await marketData.GetCandlesAsync(sym, _candleLookback, "Crypto", _timeframe).ConfigureAwait(false);
                if (candles == null || candles.Count < _candleLookback / 2)
                {
                    _logger.LogDebug("PairsDiscovery: skipping {Sym}, only {N} candles", sym, candles?.Count ?? 0);
                    continue;
                }
                series[sym] = candles.Select(c => c.Close).ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PairsDiscovery: candle fetch failed for {Sym}", sym);
            }
        }

        if (series.Count < 4)
        {
            _logger.LogWarning("PairsDiscovery: insufficient symbols with candles ({N})", series.Count);
            return;
        }

        // Align all series to the SHORTEST common length (truncate-from-front so latest bars align).
        var minLen = series.Values.Min(s => s.Count);
        var aligned = series.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<decimal>)(kv.Value.Count == minLen
                ? kv.Value
                : kv.Value.Skip(kv.Value.Count - minLen).ToArray()),
            StringComparer.OrdinalIgnoreCase);

        var results = discovery.Discover(aligned, _topN);

        var elapsed = DateTime.UtcNow - startUtc;
        _logger.LogWarning(
            "PairsDiscovery cycle complete in {Sec:F1}s — {N} candidates passed admission gate (universe={U}, lookback={LB} bars)",
            elapsed.TotalSeconds, results.Count, series.Count, minLen);

        if (results.Count == 0) return;

        // Log top 5 for visibility.
        foreach (var c in results.Take(5))
        {
            _logger.LogWarning(
                "  CANDIDATE {Y}/{X} score={Score:F1} | β={Beta:F3} ADF={ADF:F2} HL={HL:F1} corr={Corr:F2} Hurst={Hurst:F2}",
                c.SymbolY, c.SymbolX, c.QualityScore,
                c.Diagnostics.HedgeRatio, c.Diagnostics.AdfTStatistic, c.Diagnostics.HalfLife,
                c.Diagnostics.Correlation, c.Diagnostics.HurstExponent);
        }

        // Persist for human review (atomic tmp + Move).
        try
        {
            var dto = new
            {
                generatedUtc = DateTime.UtcNow,
                timeframe = _timeframe,
                lookbackBars = minLen,
                universeCount = series.Count,
                candidates = results.Select(r => new
                {
                    pair = $"{r.SymbolY}:{r.SymbolX}",
                    qualityScore = Math.Round(r.QualityScore, 1),
                    beta = Math.Round((double)r.Diagnostics.HedgeRatio, 4),
                    adfTStat = Math.Round(r.Diagnostics.AdfTStatistic, 3),
                    halfLifeBars = Math.Round(r.Diagnostics.HalfLife, 1),
                    correlation = Math.Round(r.Diagnostics.Correlation, 3),
                    hurstExponent = Math.Round(r.Diagnostics.HurstExponent, 3)
                }).ToArray()
            };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            var tmp = _outputPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _outputPath, overwrite: true);
            _logger.LogInformation("PairsDiscovery: candidates written to {Path}", _outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PairsDiscovery: failed to persist candidates JSON.");
        }
    }
}
