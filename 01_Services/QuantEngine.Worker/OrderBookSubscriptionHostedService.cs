using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Bot;

/// <summary>
/// 2026-05-11 — Maker-fill-ratio root-cause fix.
///
/// Problem: OkxWebSocketService is registered as IStreamingMarketDataService in
/// the worker, but no caller ever invokes <c>ConnectAsync(symbols[])</c> on it,
/// so <c>_activeSymbols</c> stays empty, <c>books5</c> is never subscribed, and
/// <c>OkxExecutionService._latestOrderBooks</c> remains empty. Every post-only
/// limit submission then falls back to <c>signal.Price</c> (mid), which either
/// trips OKX sCode 51015 "would_match" or sits between best bid/ask and times
/// out to the Market taker fallback — explains the 336/336 = 0% maker fill
/// ratio observed in <c>maker_fill_ratio.json</c> across all funding/pairs
/// fills. MarketDataBroadcastService does this wiring in Listener.Api, but
/// the worker has no equivalent.
///
/// This service:
///   1. Reads EdgeLabSingleAsset:Strategies + InputDir from config.
///   2. For each strategy, parses <c>&lt;InputDir&gt;/&lt;strategy&gt;/latest.json</c>
///      and extracts the symbols from <c>target_weights</c> keys.
///   3. Deduplicates and calls <c>OkxWebSocketService.ConnectAsync(symbols, ct)</c>
///      so books5 + trades arrive and populate
///      <c>OkxExecutionService._latestOrderBooks</c>.
///   4. Re-runs every <c>RefreshIntervalMinutes</c> (default 5) to pick up new
///      strategies / new underlyings as the stack evolves.
///
/// Read-only with respect to OKX (public channel, no credentials needed).
/// Idempotent on the WS side — <c>ConnectAsync</c> guards against duplicate
/// subscriptions via the internal <c>_activeSymbols</c> HashSet, and
/// <c>SubscribeAsync</c> is a no-op for already-subscribed symbols.
/// </summary>
public sealed class OrderBookSubscriptionHostedService : BackgroundService
{
    private readonly ILogger<OrderBookSubscriptionHostedService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IStreamingMarketDataService _streamingService;
    private readonly TimeSpan _refreshInterval;
    private readonly HashSet<string> _subscribed = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialConnectDone;

    public OrderBookSubscriptionHostedService(
        ILogger<OrderBookSubscriptionHostedService> logger,
        IConfiguration configuration,
        IStreamingMarketDataService streamingService)
    {
        _logger = logger;
        _configuration = configuration;
        _streamingService = streamingService;
        var minutes = int.TryParse(
            configuration["OrderBookSubscription:RefreshIntervalMinutes"],
            out var m) && m > 0 ? m : 5;
        _refreshInterval = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Short initial delay so EdgeLab signal hosted services have a chance to
        // refresh latest.json on their first cycle. The signals exist on disk
        // before the worker boots in normal operation, but guard against the
        // worker-restart-mid-Quantlab-cron race.
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_refreshInterval);
        try
        {
            do
            {
                try { await RunAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "OrderBookSubscription tick failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var symbols = DiscoverSymbols();
        if (symbols.Count == 0)
        {
            _logger.LogWarning(
                "OrderBookSubscription: no symbols discovered from EdgeLabSingleAsset.Strategies. " +
                "Books5 subscription will be empty → post-only orders will fall back to mid and submit as taker.");
            return;
        }

        var fresh = symbols.Where(s => !_subscribed.Contains(s)).ToArray();
        if (fresh.Length == 0)
        {
            _logger.LogDebug("OrderBookSubscription: no new symbols. Tracking {Count}.", _subscribed.Count);
            return;
        }

        if (!_initialConnectDone)
        {
            await _streamingService.ConnectAsync(fresh, ct).ConfigureAwait(false);
            _initialConnectDone = true;
            foreach (var s in fresh) _subscribed.Add(s);
            _logger.LogWarning(
                "OrderBookSubscription: initial books5 subscription opened for {Count} symbol(s) → {Symbols}. " +
                "This is the maker-fill-ratio fix (was 0/336 = 100% taker on the prior worker).",
                fresh.Length, string.Join(",", fresh));
            return;
        }

        // Subsequent additions: incremental subscribe (avoids tearing down the WS).
        foreach (var s in fresh)
        {
            try
            {
                await _streamingService.SubscribeAsync(s).ConfigureAwait(false);
                _subscribed.Add(s);
                _logger.LogInformation("OrderBookSubscription: +{Symbol} (total {Count}).", s, _subscribed.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OrderBookSubscription: SubscribeAsync({Symbol}) failed.", s);
            }
        }
    }

    /// <summary>
    /// Discovers symbols by reading each EdgeLab strategy's <c>latest.json</c>
    /// and extracting <c>target_weights</c> keys. Tolerates missing files /
    /// parse errors — partial coverage is better than no coverage.
    /// </summary>
    private SortedSet<string> DiscoverSymbols()
    {
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        var inputDir = _configuration["EdgeLabSingleAsset:InputDir"];
        var strategies = _configuration
            .GetSection("EdgeLabSingleAsset:Strategies").Get<string[]>() ?? Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(inputDir) || strategies.Length == 0)
        {
            // Allow a manual override via OrderBookSubscription:Symbols.
            var manual = _configuration["OrderBookSubscription:Symbols"];
            if (!string.IsNullOrWhiteSpace(manual))
            {
                foreach (var s in manual.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    result.Add(s);
            }
            return result;
        }

        foreach (var strategy in strategies)
        {
            var path = Path.Combine(inputDir, strategy, "latest.json");
            if (!File.Exists(path)) continue;

            try
            {
                using var fs = File.OpenRead(path);
                using var doc = JsonDocument.Parse(fs);
                if (!doc.RootElement.TryGetProperty("target_weights", out var tw) ||
                    tw.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (var prop in tw.EnumerateObject())
                {
                    if (!string.IsNullOrWhiteSpace(prop.Name))
                        result.Add(prop.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OrderBookSubscription: parse failed for {Strategy} ({Path}).", strategy, path);
            }
        }

        // Operator override: appends to the discovered set (does not replace).
        var manualAppend = _configuration["OrderBookSubscription:Symbols"];
        if (!string.IsNullOrWhiteSpace(manualAppend))
        {
            foreach (var s in manualAppend.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                result.Add(s);
        }

        return result;
    }
}
