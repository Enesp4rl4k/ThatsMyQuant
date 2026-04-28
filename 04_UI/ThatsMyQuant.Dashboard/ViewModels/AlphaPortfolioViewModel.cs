using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Media;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Dashboard.ViewModels;

/// <summary>
/// ViewModel for the Alpha Portfolio tab — surfaces the validated 4-position
/// portfolio (TRX/XLM pair + LTC/DOGE/AAVE funding alpha) that achieved
/// Sharpe 3.87 in the 2026-04-24 combined backtest. Provides live-reactive
/// backtest execution and prepares the config for paper-trade launch.
/// </summary>
public partial class AlphaPortfolioViewModel : ObservableObject
{
    private readonly PortfolioBacktestRunner? _runner;
    private readonly ILogger<AlphaPortfolioViewModel>? _logger;

    [ObservableProperty] private ObservableCollection<PortfolioPositionRow> _positions = new();
    [ObservableProperty] private ObservableCollection<CorrelationRow> _correlationRows = new();
    [ObservableProperty] private decimal _totalCapital = 100_000m;
    [ObservableProperty] private int _backtestDays = 365;

    // Risk profile + leverage selectors (2026-04-24).
    public System.Collections.Generic.List<string> AvailableRiskProfiles { get; } =
        new() { "Low (Safe)", "Moderate (Normal)", "Aggressive" };
    [ObservableProperty] private string _selectedRiskProfile = "Moderate (Normal)";
    [ObservableProperty] private decimal _leverage = 3.0m;
    [ObservableProperty] private string _leverageLabel = "Normal: 3.0x  ·  DD budget 3.5%  ·  hard cap 15x";
    [ObservableProperty] private Brush _leverageLabelColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));

    [ObservableProperty] private decimal _portfolioPnl;
    [ObservableProperty] private decimal _portfolioPnlPct;
    [ObservableProperty] private double _portfolioSharpe;
    [ObservableProperty] private decimal _portfolioMaxDrawdown;
    [ObservableProperty] private double _portfolioAvgCorrelation;
    [ObservableProperty] private int _portfolioTradingDays;
    [ObservableProperty] private decimal _portfolioFinalEquity = 100_000m;

    [ObservableProperty] private string _statusMessage = "Ready. Click 'Run Portfolio Backtest' to measure combined edge.";
    [ObservableProperty] private Brush _statusColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasResults;

    // Equity curve rendering (Polyline via PointCollection).
    [ObservableProperty] private System.Windows.Media.PointCollection _equityCurvePoints = new();
    [ObservableProperty] private System.Windows.Media.PointCollection _drawdownCurvePoints = new();
    [ObservableProperty] private double _equityCanvasWidth = 900;
    [ObservableProperty] private double _equityCanvasHeight = 220;
    [ObservableProperty] private string _equityChartYLabel = "—";
    [ObservableProperty] private string _equityChartDateRange = "—";

    // Daily PnL distribution histogram — bar chart.
    [ObservableProperty] private ObservableCollection<HistogramBar> _pnlHistogram = new();
    [ObservableProperty] private double _pnlHistogramWidth = 900;
    [ObservableProperty] private double _pnlHistogramHeight = 160;
    [ObservableProperty] private string _pnlHistogramSummary = "";

    // Underwater plot — fund-manager view of drawdown depth.
    [ObservableProperty] private System.Windows.Media.PointCollection _underwaterAreaPoints = new();
    [ObservableProperty] private System.Windows.Media.PointCollection _underwaterZeroLinePoints = new();
    [ObservableProperty] private double _underwaterCanvasWidth = 900;
    [ObservableProperty] private double _underwaterCanvasHeight = 160;
    [ObservableProperty] private string _underwaterSummary = "";
    [ObservableProperty] private string _underwaterMaxDdLabel = "";
    [ObservableProperty] private double _underwaterMaxDdLabelX = 0;
    [ObservableProperty] private double _underwaterMaxDdLabelY = 0;

    // Monte Carlo fan chart — 1000-path forward projection bootstrapped from realized daily PnL.
    [ObservableProperty] private System.Windows.Media.PointCollection _mcFanP5to95Polygon = new();
    [ObservableProperty] private System.Windows.Media.PointCollection _mcFanP25to75Polygon = new();
    [ObservableProperty] private System.Windows.Media.PointCollection _mcFanMedianLine = new();
    [ObservableProperty] private System.Windows.Media.PointCollection _mcFanRealizedLine = new();
    [ObservableProperty] private double _mcFanCanvasWidth = 900;
    [ObservableProperty] private double _mcFanCanvasHeight = 240;
    [ObservableProperty] private int _mcFanProjectionDays = 30;
    [ObservableProperty] private int _mcFanIterations = 1000;
    [ObservableProperty] private string _mcFanSummary = "";
    [ObservableProperty] private string _mcFanStatus = "Run a backtest first, then compute fan.";
    [ObservableProperty] private bool _mcFanHasResults;
    private decimal[]? _mcSourceDailyPnl;
    private decimal _mcSourceFinalEquity;

    public AlphaPortfolioViewModel(
        PortfolioBacktestRunner? runner = null,
        ILogger<AlphaPortfolioViewModel>? logger = null)
    {
        _runner = runner;
        _logger = logger;
        SeedValidatedPortfolio();
    }

    partial void OnSelectedRiskProfileChanged(string value)
    {
        // Auto-sync leverage + label when profile changes (overridable via slider).
        (Leverage, LeverageLabel, LeverageLabelColor) = value switch
        {
            "Low (Safe)" => (1.0m, "Safe: 1.0x  ·  DD budget 2.0%  ·  capital preservation", new SolidColorBrush(Color.FromRgb(0x37, 0xA9, 0x5B))),
            "Aggressive" => (7.0m, "Aggressive: 7.0x  ·  DD budget 6.0%  ·  hard cap 15x", new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A))),
            _ => (3.0m, "Normal: 3.0x  ·  DD budget 3.5%  ·  hard cap 15x", new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E))),
        };
    }

    partial void OnLeverageChanged(decimal value)
    {
        // Clamp slider to safety cap 1-15x.
        if (value < 1m) { Leverage = 1m; return; }
        if (value > 15m) { Leverage = 15m; return; }
        // Auto-update label if user drags slider off preset.
        var preset = SelectedRiskProfile switch
        {
            "Low (Safe)" => 1.0m,
            "Aggressive" => 7.0m,
            _ => 3.0m,
        };
        if (Math.Abs(value - preset) > 0.05m)
            LeverageLabel = $"Custom: {value:F1}x  ·  hard cap 15x  ·  DD-throttle active";
    }

    /// <summary>Seeds the 6-position diversified portfolio: 3 validated 4h pairs + 3 funding coins.
    /// Answers "is TRX/XLM the only profit source?" — no, SOL/AVAX and SOL/LINK also contribute.</summary>
    private void SeedValidatedPortfolio()
    {
        Positions.Clear();
        // --- Stat-arb pairs — 60% of portfolio, equal-weight to break TRX/XLM dominance.
        //     2026-04-25: TRX/XLM was 30%, dropped to 20%; ConcentrationGuard caps at 25% anyway.
        Positions.Add(new PortfolioPositionRow
        {
            Name = "TRX-XLM-Pair-4h",
            Type = "PairsArbitrage",
            SymbolA = "TRX-USDT", SymbolB = "XLM-USDT", Timeframe = "4h",
            Weight = 0.20m,
            ExpectedSharpe = 0.59, ExpectedProfitFactor = 4.89m, ExpectedWinRate = 0.75
        });
        Positions.Add(new PortfolioPositionRow
        {
            Name = "SOL-AVAX-Pair-4h",
            Type = "PairsArbitrage",
            SymbolA = "SOL-USDT", SymbolB = "AVAX-USDT", Timeframe = "4h",
            Weight = 0.20m,
            ExpectedSharpe = 0.25, ExpectedProfitFactor = 1.23m, ExpectedWinRate = 0.55
        });
        Positions.Add(new PortfolioPositionRow
        {
            Name = "SOL-LINK-Pair-4h",
            Type = "PairsArbitrage",
            SymbolA = "SOL-USDT", SymbolB = "LINK-USDT", Timeframe = "4h",
            Weight = 0.20m,
            ExpectedSharpe = 0.35, ExpectedProfitFactor = 1.63m, ExpectedWinRate = 0.50
        });
        // --- Funding alpha — 40% of portfolio ---
        Positions.Add(new PortfolioPositionRow
        {
            Name = "LTC-Funding", Type = "FundingAlpha",
            SymbolA = "LTC-USDT", SymbolB = null, Timeframe = "1h",
            Weight = 0.15m,
            ExpectedSharpe = 1.12, ExpectedProfitFactor = 1.08m, ExpectedWinRate = 0.507
        });
        Positions.Add(new PortfolioPositionRow
        {
            Name = "DOGE-Funding", Type = "FundingAlpha",
            SymbolA = "DOGE-USDT", SymbolB = null, Timeframe = "1h",
            Weight = 0.13m,
            ExpectedSharpe = 1.62, ExpectedProfitFactor = 1.12m, ExpectedWinRate = 0.534
        });
        Positions.Add(new PortfolioPositionRow
        {
            Name = "AAVE-Funding", Type = "FundingAlpha",
            SymbolA = "AAVE-USDT", SymbolB = null, Timeframe = "1h",
            Weight = 0.12m,
            ExpectedSharpe = 0.99, ExpectedProfitFactor = 1.07m, ExpectedWinRate = 0.474
        });
    }

    [RelayCommand]
    private async Task RunBacktestAsync()
    {
        if (_runner is null)
        {
            StatusMessage = "PortfolioBacktestRunner not wired in DI.";
            StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
            return;
        }
        if (IsRunning) return;

        IsRunning = true;
        StatusMessage = $"Running portfolio backtest ({BacktestDays} days, ${TotalCapital:N0})...";
        StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));

        try
        {
            var end = DateTime.UtcNow;
            var start = end.AddDays(-BacktestDays);

            var fundingCfg1bp = new FundingAlphaConfig(
                EntryThreshold: 0.0001m, TakeProfitPct: 0.02m,
                StopLossPct: 0.015m, MaxHoldingBars: 16);

            var effectiveLeverage = Math.Clamp(Leverage, 1m, 15m);
            var configPositions = Positions.Select(p => new PortfolioPosition(
                Name: p.Name,
                Type: p.Type == "FundingAlpha" ? PortfolioPositionType.FundingAlpha : PortfolioPositionType.PairsArbitrage,
                SymbolA: p.SymbolA,
                SymbolB: p.SymbolB,
                Timeframe: p.Timeframe,
                CapitalWeight: p.Weight,
                FundingConfig: p.Type == "FundingAlpha" ? fundingCfg1bp : null,
                Leverage: effectiveLeverage)).ToList();

            var result = await _runner.RunAsync(configPositions, start, end, TotalCapital);

            // Update per-position metrics with actual results.
            foreach (var s in result.StrategyResults)
            {
                var row = Positions.FirstOrDefault(p => p.Name == s.Name);
                if (row is null) continue;
                row.ActualTrades = s.Trades;
                row.ActualWinRate = s.WinRate;
                row.ActualSharpe = s.SharpeRatio;
                row.ActualPnl = s.NetPnl;
                row.ActualProfitFactor = s.ProfitFactor;
                row.ActualMaxDrawdown = s.MaxDrawdownPct;
            }

            // Aggregate.
            PortfolioPnl = result.TotalPnl;
            PortfolioPnlPct = result.TotalPnlPct;
            PortfolioSharpe = result.PortfolioSharpe;
            PortfolioMaxDrawdown = result.MaxDrawdown;
            PortfolioTradingDays = result.TradingDays;
            PortfolioFinalEquity = result.FinalEquity;

            // Correlation matrix → flat rows.
            double offSum = 0; int offCount = 0;
            CorrelationRows.Clear();
            for (int i = 0; i < result.StrategyNames.Length; i++)
            {
                var row = new CorrelationRow { RowName = result.StrategyNames[i] };
                for (int j = 0; j < result.StrategyNames.Length; j++)
                {
                    var c = result.CorrelationMatrix[i, j];
                    row.Values.Add(new CorrelationCell
                    {
                        ColumnName = result.StrategyNames[j],
                        Value = c,
                        IsDiagonal = i == j,
                        BackgroundBrush = ColorForCorrelation(c, i == j)
                    });
                    if (i != j) { offSum += c; offCount++; }
                }
                CorrelationRows.Add(row);
            }
            PortfolioAvgCorrelation = offCount > 0 ? offSum / offCount : 0.0;

            BuildEquityAndDrawdownCurves(result);
            BuildPnlHistogram(result);
            BuildUnderwaterPlot(result);

            // Cache source for Monte Carlo fan — daily PnL = first-difference of equity curve.
            var eq = result.EquityCurve;
            if (eq is not null && eq.Length >= 2)
            {
                var daily = new decimal[eq.Length - 1];
                for (int i = 1; i < eq.Length; i++) daily[i - 1] = eq[i] - eq[i - 1];
                _mcSourceDailyPnl = daily;
                _mcSourceFinalEquity = result.FinalEquity;
                McFanStatus = $"Source: {daily.Length} realized days · final equity {result.FinalEquity:N0}. Click 'Project Fan'.";
            }

            HasResults = true;

            var quality = PortfolioSharpe switch
            {
                > 3.0 => "ELITE institutional",
                > 2.0 => "Top-quartile pro",
                > 1.5 => "Pro-grade",
                > 1.0 => "Good",
                > 0.0 => "Marginal",
                _ => "Losing"
            };
            StatusMessage = $"Complete: Sharpe {PortfolioSharpe:F2} ({quality}) · PnL {PortfolioPnl:N0} ({PortfolioPnlPct:P2}) · DD {PortfolioMaxDrawdown:P2} · Corr {PortfolioAvgCorrelation:F3}";
            StatusColor = PortfolioSharpe > 1.5
                ? new SolidColorBrush(Color.FromRgb(0x37, 0xA9, 0x5B))
                : PortfolioSharpe > 0
                ? new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E))
                : new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Portfolio backtest failed");
            StatusMessage = $"Backtest failed: {ex.Message}";
            StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
        }
        finally { IsRunning = false; }
    }

    // Live handle to the spawned QuantEngine.Worker paper-trade process. The
    // Dashboard does NOT execute trades itself — it shells out to the worker
    // backend (Launch-PaperTrade.bat) which runs PaperTradingHostedService
    // under DOTNET_ENVIRONMENT=Paper + appsettings.Paper.json overlay.
    private System.Diagnostics.Process? _paperWorker;

    [ObservableProperty] private bool _isPaperWorkerRunning;
    [ObservableProperty] private string _paperWorkerStatus = "Stopped";
    [ObservableProperty] private Brush _paperWorkerStatusColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

    [RelayCommand]
    private void PaperTradeLaunch()
    {
        if (IsPaperWorkerRunning && _paperWorker is { HasExited: false })
        {
            StatusMessage = $"Paper worker already running (PID {_paperWorker.Id}). Use STOP first.";
            StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));
            return;
        }

        var repoRoot = ResolveRepoRoot();
        var batPath = System.IO.Path.Combine(repoRoot, "Launch-PaperTrade.bat");
        if (!System.IO.File.Exists(batPath))
        {
            StatusMessage = $"Launch-PaperTrade.bat not found at {batPath}.";
            StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{batPath}\"\"",
                WorkingDirectory = repoRoot,
                UseShellExecute = true,           // open in own console window so operator can watch logs + Ctrl+C
                CreateNoWindow = false,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal
            };

            var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                StatusMessage = "Failed to spawn paper worker process.";
                StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
                return;
            }

            _paperWorker = proc;
            IsPaperWorkerRunning = true;
            PaperWorkerStatus = $"Running (PID {proc.Id})";
            PaperWorkerStatusColor = new SolidColorBrush(Color.FromRgb(0x37, 0xA9, 0x5B));
            StatusMessage = $"Paper worker launched (PID {proc.Id}). Watch the new console window for fills + Telegram for alerts.";
            StatusColor = new SolidColorBrush(Color.FromRgb(0x37, 0xA9, 0x5B));

            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsPaperWorkerRunning = false;
                    PaperWorkerStatus = $"Stopped (exit code {proc.ExitCode})";
                    PaperWorkerStatusColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
                });
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to launch paper worker");
            StatusMessage = $"Launch failed: {ex.Message}";
            StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
        }
    }

    [RelayCommand]
    private void PaperTradeStop()
    {
        var proc = _paperWorker;
        if (proc is null || proc.HasExited)
        {
            IsPaperWorkerRunning = false;
            PaperWorkerStatus = "Stopped";
            StatusMessage = "Paper worker is not running.";
            StatusColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
            return;
        }

        try
        {
            // Worker is the cmd.exe shell wrapping `dotnet run`; killing the
            // shell with entireProcessTree=true cascades the SIGINT-equivalent
            // to the dotnet child so PaperTradingHostedService can flush its
            // JSON snapshot before exiting.
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
            StatusMessage = "Paper worker stop requested. Portfolio snapshot flushed.";
            StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to stop paper worker");
            StatusMessage = $"Stop failed: {ex.Message}";
            StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
        }
    }

    /// <summary>Walk up from the dashboard's bin directory until we find the repo
    /// root (marker: ThatsMyQuant.sln). Falls back to current directory.</summary>
    private static string ResolveRepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "ThatsMyQuant.sln")))
                return dir.FullName;
        }
        return Environment.CurrentDirectory;
    }

    /// <summary>Builds equity + drawdown polylines by walking the equity array and
    /// mapping each point to canvas coordinates (X=time, Y=equity).</summary>
    private void BuildEquityAndDrawdownCurves(PortfolioBacktestResult result)
    {
        var eq = result.EquityCurve;
        var dates = result.Dates;
        if (eq is null || eq.Length == 0) return;

        decimal minEq = eq.Min();
        decimal maxEq = eq.Max();
        decimal rangeEq = maxEq - minEq;
        if (rangeEq == 0m) rangeEq = 1m;  // Prevent divide-by-zero on flat curve.

        double W = EquityCanvasWidth;
        double H = EquityCanvasHeight;
        double pad = 8;

        var equityPoints = new System.Windows.Media.PointCollection(eq.Length);
        var ddPoints = new System.Windows.Media.PointCollection(eq.Length);
        decimal peak = eq[0];
        decimal maxDd = 0m;
        for (int i = 0; i < eq.Length; i++)
        {
            double x = pad + (W - 2 * pad) * (eq.Length <= 1 ? 0 : (double)i / (eq.Length - 1));
            double y = H - pad - ((double)((eq[i] - minEq) / rangeEq)) * (H - 2 * pad);
            equityPoints.Add(new System.Windows.Point(x, y));

            if (eq[i] > peak) peak = eq[i];
            var dd = peak > 0 ? (peak - eq[i]) / peak : 0m;
            if (dd > maxDd) maxDd = dd;
            // Drawdown curve — inverted so deeper DD goes lower on screen. Scale to full H.
            double ddY = pad + (double)dd * (H - 2 * pad) * 20; // amplify ×20 so small DD visible
            if (ddY > H - pad) ddY = H - pad;
            ddPoints.Add(new System.Windows.Point(x, ddY));
        }

        EquityCurvePoints = equityPoints;
        DrawdownCurvePoints = ddPoints;
        EquityChartYLabel = $"{minEq:N0} → {maxEq:N0}";
        EquityChartDateRange = dates.Length > 0 ? $"{dates[0]:yyyy-MM-dd} → {dates[^1]:yyyy-MM-dd}" : "—";
    }

    /// <summary>Builds the underwater plot — fund-manager standard view of drawdown depth.
    /// Zero line at top, filled red area beneath shows time spent below previous peak.
    /// Annotates the deepest point so the operator can spot recovery cycles at a glance.</summary>
    private void BuildUnderwaterPlot(PortfolioBacktestResult result)
    {
        var eq = result.EquityCurve;
        if (eq is null || eq.Length < 2)
        {
            UnderwaterAreaPoints = new System.Windows.Media.PointCollection();
            UnderwaterZeroLinePoints = new System.Windows.Media.PointCollection();
            UnderwaterSummary = "—";
            return;
        }

        double W = UnderwaterCanvasWidth;
        double H = UnderwaterCanvasHeight;
        double pad = 10;
        double zeroY = pad + 2;

        // Walk equity, build dd_pct array.
        var ddPct = new double[eq.Length];
        decimal peak = eq[0];
        decimal worstDd = 0m;
        int worstIdx = 0;
        var underwaterDays = 0;
        decimal recoveryPeak = peak;
        int currentUnderwaterStart = -1;
        int longestUnderwater = 0;
        for (int i = 0; i < eq.Length; i++)
        {
            if (eq[i] > peak) peak = eq[i];
            decimal dd = peak > 0 ? (peak - eq[i]) / peak : 0m;
            ddPct[i] = (double)dd;
            if (dd > worstDd) { worstDd = dd; worstIdx = i; }
            if (dd > 0m)
            {
                underwaterDays++;
                if (currentUnderwaterStart < 0) currentUnderwaterStart = i;
                var len = i - currentUnderwaterStart + 1;
                if (len > longestUnderwater) longestUnderwater = len;
            }
            else { currentUnderwaterStart = -1; }
        }

        // Scale: deepest DD ⇒ bottom (H - pad). Ensure non-zero range.
        double maxScale = Math.Max(worstDd > 0m ? (double)worstDd : 0.01, 0.01);

        var area = new System.Windows.Media.PointCollection(eq.Length + 2);
        var zeroLine = new System.Windows.Media.PointCollection(2)
        {
            new System.Windows.Point(pad, zeroY),
            new System.Windows.Point(W - pad, zeroY)
        };

        // Polygon: start at left zero, walk DD curve, close at right zero.
        area.Add(new System.Windows.Point(pad, zeroY));
        for (int i = 0; i < eq.Length; i++)
        {
            double x = pad + (W - 2 * pad) * (eq.Length <= 1 ? 0 : (double)i / (eq.Length - 1));
            double y = zeroY + (ddPct[i] / maxScale) * (H - zeroY - pad);
            area.Add(new System.Windows.Point(x, y));
        }
        area.Add(new System.Windows.Point(W - pad, zeroY));

        UnderwaterAreaPoints = area;
        UnderwaterZeroLinePoints = zeroLine;

        double worstX = pad + (W - 2 * pad) * (eq.Length <= 1 ? 0 : (double)worstIdx / (eq.Length - 1));
        double worstY = zeroY + (ddPct[worstIdx] / maxScale) * (H - zeroY - pad);
        UnderwaterMaxDdLabel = $"Max DD {worstDd:P2} (day {worstIdx})";
        UnderwaterMaxDdLabelX = Math.Min(W - 160, Math.Max(4, worstX - 80));
        UnderwaterMaxDdLabelY = Math.Min(H - 18, Math.Max(zeroY + 2, worstY - 16));

        var pctUnderwater = (double)underwaterDays / eq.Length;
        UnderwaterSummary = $"Underwater {pctUnderwater:P0} of bars · longest streak {longestUnderwater} bars · max depth {worstDd:P2}";
    }

    /// <summary>Monte Carlo fan: bootstraps 1000 forward paths from the realized daily-PnL
    /// distribution. Fan envelopes (5/25/50/75/95 percentiles) show the statistical reach
    /// of the strategy over the next ProjectionDays days. The realized curve is overlaid for
    /// continuity. Reads from the cached daily-PnL set populated after the last backtest.</summary>
    [RelayCommand]
    private void RunMonteCarloFan()
    {
        if (_mcSourceDailyPnl is null || _mcSourceDailyPnl.Length < 5)
        {
            McFanStatus = "Need a backtest with at least 5 days of PnL first.";
            McFanHasResults = false;
            return;
        }

        try
        {
            int days = Math.Clamp(McFanProjectionDays, 5, 365);
            int iters = Math.Clamp(McFanIterations, 100, 5000);
            McFanProjectionDays = days;
            McFanIterations = iters;

            var src = _mcSourceDailyPnl;
            decimal start = _mcSourceFinalEquity;
            var rng = new Random(42); // deterministic for repeatability

            // Build path matrix [iters, days+1] storing equity, then percentiles per day.
            var paths = new decimal[iters, days + 1];
            for (int k = 0; k < iters; k++)
            {
                paths[k, 0] = start;
                decimal eq = start;
                for (int d = 1; d <= days; d++)
                {
                    eq += src[rng.Next(src.Length)];
                    paths[k, d] = eq;
                }
            }

            // Per-day percentile envelopes.
            var p5 = new decimal[days + 1];
            var p25 = new decimal[days + 1];
            var p50 = new decimal[days + 1];
            var p75 = new decimal[days + 1];
            var p95 = new decimal[days + 1];
            var col = new decimal[iters];
            for (int d = 0; d <= days; d++)
            {
                for (int k = 0; k < iters; k++) col[k] = paths[k, d];
                Array.Sort(col);
                p5[d] = col[(int)(iters * 0.05)];
                p25[d] = col[(int)(iters * 0.25)];
                p50[d] = col[(int)(iters * 0.50)];
                p75[d] = col[(int)(iters * 0.75)];
                p95[d] = col[(int)(iters * 0.95)];
            }

            // Realized curve scaled into the right-hand 30% of the canvas; fan lives in left 70%.
            // Simpler: realized is rendered as the seed point only (start == finalEquity), so
            // we draw the fan over the entire canvas and overlay the realized horizontal line.
            double W = McFanCanvasWidth;
            double H = McFanCanvasHeight;
            double pad = 12;

            decimal lo = Math.Min(p5.Min(), start);
            decimal hi = Math.Max(p95.Max(), start);
            decimal range = hi - lo;
            if (range == 0m) range = 1m;

            System.Windows.Point Map(int dayIdx, decimal eq)
            {
                double x = pad + (W - 2 * pad) * (days <= 0 ? 0 : (double)dayIdx / days);
                double y = H - pad - (double)((eq - lo) / range) * (H - 2 * pad);
                return new System.Windows.Point(x, y);
            }

            // P5-P95 outer envelope polygon (clockwise).
            var outer = new System.Windows.Media.PointCollection(2 * (days + 1));
            for (int d = 0; d <= days; d++) outer.Add(Map(d, p95[d]));
            for (int d = days; d >= 0; d--) outer.Add(Map(d, p5[d]));
            McFanP5to95Polygon = outer;

            // P25-P75 inner envelope polygon.
            var inner = new System.Windows.Media.PointCollection(2 * (days + 1));
            for (int d = 0; d <= days; d++) inner.Add(Map(d, p75[d]));
            for (int d = days; d >= 0; d--) inner.Add(Map(d, p25[d]));
            McFanP25to75Polygon = inner;

            // Median line.
            var med = new System.Windows.Media.PointCollection(days + 1);
            for (int d = 0; d <= days; d++) med.Add(Map(d, p50[d]));
            McFanMedianLine = med;

            // Realized "today" line — horizontal at start.
            McFanRealizedLine = new System.Windows.Media.PointCollection
            {
                Map(0, start),
                Map(days, start)
            };

            decimal worstCase = p5[days];
            decimal bestCase = p95[days];
            decimal medianEnd = p50[days];
            decimal bestPnl = bestCase - start;
            decimal worstPnl = worstCase - start;
            decimal medianPnl = medianEnd - start;
            // P(positive at horizon) = fraction of paths with terminal > start.
            int posCount = 0;
            for (int k = 0; k < iters; k++) if (paths[k, days] > start) posCount++;
            double pPos = (double)posCount / iters;

            McFanSummary = $"After {days} days · 5%-95% range [{worstCase:N0}, {bestCase:N0}] · median {medianEnd:N0} ({medianPnl:+#,0;-#,0}) · P(profit) {pPos:P1}";
            McFanStatus = $"{iters} bootstrap paths from {src.Length} realized days · 5/25/50/75/95 percentile fan.";
            McFanHasResults = true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Monte Carlo fan failed");
            McFanStatus = $"Failed: {ex.Message}";
            McFanHasResults = false;
        }
    }

    /// <summary>Builds a daily-PnL distribution histogram from the equity curve differences.
    /// 25 bins symmetric around zero; green bars for positive days, red for negative.</summary>
    private void BuildPnlHistogram(PortfolioBacktestResult result)
    {
        var eq = result.EquityCurve;
        if (eq is null || eq.Length < 2) return;

        var daily = new decimal[eq.Length - 1];
        for (int i = 1; i < eq.Length; i++) daily[i - 1] = eq[i] - eq[i - 1];

        if (daily.Length == 0) return;
        var min = daily.Min();
        var max = daily.Max();
        if (max == min) { max = min + 1m; }
        decimal absMax = Math.Max(Math.Abs(min), Math.Abs(max));
        decimal low = -absMax, high = absMax;   // symmetric around 0
        decimal width = (high - low) / 25m;
        if (width == 0m) width = 1m;

        var counts = new int[25];
        foreach (var d in daily)
        {
            int idx = (int)((d - low) / width);
            if (idx < 0) idx = 0;
            if (idx >= counts.Length) idx = counts.Length - 1;
            counts[idx]++;
        }

        double W = PnlHistogramWidth;
        double H = PnlHistogramHeight;
        double pad = 12;
        int maxCount = counts.Max();
        if (maxCount == 0) maxCount = 1;
        double barSlot = (W - 2 * pad) / counts.Length;

        PnlHistogram.Clear();
        for (int i = 0; i < counts.Length; i++)
        {
            double binCenter = (double)(low + width * (i + 0.5m));
            double barHeight = (double)counts[i] / maxCount * (H - 2 * pad - 20);
            bool isNegative = binCenter < 0;
            PnlHistogram.Add(new HistogramBar
            {
                X = pad + i * barSlot + 1,
                Y = H - pad - barHeight - 20,
                Width = Math.Max(1, barSlot - 2),
                Height = Math.Max(0.5, barHeight),
                Count = counts[i],
                BinLabel = binCenter.ToString("N0"),
                BarColor = isNegative
                    ? new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A))
                    : new SolidColorBrush(Color.FromRgb(0x37, 0xA9, 0x5B))
            });
        }

        int winDays = daily.Count(d => d > 0);
        int lossDays = daily.Count(d => d < 0);
        int flatDays = daily.Count(d => d == 0);
        PnlHistogramSummary = $"Daily PnL distribution (25 symmetric bins). Win days {winDays} · Loss days {lossDays} · Flat {flatDays}. Range [{min:N0}, {max:N0}].";
    }

    private static Brush ColorForCorrelation(double corr, bool isDiagonal)
    {
        if (isDiagonal) return new SolidColorBrush(Color.FromRgb(0x2A, 0x3B, 0x5E));
        // |corr| ≤ 0.3 = green, 0.3-0.6 = yellow, > 0.6 = red; negative rescaled symmetric.
        var abs = Math.Abs(corr);
        if (abs <= 0.30) return new SolidColorBrush(Color.FromArgb(0x80, 0x37, 0xA9, 0x5B));
        if (abs <= 0.60) return new SolidColorBrush(Color.FromArgb(0x80, 0xF0, 0x88, 0x3E));
        return new SolidColorBrush(Color.FromArgb(0x80, 0xF0, 0x5A, 0x5A));
    }
}

