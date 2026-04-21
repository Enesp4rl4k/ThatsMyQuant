using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Media;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Dashboard.ViewModels;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public partial class PortfolioViewModel : ObservableObject
{
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));

    [ObservableProperty] private decimal _balance = 100_000;
    [ObservableProperty] private decimal _realizedPnL = 0;
    [ObservableProperty] private decimal _unrealizedPnL = 0;

    public double RealizedPnLPercent => Balance == 0 ? 0 : (double)(RealizedPnL / Balance * 100);
    public Brush RealizedPnLColor => RealizedPnL >= 0 ? GreenBrush : RedBrush;
    
    [ObservableProperty] private string _portfolioGrowthHeadline = "100,000";
    [ObservableProperty] private string _portfolioGrowthSubtext = "No realized growth history yet";
    [ObservableProperty] private string _portfolioRangeSummary = "Portfolio growth will update from closed trades";
    [ObservableProperty] private Brush _portfolioGrowthColor = GreenBrush;
    [ObservableProperty] private string _selectedPortfolioRange = "Monthly";
    [ObservableProperty] private string _portfolioExecutionModeLabel = "Execution mode pending";
    [ObservableProperty] private Brush _portfolioExecutionModeColor = GrayBrush;
    [ObservableProperty] private string _portfolioPromotionStageLabel = "Stage pending";

    /// <summary>Totals from closed trades in the selected portfolio range (fees paid).</summary>
    [ObservableProperty] private decimal _portfolioWindowFeesPaid;

    /// <summary>Net funding in range: positive received, negative paid.</summary>
    [ObservableProperty] private decimal _portfolioWindowNetFunding;

    [ObservableProperty] private Brush _portfolioWindowFeesBrush = GrayBrush;

    [ObservableProperty] private Brush _portfolioWindowFundingBrush = GrayBrush;

    public ObservableCollection<PositionViewModel> ActivePositions { get; } = new();
    public ObservableCollection<TradeJournalEntry> TradeJournal { get; } = new();
    
    // Charting Points (Points for Polyline)
    [ObservableProperty] private PointCollection _portfolioGrowthPoints = new();
    public ObservableCollection<ChartAxisLabel> PortfolioXAxisLabels { get; } = new();
    public ObservableCollection<ChartAxisLabel> PortfolioYAxisLabels { get; } = new();
}

public class ChartAxisLabel
{
    public string Text { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
}
