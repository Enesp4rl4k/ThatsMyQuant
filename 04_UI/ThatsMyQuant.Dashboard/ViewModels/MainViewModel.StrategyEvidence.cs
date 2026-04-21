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
}
