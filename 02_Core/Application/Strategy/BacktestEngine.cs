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

    public BacktestEngine(ITechnicalAnalysisService technicalAnalysis, ILogger<BacktestEngine> logger)
    {
        _technicalAnalysis = technicalAnalysis;
        _logger = logger;
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
        var risk = RiskParameters.Default;

        PaperPosition? openPosition = null;

        for (int i = lookback; i < historicalCandles.Count; i++)
        {
            var candle = historicalCandles[i];
            if (enforceBistSessionHours && !IsBistSessionOpen(candle.Timestamp))
            {
                continue;
            }
            var window = historicalCandles.GetRange(i - lookback, lookback + 1);

            // Update open position
            if (openPosition != null)
            {
                openPosition.CurrentPrice = candle.Close;

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
                            var adjustedPartialExit = ApplyExecutionCost(openPosition.PartialTakeProfit, openPosition.Side == "Buy", false, slippagePercent, spreadPercent);
                            var partialPnl = CalculateNetPnlWithFees(
                                openPosition.EntryPrice,
                                adjustedPartialExit,
                                partialQuantity,
                                openPosition.Side,
                                commissionPercentPerSide);

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

                if (openPosition.Side == "Buy")
                {
                    if (candle.Low <= openPosition.TrailingStopPrice && openPosition.TrailingStopPrice > 0)
                    { exitReason = "TrailingSL"; exitPrice = openPosition.TrailingStopPrice; }
                    else if (candle.Low <= openPosition.StopLoss)
                    { exitReason = "StopLoss"; exitPrice = openPosition.StopLoss; }
                    else if (candle.High >= openPosition.TakeProfit)
                    { exitReason = "TakeProfit"; exitPrice = openPosition.TakeProfit; }
                }
                else
                {
                    if (candle.High >= openPosition.TrailingStopPrice && openPosition.TrailingStopPrice > 0)
                    { exitReason = "TrailingSL"; exitPrice = openPosition.TrailingStopPrice; }
                    else if (candle.High >= openPosition.StopLoss)
                    { exitReason = "StopLoss"; exitPrice = openPosition.StopLoss; }
                    else if (candle.Low <= openPosition.TakeProfit)
                    { exitReason = "TakeProfit"; exitPrice = openPosition.TakeProfit; }
                }

                if (exitReason != null)
                {
                    var adjustedExit = ApplyExecutionCost(exitPrice, openPosition.Side == "Buy", false, slippagePercent, spreadPercent);
                    decimal netPnL = CalculateNetPnlWithFees(
                        openPosition.EntryPrice,
                        adjustedExit,
                        openPosition.RemainingQuantity,
                        openPosition.Side,
                        commissionPercentPerSide);

                    equity += netPnL;
                    equityCurve.Add(equity);

                    trades.Add(new BacktestTradeResult
                    {
                        EntryTime = openPosition.EntryTime,
                        ExitTime = candle.Timestamp,
                        Side = openPosition.Side,
                        StrategyTag = openPosition.StrategyTag,
                        RegimeTag = openPosition.RegimeTag,
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

            // Run analysis
            var signal = new Signal { Symbol = symbol, Price = candle.Close };
            var decision = await _technicalAnalysis.AnalyzeAsync(signal, window, risk);

            if (decision.ShouldTrade && decision.Confidence >= 60)
            {
                decimal positionValue = equity * decision.PositionSizePercent;
                decimal quantity = candle.Close > 0 ? Math.Floor(positionValue / candle.Close) : 0;

                // HFT COMMISSION GUARD: Ensure the spread overcomes two-way exchange fees
                bool approvedByCommissionGuard = CommissionGuard.IsTradeProfitableAfterFees(candle.Close, decision.TakeProfit, quantity);

                if (quantity > 0 && approvedByCommissionGuard)
                {
                    var adjustedEntry = ApplyExecutionCost(candle.Close, decision.Side == TradeSide.Buy, true, slippagePercent, spreadPercent);
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
                        StrategyTag = decision.StrategyTag,
                        RegimeTag = decision.RegimeTag,
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
            var adjustedExit = ApplyExecutionCost(lastCandle.Close, openPosition.Side == "Buy", false, slippagePercent, spreadPercent);
            decimal netPnL = CalculateNetPnlWithFees(
                openPosition.EntryPrice,
                adjustedExit,
                openPosition.RemainingQuantity,
                openPosition.Side,
                commissionPercentPerSide);
            
            equity += netPnL;
            trades.Add(new BacktestTradeResult
            {
                EntryTime = openPosition.EntryTime, ExitTime = lastCandle.Timestamp,
                Side = openPosition.Side, StrategyTag = openPosition.StrategyTag, RegimeTag = openPosition.RegimeTag,
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

    private static decimal ApplyExecutionCost(
        decimal price,
        bool isBuy,
        bool isEntry,
        decimal slippagePercent,
        decimal spreadPercent)
    {
        var slippage = slippagePercent / 100m;
        var spreadHalf = (spreadPercent / 100m) / 2m;
        var cost = slippage + spreadHalf;

        // Buying pays up on entry; selling pays down on exit.
        var direction = (isBuy && isEntry) || (!isBuy && !isEntry) ? 1m : -1m;
        return price * (1m + (direction * cost));
    }

    private static decimal CalculateNetPnlWithFees(
        decimal entryPrice,
        decimal exitPrice,
        decimal quantity,
        string side,
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
