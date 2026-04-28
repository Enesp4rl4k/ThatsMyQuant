using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel
{
    private readonly MonteCarloSimulator _strategyEvidenceMonteCarlo = new();
    private CancellationTokenSource? _strategyEvidenceMcDebounceCts;

    private static readonly Brush StrategyEvidenceHistBrushStandard = FreezeBrush(0x38, 0xBD, 0xF8);
    private static readonly Brush StrategyEvidenceHistBrushGrid = FreezeBrush(0xF0, 0x88, 0x3E);
    private static readonly Brush StrategyEvidenceHistBrushHft = FreezeBrush(0xE8, 0x79, 0xF9);
    private static readonly Brush QuantLabEdgeScoreBrush = FreezeBrush(0x2D, 0xD4, 0xBF);
    private static readonly Brush QuantLabEdgeBandBrush = FreezeBrush(0xA7, 0x8B, 0xFA);
    private static readonly Brush QuantLabRegimeBrushA = FreezeBrush(0x3F, 0xB9, 0x50);
    private static readonly Brush QuantLabRegimeBrushB = FreezeBrush(0xF5, 0x9E, 0x0B);
    private static readonly Brush QuantLabRegimeBrushC = FreezeBrush(0x94, 0xA3, 0xB8);

    [ObservableProperty] private string _strategyEvidenceNarrative = StrategyEvidenceRouter.GetNarrative(null);
    [ObservableProperty] private string _strategyEvidenceVisualCaption = StrategyEvidenceRouter.GetVisualModeCaption(StrategyEvidenceVisualMode.Standard);
    [ObservableProperty] private string _strategyEvidenceMonteCarloSummary = "Refresh to run a Monte Carlo bootstrap on closed trades for the selected strategy.";
    [ObservableProperty] private string _strategyEvidenceMedianEquity = "—";
    [ObservableProperty] private string _strategyEvidenceP5Equity = "—";
    [ObservableProperty] private string _strategyEvidenceP99Drawdown = "—";
    [ObservableProperty] private string _strategyEvidenceTradeLine = "—";
    [ObservableProperty] private string _strategyEvidenceGridStreakLine = "—";
    [ObservableProperty] private string _strategyEvidenceHftLatencyLine = "—";
    [ObservableProperty] private string _strategyEvidenceStandardInsightLine = "—";
    [ObservableProperty] private bool _isStrategyEvidenceBusy;
    [ObservableProperty] private StrategyEvidenceVisualMode _strategyEvidenceVisualMode;
    [ObservableProperty] private PointCollection _strategyEvidenceCumulativePnlPoints = new();
    [ObservableProperty] private PointCollection _strategyEvidencePnlZeroLinePoints = new();
    [ObservableProperty] private bool _strategyEvidenceAutoRefreshMonteCarlo;

    public ObservableCollection<StrategyEvidenceHistogramBarViewModel> StrategyEvidenceHistogramBars { get; } = new();
    public ObservableCollection<StrategyEvidenceHistogramBarViewModel> QuantLabEdgeScoreBars { get; } = new();
    public ObservableCollection<StrategyEvidenceHistogramBarViewModel> QuantLabEdgeBandBars { get; } = new();
    public ObservableCollection<StrategyEvidenceHistogramBarViewModel> QuantLabRegimeBars { get; } = new();
    public ObservableCollection<StrategyEvidenceHistogramBarViewModel> QuantLabReturnDistributionBars { get; } = new();
    public ObservableCollection<QuantLabScatterDotViewModel> QuantLabMfeMaeDots { get; } = new();

    [ObservableProperty] private PointCollection _quantLabRollingSharpePoints = new();
    [ObservableProperty] private PointCollection _quantLabRollingSharpeZeroLine = new();
    [ObservableProperty] private PointCollection _quantLabDrawdownPoints = new();
    [ObservableProperty] private string _quantLabVarCvarSummary = "VaR/CVaR needs ≥20 trades.";
    [ObservableProperty] private string _quantLabRollingSharpeSummary = "Rolling Sharpe (20-trade window).";
    [ObservableProperty] private string _quantLabDrawdownSummary = "Peak-to-trough drawdown over closed book.";
    [ObservableProperty] private string _quantLabMfeMaeSummary = "Max Favorable vs Max Adverse Excursion per trade.";

    // -------------------- Regime Timeline + Entropy --------------------
    [ObservableProperty] private string _quantLabRegimeSymbol = "BTCUSDT";
    [ObservableProperty] private string _quantLabRegimeTimeframe = "1h";
    [ObservableProperty] private string _quantLabRegimeSummary = "Regime timeline — enter a symbol and hit ANALYZE.";
    [ObservableProperty] private bool _isQuantLabRegimeBusy;
    [ObservableProperty] private PointCollection _quantLabRegimeEntropyPoints = new();
    [ObservableProperty] private PointCollection _quantLabRegimeEntropyThresholdLine = new();
    public ObservableCollection<QuantLabRegimeCellViewModel> QuantLabRegimeTimelineCells { get; } = new();

    // -------------------- Pairs Cointegration --------------------
    [ObservableProperty] private string _quantLabPairsYSymbol = "SOLUSDT";
    [ObservableProperty] private string _quantLabPairsXSymbol = "AVAXUSDT";
    [ObservableProperty] private string _quantLabPairsTimeframe = "4h";
    [ObservableProperty] private string _quantLabPairsSummary = "Enter Y and X symbols then hit ANALYZE PAIR.";
    [ObservableProperty] private bool _isQuantLabPairsBusy;
    [ObservableProperty] private string _quantLabPairsCointegratedBadge = "—";
    [ObservableProperty] private string _quantLabPairsBetaText = "—";
    [ObservableProperty] private string _quantLabPairsHurstText = "—";
    [ObservableProperty] private string _quantLabPairsHalfLifeText = "—";
    [ObservableProperty] private string _quantLabPairsAdfText = "—";
    [ObservableProperty] private string _quantLabPairsCorrelationText = "—";
    [ObservableProperty] private PointCollection _quantLabPairsSpreadPoints = new();
    [ObservableProperty] private PointCollection _quantLabPairsUpperBandPoints = new();
    [ObservableProperty] private PointCollection _quantLabPairsLowerBandPoints = new();
    [ObservableProperty] private PointCollection _quantLabPairsMeanLinePoints = new();

    // -------------------- Performance-Stability Folds + Deflated/PSR --------------------
    public ObservableCollection<QuantLabFoldBarViewModel> QuantLabPerformanceFolds { get; } = new();
    [ObservableProperty] private string _quantLabFoldStabilitySummary = "Fold analysis needs ≥24 closed trades.";
    [ObservableProperty] private string _quantLabDeflatedSharpeText = "—";
    [ObservableProperty] private string _quantLabProbabilisticSharpeText = "—";
    [ObservableProperty] private string _quantLabSharpeMomentsText = "skew/kurt pending";

    // -------------------- Strategy Correlation Heatmap --------------------
    public ObservableCollection<QuantLabHeatmapRowViewModel> QuantLabCorrelationRows { get; } = new();
    [ObservableProperty] private string _quantLabCorrelationSummary = "Correlation needs ≥2 strategies with shared days.";

    // -------------------- P&L Attribution --------------------
    public ObservableCollection<QuantLabAttributionRowViewModel> QuantLabAttributionRows { get; } = new();
    [ObservableProperty] private string _quantLabAttributionSummary = "Per-strategy PnL across 1h / 1d / 1w windows.";

    // -------------------- TCA Report --------------------
    [ObservableProperty] private string _quantLabTcaSummary = "TCA needs closed trades with slippage/fee data.";
    [ObservableProperty] private string _quantLabTcaSlippageText = "—";
    [ObservableProperty] private string _quantLabTcaFeesPctOfEdgeText = "—";
    [ObservableProperty] private string _quantLabTcaMakerRatioText = "—";
    [ObservableProperty] private string _quantLabTcaLatencyText = "—";
    [ObservableProperty] private string _quantLabTcaFeesPerTradeText = "—";
    [ObservableProperty] private string _quantLabTcaTotalFeesText = "—";

    partial void OnStrategyEvidenceAutoRefreshMonteCarloChanged(bool value)
    {
        if (value)
        {
            TryScheduleDebouncedMonteCarloRefresh();
        }
        else
        {
            _strategyEvidenceMcDebounceCts?.Cancel();
        }
    }

    internal void WireStrategyEvidenceStudioWatch()
    {
        Studio.PropertyChanged += StudioOnPropertyChangedForStrategyEvidence;
        ApplyStrategyEvidenceNarrativeOnly();
    }

    private void StudioOnPropertyChangedForStrategyEvidence(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StrategyStudioViewModel.SelectedBotStrategy)
            or nameof(StrategyStudioViewModel.EvidenceDeskMode))
        {
            ApplyStrategyEvidenceNarrativeOnly();
            TryScheduleDebouncedMonteCarloRefresh();
        }
    }

    private void TryScheduleDebouncedMonteCarloRefresh()
    {
        if (!StrategyEvidenceAutoRefreshMonteCarlo)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _strategyEvidenceMcDebounceCts?.Cancel();
        _strategyEvidenceMcDebounceCts?.Dispose();
        _strategyEvidenceMcDebounceCts = new CancellationTokenSource();
        var token = _strategyEvidenceMcDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(750, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                await dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _ = RunStrategyEvidenceChartsAsync();
                });
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled — expected.
            }
        });
    }

    private static StrategyEvidenceVisualMode ResolveEffectiveEvidenceVisualMode(string label, StrategyStudioViewModel studio) =>
        StrategyEvidenceRouter.ResolveVisualModeForDesk(StrategyEvidenceRouter.GetVisualMode(label), studio.EvidenceDeskMode);

    private void ApplyStrategyEvidenceNarrativeOnly()
    {
        var label = NormalizeBotStrategyLabel(Studio.SelectedBotStrategy);
        var effective = ResolveEffectiveEvidenceVisualMode(label, Studio);
        StrategyEvidenceVisualMode = effective;
        StrategyEvidenceVisualCaption = StrategyEvidenceRouter.GetVisualModeCaption(effective);

        var baseNarrative = StrategyEvidenceRouter.GetNarrative(label);
        if (StrategyEvidenceRouter.GetVisualMode(label) == StrategyEvidenceVisualMode.Hft
            && effective == StrategyEvidenceVisualMode.Standard)
        {
            StrategyEvidenceNarrative =
                baseNarrative + " You chose Normal desk: HFT latency strip stays off; book stats below still apply.";
        }
        else
        {
            StrategyEvidenceNarrative = baseNarrative;
        }

        var trades = GetClosedExecutedTradesForStrategyEvidence(label);
        PopulateStrategyEvidenceTradeDerivedLines(trades, label);
        PopulateQuantLabBookDistributionCharts(trades, ResolveEffectiveEvidenceVisualMode(label, Studio));
    }

    private List<Trade> GetClosedExecutedTradesForStrategyEvidence(string label) =>
        _tradeCache
            .Where(t => t.Status == TradeStatus.Executed)
            .Where(t => t.ExitTime.HasValue && t.IsClosed)
            .Where(t => StrategyTagsMatch(t.StrategyTag, label))
            .OrderBy(t => t.ExitTime!.Value)
            .ToList();

    private void PopulateStrategyEvidenceTradeDerivedLines(IReadOnlyList<Trade> trades, string label)
    {
        if (trades.Count == 0)
        {
            StrategyEvidenceTradeLine = "No closed executed trades for this strategy tag yet.";
            StrategyEvidenceGridStreakLine = "—";
            StrategyEvidenceHftLatencyLine = "—";
            StrategyEvidenceStandardInsightLine = "—";
            return;
        }

        StrategyEvidenceTradeLine = $"{trades.Count} closed trade(s) tagged for «{label}».";

        var pnls = trades.Select(CalculateTradePnL).ToList();
        var maxLossRun = 0;
        var cur = 0;
        foreach (var p in pnls)
        {
            if (p < 0)
            {
                cur++;
                maxLossRun = Math.Max(maxLossRun, cur);
            }
            else
            {
                cur = 0;
            }
        }

        StrategyEvidenceGridStreakLine = maxLossRun > 0
            ? $"Largest consecutive losing streak: {maxLossRun} trade(s) (exit-time order)."
            : "No consecutive losing streak on this slice.";

        var latencies = trades.Where(t => t.ExecutionLatencyMs > 0).Select(t => t.ExecutionLatencyMs).OrderBy(x => x).ToList();
        if (latencies.Count > 0)
        {
            var med = latencies[latencies.Count / 2];
            var p90Idx = Math.Clamp((int)(latencies.Count * 0.90d), 0, latencies.Count - 1);
            var p90 = latencies[p90Idx];
            StrategyEvidenceHftLatencyLine =
                $"Closed-book latency median / P90: {med} / {p90} ms ({latencies.Count} fills with timing).";
        }
        else
        {
            StrategyEvidenceHftLatencyLine = "No execution latency samples on these closes (common in paper / synthetic fills).";
        }

        var wins = pnls.Count(p => p > 0);
        var avg = pnls.Average();
        StrategyEvidenceStandardInsightLine =
            $"Win rate {100.0 * wins / pnls.Count:F1}% · mean PnL {avg:N2} · cumulative sum {pnls.Sum():N2}";
    }

    [RelayCommand]
    private Task RefreshStrategyEvidenceAsync() => RunStrategyEvidenceChartsAsync();

    private async Task RunStrategyEvidenceChartsAsync()
    {
        var label = NormalizeBotStrategyLabel(Studio.SelectedBotStrategy);
        var effective = ResolveEffectiveEvidenceVisualMode(label, Studio);
        StrategyEvidenceVisualMode = effective;
        StrategyEvidenceVisualCaption = StrategyEvidenceRouter.GetVisualModeCaption(effective);

        var baseNarrative = StrategyEvidenceRouter.GetNarrative(label);
        if (StrategyEvidenceRouter.GetVisualMode(label) == StrategyEvidenceVisualMode.Hft
            && effective == StrategyEvidenceVisualMode.Standard)
        {
            StrategyEvidenceNarrative =
                baseNarrative + " You chose Normal desk: HFT latency strip stays off; book stats below still apply.";
        }
        else
        {
            StrategyEvidenceNarrative = baseNarrative;
        }

        var trades = GetClosedExecutedTradesForStrategyEvidence(label);
        PopulateStrategyEvidenceTradeDerivedLines(trades, label);
        PopulateQuantLabBookDistributionCharts(trades, effective);

        StrategyEvidenceHistogramBars.Clear();
        StrategyEvidenceCumulativePnlPoints = new PointCollection();
        StrategyEvidencePnlZeroLinePoints = new PointCollection();

        if (trades.Count == 0)
        {
            StrategyEvidenceMonteCarloSummary = "Monte Carlo needs at least one realized PnL sample.";
            StrategyEvidenceMedianEquity = "—";
            StrategyEvidenceP5Equity = "—";
            StrategyEvidenceP99Drawdown = "—";
            return;
        }

        var pnls = trades.Select(CalculateTradePnL).ToList();
        var cumulative = new List<decimal>(pnls.Count);
        decimal run = 0;
        foreach (var p in pnls)
        {
            run += p;
            cumulative.Add(run);
        }

        const double chartW = 520;
        const double chartH = 140;
        StrategyEvidenceCumulativePnlPoints = BuildCumulativePnlPolyline(cumulative, chartW, chartH);
        StrategyEvidencePnlZeroLinePoints = BuildPnlZeroCrossingLine(cumulative, chartW, chartH);

        var hist = new List<double>(pnls.Count);
        foreach (var p in pnls)
        {
            hist.Add((double)p);
        }

        var histBrush = ResolveHistogramBrush(effective);

        IsStrategyEvidenceBusy = true;
        try
        {
            const int iterations = 2000;
            var result = await _strategyEvidenceMonteCarlo.RunSimulationsAsync(hist, Balance, iterations).ConfigureAwait(true);

            StrategyEvidenceMedianEquity = $"{result.MedianFinalEquity:N2}";
            StrategyEvidenceP5Equity = $"{result.FifthPercentileEquity:N2}";
            StrategyEvidenceP99Drawdown = $"{result.NinetyNinthPercentileDrawdown:N2}";
            StrategyEvidenceMonteCarloSummary =
                $"Bootstrap: {iterations} paths, start {Balance:N0}, 252 steps/path, from {hist.Count} trade PnLs.";

            var bins = result.FinalEquityHistogram;
            var maxCount = bins.Count == 0 ? 0 : bins.Max(b => b.Count);
            const double maxBar = 96d;
            foreach (var b in bins)
            {
                var h = maxCount <= 0 ? 0d : maxBar * b.Count / maxCount;
                var lab = $"{b.BinStart:N0}–{b.BinEnd:N0}";
                StrategyEvidenceHistogramBars.Add(new StrategyEvidenceHistogramBarViewModel(lab, h, b.Count, histBrush));
            }
        }
        catch (Exception ex)
        {
            StrategyEvidenceMonteCarloSummary = $"Monte Carlo failed: {ex.Message}";
            StrategyEvidenceMedianEquity = "—";
            StrategyEvidenceP5Equity = "—";
            StrategyEvidenceP99Drawdown = "—";
        }
        finally
        {
            IsStrategyEvidenceBusy = false;
        }
    }

    private static Brush ResolveHistogramBrush(StrategyEvidenceVisualMode mode) => mode switch
    {
        StrategyEvidenceVisualMode.Grid => StrategyEvidenceHistBrushGrid,
        StrategyEvidenceVisualMode.Hft => StrategyEvidenceHistBrushHft,
        _ => StrategyEvidenceHistBrushStandard
    };

    private static Brush FreezeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        return brush;
    }

    private static PointCollection BuildPnlZeroCrossingLine(IReadOnlyList<decimal> cumulative, double width, double height)
    {
        var pc = new PointCollection();
        if (cumulative.Count == 0)
        {
            return pc;
        }

        var min = (double)cumulative.Min();
        var max = (double)cumulative.Max();
        if (min >= 0d || max <= 0d)
        {
            return pc;
        }

        var span = Math.Max(max - min, 1e-9);
        var yZero = height - (0d - min) / span * height;
        pc.Add(new Point(0, yZero));
        pc.Add(new Point(width, yZero));
        return pc;
    }

    private static PointCollection BuildCumulativePnlPolyline(IReadOnlyList<decimal> cumulative, double width, double height)
    {
        var pc = new PointCollection();
        if (cumulative.Count == 0)
        {
            return pc;
        }

        var min = (double)cumulative.Min();
        var max = (double)cumulative.Max();
        var span = Math.Max(max - min, 1e-9);
        if (cumulative.Count == 1)
        {
            var y = height - (((double)cumulative[0] - min) / span * height);
            pc.Add(new Point(0, y));
            pc.Add(new Point(width, y));
            return pc;
        }

        for (var i = 0; i < cumulative.Count; i++)
        {
            var x = width * i / (cumulative.Count - 1d);
            var y = height - (((double)cumulative[i] - min) / span * height);
            pc.Add(new Point(x, y));
        }

        return pc;
    }

    private void PopulateQuantLabBookDistributionCharts(IReadOnlyList<Trade> trades, StrategyEvidenceVisualMode effectiveMode)
    {
        QuantLabEdgeScoreBars.Clear();
        QuantLabEdgeBandBars.Clear();
        QuantLabRegimeBars.Clear();
        PopulateQuantLabDiagnosticCharts(trades, effectiveMode);

        if (trades.Count == 0)
        {
            return;
        }

        const double maxBar = 72d;
        var accent = ResolveHistogramBrush(effectiveMode);

        var edgeMetaTrades = trades
            .Where(t => t.EdgeScore > 0m || !string.IsNullOrWhiteSpace(t.EdgeBand))
            .ToList();

        if (edgeMetaTrades.Count > 0)
        {
            var scoreBins = new int[5];
            foreach (var t in edgeMetaTrades)
            {
                var s = (double)t.EdgeScore;
                var idx = (int)Math.Floor(s / 20d);
                if (idx < 0) idx = 0;
                if (idx > 4) idx = 4;
                scoreBins[idx]++;
            }

            var labels = new[] { "0–20", "20–40", "40–60", "60–80", "80–100" };
            AddHistogramBars(scoreBins, labels, QuantLabEdgeScoreBars, QuantLabEdgeScoreBrush, maxBar);

            var bandGroups = edgeMetaTrades
                .GroupBy(t => string.IsNullOrWhiteSpace(t.EdgeBand) ? "—" : t.EdgeBand.Trim())
                .OrderByDescending(g => g.Count())
                .Take(10)
                .ToList();
            var maxBand = bandGroups.Count == 0 ? 0 : bandGroups.Max(g => g.Count());
            foreach (var g in bandGroups)
            {
                var c = g.Count();
                var h = maxBand <= 0 ? 0d : maxBar * c / maxBand;
                QuantLabEdgeBandBars.Add(new StrategyEvidenceHistogramBarViewModel(g.Key, h, c, QuantLabEdgeBandBrush));
            }
        }

        var regimeGroups = trades
            .GroupBy(t => string.IsNullOrWhiteSpace(t.RegimeTag) ? "Unknown" : t.RegimeTag.Trim())
            .OrderByDescending(g => g.Count())
            .Take(8)
            .ToList();
        var maxReg = regimeGroups.Count == 0 ? 0 : regimeGroups.Max(g => g.Count());
        var regimeBrushes = new[] { QuantLabRegimeBrushA, QuantLabRegimeBrushB, QuantLabRegimeBrushC, accent };
        for (var i = 0; i < regimeGroups.Count; i++)
        {
            var g = regimeGroups[i];
            var c = g.Count();
            var h = maxReg <= 0 ? 0d : maxBar * c / maxReg;
            var brush = regimeBrushes[i % regimeBrushes.Length];
            QuantLabRegimeBars.Add(new StrategyEvidenceHistogramBarViewModel(g.Key, h, c, brush));
        }
    }

    private static void AddHistogramBars(
        int[] counts,
        IReadOnlyList<string> labels,
        ObservableCollection<StrategyEvidenceHistogramBarViewModel> target,
        Brush brush,
        double maxBarHeight)
    {
        var max = counts.Length == 0 ? 0 : counts.Max();
        for (var i = 0; i < counts.Length; i++)
        {
            var c = counts[i];
            var h = max <= 0 ? 0d : maxBarHeight * c / max;
            var lab = i < labels.Count ? labels[i] : $"Bin {i}";
            target.Add(new StrategyEvidenceHistogramBarViewModel(lab, h, c, brush));
        }
    }

    // --------------------------------------------------------------------------------
    //                          QUANT-DIAGNOSTIC CHARTS
    // --------------------------------------------------------------------------------

    private void PopulateQuantLabDiagnosticCharts(IReadOnlyList<Trade> trades, StrategyEvidenceVisualMode effectiveMode)
    {
        QuantLabReturnDistributionBars.Clear();
        QuantLabMfeMaeDots.Clear();
        QuantLabRollingSharpePoints = new PointCollection();
        QuantLabRollingSharpeZeroLine = new PointCollection();
        QuantLabDrawdownPoints = new PointCollection();

        QuantLabVarCvarSummary = "VaR/CVaR needs ≥20 trades.";
        QuantLabRollingSharpeSummary = "Rolling Sharpe — needs ≥25 trades.";
        QuantLabDrawdownSummary = "Peak-to-trough drawdown over closed book.";
        QuantLabMfeMaeSummary = "MFE vs MAE — needs trade-level excursion data.";

        if (trades.Count == 0) return;

        var returnsPct = BuildReturnPercents(trades);
        PopulateReturnDistribution(returnsPct, effectiveMode);
        PopulateRollingSharpe(returnsPct);
        PopulateDrawdownCurve(trades);
        PopulateMfeMaeScatter(trades);
        PopulatePerformanceStabilityFolds(trades, returnsPct, effectiveMode);
        PopulateDeflatedAndProbabilisticSharpe(returnsPct);
        PopulateStrategyCorrelationHeatmap();
        PopulateTcaReport(trades);
        PopulateAttributionTable();
    }

    private static List<double> BuildReturnPercents(IReadOnlyList<Trade> trades)
    {
        // Per-trade return % = ((exit - entry) / entry) * side (long=+1, short=-1). Skips malformed rows.
        var list = new List<double>(trades.Count);
        foreach (var t in trades)
        {
            if (t.EntryPrice <= 0m) continue;
            var exit = t.AverageExitPrice > 0m ? t.AverageExitPrice : t.ExitPrice;
            if (exit <= 0m) continue;
            var direction = t.Side == TradeSide.Buy ? 1d : -1d;
            var r = (double)((exit - t.EntryPrice) / t.EntryPrice) * direction * 100d;
            list.Add(r);
        }
        return list;
    }

    private void PopulateReturnDistribution(IReadOnlyList<double> returnsPct, StrategyEvidenceVisualMode effectiveMode)
    {
        if (returnsPct.Count < 5) return;

        // 11 bins across [min,max] of return %. Mark VaR95/CVaR95 in the summary.
        var sorted = returnsPct.OrderBy(x => x).ToList();
        var min = sorted[0];
        var max = sorted[^1];
        if (Math.Abs(max - min) < 1e-9) return;

        const int binCount = 11;
        var width = (max - min) / binCount;
        var counts = new int[binCount];
        foreach (var v in sorted)
        {
            var idx = Math.Min(binCount - 1, Math.Max(0, (int)((v - min) / width)));
            counts[idx]++;
        }

        var labels = new string[binCount];
        for (int i = 0; i < binCount; i++)
        {
            var lo = min + i * width;
            var hi = lo + width;
            labels[i] = $"{lo:F1}→{hi:F1}%";
        }

        var brush = ResolveHistogramBrush(effectiveMode);
        AddHistogramBars(counts, labels, QuantLabReturnDistributionBars, brush, 72d);

        if (returnsPct.Count >= 20)
        {
            var varIdx = Math.Max(0, (int)Math.Floor(sorted.Count * 0.05));
            var var95 = sorted[varIdx];
            var tailSize = Math.Max(1, varIdx + 1);
            var cvar95 = sorted.Take(tailSize).Average();
            var mean = sorted.Average();
            var std = Math.Sqrt(sorted.Sum(v => (v - mean) * (v - mean)) / sorted.Count);
            QuantLabVarCvarSummary =
                $"Mean {mean:F2}% · σ {std:F2}% · VaR95 {var95:F2}% · CVaR95 {cvar95:F2}% · n={sorted.Count}";
        }
    }

    private void PopulateRollingSharpe(IReadOnlyList<double> returnsPct)
    {
        const int window = 20;
        if (returnsPct.Count < window + 5) return;

        var series = new List<double>(returnsPct.Count - window + 1);
        for (int i = window - 1; i < returnsPct.Count; i++)
        {
            double sum = 0d, sumSq = 0d;
            for (int j = i - window + 1; j <= i; j++) { sum += returnsPct[j]; sumSq += returnsPct[j] * returnsPct[j]; }
            var mean = sum / window;
            var variance = (sumSq / window) - mean * mean;
            var std = variance > 0d ? Math.Sqrt(variance) : 0d;
            // Per-trade Sharpe; multiply by √252 only when trades ~= daily. Keep raw per-trade here.
            var sharpe = std > 1e-9d ? mean / std : 0d;
            series.Add(sharpe);
        }

        const double chartW = 520d;
        const double chartH = 110d;
        QuantLabRollingSharpePoints = BuildPolylineFromSeries(series, chartW, chartH);
        QuantLabRollingSharpeZeroLine = BuildZeroReferenceLine(series, chartW, chartH);

        var last = series[^1];
        var min = series.Min();
        var max = series.Max();
        QuantLabRollingSharpeSummary =
            $"Rolling {window}-trade Sharpe · last {last:F2} · min {min:F2} · max {max:F2}";
    }

    private void PopulateDrawdownCurve(IReadOnlyList<Trade> trades)
    {
        var pnls = trades.Select(CalculateTradePnL).ToList();
        if (pnls.Count == 0) return;

        decimal peak = Balance;
        decimal running = Balance;
        var ddSeries = new List<double>(pnls.Count);
        decimal maxDd = 0m;
        foreach (var p in pnls)
        {
            running += p;
            if (running > peak) peak = running;
            var dd = peak == 0m ? 0m : (peak - running) / peak * 100m;
            if (dd > maxDd) maxDd = dd;
            ddSeries.Add(-(double)dd); // invert so 0 is top and worst is bottom
        }

        const double chartW = 520d;
        const double chartH = 110d;
        QuantLabDrawdownPoints = BuildPolylineFromSeries(ddSeries, chartW, chartH);
        QuantLabDrawdownSummary = $"Max drawdown {maxDd:F2}% · {pnls.Count} closed trades · peak equity {peak:N2}";
    }

    private void PopulateMfeMaeScatter(IReadOnlyList<Trade> trades)
    {
        // Prefer stored MAFE fields on Trade. Only emit dots with meaningful excursions.
        var candidates = trades
            .Where(t => t.MaxFavorableExcursionPercent != 0m || t.MaxAdverseExcursionPercent != 0m)
            .ToList();

        if (candidates.Count == 0) return;

        var maxMfe = candidates.Max(t => Math.Abs((double)t.MaxFavorableExcursionPercent));
        var maxMae = candidates.Max(t => Math.Abs((double)t.MaxAdverseExcursionPercent));
        var axis = Math.Max(1d, Math.Max(maxMfe, maxMae));

        const double canvasW = 260d;
        const double canvasH = 180d;
        var winBrush = FreezeBrush(0x3F, 0xB9, 0x50);
        var loseBrush = FreezeBrush(0xF7, 0x5F, 0x68);

        foreach (var t in candidates)
        {
            var mfe = Math.Abs((double)t.MaxFavorableExcursionPercent);
            var mae = Math.Abs((double)t.MaxAdverseExcursionPercent);
            var x = canvasW * (mae / axis);
            var y = canvasH - canvasH * (mfe / axis);
            var pnl = CalculateTradePnL(t);
            var brush = pnl >= 0m ? winBrush : loseBrush;
            var tip = $"{t.Symbol} · MFE {mfe:F2}% · MAE {mae:F2}% · PnL {pnl:N2}";
            QuantLabMfeMaeDots.Add(new QuantLabScatterDotViewModel(x, y, brush, tip));
        }

        QuantLabMfeMaeSummary =
            $"{candidates.Count} trades with excursion data · axis cap {axis:F2}% · green=winner, red=loser";
    }

    private static PointCollection BuildPolylineFromSeries(IReadOnlyList<double> series, double width, double height)
    {
        var pc = new PointCollection();
        if (series.Count == 0) return pc;
        var min = series.Min();
        var max = series.Max();
        var span = Math.Max(max - min, 1e-9);
        if (series.Count == 1)
        {
            var y = height - (series[0] - min) / span * height;
            pc.Add(new Point(0, y));
            pc.Add(new Point(width, y));
            return pc;
        }
        for (int i = 0; i < series.Count; i++)
        {
            var x = width * i / (series.Count - 1d);
            var y = height - (series[i] - min) / span * height;
            pc.Add(new Point(x, y));
        }
        return pc;
    }

    private static PointCollection BuildZeroReferenceLine(IReadOnlyList<double> series, double width, double height)
    {
        var pc = new PointCollection();
        if (series.Count == 0) return pc;
        var min = series.Min();
        var max = series.Max();
        if (min >= 0d || max <= 0d) return pc;
        var span = Math.Max(max - min, 1e-9);
        var y = height - (0d - min) / span * height;
        pc.Add(new Point(0, y));
        pc.Add(new Point(width, y));
        return pc;
    }

    // --------------------------------------------------------------------------------
    //                   REGIME TIMELINE + SHANNON ENTROPY SPARKLINE
    // --------------------------------------------------------------------------------

    private static readonly Brush RegimeBrushTrending = FreezeBrush(0x3F, 0xB9, 0x50);
    private static readonly Brush RegimeBrushRanging = FreezeBrush(0xF5, 0x9E, 0x0B);
    private static readonly Brush RegimeBrushVolatile = FreezeBrush(0xF7, 0x5F, 0x68);
    private static readonly Brush RegimeBrushUnknown = FreezeBrush(0x5C, 0x65, 0x70);

    [RelayCommand]
    private async Task AnalyzeRegimeTimelineAsync()
    {
        var sym = (QuantLabRegimeSymbol ?? "BTCUSDT").Trim().ToUpperInvariant();
        var tf = (QuantLabRegimeTimeframe ?? "1h").Trim();
        if (string.IsNullOrWhiteSpace(sym)) { QuantLabRegimeSummary = "Enter a symbol."; return; }

        QuantLabRegimeTimelineCells.Clear();
        QuantLabRegimeEntropyPoints = new PointCollection();
        QuantLabRegimeEntropyThresholdLine = new PointCollection();
        IsQuantLabRegimeBusy = true;
        QuantLabRegimeSummary = $"Loading {sym} {tf} candles…";

        try
        {
            const int candleCount = 500;
            var candles = await _marketDataService.GetCandlesAsync(sym, candleCount, "Crypto", tf).ConfigureAwait(true);
            if (candles is null || candles.Count < 80)
            {
                QuantLabRegimeSummary = $"Not enough candles for {sym} {tf} (got {candles?.Count ?? 0}).";
                return;
            }

            const int window = 50;
            var results = new List<(MarketRegime Regime, double Entropy)>(candles.Count - window + 1);
            for (int i = window; i <= candles.Count; i++)
            {
                var slice = candles.Skip(i - window).Take(window).ToList();
                var res = MarketRegimeDetector.Detect(slice, lookback: 20, lagBars: 0, timeframe: tf);
                double entropy = ComputeShannonEntropyOnCloses(slice);
                results.Add((res.Regime, entropy));
            }

            const double chartW = 520d;
            var cellW = chartW / Math.Max(1, results.Count);
            for (int i = 0; i < results.Count; i++)
            {
                var brush = ResolveRegimeBrush(results[i].Regime);
                var x = i * cellW;
                var tip = $"bar#{i + window} · {results[i].Regime} · H={results[i].Entropy:F2}";
                QuantLabRegimeTimelineCells.Add(new QuantLabRegimeCellViewModel(x, cellW + 0.5, brush, tip));
            }

            // Entropy sparkline: fixed [0,1] y-axis with horizontal 0.82 threshold line.
            const double entH = 90d;
            var entPoints = new PointCollection();
            for (int i = 0; i < results.Count; i++)
            {
                var xx = chartW * i / Math.Max(1, results.Count - 1);
                var yy = entH - Math.Clamp(results[i].Entropy, 0d, 1d) * entH;
                entPoints.Add(new Point(xx, yy));
            }
            QuantLabRegimeEntropyPoints = entPoints;
            var thresh = new PointCollection
            {
                new Point(0, entH - 0.82 * entH),
                new Point(chartW, entH - 0.82 * entH)
            };
            QuantLabRegimeEntropyThresholdLine = thresh;

            var totalBars = results.Count;
            var trending = results.Count(r => r.Regime == MarketRegime.Trending);
            var ranging = results.Count(r => r.Regime == MarketRegime.Ranging);
            var volatile_ = results.Count(r => r.Regime == MarketRegime.Volatile);
            var current = results[^1].Regime;
            var latestEntropy = results[^1].Entropy;
            QuantLabRegimeSummary =
                $"{sym} · {tf} · current {current} (H={latestEntropy:F2}) · " +
                $"Trending {100d * trending / totalBars:F1}% / Ranging {100d * ranging / totalBars:F1}% / Volatile {100d * volatile_ / totalBars:F1}% " +
                $"over {totalBars} windows";
        }
        catch (Exception ex)
        {
            QuantLabRegimeSummary = $"Regime fetch failed: {ex.Message}";
        }
        finally
        {
            IsQuantLabRegimeBusy = false;
        }
    }

    private static Brush ResolveRegimeBrush(MarketRegime regime) => regime switch
    {
        MarketRegime.Trending => RegimeBrushTrending,
        MarketRegime.Ranging => RegimeBrushRanging,
        MarketRegime.Volatile => RegimeBrushVolatile,
        _ => RegimeBrushUnknown
    };

    /// <summary>
    /// Mirrors the private Shannon entropy computation in MarketRegimeDetector:
    /// log-returns discretized into 5 equal-range bins, entropy normalized by log2(5).
    /// </summary>
    private static double ComputeShannonEntropyOnCloses(IReadOnlyList<Candle> window)
    {
        if (window.Count < 3) return 0d;
        var returns = new List<double>(window.Count - 1);
        for (int i = 1; i < window.Count; i++)
        {
            var prev = (double)window[i - 1].Close;
            var cur = (double)window[i].Close;
            if (prev <= 0d) continue;
            returns.Add(Math.Log(cur / prev));
        }
        if (returns.Count == 0) return 0d;

        double min = returns.Min();
        double max = returns.Max();
        if (Math.Abs(max - min) < 1e-12) return 0d;
        const int bins = 5;
        var width = (max - min) / bins;
        var counts = new int[bins];
        foreach (var r in returns)
        {
            var idx = Math.Min(bins - 1, Math.Max(0, (int)((r - min) / width)));
            counts[idx]++;
        }
        double h = 0d;
        foreach (var c in counts)
        {
            if (c == 0) continue;
            var p = (double)c / returns.Count;
            h -= p * Math.Log2(p);
        }
        return h / Math.Log2(bins);
    }

    // --------------------------------------------------------------------------------
    //                   PAIRS COINTEGRATION ANALYTICS
    // --------------------------------------------------------------------------------

    [RelayCommand]
    private async Task AnalyzePairsCointegrationAsync()
    {
        var y = (QuantLabPairsYSymbol ?? "").Trim().ToUpperInvariant();
        var x = (QuantLabPairsXSymbol ?? "").Trim().ToUpperInvariant();
        var tf = (QuantLabPairsTimeframe ?? "4h").Trim();
        if (string.IsNullOrWhiteSpace(y) || string.IsNullOrWhiteSpace(x))
        {
            QuantLabPairsSummary = "Enter both Y and X symbols.";
            return;
        }

        QuantLabPairsSpreadPoints = new PointCollection();
        QuantLabPairsUpperBandPoints = new PointCollection();
        QuantLabPairsLowerBandPoints = new PointCollection();
        QuantLabPairsMeanLinePoints = new PointCollection();
        QuantLabPairsCointegratedBadge = "—";
        QuantLabPairsBetaText = "—";
        QuantLabPairsHurstText = "—";
        QuantLabPairsHalfLifeText = "—";
        QuantLabPairsAdfText = "—";
        QuantLabPairsCorrelationText = "—";
        IsQuantLabPairsBusy = true;
        QuantLabPairsSummary = $"Fetching {y} / {x} @ {tf}…";

        try
        {
            const int n = 500;
            var taskY = _marketDataService.GetCandlesAsync(y, n, "Crypto", tf);
            var taskX = _marketDataService.GetCandlesAsync(x, n, "Crypto", tf);
            await Task.WhenAll(taskY, taskX).ConfigureAwait(true);
            var yCandles = taskY.Result;
            var xCandles = taskX.Result;
            if (yCandles is null || xCandles is null || yCandles.Count < 40 || xCandles.Count < 40)
            {
                QuantLabPairsSummary = $"Not enough candles ({yCandles?.Count ?? 0}/{xCandles?.Count ?? 0}). Need ≥40 each.";
                return;
            }
            var pairCount = Math.Min(yCandles.Count, xCandles.Count);
            var ySeries = yCandles.TakeLast(pairCount).Select(c => c.Close).ToList();
            var xSeries = xCandles.TakeLast(pairCount).Select(c => c.Close).ToList();

            var result = EngleGrangerDetector.TestCointegration(ySeries, xSeries);

            // Reconstruct spread series from hedge ratio for charting.
            var beta = (double)result.HedgeRatio;
            var spread = new List<double>(pairCount);
            for (int i = 0; i < pairCount; i++)
            {
                spread.Add((double)ySeries[i] - beta * (double)xSeries[i]);
            }
            var mean = (double)result.SpreadAverage;
            var std = (double)result.SpreadStdDev;
            var upper = mean + 2d * std;
            var lower = mean - 2d * std;

            const double chartW = 520d;
            const double chartH = 140d;
            double yMin = Math.Min(spread.Min(), lower);
            double yMax = Math.Max(spread.Max(), upper);
            var yspan = Math.Max(yMax - yMin, 1e-9);

            double ToY(double v) => chartH - (v - yMin) / yspan * chartH;

            var spreadPts = new PointCollection();
            for (int i = 0; i < spread.Count; i++)
            {
                var xx = chartW * i / Math.Max(1, spread.Count - 1);
                spreadPts.Add(new Point(xx, ToY(spread[i])));
            }
            QuantLabPairsSpreadPoints = spreadPts;
            QuantLabPairsUpperBandPoints = new PointCollection { new Point(0, ToY(upper)), new Point(chartW, ToY(upper)) };
            QuantLabPairsLowerBandPoints = new PointCollection { new Point(0, ToY(lower)), new Point(chartW, ToY(lower)) };
            QuantLabPairsMeanLinePoints = new PointCollection { new Point(0, ToY(mean)), new Point(chartW, ToY(mean)) };

            QuantLabPairsCointegratedBadge = result.IsCointegrated ? "✓ Cointegrated" : "✗ Not Cointegrated";
            QuantLabPairsBetaText = $"β {result.HedgeRatio:F4}";
            QuantLabPairsHurstText = $"Hurst {result.HurstExponent:F2}";
            QuantLabPairsHalfLifeText = double.IsFinite(result.HalfLife)
                ? $"HL {result.HalfLife:F1} bars"
                : "HL ∞";
            QuantLabPairsAdfText = $"ADF t {result.AdfTStatistic:F2}";
            QuantLabPairsCorrelationText = $"ρ {result.Correlation:F2}";
            QuantLabPairsSummary =
                $"{y}/{x} · {tf} · n={pairCount} · spread μ={mean:F4} σ={std:F4} · " +
                $"OU(κ={result.OuKappa:F3}, θ={result.OuTheta:F3}, σ={result.OuSigma:F3})";
        }
        catch (Exception ex)
        {
            QuantLabPairsSummary = $"Pair analysis failed: {ex.Message}";
        }
        finally
        {
            IsQuantLabPairsBusy = false;
        }
    }

    // --------------------------------------------------------------------------------
    //                   PERFORMANCE STABILITY FOLDS (Sharpe-decay detector)
    // --------------------------------------------------------------------------------

    private void PopulatePerformanceStabilityFolds(
        IReadOnlyList<Trade> trades,
        IReadOnlyList<double> returnsPct,
        StrategyEvidenceVisualMode effectiveMode)
    {
        QuantLabPerformanceFolds.Clear();

        if (trades.Count < 24 || returnsPct.Count < 24)
        {
            QuantLabFoldStabilitySummary = $"Fold analysis needs ≥24 closed trades (have {trades.Count}).";
            return;
        }

        const int folds = 6;
        var foldSize = returnsPct.Count / folds;
        if (foldSize < 4) { QuantLabFoldStabilitySummary = "Fold size too small (<4 trades)."; return; }

        var accentSharpe = FreezeBrush(0x38, 0xBD, 0xF8);
        var accentWin = FreezeBrush(0xA7, 0x8B, 0xFA);

        var sharpes = new List<double>(folds);
        var winRates = new List<double>(folds);
        var counts = new List<int>(folds);
        for (int f = 0; f < folds; f++)
        {
            var start = f * foldSize;
            var end = (f == folds - 1) ? returnsPct.Count : start + foldSize;
            var slice = new List<double>();
            for (int j = start; j < end; j++) slice.Add(returnsPct[j]);
            if (slice.Count == 0) continue;
            var mean = slice.Average();
            var variance = slice.Sum(v => (v - mean) * (v - mean)) / slice.Count;
            var std = variance > 0d ? Math.Sqrt(variance) : 0d;
            var sharpe = std > 1e-9d ? mean / std : 0d;
            var winRate = slice.Count(v => v > 0d) / (double)slice.Count;
            sharpes.Add(sharpe);
            winRates.Add(winRate);
            counts.Add(slice.Count);
        }

        if (sharpes.Count == 0) return;
        var maxAbsSharpe = Math.Max(1e-9, sharpes.Max(Math.Abs));
        const double barMax = 72d;

        for (int i = 0; i < sharpes.Count; i++)
        {
            var shBar = Math.Abs(sharpes[i]) / maxAbsSharpe * barMax;
            var wrBar = winRates[i] * barMax;
            var shBrush = sharpes[i] >= 0d ? accentSharpe : FreezeBrush(0xF7, 0x5F, 0x68);
            var lab = $"F{i + 1}";
            QuantLabPerformanceFolds.Add(new QuantLabFoldBarViewModel(
                lab, shBar, wrBar, sharpes[i], winRates[i], counts[i], shBrush, accentWin));
        }

        // Decay signal: linear regression slope of fold Sharpe vs fold index.
        var x = Enumerable.Range(0, sharpes.Count).Select(i => (double)i).ToArray();
        var yMean = sharpes.Average();
        var xMean = x.Average();
        double num = 0d, den = 0d;
        for (int i = 0; i < sharpes.Count; i++)
        {
            num += (x[i] - xMean) * (sharpes[i] - yMean);
            den += (x[i] - xMean) * (x[i] - xMean);
        }
        var slope = den > 1e-9 ? num / den : 0d;
        var decayTag = slope < -0.1 ? "⚠ DECAYING" : slope > 0.1 ? "✓ IMPROVING" : "≈ STABLE";
        var min = sharpes.Min();
        var max = sharpes.Max();
        QuantLabFoldStabilitySummary =
            $"{sharpes.Count} chronological folds · {decayTag} · slope {slope:+0.00;-0.00;0.00} · range [{min:F2}, {max:F2}]";
    }

    // --------------------------------------------------------------------------------
    //                   DEFLATED + PROBABILISTIC SHARPE RATIO
    //                   (Bailey & López de Prado 2012–2014)
    // --------------------------------------------------------------------------------

    private void PopulateDeflatedAndProbabilisticSharpe(IReadOnlyList<double> returnsPct)
    {
        QuantLabDeflatedSharpeText = "—";
        QuantLabProbabilisticSharpeText = "—";
        QuantLabSharpeMomentsText = "skew/kurt pending";

        if (returnsPct.Count < 30) return;

        int n = returnsPct.Count;
        double mean = returnsPct.Average();
        double m2 = 0d, m3 = 0d, m4 = 0d;
        foreach (var r in returnsPct)
        {
            var d = r - mean;
            m2 += d * d;
            m3 += d * d * d;
            m4 += d * d * d * d;
        }
        m2 /= n; m3 /= n; m4 /= n;
        if (m2 <= 1e-12) return;

        double std = Math.Sqrt(m2);
        double sharpe = mean / std;
        double skew = m3 / Math.Pow(m2, 1.5);
        double kurt = m4 / (m2 * m2); // raw (3 for normal) — we use excess = kurt - 3 below

        // Probabilistic Sharpe Ratio: probability that true SR > SR_threshold.
        //  PSR(SR*) = Φ( (SR_obs - SR*) · √(n-1) / √(1 - skew·SR_obs + (kurt-1)/4 · SR_obs²) )
        double Psr(double threshold)
        {
            var variance = 1d - skew * sharpe + (kurt - 1d) / 4d * sharpe * sharpe;
            if (variance <= 1e-12) return double.NaN;
            var z = (sharpe - threshold) * Math.Sqrt(n - 1) / Math.Sqrt(variance);
            return NormalCdf(z);
        }

        // Deflated Sharpe Ratio — assumes N≈50 independent strategy trials
        // (typical exploratory research). Use Bailey & López de Prado 2014
        // expected max Sharpe under null:
        //   E[maxSR] ≈ √(2 ln N) − (γ + ln ln N) / √(2 ln N)
        // where γ ≈ 0.5772 (Euler-Mascheroni).
        const int trials = 50;
        const double euler = 0.5772156649d;
        double lnN = Math.Log(trials);
        double expectedMaxSr = Math.Sqrt(2d * lnN) - (euler + Math.Log(lnN)) / Math.Sqrt(2d * lnN);
        double psrVsThreshold = Psr(expectedMaxSr);
        double psrVsZero = Psr(0d);

        QuantLabProbabilisticSharpeText = double.IsNaN(psrVsZero) ? "—" : $"{psrVsZero:P1} (SR > 0)";
        QuantLabDeflatedSharpeText = double.IsNaN(psrVsThreshold)
            ? "—"
            : $"{psrVsThreshold:P1} (N={trials}, E[maxSR]={expectedMaxSr:F2})";
        QuantLabSharpeMomentsText =
            $"SR {sharpe:F2} · skew {skew:F2} · kurt {kurt:F2} · n={n}";
    }

    private static double NormalCdf(double z)
    {
        // Abramowitz & Stegun 7.1.26 approximation.
        double t = 1d / (1d + 0.2316419 * Math.Abs(z));
        double poly = t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
        double pdf = Math.Exp(-0.5 * z * z) / Math.Sqrt(2d * Math.PI);
        double cdf = 1d - pdf * poly;
        return z < 0 ? 1d - cdf : cdf;
    }

    // --------------------------------------------------------------------------------
    //                   STRATEGY CORRELATION HEATMAP
    // --------------------------------------------------------------------------------

    private void PopulateStrategyCorrelationHeatmap()
    {
        QuantLabCorrelationRows.Clear();

        var strategyDaily = new Dictionary<string, Dictionary<DateTime, decimal>>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in _tradeCache.Where(x => x.Status == TradeStatus.Executed && x.IsClosed && x.ExitTime.HasValue))
        {
            var tag = string.IsNullOrWhiteSpace(t.StrategyTag) ? "—" : t.StrategyTag!.Trim();
            var day = t.ExitTime!.Value.Date;
            if (!strategyDaily.TryGetValue(tag, out var bucket))
            {
                bucket = new Dictionary<DateTime, decimal>();
                strategyDaily[tag] = bucket;
            }
            bucket.TryGetValue(day, out var existing);
            bucket[day] = existing + CalculateTradePnL(t);
        }

        var strategies = strategyDaily
            .Where(kv => kv.Value.Count >= 3)
            .OrderByDescending(kv => kv.Value.Count)
            .Take(8)
            .Select(kv => kv.Key)
            .ToList();

        if (strategies.Count < 2)
        {
            QuantLabCorrelationSummary = $"Need ≥2 strategies with ≥3 overlapping days (have {strategies.Count}).";
            return;
        }

        // Build series over intersection of trading days so each pair sees identical timeline.
        var commonDays = strategyDaily[strategies[0]].Keys.ToHashSet();
        foreach (var s in strategies.Skip(1)) commonDays.IntersectWith(strategyDaily[s].Keys);
        if (commonDays.Count < 3)
        {
            QuantLabCorrelationSummary = $"Overlap too small ({commonDays.Count} common day(s)).";
            return;
        }
        var dayList = commonDays.OrderBy(d => d).ToList();

        var seriesByStrategy = new Dictionary<string, double[]>();
        foreach (var s in strategies)
        {
            var arr = new double[dayList.Count];
            for (int i = 0; i < dayList.Count; i++)
                arr[i] = (double)strategyDaily[s][dayList[i]];
            seriesByStrategy[s] = arr;
        }

        for (int i = 0; i < strategies.Count; i++)
        {
            var cells = new List<QuantLabHeatmapCellViewModel>(strategies.Count);
            for (int j = 0; j < strategies.Count; j++)
            {
                var rho = PearsonCorrelation(seriesByStrategy[strategies[i]], seriesByStrategy[strategies[j]]);
                var brush = CorrelationBrush(rho);
                var label = i == j ? "1.00" : rho.ToString("F2");
                var tip = $"{strategies[i]} × {strategies[j]} · ρ {rho:F3} · n={dayList.Count}";
                cells.Add(new QuantLabHeatmapCellViewModel(label, rho, brush, tip));
            }
            var rowLabel = strategies[i].Length > 14 ? strategies[i][..14] : strategies[i];
            QuantLabCorrelationRows.Add(new QuantLabHeatmapRowViewModel(rowLabel, cells));
        }

        QuantLabCorrelationSummary =
            $"{strategies.Count} strategies · {dayList.Count} overlapping days · daily-PnL Pearson ρ (green ≈ 0, red ≈ ±1 — diversification poor)";
    }

    private static double PearsonCorrelation(double[] a, double[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0d;
        double meanA = a.Average(), meanB = b.Average();
        double num = 0d, dA = 0d, dB = 0d;
        for (int i = 0; i < a.Length; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            num += da * db; dA += da * da; dB += db * db;
        }
        var den = Math.Sqrt(dA * dB);
        return den > 1e-12 ? num / den : 0d;
    }

    private static Brush CorrelationBrush(double rho)
    {
        // Green at |ρ|≈0 (well-diversified) → amber at 0.5 → red at ±1.
        var abs = Math.Min(1d, Math.Abs(rho));
        byte r = (byte)(48 + 200 * abs);
        byte g = (byte)(200 - 150 * abs);
        byte b = (byte)(80);
        return FreezeBrush(r, g, b);
    }

    // --------------------------------------------------------------------------------
    //                   TCA REPORT (Transaction Cost Analysis)
    // --------------------------------------------------------------------------------

    private void PopulateTcaReport(IReadOnlyList<Trade> trades)
    {
        QuantLabTcaSummary = "TCA needs closed trades with slippage/fee data.";
        QuantLabTcaSlippageText = "—";
        QuantLabTcaFeesPctOfEdgeText = "—";
        QuantLabTcaMakerRatioText = "—";
        QuantLabTcaLatencyText = "—";
        QuantLabTcaFeesPerTradeText = "—";
        QuantLabTcaTotalFeesText = "—";

        if (trades.Count == 0) return;

        // Slippage (EntrySlippagePercent) — only from trades that logged it (>0 means measured).
        var slippageSamples = trades
            .Where(t => t.EntrySlippagePercent != 0m)
            .Select(t => Math.Abs((double)t.EntrySlippagePercent))
            .OrderBy(x => x).ToList();
        string slippageLine = "no samples";
        if (slippageSamples.Count > 0)
        {
            var medS = slippageSamples[slippageSamples.Count / 2];
            var p90S = slippageSamples[Math.Clamp((int)(slippageSamples.Count * 0.9), 0, slippageSamples.Count - 1)];
            var maxS = slippageSamples[^1];
            slippageLine = $"med {medS:F3}% · P90 {p90S:F3}% · max {maxS:F3}% (n={slippageSamples.Count})";
        }
        QuantLabTcaSlippageText = slippageLine;

        // Fees — total + per-trade stats; also fees as % of gross PnL (edge leakage).
        var feeSamples = trades.Where(t => t.TotalFeesPaid > 0m).Select(t => (double)t.TotalFeesPaid).ToList();
        decimal totalFees = trades.Sum(t => t.TotalFeesPaid);
        decimal grossAbsPnl = trades.Select(CalculateTradePnL).Sum(p => Math.Abs(p));
        var feesPctOfEdge = grossAbsPnl > 0m ? (double)(totalFees / grossAbsPnl) * 100d : 0d;
        QuantLabTcaTotalFeesText = $"{totalFees:N2}";
        QuantLabTcaFeesPerTradeText = feeSamples.Count > 0
            ? $"avg {feeSamples.Average():F3} · med {feeSamples.OrderBy(x => x).ElementAt(feeSamples.Count / 2):F3}"
            : "—";
        QuantLabTcaFeesPctOfEdgeText = grossAbsPnl > 0m
            ? $"{feesPctOfEdge:F2}% of |PnL|"
            : "—";

        // Maker / Taker inferred from slippage: near-zero = maker fill at limit, non-zero = taker crossing the spread.
        var makerCount = trades.Count(t => Math.Abs(t.EntrySlippagePercent) < 0.01m);
        var makerPct = trades.Count > 0 ? 100d * makerCount / trades.Count : 0d;
        QuantLabTcaMakerRatioText = $"~{makerPct:F0}% maker · ~{100d - makerPct:F0}% taker (from slippage)";

        // Latency p50/p90.
        var latencies = trades.Where(t => t.ExecutionLatencyMs > 0).Select(t => t.ExecutionLatencyMs).OrderBy(x => x).ToList();
        if (latencies.Count > 0)
        {
            var medL = latencies[latencies.Count / 2];
            var p90L = latencies[Math.Clamp((int)(latencies.Count * 0.9), 0, latencies.Count - 1)];
            QuantLabTcaLatencyText = $"med {medL}ms · P90 {p90L}ms (n={latencies.Count})";
        }
        else
        {
            QuantLabTcaLatencyText = "no latency samples";
        }

        var edgeHealth = feesPctOfEdge < 10d ? "✓ healthy"
            : feesPctOfEdge < 25d ? "⚠ borderline"
            : "🚨 edge-killer";
        QuantLabTcaSummary =
            $"{trades.Count} closed trades · fees leak {feesPctOfEdge:F1}% of gross |PnL| → {edgeHealth}. " +
            $"Paper-vs-live cost parity check: compare live slippage median to backtest assumption.";
    }

    // --------------------------------------------------------------------------------
    //                   P&L ATTRIBUTION TABLE (1h / 1d / 1w per strategy)
    // --------------------------------------------------------------------------------

    private void PopulateAttributionTable()
    {
        QuantLabAttributionRows.Clear();
        var now = DateTime.UtcNow;
        var cutoff1h = now.AddHours(-1);
        var cutoff1d = now.AddDays(-1);
        var cutoff1w = now.AddDays(-7);

        var greenBrush = FreezeBrush(0x3F, 0xB9, 0x50);
        var redBrush = FreezeBrush(0xF7, 0x5F, 0x68);
        var mutedBrush = FreezeBrush(0x94, 0xA3, 0xB8);

        Brush PickBrush(decimal v) => v > 0m ? greenBrush : v < 0m ? redBrush : mutedBrush;

        var byStrategy = _tradeCache
            .Where(t => t.Status == TradeStatus.Executed && t.IsClosed && t.ExitTime.HasValue)
            .GroupBy(t => string.IsNullOrWhiteSpace(t.StrategyTag) ? "—" : t.StrategyTag!.Trim())
            .OrderByDescending(g => g.Sum(CalculateTradePnL))
            .Take(12)
            .ToList();

        if (byStrategy.Count == 0)
        {
            QuantLabAttributionSummary = "No closed trades yet — attribution will populate after first paper/live fill.";
            return;
        }

        decimal totalWeek = 0m;
        foreach (var g in byStrategy)
        {
            var pnl1h = g.Where(t => t.ExitTime!.Value >= cutoff1h).Sum(CalculateTradePnL);
            var pnl1d = g.Where(t => t.ExitTime!.Value >= cutoff1d).Sum(CalculateTradePnL);
            var pnl1w = g.Where(t => t.ExitTime!.Value >= cutoff1w).Sum(CalculateTradePnL);
            totalWeek += pnl1w;
            QuantLabAttributionRows.Add(new QuantLabAttributionRowViewModel(
                g.Key, pnl1h, pnl1d, pnl1w, g.Count(),
                PickBrush(pnl1h), PickBrush(pnl1d), PickBrush(pnl1w)));
        }

        QuantLabAttributionSummary =
            $"{byStrategy.Count} strategies · total 1w PnL {totalWeek:N2} · sorted by all-time PnL";
    }
}
