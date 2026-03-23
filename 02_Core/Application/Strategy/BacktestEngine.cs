using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Backtesting engine — runs the confluence strategy against historical candle data
/// and computes performance metrics (Sharpe, Sortino, drawdown, profit factor).
/// </summary>
public class BacktestEngine
{
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly ILogger<BacktestEngine> _logger;
    private readonly MarketExecutionProfileService _executionProfiles;
    private readonly StrategyPromotionService? _promotionService;
    private readonly ExitPolicyLearningService? _exitPolicyLearningService;

    public BacktestEngine(
        ITechnicalAnalysisService technicalAnalysis,
        ILogger<BacktestEngine> logger,
        MarketExecutionProfileService? executionProfiles = null,
        StrategyPromotionService? promotionService = null,
        ExitPolicyLearningService? exitPolicyLearningService = null)
    {
        _technicalAnalysis = technicalAnalysis;
        _logger = logger;
        _executionProfiles = executionProfiles ?? new MarketExecutionProfileService();
        _promotionService = promotionService;
        _exitPolicyLearningService = exitPolicyLearningService;
    }

    public async Task<BacktestResult> RunAsync(
        List<Candle> historicalCandles,
        string symbol,
        decimal startingCapital = 100_000m,
        int lookback = 60,
        decimal slippagePercent = 0.05m,
        decimal spreadPercent = 0.02m,
        decimal commissionPercentPerSide = 0.05m,
        bool enforceBistSessionHours = true)
    {
        _logger.LogInformation("Starting backtest for {Symbol} with {Count} candles, ₺{Capital:N0} capital",
            symbol, historicalCandles.Count, startingCapital);

        var trades = new List<BacktestTradeResult>();
        var equityCurve = new List<decimal> { startingCapital };
        decimal equity = startingCapital;
        decimal peakEquity = startingCapital;
        decimal maxDrawdown = 0;
        decimal maxDrawdownPercent = 0;
        int consecutiveLosses = 0;
        int maxConsecutiveLosses = 0;
        int cooldownCandlesRemaining = 0;
        var risk = RiskParameters.Default;
        string market = symbol.Contains("-") ? "Crypto" : (symbol.Contains("=") ? "Forex" : "BIST");
        var profile = _executionProfiles.ResolveForSymbol(symbol);
        var effectiveSlippage = slippagePercent == 0.05m ? profile.SlippagePercent : slippagePercent;
        var effectiveSpread = spreadPercent == 0.02m ? profile.SpreadPercent : spreadPercent;
        var effectiveCommission = commissionPercentPerSide == 0.05m ? profile.CommissionPercentPerSide : commissionPercentPerSide;

        PaperPosition? openPosition = null;
        var windowBuffer = new List<Candle>(lookback + 1);

        for (int i = lookback; i < historicalCandles.Count; i++)
        {
            var candle = historicalCandles[i];
            if (enforceBistSessionHours && !IsBistSessionOpen(candle.Timestamp))
            {
                continue;
            }
            if (!IsPreferredTradeWindow(market, candle.Timestamp))
            {
                continue;
            }
            
            windowBuffer.Clear();
            for (int j = i - lookback; j <= i; j++)
            {
                windowBuffer.Add(historicalCandles[j]);
            }
            var window = windowBuffer;

            // Update open position
            if (openPosition != null)
            {
                openPosition.BarsHeld++;
                openPosition.CurrentPrice = candle.Close;
                ApplyBreakevenProtection(openPosition, candle);
                var previousCandle = i > 0 ? historicalCandles[i - 1] : candle;

                if (!openPosition.HasTakenPartialProfit && openPosition.PartialTakeProfit > 0)
                {
                    var partialTriggered = openPosition.Side == "Buy"
                        ? candle.High >= openPosition.PartialTakeProfit
                        : candle.Low <= openPosition.PartialTakeProfit;

                    if (partialTriggered)
                    {
                        var partialQuantity = Math.Max(1m, Math.Floor(openPosition.Quantity * openPosition.PartialExitFraction));
                        partialQuantity = Math.Min(partialQuantity, openPosition.RemainingQuantity);

                        if (partialQuantity > 0)
                        {
                            var adjustedPartialExit = _executionProfiles.ApplyExecutionPrice(
                                openPosition.PartialTakeProfit,
                                openPosition.Side == "Buy",
                                false,
                                symbol,
                                null,
                                effectiveSlippage,
                                effectiveSpread,
                                candle);
                            var partialPnl = CalculateNetPnlWithFees(
                                openPosition.EntryPrice,
                                adjustedPartialExit,
                                partialQuantity,
                                openPosition.Side,
                                symbol,
                                effectiveCommission);

                            equity += partialPnl;
                            equityCurve.Add(equity);
                            openPosition.RemainingQuantity -= partialQuantity;
                            openPosition.HasTakenPartialProfit = true;
                            openPosition.StopLoss = openPosition.EntryPrice;
                            openPosition.TrailingStopPrice = openPosition.EntryPrice;

                            if (openPosition.RemainingQuantity <= 0)
                            {
                                trades.Add(new BacktestTradeResult
                                {
                                    EntryTime = openPosition.EntryTime,
                                    ExitTime = candle.Timestamp,
                                    Side = openPosition.Side,
                                    StrategyTag = openPosition.StrategyTag,
                                    RegimeTag = openPosition.RegimeTag,
                                    ExitPolicyTag = openPosition.ExitPolicyTag,
                                    ExecutionMode = openPosition.ExecutionMode,
                                    PromotionStage = openPosition.PromotionStage,
                                    EntryPrice = openPosition.EntryPrice,
                                    ExitPrice = openPosition.PartialTakeProfit,
                                    PnL = partialPnl,
                                    // denominator = notional value of this specific leg (partial qty)
                                    PnLPercent = openPosition.EntryPrice > 0
                                        ? partialPnl / (openPosition.EntryPrice * partialQuantity) * 100
                                        : 0,
                                    ExitReason = "PartialTakeProfit"
                                });
                                openPosition = null;
                                continue;
                            }
                        }
                    }
                }

                // Trailing stop update
                if (openPosition.Side == "Buy")
                {
                    if (candle.High > openPosition.HighestPriceSinceEntry)
                    {
                        openPosition.HighestPriceSinceEntry = candle.High;
                        decimal newTrailing = candle.High - (openPosition.EntryPrice - openPosition.StopLoss);
                        if (newTrailing > openPosition.TrailingStopPrice)
                            openPosition.TrailingStopPrice = newTrailing;
                    }
                }
                else
                {
                    if (candle.Low < openPosition.HighestPriceSinceEntry || openPosition.HighestPriceSinceEntry == 0)
                    {
                        openPosition.HighestPriceSinceEntry = candle.Low;
                        decimal newTrailing = candle.Low + (openPosition.StopLoss - openPosition.EntryPrice);
                        if (newTrailing < openPosition.TrailingStopPrice || openPosition.TrailingStopPrice == 0)
                            openPosition.TrailingStopPrice = newTrailing;
                    }
                }

                // Check exit conditions
                string? exitReason = null;
                decimal exitPrice = candle.Close;
                bool isStopMarketOrder = false;

                if (openPosition.Side == "Buy")
                {
                    if (candle.Low <= openPosition.TrailingStopPrice && openPosition.TrailingStopPrice > 0)
                    {
                        exitReason = "TrailingSL";
                        exitPrice = candle.Open < openPosition.TrailingStopPrice ? candle.Open : openPosition.TrailingStopPrice;
                        isStopMarketOrder = true;
                    }
                    else if (candle.Low <= openPosition.StopLoss)
                    {
                        exitReason = "StopLoss";
                        exitPrice = candle.Open < openPosition.StopLoss ? candle.Open : openPosition.StopLoss;
                        isStopMarketOrder = true;
                    }
                    else if (candle.High >= openPosition.TakeProfit)
                    { exitReason = "TakeProfit"; exitPrice = openPosition.TakeProfit; }
                }
                else
                {
                    if (candle.High >= openPosition.TrailingStopPrice && openPosition.TrailingStopPrice > 0)
                    {
                        exitReason = "TrailingSL";
                        exitPrice = candle.Open > openPosition.TrailingStopPrice ? candle.Open : openPosition.TrailingStopPrice;
                        isStopMarketOrder = true;
                    }
                    else if (candle.High >= openPosition.StopLoss)
                    {
                        exitReason = "StopLoss";
                        exitPrice = candle.Open > openPosition.StopLoss ? candle.Open : openPosition.StopLoss;
                        isStopMarketOrder = true;
                    }
                    else if (candle.Low <= openPosition.TakeProfit)
                    { exitReason = "TakeProfit"; exitPrice = openPosition.TakeProfit; }
                }

                if (exitReason == null)
                {
                    exitReason = TryGetStrategyPolicyExit(openPosition, candle, previousCandle);
                    if (exitReason != null)
                    {
                        exitPrice = candle.Close;
                    }
                }

                if (exitReason == null && openPosition.MaxHoldingBars > 0 && openPosition.BarsHeld >= openPosition.MaxHoldingBars)
                {
                    exitReason = $"TimeStop({openPosition.ExitPolicyTag})";
                    exitPrice = candle.Close;
                }

                if (exitReason != null)
                {
                    var appliedSlippage = isStopMarketOrder ? effectiveSlippage * 2.0m : effectiveSlippage;
                    var adjustedExit = _executionProfiles.ApplyExecutionPrice(
                        exitPrice,
                        openPosition.Side == "Buy",
                        false,
                        symbol,
                        null,
                        appliedSlippage,
                        effectiveSpread,
                        candle);
                    decimal netPnL = CalculateNetPnlWithFees(
                        openPosition.EntryPrice,
                        adjustedExit,
                        openPosition.RemainingQuantity,
                        openPosition.Side,
                        symbol,
                        effectiveCommission);

                    equity += netPnL;
                    equityCurve.Add(equity);

                    trades.Add(new BacktestTradeResult
                    {
                        EntryTime = openPosition.EntryTime,
                        ExitTime = candle.Timestamp,
                        Side = openPosition.Side,
                        StrategyTag = openPosition.StrategyTag,
                        RegimeTag = openPosition.RegimeTag,
                        ExitPolicyTag = openPosition.ExitPolicyTag,
                        ExecutionMode = openPosition.ExecutionMode,
                        PromotionStage = openPosition.PromotionStage,
                        EntryPrice = openPosition.EntryPrice,
                        ExitPrice = exitPrice,
                        PnL = netPnL,
                        // denominator = notional of the remaining leg that was just closed
                        PnLPercent = openPosition.EntryPrice > 0
                            ? netPnL / (openPosition.EntryPrice * openPosition.RemainingQuantity) * 100
                            : 0,
                        ExitReason = openPosition.HasTakenPartialProfit ? $"PartialTakeProfit -> {exitReason}" : exitReason
                    });

                    if (netPnL < 0) { consecutiveLosses++; maxConsecutiveLosses = Math.Max(maxConsecutiveLosses, consecutiveLosses); }
                    else consecutiveLosses = 0;

                    if (consecutiveLosses >= 3)
                    {
                        cooldownCandlesRemaining = 5;
                    }

                    // Drawdown tracking
                    if (equity > peakEquity) peakEquity = equity;
                    decimal drawdown = peakEquity - equity;
                    if (drawdown > maxDrawdown)
                    {
                        maxDrawdown = drawdown;
                        maxDrawdownPercent = peakEquity > 0 ? drawdown / peakEquity * 100 : 0;
                    }

                    openPosition = null;
            }
            continue; // Skip analysis while position is open
        }

            if (cooldownCandlesRemaining > 0)
            {
                cooldownCandlesRemaining--;
                continue;
            }

            // Run analysis
            var signal = new Signal { Symbol = symbol, Price = candle.Close };
            var decision = await _technicalAnalysis.AnalyzeAsync(signal, window, risk);

            if (decision.ShouldTrade && decision.Confidence >= 60)
            {
                decimal adaptiveSize = AdaptiveSizingService.Apply(
                    decision.PositionSizePercent,
                    decision.Confidence,
                    symbol.Contains("-") ? "Crypto" : (symbol.Contains("=") ? "Forex" : "BIST"),
                    candle.Timestamp);
                decimal positionValue = equity * adaptiveSize;
                decimal quantity = candle.Close > 0 ? Math.Floor(positionValue / candle.Close) : 0;

                // HFT COMMISSION GUARD: Ensure the spread overcomes two-way exchange fees
                bool approvedByCommissionGuard = CommissionGuard.IsTradeProfitableAfterFees(
                    candle.Close,
                    decision.TakeProfit,
                    quantity,
                    effectiveCommission / 100m);

                if (quantity > 0 && approvedByCommissionGuard)
                {
                    var adjustedEntry = _executionProfiles.ApplyExecutionPrice(
                        candle.Close,
                        decision.Side == TradeSide.Buy,
                        true,
                        symbol,
                        null,
                        effectiveSlippage,
                        effectiveSpread,
                        candle);
                    openPosition = new PaperPosition
                    {
                        Symbol = symbol,
                        Side = decision.Side.ToString(),
                        EntryPrice = adjustedEntry,
                        CurrentPrice = adjustedEntry,
                        Quantity = quantity,
                        RemainingQuantity = quantity,
                        StopLoss = decision.StopLoss,
                        TakeProfit = decision.TakeProfit,
                        PartialTakeProfit = decision.PartialTakeProfit,
                        PartialExitFraction = decision.PartialExitFraction <= 0 ? 0.50m : decision.PartialExitFraction,
                        ExitPolicyTag = decision.ExitPolicyTag,
                        BreakevenTriggerFraction = decision.BreakevenTriggerFraction,
                        MaxHoldingBars = decision.MaxHoldingBars,
                        StrategyTag = decision.StrategyTag,
                        RegimeTag = decision.RegimeTag,
                        ExecutionMode = "SIMULATED",
                        PromotionStage = "Backtest",
                        TrailingStopPrice = 0,
                        HighestPriceSinceEntry = candle.Close,
                        EntryTime = candle.Timestamp
                    };
                }
            }
        }

        // Close any remaining position at last price
        if (openPosition != null)
        {
            var lastCandle = historicalCandles.Last();
            var adjustedExit = _executionProfiles.ApplyExecutionPrice(
                lastCandle.Close,
                openPosition.Side == "Buy",
                false,
                symbol,
                null,
                effectiveSlippage,
                effectiveSpread,
                lastCandle);
            decimal netPnL = CalculateNetPnlWithFees(
                openPosition.EntryPrice,
                adjustedExit,
                openPosition.RemainingQuantity,
                openPosition.Side,
                symbol,
                effectiveCommission);
            
            equity += netPnL;
            trades.Add(new BacktestTradeResult
            {
                EntryTime = openPosition.EntryTime, ExitTime = lastCandle.Timestamp,
                Side = openPosition.Side, StrategyTag = openPosition.StrategyTag, RegimeTag = openPosition.RegimeTag,
                ExitPolicyTag = openPosition.ExitPolicyTag,
                ExecutionMode = openPosition.ExecutionMode, PromotionStage = openPosition.PromotionStage,
                EntryPrice = openPosition.EntryPrice,
                ExitPrice = lastCandle.Close, PnL = netPnL, ExitReason = openPosition.HasTakenPartialProfit ? "PartialTakeProfit -> EndOfData" : "EndOfData"
            });
        }

        // Compute metrics
        var pnls = trades.Select(t => (double)t.PnL).ToList();
        double sharpe = ComputeSharpe(pnls);
        double sortino = ComputeSortino(pnls);
        double profitFactor = ComputeProfitFactor(trades);
        double avgRR = ComputeAverageRR(trades);

        var result = new BacktestResult
        {
            Symbol = symbol,
            TotalTrades = trades.Count,
            WinningTrades = trades.Count(t => t.PnL > 0),
            LosingTrades = trades.Count(t => t.PnL <= 0),
            TotalPnL = equity - startingCapital,
            MaxDrawdown = maxDrawdown,
            MaxDrawdownPercent = maxDrawdownPercent,
            SharpeRatio = sharpe,
            SortinoRatio = sortino,
            AverageRR = avgRR,
            ProfitFactor = profitFactor,
            MaxConsecutiveLosses = maxConsecutiveLosses,
            StartingCapital = startingCapital,
            FinalEquity = equity,
            EquityCurve = equityCurve,
            Trades = trades,
            StartDate = historicalCandles.First().Timestamp,
            EndDate = historicalCandles.Last().Timestamp
        };

        _logger.LogInformation(
            "Backtest Complete: {Trades} trades | WinRate={WR:F1}% | PnL=₺{PnL:N0} | Sharpe={S:F2} | MaxDD={DD:F1}%",
            result.TotalTrades, result.WinRate, result.TotalPnL, result.SharpeRatio, result.MaxDrawdownPercent);

        _promotionService?.RecordBacktestState(result);
        _exitPolicyLearningService?.RecordBacktestState(result);
        return result;
    }

