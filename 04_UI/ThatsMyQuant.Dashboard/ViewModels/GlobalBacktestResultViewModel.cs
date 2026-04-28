using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class GlobalBacktestResultViewModel : ObservableObject
{
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private int _totalTrades;
    [ObservableProperty] private double _winRate;
    [ObservableProperty] private decimal _totalPnL;
    [ObservableProperty] private decimal _profitFactor;
    [ObservableProperty] private double _sharpeRatio;
    [ObservableProperty] private double _sortinoRatio;
    [ObservableProperty] private decimal _maxDrawdownPercent;
    [ObservableProperty] private double _averageRR;
    [ObservableProperty] private int _maxConsecutiveLosses;
    [ObservableProperty] private decimal _startingCapital;
    [ObservableProperty] private decimal _finalEquity;
    [ObservableProperty] private string _returnPercent = "--";
    [ObservableProperty] private Brush _pnLColor = Brushes.Gray;
    [ObservableProperty] private bool _backtestPass;

    // --- Phase 3/4 pro analytics surfaced to UI (2026-04-24) ---
    /// <summary>Post-trade TCA leakage (fees+slippage as % of gross PnL).</summary>
    [ObservableProperty] private double _costLeakagePercent;
    /// <summary>Gross PnL before explicit costs — shows raw alpha.</summary>
    [ObservableProperty] private decimal _grossPnL;
    /// <summary>Total commission + slippage across all trades.</summary>
    [ObservableProperty] private decimal _totalCost;
    /// <summary>Monte Carlo bootstrap — 5th percentile Sharpe (pessimistic).</summary>
    [ObservableProperty] private double _sharpe5Pct;
    /// <summary>Monte Carlo bootstrap — 95th percentile Sharpe (optimistic).</summary>
    [ObservableProperty] private double _sharpe95Pct;
    /// <summary>Probability of loss from bootstrap resampling (0-1). Higher = weaker edge.</summary>
    [ObservableProperty] private double _probabilityOfLoss;
    /// <summary>Fractional Kelly sizing (% equity per trade).</summary>
    [ObservableProperty] private decimal _fractionalKelly;
    /// <summary>Historical 95% Conditional VaR on periodic returns.</summary>
    [ObservableProperty] private decimal _cvar95;
    /// <summary>Formal promotion gate verdict.</summary>
    [ObservableProperty] private bool _promotionApproved;
    /// <summary>Human-readable promotion gate summary (pass/fail criteria).</summary>
    [ObservableProperty] private string _promotionSummary = "";
}
