using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel
{
    private void UpdatePortfolioGrowth()
    {
        var baseline = Convert.ToDouble(Balance);
        var closedTrades = _tradeCache
            .Where(t => t.IsClosed && t.ExitTime.HasValue && t.Status == TradeStatus.Executed)
            .OrderBy(t => t.ExitTime)
            .ToList();

        UpdatePortfolioStageUi(closedTrades);

        if (closedTrades.Count == 0)
        {
            SetEmptyGrowthState(baseline);
            return;
        }

        var (windowStart, labelSelector, summary) = ResolvePortfolioGrowthWindow();
        PortfolioView.PortfolioRangeSummary = summary;

        var tradesInWindow = closedTrades.Where(t => t.ExitTime!.Value >= windowStart).ToList();
        if (tradesInWindow.Count == 0)
        {
            SetEmptyWindowGrowthState(baseline, summary);
            return;
        }

        ProcessAndApplyGrowth(tradesInWindow, labelSelector, baseline);
    }

    private void UpdatePortfolioStageUi(List<Trade> closedTrades)
    {
        var dominantStage = closedTrades.Count == 0
            ? "No ladder data yet"
            : closedTrades
                .GroupBy(InferPromotionStage)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault() ?? "UNKNOWN";

        PortfolioView.PortfolioPromotionStageLabel = dominantStage;
    }

    private (DateTime Start, Func<DateTime, string> Labeler, string Summary) ResolvePortfolioGrowthWindow()
    {
        var now = DateTime.Now;
        var storedStageContext = ResolveStoredPromotionStageContext(
            _tradeCache.Where(t => t.IsClosed).ToList(),
            PortfolioView.PortfolioPromotionStageLabel);

        return PortfolioView.SelectedPortfolioRange switch
        {
            "Daily" => (now.Date, d => d.ToString("HH"),
                $"{PortfolioView.PortfolioExecutionModeLabel} | Stage {PortfolioView.PortfolioPromotionStageLabel} | Daily equity curve | {storedStageContext}"),
            "Weekly" => (now.Date.AddDays(-6), d => d.ToString("ddd"),
                $"{PortfolioView.PortfolioExecutionModeLabel} | Stage {PortfolioView.PortfolioPromotionStageLabel} | Weekly growth | {storedStageContext}"),
            "Yearly" => (new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Local), d => $"Q{((d.Month - 1) / 3) + 1}",
                $"{PortfolioView.PortfolioExecutionModeLabel} | Stage {PortfolioView.PortfolioPromotionStageLabel} | Yearly growth | {storedStageContext}"),
            _ => (new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Local), d => $"W{((d.Day - 1) / 7) + 1}",
                $"{PortfolioView.PortfolioExecutionModeLabel} | Stage {PortfolioView.PortfolioPromotionStageLabel} | Monthly growth | {storedStageContext}")
        };
    }

    private void SetEmptyGrowthState(double baseline)
    {
        var labels = new List<string>();
        var values = new List<double>();
        BuildFlatPortfolioSeries(labels, values, baseline);
        var (_, _, summary) = ResolvePortfolioGrowthWindow();
        var storedStageContext = ResolveStoredPromotionStageContext(
            _tradeCache.Where(t => t.IsClosed).ToList(),
            PortfolioView.PortfolioPromotionStageLabel);

        PortfolioView.PortfolioGrowthHeadline = $"{Balance:N0}";
        PortfolioView.PortfolioRangeSummary = summary;
        PortfolioView.PortfolioGrowthSubtext =
            $"No realized growth history yet. Dominant stage: {PortfolioView.PortfolioPromotionStageLabel}. {storedStageContext}";
        PortfolioView.PortfolioGrowthColor = GrayBrush;
        ApplyPortfolioSeries(labels, values);
    }

    private void SetEmptyWindowGrowthState(double baseline, string windowSummary)
    {
        var labels = new List<string>();
        var values = new List<double>();
        BuildFlatPortfolioSeries(labels, values, baseline);
        var storedStageContext = ResolveStoredPromotionStageContext(
            _tradeCache.Where(t => t.IsClosed).ToList(),
            PortfolioView.PortfolioPromotionStageLabel);

        PortfolioView.PortfolioGrowthHeadline = $"{Balance:N0}";
        PortfolioView.PortfolioRangeSummary = windowSummary;
        PortfolioView.PortfolioGrowthSubtext =
            $"No closed trades in this window. Dominant stage: {PortfolioView.PortfolioPromotionStageLabel}. {storedStageContext}";
        PortfolioView.PortfolioGrowthColor = GrayBrush;
        ApplyPortfolioSeries(labels, values);
    }

    private void ProcessAndApplyGrowth(List<Trade> trades, Func<DateTime, string> labelSelector, double baseline)
    {
        var labels = new List<string>();
        var values = new List<double>();
        decimal cumulative = Balance;
        var storedStageContext = ResolveStoredPromotionStageContext(trades, PortfolioView.PortfolioPromotionStageLabel);

        foreach (var bucket in trades.GroupBy(t => labelSelector(t.ExitTime!.Value)))
        {
            cumulative += bucket.Sum(CalculateTradePnL);
            labels.Add(bucket.Key);
            values.Add(Convert.ToDouble(cumulative));
        }

        var first = values.FirstOrDefault(baseline);
        var last = values.LastOrDefault(baseline);
        var changePercent = Math.Abs(first) < 1e-9 ? 0 : ((last - first) / first) * 100d;

        PortfolioView.PortfolioGrowthHeadline = $"{last:N0}";
        PortfolioView.PortfolioGrowthSubtext =
            $"{changePercent:+0.00;-0.00;0.00}% realized | Dominant stage {PortfolioView.PortfolioPromotionStageLabel} | {storedStageContext}";
        PortfolioView.PortfolioGrowthColor = changePercent >= 0 ? GreenBrush : RedBrush;

        ApplyPortfolioSeries(labels, values);
    }

    private void BuildFlatPortfolioSeries(List<string> labels, List<double> values, double baseline)
    {
        labels.AddRange(PortfolioView.SelectedPortfolioRange switch
        {
            "Daily" => new[] { "09", "11", "13", "15", "17", "19" },
            "Weekly" => new[] { "Mon", "Tue", "Wed", "Thu", "Fri" },
            "Yearly" => new[] { "Q1", "Q2", "Q3", "Q4" },
            _ => new[] { "W1", "W2", "W3", "W4" }
        });

        foreach (var _ in labels)
        {
            values.Add(baseline);
        }
    }

    private void ApplyPortfolioSeries(List<string> labels, List<double> values)
    {
        if (labels.Count == 0 || values.Count == 0)
        {
            return;
        }

        var min = values.Min();
        var max = values.Max();
        var range = Math.Max(1d, max - min);
        var width = 760d;
        var height = 220d;
        var points = new PointCollection();

        PortfolioXAxisLabels.Clear();
        PortfolioYAxisLabels.Clear();

        for (var i = 0; i < values.Count; i++)
        {
            var x = values.Count == 1 ? 0 : i * (width / (values.Count - 1));
            var y = height - ((values[i] - min) / range * height);
            points.Add(new Point(x, y));

            PortfolioXAxisLabels.Add(new ChartAxisLabel
            {
                Text = labels[i],
                X = x - 8,
                Y = height + 12
            });
        }

        for (var step = 0; step <= 4; step++)
        {
            var value = max - ((max - min) * step / 4d);
            var y = height * step / 4d;
            PortfolioYAxisLabels.Add(new ChartAxisLabel
            {
                Text = value.ToString("N0"),
                X = width + 16,
                Y = y - 8
            });
        }

        PortfolioGrowthPoints = points;
    }

    private void PopulateChartScales(List<Candle> candles, decimal min, decimal max, double canvasWidth, double canvasHeight)
    {
        const int horizontalSteps = 5;
        const int verticalSteps = 6;

        InvokeOnUi(() =>
        {
            for (int i = 0; i <= horizontalSteps; i++)
            {
                var y = canvasHeight / horizontalSteps * i;
                var price = max - (max - min) * (decimal)i / horizontalSteps;
                HorizontalGridLines.Add(new ChartGridLineViewModel { X1 = 0, Y1 = y, X2 = canvasWidth, Y2 = y });
                PriceScaleLabels.Add(new ThatsMyQuant.Dashboard.ViewModels.SubViewModels.ChartAxisLabel
                {
                    Text = price.ToString("F2"),
                    X = canvasWidth + 10,
                    Y = y - 8
                });
            }

            for (int i = 0; i < verticalSteps; i++)
            {
                var x = canvasWidth / (verticalSteps - 1) * i;
                var index = Math.Min(candles.Count - 1, (int)Math.Round((double)i / Math.Max(1, verticalSteps - 1) * (candles.Count - 1)));
                VerticalGridLines.Add(new ChartGridLineViewModel { X1 = x, Y1 = 0, X2 = x, Y2 = canvasHeight });
                TimeScaleLabels.Add(new ThatsMyQuant.Dashboard.ViewModels.SubViewModels.ChartAxisLabel
                {
                    Text = candles[index].Timestamp.ToString(candles.Count > 120 ? "dd MMM" : "dd MMM HH:mm"),
                    X = Math.Max(0, x - 24),
                    Y = canvasHeight + 10
                });
            }
        });
    }

    private void UpdateIndicatorAgenda(IndicatorSnapshot ind)
    {
        IndicatorDetails.Clear();
        LogicTree.Clear();

        LogicTree.Add(BuildMomentumNode(ind));
        LogicTree.Add(BuildTrendNode(ind));
        LogicTree.Add(BuildVolatilityNode(ind));

        foreach (var node in LogicTree)
        {
            foreach (var child in node.Indicators)
            {
                IndicatorDetails.Add(child);
            }
        }
    }

    private LogicTreeNode BuildMomentumNode(IndicatorSnapshot ind)
    {
        var node = new LogicTreeNode { Category = "Momentum Weights" };
        node.Indicators.Add(new IndicatorDetailViewModel
        {
            Name = "RSI (14)",
            Category = "Momentum",
            Value = ind.RSI?.ToString("F1") ?? "--",
            Status = ind.RSI > 70 ? "Overbought" : ind.RSI < 30 ? "Oversold" : "Neutral",
            Color = ind.RSI > 70 ? RedBrush : ind.RSI < 30 ? GreenBrush : GrayBrush
        });
        node.Indicators.Add(new IndicatorDetailViewModel
        {
            Name = "MACD",
            Category = "Momentum",
            Value = ind.MACDHistogram?.ToString("F4") ?? "--",
            Status = ind.MACDHistogram > 0 ? "Bullish" : "Bearish",
            Color = ind.MACDHistogram > 0 ? GreenBrush : RedBrush
        });
        return node;
    }

    private LogicTreeNode BuildTrendNode(IndicatorSnapshot ind)
    {
        var node = new LogicTreeNode { Category = "Trend Confluence" };
        node.Indicators.Add(new IndicatorDetailViewModel
        {
            Name = "EMA 20/50",
            Category = "Trend",
            Value = ind.EMA20?.ToString("F2") ?? "--",
            Status = ind.EMA20 > ind.EMA50 ? "Golden Cross" : "Death Cross",
            Color = ind.EMA20 > ind.EMA50 ? GreenBrush : RedBrush
        });
        node.Indicators.Add(new IndicatorDetailViewModel
        {
            Name = "AlphaTrend",
            Category = "Trend",
            Value = ind.AlphaTrend?.Value.ToString("F2") ?? "--",
            Status = ind.AlphaTrend?.IsBullish == true ? "Bullish Trend" : "Bearish Trend",
            Color = ind.AlphaTrend?.IsBullish == true ? GreenBrush : RedBrush
        });
        node.Indicators.Add(new IndicatorDetailViewModel
        {
            Name = "PMax",
            Category = "Trend",
            Value = ind.PMax?.Value.ToString("F2") ?? "--",
            Status = ind.PMax?.IsBullish == true ? "Bullish Filter" : "Bearish Filter",
            Color = ind.PMax?.IsBullish == true ? GreenBrush : RedBrush
        });
        node.Indicators.Add(new IndicatorDetailViewModel
        {
            Name = "Price Action",
            Category = "Trend",
            Value = ind.DetectedPattern,
            Status = ind.DetectedPattern == "None" ? "Waiting..." : "Active Pattern",
            Color = ind.DetectedPattern.Contains("Bullish") ? GreenBrush :
                ind.DetectedPattern.Contains("Bearish") ? RedBrush :
                GrayBrush
        });
        return node;
    }

    private LogicTreeNode BuildVolatilityNode(IndicatorSnapshot ind)
    {
        var node = new LogicTreeNode { Category = "Volatility / Volume" };
        node.Indicators.Add(new IndicatorDetailViewModel
        {
            Name = "Volatility (ATR)",
            Category = "Volatility",
            Value = ind.ATR?.ToString("F2") ?? "--",
            Status = "Normal",
            Color = GrayBrush
        });
        node.Indicators.Add(new IndicatorDetailViewModel
        {
            Name = "Volume Ratio",
            Category = "Volume",
            Value = (ind.VolumeRatio?.ToString("F1") ?? "--") + "x",
            Status = ind.VolumeRatio > 1.5 ? "High Relative Volume" : "Normal",
            Color = ind.VolumeRatio > 1.5 ? GreenBrush : GrayBrush
        });
        return node;
    }

    private void RefreshHeatmap()
    {
        var currentYear = DateTime.Now.Year;
        var monthlyPnL = new decimal[12];

        foreach (var trade in TradeJournal.Where(t => t.Time.Year == currentYear))
        {
            monthlyPnL[trade.Time.Month - 1] += trade.PnL;
        }

        while (MonthlyPerformance.Count < 12)
        {
            MonthlyPerformance.Add(new MonthlyPerformanceViewModel());
        }

        while (MonthlyPerformance.Count > 12)
        {
            MonthlyPerformance.RemoveAt(MonthlyPerformance.Count - 1);
        }

        decimal totalYtd = 0;
        for (int i = 0; i < 12; i++)
        {
            var pnl = monthlyPnL[i];
            totalYtd += pnl;

            var monthVm = new MonthlyPerformanceViewModel();
            var monthName = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(i + 1).ToUpper();

            Brush color = Brushes.Transparent;
            double opacity = 0.3;

            if (pnl > 0)
            {
                color = GreenBrush;
                opacity = Math.Min(1.0, 0.3 + (double)(pnl / 1000m));
            }
            else if (pnl < 0)
            {
                color = RedBrush;
                opacity = Math.Min(1.0, 0.3 + (double)(Math.Abs(pnl) / 1000m));
            }
            else if (i + 1 <= DateTime.Now.Month)
            {
                color = new SolidColorBrush(Color.FromRgb(0x17, 0x1D, 0x26));
            }

            monthVm.Update(monthName, pnl, color, opacity);
            MonthlyPerformance[i] = monthVm;
        }

        CurrentYtdPerformance = $"{totalYtd:+0.0;-0.0;0.0}%";
    }

    private void PopulateSafetyBanners(IndicatorSnapshot indicators)
    {
        SafetyBanners.Clear();

        if (indicators.VolumeRatio > 2.0)
        {
            SafetyBanners.Add(new SafetyBannerRow("Extreme Volumetric Pressure Detected", OrangeBrush));
        }

        if (indicators.ATR > (double?)indicators.CurrentPrice * 0.03)
        {
            SafetyBanners.Add(new SafetyBannerRow("High ATR: Expect Wider Stops", RedBrush));
        }

        if (SafetyBanners.Count == 0 && indicators.CurrentPrice > 0)
        {
            SafetyBanners.Add(new SafetyBannerRow("Environment: Standard Operating Conditions", GreenBrush));
        }
    }

    public record SafetyBannerRow(string Message, Brush Color);

    private async Task UpdateMultiTimeframeAsync(string symbol, string market)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        try
        {
            var timeframes = new[] { "15m", "1h", "4h" };
            var confluences = new List<ChecklistItemViewModel>();

            foreach (var timeframe in timeframes)
            {
                var candles = await _marketDataService.GetCandlesAsync(symbol, 60, market, timeframe);
                var indicators = await _technicalAnalysis.ComputeIndicatorsAsync(candles);
                var pmaxText = indicators.PMax?.IsBullish == true ? "bullish" : "bearish";
                var alphaTrendText = indicators.AlphaTrend?.IsBullish == true ? "bullish" : "bearish";
                var aligned = indicators.PMax?.IsBullish == true || indicators.AlphaTrend?.IsBullish == true;

                confluences.Add(new ChecklistItemViewModel
                {
                    Title = timeframe,
                    Status = aligned ? "Aligned" : "Review",
                    Value = $"PMAX {pmaxText} | AlphaTrend {alphaTrendText}",
                    Detail = $"RSI {(indicators.RSI ?? 0):F1} | Vol {(indicators.VolumeRatio ?? 1.0):F1}x",
                    IsPassing = aligned,
                    Color = aligned ? GreenBrush : OrangeBrush
                });
            }

            InvokeOnUi(() =>
            {
                TimeframeConfluences.Clear();
                foreach (var item in confluences)
                {
                    TimeframeConfluences.Add(item);
                }
            });
        }
        catch (Exception ex)
        {
            InvokeOnUi(() =>
            {
                TimeframeConfluences.Clear();
                TimeframeConfluences.Add(new ChecklistItemViewModel
                {
                    Title = "MTF",
                    Status = "Unavailable",
                    Value = "PMAX unavailable",
                    Detail = ex.Message,
                    IsPassing = false,
                    Color = OrangeBrush
                });
            });
        }
    }

    private decimal CalculatedProtectionScore()
    {
        if (_riskAgent.IsSoftPauseArmed) return 0;

        var score = 100m;
        var maxLossPercent = 5.0m;
        var maxLossAmount = Balance * (maxLossPercent / 100m);
        if (DailyPnL < 0 && maxLossAmount > 0)
        {
            var drawdownImpact = (Math.Abs(DailyPnL) / maxLossAmount) * 100m;
            score -= drawdownImpact;
        }

        if (ListenerHealth != "Healthy") score -= 25;

        return Math.Clamp(score, 0, 100);
    }

    private decimal CalculatedInvestmentScore()
    {
        if (_tradeCache.Count == 0) return 50;

        var recentTrades = _tradeCache.Take(20).ToList();
        var avgEdge = recentTrades.Average(t => (decimal)t.EdgeScore);
        var winRate = recentTrades.Count(t => t.RealisedPnL > 0) * 100m / recentTrades.Count;

        var score = (avgEdge * 0.6m) + (winRate * 0.4m);
        return Math.Clamp(score, 0, 100);
    }

    private decimal CalculatedFitnessScore()
    {
        var score = 100m;
        var recentTrades = _tradeCache.Take(10).ToList();
        if (recentTrades.Any())
        {
            var avgLatency = recentTrades.Average(t => (decimal)t.ExecutionLatencyMs);
            if (avgLatency > 100)
            {
                score -= (avgLatency - 100) / 10m;
            }

            var avgSlippage = recentTrades.Average(t => t.EntrySlippagePercent);
            if (avgSlippage > 0.1m)
            {
                score -= (avgSlippage - 0.1m) * 100m;
            }
        }

        if (QueueHealth != "Optimal") score -= 15;

        return Math.Clamp(score, 0, 100);
    }

    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xDA, 0x36, 0x33));
    private static readonly Brush BuyColorBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0xBB));
    private static readonly Brush SellColorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x33, 0x66));
    private static readonly Brush OrangeBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

    private void InvokeOnUi(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.Invoke(action);
            }
        }
        else
        {
            action();
        }
    }
}
