using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediatR;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Periodic reconciliation + optional chaos-mode signal injection.
/// Random signals only fire when TMQ_CHAOS_MODE=true (dev-time resilience test).
/// In live mode, low-latency tick/HFT signals are produced in Execution.Worker (TickPipeline + WebSocket).
/// This worker runs reconciliation; optional candle-based scans use <see cref="LiveSignalScanHostedService"/> when enabled.
/// </summary>
public class TradeBotWorker : BackgroundService
{
    private readonly ILogger<TradeBotWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly bool _chaosMode;
    private readonly string _chaosSymbol;

    public TradeBotWorker(
        ILogger<TradeBotWorker> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;

        var chaosFlag = configuration["TMQ_CHAOS_MODE"] ?? configuration["Chaos:Enabled"];
        _chaosMode = bool.TryParse(chaosFlag, out var enabled) && enabled;
        _chaosSymbol = configuration["Chaos:Symbol"] ?? "BTC-USDT";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "QuantEngine Worker starting. ChaosMode={ChaosMode}. Tick/HFT path: Execution.Worker; optional LiveSignalScan: see QuantEngine:LiveSignalScan:*.",
            _chaosMode);
        int tickCounter = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            tickCounter++;
            _logger.LogInformation("Bot Heartbeat at: {time}", DateTimeOffset.UtcNow);

            using (var scope = _serviceProvider.CreateScope())
            {
                try
                {
                    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                    var reco = scope.ServiceProvider.GetRequiredService<ReconciliationAgent>();

                    // Periodic Reconciliation Audit (Every 12 ticks / ~60s)
                    if (tickCounter % 12 == 0)
                    {
                        var audit = await reco.VerifyIntegrityAsync(0.5m);
                        if (!audit.InSync)
                        {
                            _logger.LogCritical("INTEGRITY BREACH: {Msg}", audit.Message);
                        }
                    }

                    if (_chaosMode)
                    {
                        await InjectChaosSignalAsync(mediator, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CHAOS RECOVERY: Execution loop caught an error. " +
                                         "Dead-Man-Switch confirmed active.");
                }
            }

            await Task.Delay(5000, stoppingToken);
        }
    }

    private async Task InjectChaosSignalAsync(IMediator mediator, CancellationToken stoppingToken)
    {
        bool isPerfectSignal = Random.Shared.NextDouble() > 0.9;

        var signal = new Signal
        {
            Source = isPerfectSignal ? "Alpha.PerfectSetup" : "Chaos.Simulation",
            Symbol = _chaosSymbol,
            Side = ThatsMyQuant.Contracts.Enums.TradeSide.Buy,
            Price = 60000m + Random.Shared.Next(-10, 10),
            Confidence = Random.Shared.Next(40, 70),
            Volume24h = 50_000_000m,
            Timestamp = DateTime.UtcNow
        };

        _logger.LogInformation("Chaos signal injected: {Symbol} @ {Price} (Conf: {Conf}%)",
            signal.Symbol, signal.Price, signal.Confidence);

        await mediator.Send(new ProcessSignalCommand(signal), stoppingToken);
    }
}