    private static double ComputeSharpe(List<double> pnls)
    {
        if (pnls.Count < 2) return 0;
        double avg = pnls.Average();
        double stdDev = Math.Sqrt(pnls.Sum(p => Math.Pow(p - avg, 2)) / (pnls.Count - 1));
        return stdDev > 0 ? avg / stdDev * Math.Sqrt(252) : 0; // Annualized
    }

    private static double ComputeSortino(List<double> pnls)
    {
        if (pnls.Count < 2) return 0;
        double avg = pnls.Average();
        var downsideReturns = pnls.Where(p => p < 0).ToList();
        if (downsideReturns.Count == 0) return avg > 0 ? 99 : 0;
        double downDev = Math.Sqrt(downsideReturns.Sum(p => Math.Pow(p, 2)) / downsideReturns.Count);
        return downDev > 0 ? avg / downDev * Math.Sqrt(252) : 0;
    }

    private static double ComputeProfitFactor(List<BacktestTradeResult> trades)
    {
        decimal grossProfit = trades.Where(t => t.PnL > 0).Sum(t => t.PnL);
        decimal grossLoss = Math.Abs(trades.Where(t => t.PnL < 0).Sum(t => t.PnL));
        return grossLoss > 0 ? (double)(grossProfit / grossLoss) : grossProfit > 0 ? 99 : 0;
    }

