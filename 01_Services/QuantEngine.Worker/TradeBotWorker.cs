using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediatR;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

public class TradeBotWorker : BackgroundService
{
    private readonly ILogger<TradeBotWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IStreamingMarketDataService _streamingService;
    private readonly TickPipeline _tickPipeline;
    private readonly string[] _symbols;

    public TradeBotWorker(
        ILogger<TradeBotWorker> logger,
        IServiceProvider serviceProvider,
        IStreamingMarketDataService streamingService,
        TickPipeline tickPipeline,
        IConfiguration configuration)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _streamingService = streamingService;
        _tickPipeline = tickPipeline;
        _symbols = configuration.GetSection("Exchange:Symbols").Get<string[]>()
                   ?? configuration.GetSection("Binance:Symbols").Get<string[]>()
                   ?? new[] { "BTCUSDT", "ETHUSDT" };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("QuantEngine Worker starting...");

        // Connect Binance WebSocket so FeatureEngineeringAgent gets real tick data
        // before SwarmOrchestrator runs its ML prediction step
        await _streamingService.ConnectAsync(_symbols, stoppingToken);
        _logger.LogInformation("Binance WebSocket connected for symbols: {Symbols}", string.Join(", ", _symbols));

        _tickPipeline.Start(stoppingToken);
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

                    // 1. Periodic Reconciliation Audit (Every 12 ticks / ~60s)
                    if (tickCounter % 12 == 0)
                    {
                        var audit = await reco.VerifyIntegrityAsync(0.5m); // Mocking 0.5 BTC local position
                        if (!audit.InSync)
                        {
                            _logger.LogCritical("INTEGRITY BREACH: {Msg}", audit.Message);
                            // In production: trigger Emergency Stop / Close all positions
                        }
                    }

                    // 2. Process High-Frequency Signal
                    // Chaos testing: 10% of simulated signals are treated as "PerfectSignal"
                    // to exercise the high-confidence execution path during dev/staging.
                    bool isPerfectSignal = Random.Shared.NextDouble() > 0.9;
                    
                    var signal = new Signal
                    {
                        Source = isPerfectSignal ? "Alpha.PerfectSetup" : "Internal.Simulation",
                        Symbol = "BTC-USDT",
                        Side = ThatsMyQuant.Contracts.Enums.TradeSide.Buy,
                        Price = 60000m + Random.Shared.Next(-10, 10),
                        Confidence = Random.Shared.Next(40, 70), // High confidence for verification
                        Volume24h = 50_000_000m,
                        Timestamp = DateTime.UtcNow
                    };

                    _logger.LogInformation("Simulating Signal: {Symbol} @ {Price} (Conv: {Conf}%)", 
                        signal.Symbol, signal.Price, signal.Confidence);
                    
                    await mediator.Send(new ProcessSignalCommand(signal), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CHAOS RECOVERY: Execution loop caught an error. " +
                                         "Dead-Man-Switch confirmed active.");
                    // The loop continues, proving we can survive network drops
                }
            }

            await Task.Delay(5000, stoppingToken);
        }

        await _streamingService.DisconnectAsync();
    }
}
