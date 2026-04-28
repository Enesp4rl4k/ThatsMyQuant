using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using ThatsMyQuant.Infrastructure.Services;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [P1.1] Hosted service that consumes the OKX <c>liquidation-orders</c> WebSocket
/// channel via <see cref="OkxWebSocketService.OnLiquidationReceived"/>, runs
/// <see cref="LiquidationCascadeFader.Detect"/> on a rolling per-symbol window, and
/// dispatches a fade <see cref="ExecuteTradeCommand"/> when a cascade clears the
/// configured trigger + dominance + strength thresholds.
///
/// State is in-memory only — the rolling window is a per-symbol queue trimmed each
/// time an event arrives (no separate timer needed). Cooldown is also per-symbol and
/// in-memory; on restart the system re-arms after a cold-start grace period equal to
/// the configured window length (any cascade that started before boot is incomplete
/// and would fire spuriously).
/// </summary>
public sealed class LiquidationCascadeHostedService : IHostedService, IDisposable
{
    private readonly ILogger<LiquidationCascadeHostedService> _logger;
    private readonly OkxWebSocketService _wsService;
    private readonly IServiceProvider _serviceProvider;
    private readonly LiquidationCascadeOptions _options;

    private readonly Dictionary<string, Queue<LiquidationCascadeFader.LiquidationEvent>> _eventsBySymbol =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastFiredAt =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly DateTime _coldStartUntil;

    public LiquidationCascadeHostedService(
        ILogger<LiquidationCascadeHostedService> logger,
        OkxWebSocketService wsService,
        IServiceProvider serviceProvider,
        IOptions<LiquidationCascadeOptions>? options = null)
    {
        _logger = logger;
        _wsService = wsService;
        _serviceProvider = serviceProvider;
        _options = options?.Value ?? new LiquidationCascadeOptions();
        _coldStartUntil = DateTime.UtcNow.AddSeconds(_options.WindowSeconds);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("LiquidationCascadeHostedService disabled (LiquidationCascade:Enabled=false).");
            return Task.CompletedTask;
        }

        _wsService.OnLiquidationReceived += OnLiquidation;
        _logger.LogInformation(
            "LiquidationCascadeHostedService armed: window={Win}s btcTrigger={Btc} altTrigger={Alt} dominance={Dom:F2} minStrength={Str} cooldown={Cd}s dispatch={Dis}",
            _options.WindowSeconds, _options.BtcTriggerNotional, _options.AltTriggerNotional,
            _options.DominanceRatio, _options.MinStrengthScore, _options.PerSymbolCooldownSeconds,
            _options.DispatchSignals);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _wsService.OnLiquidationReceived -= OnLiquidation;
        return Task.CompletedTask;
    }

    private void OnLiquidation(LiquidationCascadeFader.LiquidationEvent ev)
    {
        try
        {
            LiquidationCascadeFader.CascadeEvent? cascade = null;
            DateTime now = DateTime.UtcNow;

            lock (_gate)
            {
                if (!_eventsBySymbol.TryGetValue(ev.Symbol, out var queue))
                {
                    queue = new Queue<LiquidationCascadeFader.LiquidationEvent>();
                    _eventsBySymbol[ev.Symbol] = queue;
                }
                queue.Enqueue(ev);

                // Trim by time window
                var cutoff = now.AddSeconds(-_options.WindowSeconds);
                while (queue.Count > 0 && queue.Peek().Timestamp < cutoff)
                    queue.Dequeue();

                // Memory cap (defensive — should never bind in practice given window trim)
                while (queue.Count > _options.MaxEventsPerSymbol)
                    queue.Dequeue();

                // Cold-start grace: don't trade during the first window after boot.
                if (now < _coldStartUntil) return;

                // Cooldown gate per-symbol
                if (_lastFiredAt.TryGetValue(ev.Symbol, out var last) &&
                    (now - last).TotalSeconds < _options.PerSymbolCooldownSeconds)
                    return;

                var trigger = ResolveTrigger(ev.Symbol);
                cascade = LiquidationCascadeFader.Detect(
                    events: queue.ToArray(),
                    symbol: ev.Symbol,
                    windowEnd: now,
                    triggerNotional: trigger,
                    window: TimeSpan.FromSeconds(_options.WindowSeconds),
                    dominanceRatio: _options.DominanceRatio);

                if (cascade is null) return;
                if (cascade.StrengthScore < _options.MinStrengthScore) return;

                _lastFiredAt[ev.Symbol] = now;
            }

            _logger.LogInformation(
                "Liquidation cascade detected: {Symbol} fade={Fade} strength={Str} long=${Long:N0} short=${Short:N0}",
                cascade.Symbol, cascade.FadeSide, cascade.StrengthScore,
                cascade.LongSideNotional, cascade.ShortSideNotional);

            if (_options.DispatchSignals)
            {
                _ = DispatchFadeAsync(cascade);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiquidationCascadeHostedService: error processing event for {Symbol}", ev.Symbol);
        }
    }

    private decimal ResolveTrigger(string symbol)
    {
        bool isBtc = symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase);
        return isBtc ? _options.BtcTriggerNotional : _options.AltTriggerNotional;
    }

    private async Task DispatchFadeAsync(LiquidationCascadeFader.CascadeEvent cascade)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            // Quantity is left to the execution layer's sizing logic — passing 0 means
            // the AdaptiveExecutionOrchestrator + risk supervisor compute the notional
            // from their own policy. Confidence is mapped from cascade strength score.
            var signal = new Signal
            {
                Symbol = cascade.Symbol,
                Side = cascade.FadeSide,
                Price = 0m,
                Quantity = 0m,
                Confidence = cascade.StrengthScore,
                Source = "LiquidationCascadeFader",
                StrategyTag = "LiquidationCascadeFade",
                RegimeTag = "EventDriven",
                OrderType = OrderType.Market,
                Reasoning = $"Cascade {cascade.DominantSide} liquidations $" +
                            $"{(cascade.LongSideNotional + cascade.ShortSideNotional):N0} → fade {cascade.FadeSide}",
                Metadata = new Dictionary<string, string>
                {
                    ["CascadeStrength"] = cascade.StrengthScore.ToString(),
                    ["CascadeLongNotional"] = cascade.LongSideNotional.ToString("F0"),
                    ["CascadeShortNotional"] = cascade.ShortSideNotional.ToString("F0"),
                    ["CascadeHoldMinutes"] = ((int)cascade.ExpectedHoldDuration.TotalMinutes).ToString(),
                    ["ExecutionMode"] = "EventDriven"
                }
            };

            await mediator.Send(new ExecuteTradeCommand(signal));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LiquidationCascadeHostedService: dispatch failed for {Symbol}", cascade.Symbol);
        }
    }

    public void Dispose() => _wsService.OnLiquidationReceived -= OnLiquidation;
}