    private static double ComputeAverageRR(List<BacktestTradeResult> trades)
    {
        var winners = trades.Where(t => t.PnL > 0).Select(t => (double)t.PnL).ToList();
        var losers = trades.Where(t => t.PnL < 0).Select(t => Math.Abs((double)t.PnL)).ToList();
        if (winners.Count == 0 || losers.Count == 0) return 0;
        return winners.Average() / losers.Average();
    }

    private static void ApplyBreakevenProtection(PaperPosition position, Candle candle)
    {
        if (position.IsBreakevenArmed || position.BreakevenTriggerFraction <= 0)
        {
            return;
        }

        var targetDistance = Math.Abs(position.TakeProfit - position.EntryPrice);
        if (targetDistance <= 0)
        {
            return;
        }

        var triggerDistance = targetDistance * position.BreakevenTriggerFraction;
        var triggerReached = position.Side == "Buy"
            ? candle.High >= position.EntryPrice + triggerDistance
            : candle.Low <= position.EntryPrice - triggerDistance;

        if (!triggerReached)
        {
            return;
        }

        position.IsBreakevenArmed = true;
        var breakevenBuffer = position.EntryPrice * 0.0005m;

        if (position.Side == "Buy")
        {
            position.StopLoss = Math.Max(position.StopLoss, position.EntryPrice + breakevenBuffer);
        }
        else
        {
            position.StopLoss = position.StopLoss == 0
                ? position.EntryPrice - breakevenBuffer
                : Math.Min(position.StopLoss, position.EntryPrice - breakevenBuffer);
        }
    }

