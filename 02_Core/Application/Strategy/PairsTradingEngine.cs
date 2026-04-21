using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using MediatR;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A zero-allocation HFT engine that tracks the price spread between two correlated assets.
/// It uses Engle-Granger Cointegration to dynamically adjust the Hedge Ratio and emits dual-leg spread capturing orders.
/// </summary>
public class PairsTradingEngine : INotificationHandler<Signal>
{
    private readonly ILogger<PairsTradingEngine> _logger;
    private readonly IMediator _mediator;
    private readonly PairsTradingOptions _options;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    
    private readonly string _symbolA;
    private readonly string _symbolB;
    
    // Circular buffers for raw prices to run periodic EG-tests
    private readonly decimal[] _priceWindowY; // Asset A
    private readonly decimal[] _priceWindowX; // Asset B
    private int _historyIndex = 0;
    private int _historyCount = 0;
    private readonly int _windowSize;

    private decimal _latestPriceA;
    private decimal _latestPriceB;

    private bool _isCointegrated;
    private decimal _hedgeRatio = 1m;
    private decimal _spreadMean;
    private decimal _spreadStdDev;
    private int _tickCounter = 0;
    private int _lastSignalTick = int.MinValue / 2;

    [ActivatorUtilitiesConstructor]
    public PairsTradingEngine(
        ILogger<PairsTradingEngine> logger,
        IMediator mediator,
        IOptions<PairsTradingOptions>? options = null)
    {
        _options = options?.Value ?? new PairsTradingOptions();
        _symbolA = _options.SymbolA;
        _symbolB = _options.SymbolB;
        _windowSize = Math.Max(16, _options.WindowSize);
        _priceWindowY = new decimal[_windowSize];
        _priceWindowX = new decimal[_windowSize];
        _logger = logger;
        _mediator = mediator;
    }

    internal PairsTradingEngine(string symbolA, string symbolB, int windowSize, ILogger<PairsTradingEngine> logger, IMediator mediator)
        : this(
            logger,
            mediator,
            Options.Create(new PairsTradingOptions
            {
                SymbolA = symbolA,
                SymbolB = symbolB,
                WindowSize = windowSize
            }))
    {
    }

    public async Task Handle(Signal notification, CancellationToken cancellationToken)
    {
        // Treat signal updates as market ticks for pairs monitoring
        await ProcessTickAsync(notification.Symbol, notification.Price, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessTickAsync(string symbol, decimal price, CancellationToken ct)
    {
        bool updated = false;

        if (symbol == _symbolA)
        {
            _latestPriceA = price;
            updated = true;
        }
        else if (symbol == _symbolB)
        {
            _latestPriceB = price;
            updated = true;
        }

        if (updated && _latestPriceA > 0 && _latestPriceB > 0)
        {
            _tickCounter++;
            
            // Insert into rolling window
            _priceWindowY[_historyIndex] = _latestPriceA;
            _priceWindowX[_historyIndex] = _latestPriceB;
            
            _historyIndex = (_historyIndex + 1) % _windowSize;
            if (_historyCount < _windowSize) _historyCount++;

            // Periodically check cointegration if we have enough data
            if (_historyCount == _windowSize && _tickCounter % Math.Max(1, _options.RebalanceInterval) == 0)
            {
                RecomputeCointegration();
            }

            if (_isCointegrated)
            {
                await EvaluateSpreadAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private void RecomputeCointegration()
    {
        // EG test requires contiguous arrays
        var seriesY = new decimal[_windowSize];
        var seriesX = new decimal[_windowSize];
        
        for (int i = 0; i < _windowSize; i++)
        {
            int idx = (_historyIndex + i) % _windowSize;
            seriesY[i] = _priceWindowY[idx];
            seriesX[i] = _priceWindowX[idx];
        }

        var result = EngleGrangerDetector.TestCointegration(seriesY, seriesX);
        _isCointegrated = result.IsCointegrated;
        
        if (_isCointegrated)
        {
            _hedgeRatio = result.HedgeRatio;
            _spreadMean = result.SpreadAverage;
            _spreadStdDev = Math.Max(0.0001m, result.SpreadStdDev);
            _logger.LogDebug("Pairs {A}/{B} Cointegrated! Beta: {Beta:F3}, StdDev: {StdDev:F3}", _symbolA, _symbolB, _hedgeRatio, _spreadStdDev);
        }
    }

    private async Task EvaluateSpreadAsync(CancellationToken ct)
    {
        // currentSpread = Y - (Beta * X)
        decimal currentSpread = _latestPriceA - (_hedgeRatio * _latestPriceB);
        double zScore = (double)((currentSpread - _spreadMean) / _spreadStdDev);

        if (_tickCounter - _lastSignalTick < Math.Max(1, _options.SignalCooldownTicks))
        {
            return;
        }

        if (zScore > ZScoreThresholdEntry)
        {
            _logger.LogInformation("PAIRS SIGNAL: Short {SymbolA} / Long {SymbolB} | Z-Score: {Z:F2}", _symbolA, _symbolB, zScore);
            await ExecuteLegsAsync(TradeSide.Sell, TradeSide.Buy, zScore, ct).ConfigureAwait(false);
        }
        else if (zScore < -ZScoreThresholdEntry)
        {
            _logger.LogInformation("PAIRS SIGNAL: Long {SymbolA} / Short {SymbolB} | Z-Score: {Z:F2}", _symbolA, _symbolB, zScore);
            await ExecuteLegsAsync(TradeSide.Buy, TradeSide.Sell, zScore, ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteLegsAsync(TradeSide sideA, TradeSide sideB, double zScore, CancellationToken ct)
    {
        await _executionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tickCounter - _lastSignalTick < Math.Max(1, _options.SignalCooldownTicks))
            {
                return;
            }

            _lastSignalTick = _tickCounter;
            var baseAllocation = Math.Max(100m, _options.BaseAllocationUsd);

            var sigA = new Signal
            {
                Symbol = _symbolA,
                Price = _latestPriceA,
                Side = sideA,
                StrategyTag = "PairsTrading",
                Confidence = 95,
                Reasoning = $"Cointegration reversion bounds reached. HedgeRatio: {_hedgeRatio:F3}, ZScore: {zScore:F2}",
                Quantity = baseAllocation / Math.Max(0.0000001m, _latestPriceA)
            };

            var sigB = new Signal
            {
                Symbol = _symbolB,
                Price = _latestPriceB,
                Side = sideB,
                StrategyTag = "PairsTrading",
                Confidence = 95,
                Reasoning = $"Cointegration reversion bounds reached. HedgeRatio: {_hedgeRatio:F3}, ZScore: {zScore:F2}",
                Quantity = (baseAllocation * _hedgeRatio) / Math.Max(0.0000001m, _latestPriceB)
            };

            var legA = _mediator.Send(new ExecuteTradeCommand(sigA), ct);
            var legB = _mediator.Send(new ExecuteTradeCommand(sigB), ct);

            await Task.WhenAll(legA, legB).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pairs execution failed for {SymbolA}/{SymbolB}. One or both hedge legs did not complete cleanly.", _symbolA, _symbolB);
            throw;
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private double ZScoreThresholdEntry => _options.EntryZScoreThreshold > 0 ? _options.EntryZScoreThreshold : 2.0;
}