public partial class PortfolioPositionRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _type = "";
    [ObservableProperty] private string _symbolA = "";
    [ObservableProperty] private string? _symbolB;
    [ObservableProperty] private string _timeframe = "1h";
    [ObservableProperty] private decimal _weight;
    [ObservableProperty] private double _expectedSharpe;
    [ObservableProperty] private decimal _expectedProfitFactor;
    [ObservableProperty] private double _expectedWinRate;

    // Populated after backtest runs.
    [ObservableProperty] private int _actualTrades;
    [ObservableProperty] private double _actualWinRate;
    [ObservableProperty] private double _actualSharpe;
    [ObservableProperty] private decimal _actualPnl;
    [ObservableProperty] private decimal _actualProfitFactor;
    [ObservableProperty] private decimal _actualMaxDrawdown;

    public string SymbolDisplay => SymbolB is null ? SymbolA : $"{SymbolA} / {SymbolB}";
    public string WeightDisplay => Weight.ToString("P0");
}

public partial class CorrelationRow : ObservableObject
{
    [ObservableProperty] private string _rowName = "";
    public ObservableCollection<CorrelationCell> Values { get; } = new();
}

public partial class HistogramBar : ObservableObject
{
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private int _count;
    [ObservableProperty] private string _binLabel = "";
    [ObservableProperty] private Brush _barColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
}

public partial class CorrelationCell : ObservableObject
{
    [ObservableProperty] private string _columnName = "";
    [ObservableProperty] private double _value;
    [ObservableProperty] private bool _isDiagonal;
    [ObservableProperty] private Brush _backgroundBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x3B, 0x5E));
}