    private static string? TryGetStrategyPolicyExit(PaperPosition position, Candle candle, Candle previousCandle)
    {
        var bearishBar = candle.Close < candle.Open && candle.Close < previousCandle.Close;
        var bullishBar = candle.Close > candle.Open && candle.Close > previousCandle.Close;

        return position.ExitPolicyTag switch
        {
            "TrendRunner" when position.IsBreakevenArmed &&
                               position.BarsHeld >= Math.Max(6, position.MaxHoldingBars / 2) &&
                               ((position.Side == "Buy" && bearishBar) || (position.Side == "Sell" && bullishBar))
                => "TrendMomentumFade",
            "MeanReversion" when position.BarsHeld >= 4 && !position.IsBreakevenArmed
                => "MeanReversionTimeout",
            "BreakoutExpansion" when position.BarsHeld >= 3 && position.IsBreakevenArmed &&
                                      ((position.Side == "Buy" && bearishBar) || (position.Side == "Sell" && bullishBar))
                => "BreakoutFailure",
            "HftScalp" when position.BarsHeld >= 2
                => "HftMicroExit",
            _ => null
        };
    }

    private static bool IsPreferredTradeWindow(string market, DateTime timestamp)
    {
        if (string.Equals(market, "BIST", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var turkeyTime = timestamp.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp, "Turkey Standard Time")
            : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp.ToUniversalTime(), "Turkey Standard Time");

        return turkeyTime.Hour is >= 10 and < 13 or >= 15 and < 18;
    }

    private static decimal CalculateNetPnlWithFees(
        decimal entryPrice,
        decimal exitPrice,
        decimal quantity,
        string side,
        string symbol,
        decimal commissionPercentPerSide)
    {
        var direction = side == "Sell" ? -1m : 1m;
        var gross = (exitPrice - entryPrice) * quantity * direction;
        var commissionRate = commissionPercentPerSide / 100m;
        var fees = ((entryPrice * quantity) + (exitPrice * quantity)) * commissionRate;
        return gross - fees;
    }

    private static bool IsBistSessionOpen(DateTime utcTimestamp)
    {
        var turkeyTime = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utcTimestamp, DateTimeKind.Utc),
            TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"));

        if (turkeyTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        var t = turkeyTime.TimeOfDay;
        return (t >= new TimeSpan(10, 0, 0) && t < new TimeSpan(13, 0, 0)) ||
               (t >= new TimeSpan(14, 0, 0) && t < new TimeSpan(18, 0, 0));
    }
}
