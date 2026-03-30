using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Media;
using ThatsMyQuant.Dashboard.ViewModels;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public partial class StrategyStudioViewModel : ObservableObject
{
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

    // --- STRATEGY & AI STUDIO ---
    [ObservableProperty] private string _strategyPrompt = "";
    [ObservableProperty] private string _obTimeframe = "1h";
    public ObservableCollection<string> TimeframeOptions { get; } = new() { "1m", "5m", "15m", "1h", "4h", "1d" };
    [ObservableProperty] private double _fvgSensitivity = 50;
    [ObservableProperty] private double _aiConfidenceThreshold = 75;
    [ObservableProperty] private bool _isAiAutomationEnabled = false;

    // --- RISK MANAGEMENT ---
    [ObservableProperty] private decimal _riskDailyLossLimit = 5.0m;
    [ObservableProperty] private decimal _riskMaxPositionSize = 2.0m;
    [ObservableProperty] private decimal _riskTrailingStopAtr = 1.5m;
    
    public ObservableCollection<string> RiskProfiles { get; } = new() { "Low", "Moderate", "Aggressive" };
    [ObservableProperty] private string _selectedRiskProfile = "Moderate";

    // --- BOT CONFIG ---
    public ObservableCollection<string> BotStrategies { get; } = new() { "Swarm Orchestrator (Multi-Agent)", "Trend Confluence", "Breakout Hunter", "Range Reversion", "HFT Micro Edge" };
    public ObservableCollection<string> BotObjectives { get; } = new() { "Capital Growth", "Income Flow", "Low Drawdown", "Fast Rotation" };
    public ObservableCollection<string> BotBaseAssets { get; } = new() { "BTC", "ETH", "USDT", "TRY" };
    public ObservableCollection<string> BotQuoteAssets { get; } = new() { "USDC", "USDT", "TRY", "USD" };
    public ObservableCollection<string> BotPresets { get; } = new() { "Trend Attack", "Safe Income", "Recovery Probe", "HFT Probe" };
    public ObservableCollection<int> GridDensityOptions { get; } = new() { 6, 8, 10, 12, 16, 20 };

    [ObservableProperty] private string _selectedBotStrategy = "Trend Confluence";
    [ObservableProperty] private string _selectedBotObjective = "Capital Growth";
    [ObservableProperty] private string _selectedBotBaseAsset = "BTC";
    [ObservableProperty] private string _selectedBotQuoteAsset = "USDT";
    [ObservableProperty] private decimal _botFundingAmount = 2000m;
    [ObservableProperty] private int _gridDensity = 12;
    [ObservableProperty] private double _gridUpperLimit = 14;
    [ObservableProperty] private double _gridLowerLimit = 6.5;
    [ObservableProperty] private double _gridSlopeBias = 50;
    
    [ObservableProperty] private decimal _botRecommendedFunding = 0m;
    [ObservableProperty] private string _botLaunchGuardStatus = "Review";
    [ObservableProperty] private Brush _botLaunchGuardColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _botLaunchGuardReason = "Waiting for strategy and session context.";
    [ObservableProperty] private string _botProfitabilityFocus = "Profitability guidance will appear after analytics refresh.";
    
    [ObservableProperty] private string _botProductivityScore = "Setup score --";
    [ObservableProperty] private Brush _botProductivityColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _botSetupVelocity = "Velocity waiting for configuration.";
    [ObservableProperty] private string _botWorkflowAdvice = "Use presets or auto-tune to reduce launch friction.";

    [ObservableProperty] private string _botLaunchHint = "Select strategy and funding to see summary.";
    [ObservableProperty] private string _botLaunchSummary = "Confirm all parameters before starting the automated loop.";

    // --- SYSTEM & API SETTINGS ---
    [ObservableProperty] private string _apiPublicKey = "************************";
    [ObservableProperty] private string _apiSecretKey = "************************";
    [ObservableProperty] private string _telegramWebhookUrl = "https://api.telegram.org/bot...";
    [ObservableProperty] private string _discordWebhookUrl = "https://discord.com/api/webhooks/...";

    // --- BACKTEST ---
    [ObservableProperty] private string _backtestStatus = "Backtest not run yet.";
    [ObservableProperty] private string _backtestScenario = "Select a symbol, timeframe, and risk profile to test the current strategy stack.";
    [ObservableProperty] private string _backtestDateRange = "Date range: --";
    [ObservableProperty] private string _backtestPnlLabel = "PnL: --";
    [ObservableProperty] private string _backtestEquityLabel = "Final equity: --";
    [ObservableProperty] private string _backtestRiskSummary = "Sharpe -- | Sortino -- | Max DD --";
    [ObservableProperty] private string _backtestTradeBreakdown = "Wins -- | Losses -- | Avg R/R --";
    [ObservableProperty] private double _winRate = 0;
    [ObservableProperty] private int _totalTrades = 0;
    [ObservableProperty] private double _profitFactor = 0;

    public ObservableCollection<BacktestTradeViewModel> BacktestTrades { get; } = new();
    public ObservableCollection<GlobalBacktestResultViewModel> GlobalBacktestResults { get; } = new();

    // Tier 3 - Strategy Votes
    [ObservableProperty] private string _momentumVote = "Abstain";
    [ObservableProperty] private string _breakoutVote = "Abstain";
    [ObservableProperty] private string _statArbVote = "Abstain";
    [ObservableProperty] private string _scalpVote = "Abstain";

    // Tier 4 - Risk Guards
    [ObservableProperty] private string _riskGauntletSummary = "Waiting for consensus...";
    [ObservableProperty] private decimal _kellyFraction = 0;
}
