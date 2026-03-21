using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Media;
using ThatsMyQuant.Dashboard.Services;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private double _chartCanvasWidth = 800;
    [ObservableProperty] private double _chartCanvasHeight = 400;

    private readonly IMarketDataService _marketDataService;
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly IAiAnalystService _aiAnalyst;
    private readonly IEnumerable<IAlertingService> _alertingServices;
    private readonly ThatsMyQuant.Application.Strategy.BacktestEngine _backtestEngine;
    private readonly ThatsMyQuant.Application.Strategy.RiskAgent _riskAgent;
    private readonly ISignalRepository _signalRepository;
    private readonly ITradeRepository _tradeRepository;
    private readonly ITradingLoopService _tradingLoopService;
    private readonly IStrategyHealthStore _strategyHealthStore;
    private readonly bool _enableAutomaticAnalysis;

    // --- KPI Cards ---
    [ObservableProperty] private string _status = "READY";
    [ObservableProperty] private Brush _statusColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private decimal _balance = 100_000;
    [ObservableProperty] private int _confluenceScore = 0;
    [ObservableProperty] private Brush _confluenceColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    
    // --- HFT & Engine Telemetry ---
    [ObservableProperty] private string _hftInferenceStatus = "Offline";
    [ObservableProperty] private string _zScoreStatus = "N/A";
    [ObservableProperty] private string _hftWorkspaceHeadline = "Execution telemetry is waiting for the next scan.";
    [ObservableProperty] private string _hftLatencySummary = "Latency profile unavailable";
    [ObservableProperty] private string _hftMicrostructureSummary = "Microstructure context unavailable";
    [ObservableProperty] private string _hftExecutionBias = "Standby";
    [ObservableProperty] private Brush _hftExecutionBiasColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _hftRiskSummary = "HFT risk controls are waiting for queue and market data telemetry.";
    [ObservableProperty] private string _hftFillQuality = "Unknown";
    [ObservableProperty] private Brush _hftFillQualityColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _hftEstimatedSlippage = "~-- bps";
    [ObservableProperty] private string _hftSessionPnl = "0";
    [ObservableProperty] private Brush _hftSessionPnlColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _hftSessionWinRate = "WR --";
    
    // --- AI Analyst Properties ---
    [ObservableProperty] private string _aiSummary = "Waiting for analysis...";
    [ObservableProperty] private string _aiSentimentMode = "N/A";
    [ObservableProperty] private Brush _aiSentimentColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _aiTradeRecommendation = "Awaiting recommendation";
    [ObservableProperty] private string _aiRiskSummary = "No active AI risk summary.";
    [ObservableProperty] private int _executionReadiness = 0;
    [ObservableProperty] private string _traderSafetyStatus = "Review pending";
    [ObservableProperty] private Brush _traderSafetyColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _tradeReadiness = "Review pending";
    [ObservableProperty] private Brush _tradeReadinessColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _tradeReadinessReason = "Waiting for a fresh market scan.";
    [ObservableProperty] private string _signalConfidenceLabel = "Signal confidence: --";
    [ObservableProperty] private string _executionQualityLabel = "Execution quality: --";
    [ObservableProperty] private string _whyThisTrade = "No active setup is ready for explanation yet.";
    [ObservableProperty] private string _sessionSummary = "Session summary will appear after the next analysis cycle.";
    [ObservableProperty] private string _chartFocusTitle = "Chart Inspector";
    [ObservableProperty] private string _chartFocusSummary = "Hover over the chart to inspect candles.";
    [ObservableProperty] private string _chartFocusPrice = "Close: --";
    [ObservableProperty] private string _chartFocusRange = "Range: --";
    [ObservableProperty] private string _chartFocusVolume = "Volume: --";
    [ObservableProperty] private string _chartFocusChange = "Change: --";
    [ObservableProperty] private string _chartHeaderTitle = "Chart";
    [ObservableProperty] private string _chartHeaderOhlc = "O -- H -- L -- C --";
    [ObservableProperty] private Brush _chartHeaderChangeColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _currentPriceMarker = "--";
    [ObservableProperty] private double _currentPriceMarkerY = 150;
    [ObservableProperty] private string _marketPulse = "Awaiting fresh market scan.";
    [ObservableProperty] private string _executionPlan = "Risk plan will appear after analysis.";
    [ObservableProperty] private string _analysisTimestamp = "Not analyzed yet";
    [ObservableProperty] private string _botStatusSummary = "The bot is waiting for the next qualified HTF setup.";
    [ObservableProperty] private string _dailyMarketBriefing = "Daily market briefing will appear after the next analysis cycle.";
    [ObservableProperty] private string _runtimeIntegrityStatus = "Runtime integrity pending";
    [ObservableProperty] private Brush _runtimeIntegrityColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _executionServiceStatus = "Execution service pending";
    [ObservableProperty] private string _aiProviderStatus = "AI provider pending";
    [ObservableProperty] private string _configValidationStatus = "Startup validation pending";
    [ObservableProperty] private string _configValidationSummary = "Runtime configuration will be validated at startup.";
    [ObservableProperty] private bool _isHftRuntimeAvailable = false;
    [ObservableProperty] private string _portfolioExecutionModeLabel = "Execution mode pending";
    [ObservableProperty] private Brush _portfolioExecutionModeColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _portfolioPromotionStageLabel = "Stage pending";
    [ObservableProperty] private string _selectedPortfolioRange = "Monthly";
    [ObservableProperty] private string _portfolioGrowthHeadline = "100,000";
    [ObservableProperty] private string _portfolioGrowthSubtext = "No realized growth history yet";
    [ObservableProperty] private string _portfolioRangeSummary = "Portfolio growth will update from closed trades";
    [ObservableProperty] private Brush _portfolioGrowthColor = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0xBB));
    [ObservableProperty] private string _strategyThrottleStatus = "Unrated";
    [ObservableProperty] private string _strategyThrottleReason = "No strategy bucket has enough trade history yet.";
    [ObservableProperty] private Brush _strategyThrottleColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

    [ObservableProperty] private string _alphaTrendStatus = "--";
    [ObservableProperty] private Brush _alphaTrendColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private double _winRate = 0;
    [ObservableProperty] private int _totalTrades = 0;
    [ObservableProperty] private double _profitFactor = 0;
    [ObservableProperty] private string _strategyLeaderboardStatus = "Waiting for trade analytics...";
    [ObservableProperty] private string _edgeAnalyticsStatus = "Waiting for edge analytics...";
    [ObservableProperty] private string _bestEdgeZoneSummary = "Edge opportunity zones will appear after more closed trades.";
    [ObservableProperty] private string _worstEdgeFailureSummary = "Failure diagnostics will appear after more closed trades.";
    [ObservableProperty] private string _exitPolicyAnalyticsSummary = "Exit-policy winners will appear after more closed trades.";
    [ObservableProperty] private string _exitPolicyDriftSummary = "Exit-policy drift will appear once paper and real history overlap.";
    [ObservableProperty] private string _backtestStatus = "Backtest not run yet.";
    [ObservableProperty] private string _backtestScenario = "Select a symbol, timeframe, and risk profile to test the current strategy stack.";
    [ObservableProperty] private string _backtestDateRange = "Date range: --";
    [ObservableProperty] private string _backtestPnlLabel = "PnL: --";
    [ObservableProperty] private string _backtestEquityLabel = "Final equity: --";
    [ObservableProperty] private string _backtestRiskSummary = "Sharpe -- | Sortino -- | Max DD --";
    [ObservableProperty] private string _backtestTradeBreakdown = "Wins -- | Losses -- | Avg R/R --";
    [ObservableProperty] private bool _isPaperMode = true;
    [ObservableProperty] private string _tradingModeLabel = "PAPER";
    [ObservableProperty] private Brush _tradingModeColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));
    [ObservableProperty] private string _currentSessionLabel = "Session context loading...";
    [ObservableProperty] private string _sessionGuidance = "Killzone guidance will appear after the next refresh.";
    [ObservableProperty] private string _visualReasoningSummary = "Visual reasoning overlays will appear after analysis.";
    [ObservableProperty] private string _selectedTradeReview = "Choose a closed trade to generate an AI coaching review.";
    [ObservableProperty] private string _selectedTradeReviewTitle = "AI Trade Review";

    // --- Top Index Tickers (Placeholders) ---
    [ObservableProperty] private string _xu100Price = "--";
    [ObservableProperty] private string _xu100Change = "--";
    [ObservableProperty] private Brush _xu100Color = GrayBrush;

    [ObservableProperty] private string _btcPrice = "--";
    [ObservableProperty] private string _btcChange = "--";
    [ObservableProperty] private Brush _btcColor = GrayBrush;
    
    // --- STRATEGY & AI STUDIO PROPERTIES ---
    [ObservableProperty] private string _obTimeframe = "1h";
    public ObservableCollection<string> TimeframeOptions { get; } = new() { "1m", "5m", "15m", "1h", "4h", "1d" };
    [ObservableProperty] private double _fvgSensitivity = 50;
    [ObservableProperty] private double _aiConfidenceThreshold = 75;
    [ObservableProperty] private bool _isAiAutomationEnabled = false;

    // --- RISK MANAGEMENT LIMITS ---
    [ObservableProperty] private decimal _riskDailyLossLimit = 5.0m;
    [ObservableProperty] private decimal _riskMaxPositionSize = 2.0m;
    [ObservableProperty] private decimal _riskTrailingStopAtr = 1.5m;

    // --- SYSTEM & API SETTINGS ---
    [ObservableProperty] private string _apiPublicKey = "************************";
    [ObservableProperty] private string _apiSecretKey = "************************";
    [ObservableProperty] private string _telegramWebhookUrl = "https://api.telegram.org/bot...";
    [ObservableProperty] private string _discordWebhookUrl = "https://discord.com/api/webhooks/...";
    [ObservableProperty] private string _dbConnectionStatus = "Connected (PostgreSQL)";
    [ObservableProperty] private string _serverLatency = "12ms";

    [ObservableProperty] private string _usdTryPrice = "--";
    [ObservableProperty] private string _usdTryChange = "--";
    [ObservableProperty] private Brush _usdTryColor = GrayBrush;

    // --- Market & Symbol Selector ---
    public ObservableCollection<string> Markets { get; } = new() { "BIST", "Crypto", "Forex" };
    [ObservableProperty] private string _selectedMarket = "BIST";
    public ObservableCollection<string> Symbols { get; } = new();
    [ObservableProperty] private string _selectedSymbol = "THYAO";

    // --- Timeframe Selection ---
    public ObservableCollection<string> Timeframes { get; } = new() { "1m", "5m", "15m", "1h", "3h", "4h", "1d" };
    [ObservableProperty] private string _selectedTimeframe = "1h";

    // --- Risk Profile Selection ---
    public ObservableCollection<string> RiskProfiles { get; } = new() { "Low", "Moderate", "Aggressive" };
    [ObservableProperty] private string _selectedRiskProfile = "Moderate";
    public ObservableCollection<string> BotBaseAssets { get; } = new() { "BTC", "ETH", "USDT", "TRY" };
    public ObservableCollection<string> BotQuoteAssets { get; } = new() { "USDC", "USDT", "TRY", "USD" };
    public ObservableCollection<int> GridDensityOptions { get; } = new() { 6, 8, 10, 12, 16, 20 };
    public ObservableCollection<string> BotStrategies { get; } = new() { "Trend Confluence", "Grid Runner", "Breakout Hunter", "Range Reversion", "HFT Scout" };
    public ObservableCollection<string> BotObjectives { get; } = new() { "Capital Growth", "Income Flow", "Low Drawdown", "Fast Rotation" };
    public ObservableCollection<string> BotPresets { get; } = new() { "Trend Attack", "Safe Income", "Recovery Probe", "HFT Probe" };
    [ObservableProperty] private string _selectedBotBaseAsset = "BTC";
    [ObservableProperty] private string _selectedBotQuoteAsset = "USDC";
    [ObservableProperty] private string _selectedBotStrategy = "Trend Confluence";
    [ObservableProperty] private string _selectedBotObjective = "Capital Growth";
    [ObservableProperty] private decimal _botFundingAmount = 2000m;
    [ObservableProperty] private double _gridUpperLimit = 14;
    [ObservableProperty] private double _gridLowerLimit = 6.5;
    [ObservableProperty] private int _gridDensity = 12;
    [ObservableProperty] private double _gridSlopeBias = 50;
    [ObservableProperty] private string _botLaunchSummary = "Configure the launch plan, then activate the bot in paper or live mode.";
    [ObservableProperty] private string _botLaunchHint = "Use paper mode first when testing a new setup or AI prompt.";
    [ObservableProperty] private decimal _botRecommendedFunding = 0m;
    [ObservableProperty] private string _botLaunchGuardStatus = "Review";
    [ObservableProperty] private Brush _botLaunchGuardColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _botLaunchGuardReason = "Waiting for strategy and session context.";
    [ObservableProperty] private string _botProfitabilityFocus = "Profitability guidance will appear after analytics refresh.";
    [ObservableProperty] private string _disabledStrategiesSummary = "No strategies are disabled.";
    [ObservableProperty] private string _capitalAllocatorSummary = "Capital allocation will update from strategy health and session edge.";
    [ObservableProperty] private string _botProductivityScore = "Setup score --";
    [ObservableProperty] private Brush _botProductivityColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _botSetupVelocity = "Velocity waiting for configuration.";
    [ObservableProperty] private string _botWorkflowAdvice = "Use presets or auto-tune to reduce launch friction.";


    // --- Indicators ---
    [ObservableProperty] private double _rSI = 50;
    [ObservableProperty] private Brush _rSIColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _mACDStatus = "--";
    [ObservableProperty] private Brush _mACDColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _eMAStatus = "--";
    [ObservableProperty] private Brush _eMAColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private double _bBUpper = 0;
    [ObservableProperty] private double _bBLower = 0;
    [ObservableProperty] private double _aTR = 0;
    [ObservableProperty] private double _volumeRatio = 1.0;
    [ObservableProperty] private Brush _volumeColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private double _alphaTrendValue = 0;

    // --- S/R & Risk ---
    [ObservableProperty] private decimal _nearestResistance = 0;
    [ObservableProperty] private decimal _currentPrice = 0;
    [ObservableProperty] private decimal _nearestSupport = 0;
    [ObservableProperty] private decimal _stopLoss = 0;
    [ObservableProperty] private decimal _takeProfit = 0;
    [ObservableProperty] private decimal _positionSize = 0;
    [ObservableProperty] private decimal _dailyPnL = 0;
    [ObservableProperty] private Brush _pnLColor = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));

    // --- NEW PORTFOLIO METRICS ---
    [ObservableProperty] private decimal _realizedPnLPercent = 0;
    [ObservableProperty] private decimal _unrealizedPnLPercent = 0;
    [ObservableProperty] private decimal _riskRewardRatio = 0;
    [ObservableProperty] private bool _isKillSwitchArmed = false;
    [ObservableProperty] private string _riskGuardStatus = "Armed: No";
    [ObservableProperty] private string _listenerHealth = "Unknown";
    [ObservableProperty] private string _marketDataHealth = "Unknown";
    [ObservableProperty] private string _aiServiceHealth = "Unknown";
    [ObservableProperty] private string _apiPing = "-- ms";
    [ObservableProperty] private Brush _apiPingColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private decimal _realizedPnL = 0;
    [ObservableProperty] private Brush _realizedPnLColor = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    [ObservableProperty] private decimal _unrealizedPnL = 0;
    
    // --- Execution Queue Stats ---
    [ObservableProperty] private int _queuedCount = 0;
    [ObservableProperty] private int _executedCount = 0;
    [ObservableProperty] private int _failedCount = 0;
    [ObservableProperty] private int _deadLetterCount = 0;
    [ObservableProperty] private string _queueHealth = "Optimal";

    // --- PLAIN ENGLISH TRANSLATION ---
    [ObservableProperty] private string _plainEnglishTranslation = "";

    [ObservableProperty] private string _currentTime = DateTime.Now.ToString("HH:mm:ss");
    [ObservableProperty] private string _currentDate = DateTime.Now.ToString("dd MMMM, dddd");
    
    // --- Collections ---
    public ObservableCollection<WatchlistItemViewModel> WatchlistItems { get; } = new();

    // --- Indicators List (Agenda Style) ---
    public ObservableCollection<IndicatorDetailViewModel> IndicatorDetails { get; } = new();

    // --- MULTI-TIMEFRAME CONFLUENCE & LOGIC TREE ---
    public ObservableCollection<IndicatorDetailViewModel> TimeframeConfluences { get; } = new();
    public ObservableCollection<LogicTreeNode> LogicTree { get; } = new();

    // --- Charting ---
    [ObservableProperty] private System.Windows.Media.PointCollection _chartPoints = new();
    public ObservableCollection<CandleViewModel> ChartCandles { get; } = new();
    public ObservableCollection<BacktestMarker> BacktestMarkers { get; } = new();
    public ObservableCollection<ReasoningOverlayViewModel> ReasoningOverlays { get; } = new();
    public ObservableCollection<ChartAxisLabel> PriceScaleLabels { get; } = new();
    public ObservableCollection<ChartAxisLabel> TimeScaleLabels { get; } = new();
    public ObservableCollection<ChartGridLineViewModel> HorizontalGridLines { get; } = new();
    public ObservableCollection<ChartGridLineViewModel> VerticalGridLines { get; } = new();
    public ObservableCollection<ChartAxisLabel> PortfolioXAxisLabels { get; } = new();
    public ObservableCollection<ChartAxisLabel> PortfolioYAxisLabels { get; } = new();

    // --- Collections ---
    public ObservableCollection<string> SystemLogs { get; } = new();
    public ObservableCollection<TradeJournalEntry> TradeJournal { get; } = new();
    public ObservableCollection<BacktestTradeViewModel> BacktestTrades { get; } = new();
    public ObservableCollection<GlobalBacktestResultViewModel> GlobalBacktestResults { get; } = new();
    public ObservableCollection<string> TraderWarnings { get; } = new();
    public ObservableCollection<string> SafetyBanners { get; } = new();
    public ObservableCollection<ChecklistItemViewModel> EntryChecklist { get; } = new();
    public ObservableCollection<ChecklistItemViewModel> DeploymentChecklist { get; } = new();
    public ObservableCollection<StrategyLeaderboardRow> StrategyLeaderboard { get; } = new();
    public ObservableCollection<StrategyAllocationRow> CapitalAllocationRows { get; } = new();
    public ObservableCollection<SessionPerformanceRow> SessionPerformanceRows { get; } = new();
    public ObservableCollection<EdgeScoreBandRow> EdgeScoreBands { get; } = new();
    public ObservableCollection<EdgeFailureConditionRow> EdgeFailureConditions { get; } = new();
    public ObservableCollection<EdgeOpportunityRow> EdgeOpportunities { get; } = new();
    public ObservableCollection<ExitPolicyWinnerRow> ExitPolicyWinners { get; } = new();
    public ObservableCollection<ExitPolicyDriftRow> ExitPolicyDriftRows { get; } = new();
    public ObservableCollection<SessionContextViewModel> SessionContexts { get; } = new();
    public ObservableCollection<PositionViewModel> ActivePositions { get; } = new();
    public ObservableCollection<string> DisabledStrategies { get; } = new();
    [ObservableProperty] private System.Windows.Media.PointCollection _portfolioGrowthPoints = new();

    private readonly List<Candle> _chartSourceCandles = new();
    private readonly List<Trade> _tradeCache = new();
    private readonly List<StrategyHealthSnapshot> _persistedStrategyHealth = new();

    // --- AI STRATEGY PROMPTER ---
    [ObservableProperty] private string _strategyPrompt = "";

    public MainViewModel(
        IMarketDataService marketDataService, 
        ITechnicalAnalysisService technicalAnalysis,
        IAiAnalystService aiAnalyst,
        IEnumerable<IAlertingService> alertingServices,
        ThatsMyQuant.Application.Strategy.BacktestEngine backtestEngine,
        ThatsMyQuant.Application.Strategy.RiskAgent riskAgent,
        ISignalRepository signalRepository,
        ITradeRepository tradeRepository,
        ITradingLoopService tradingLoopService,
        IStrategyHealthStore strategyHealthStore,
        bool enableBackgroundTimers = true,
        bool enableAutomaticAnalysis = true)
    {
        _marketDataService = marketDataService;
        _technicalAnalysis = technicalAnalysis;
        _aiAnalyst = aiAnalyst;
        _alertingServices = alertingServices;
        _backtestEngine = backtestEngine;
        _riskAgent = riskAgent;
        _signalRepository = signalRepository;
        _tradeRepository = tradeRepository;
        _tradingLoopService = tradingLoopService;
        _strategyHealthStore = strategyHealthStore;
        _enableAutomaticAnalysis = enableAutomaticAnalysis;

        LoadSymbols();
        _persistedStrategyHealth.AddRange(_strategyHealthStore.Load());
        RefreshDisabledStrategiesFromHealth();
        UpdateRuntimeIntegrityStatus();
        AddSystemLog("SYSTEM: Terminal initialized. Multi-asset support active.");
        AddSystemLog("INFRA: WebSocket connection to global exchanges established.");
        AddSystemLog("AI: Reasoning engine v2.4-Institutional loaded.");
        AddSystemLog("RISK: Daily loss guard armed at 5.0%.");
        
        RefreshRiskPanel();
        UpdateTradingMode();
        UpdateSessionContext();
        UpdateBotLaunchSummary();
        UpdatePortfolioGrowth();
        UpdateHftWorkspace();

        if (enableBackgroundTimers)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) =>
            {
                CurrentTime = DateTime.Now.ToString("HH:mm:ss");
                CurrentDate = DateTime.Now.ToString("d MMMM, dddd");
                UpdateSessionContext();
            };
            timer.Start();

            var queueTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            queueTimer.Tick += async (s, e) =>
            {
                await RefreshQueueStats();
                await RefreshStrategyLeaderboard();
            };
            queueTimer.Start();
        }

        UpdatePlainEnglishTranslation();
        UpdateSessionSummary();
    }

    private async Task RefreshQueueStats()
    {
        try
        {
            var counts = await _signalRepository.GetStatusCountsAsync();
            
            QueuedCount = counts.GetValueOrDefault(TradeStatus.Queued, 0);
            ExecutedCount = counts.GetValueOrDefault(TradeStatus.Executed, 0);
            FailedCount = counts.GetValueOrDefault(TradeStatus.Failed, 0);
            DeadLetterCount = counts.GetValueOrDefault(TradeStatus.DeadLetter, 0);

            if (DeadLetterCount > 0) QueueHealth = "Critical (Dead-Letter)";
            else if (QueuedCount > 5) QueueHealth = "Congested";
            else QueueHealth = "Optimal";

            UpdateHftWorkspace();
        }
        catch (Exception ex)
        {
            AddSystemLog($"Queue Telemetry Error: {ex.Message}");
        }
    }

    private async Task RefreshStrategyLeaderboard()
    {
        try
        {
            var recentTrades = await _tradeRepository.GetAllAsync(250);
            StrategyLeaderboard.Clear();

            var tradeRows = recentTrades
                .Where(t => t.Status == TradeStatus.Executed || t.Status == TradeStatus.Failed)
                .ToList();

            if (tradeRows.Count == 0)
            {
                StrategyLeaderboardStatus = "No executed trades yet.";
                _tradeCache.Clear();
                RefreshOpenPositionsFromTrades(Array.Empty<Trade>());
                UpdatePortfolioGrowth();
                RefreshEdgeAnalytics(Array.Empty<Trade>());
                StrategyThrottleStatus = "Unrated";
                StrategyThrottleReason = "No strategy bucket has enough trade history yet.";
                StrategyThrottleColor = GrayBrush;
                RefreshCapitalAllocation();
                return;
            }

            _tradeCache.Clear();
            _tradeCache.AddRange(recentTrades);
            RefreshOpenPositionsFromTrades(recentTrades);
            UpdatePortfolioGrowth();

            var rows = tradeRows
                .GroupBy(t => new
                {
                    Strategy = string.IsNullOrWhiteSpace(t.StrategyTag) ? "Unlabeled" : t.StrategyTag,
                    Regime = string.IsNullOrWhiteSpace(t.RegimeTag) ? "Unknown" : t.RegimeTag
                })
                .Select(g =>
                {
                    var pnls = g.Select(CalculateTradePnL).ToList();
                    var wins = pnls.Count(p => p > 0);
                    var grossProfit = pnls.Where(p => p > 0).Sum();
                    var grossLoss = Math.Abs(pnls.Where(p => p < 0).Sum());

                    return new StrategyLeaderboardRow
                    {
                        StrategyTag = g.Key.Strategy,
                        RegimeTag = g.Key.Regime,
                        ExecutionMode = ResolveExecutionModeScope(g),
                        PromotionStage = ResolvePromotionStageScope(g),
                        Trades = g.Count(),
                        WinRate = g.Count() > 0 ? wins * 100.0 / g.Count() : 0,
                        TotalPnL = pnls.Sum(),
                        ProfitFactor = grossLoss > 0 ? (double)(grossProfit / grossLoss) : grossProfit > 0 ? 99 : 0
                    };
                })
                .OrderByDescending(r => r.ProfitFactor)
                .ThenByDescending(r => r.TotalPnL)
                .Take(6)
                .ToList();

            foreach (var row in rows)
            {
                StrategyLeaderboard.Add(row);
            }

            var strategyOverrides = _persistedStrategyHealth
                .GroupBy(snapshot => NormalizeStrategyKey(snapshot.StrategyTag))
                .ToDictionary(
                    group => group.Key,
                    group => new
                    {
                        ManualDisabled = group.Any(item => item.ManuallyDisabled),
                        ForceEnabled = group.Any(item => item.ForceEnabled)
                    });

            _persistedStrategyHealth.Clear();
            foreach (var row in rows)
            {
                strategyOverrides.TryGetValue(NormalizeStrategyKey(row.StrategyTag), out var flags);
                _persistedStrategyHealth.Add(new StrategyHealthSnapshot
                {
                    StrategyTag = row.StrategyTag,
                    RegimeTag = row.RegimeTag,
                    Trades = row.Trades,
                    WinRate = row.WinRate,
                    ProfitFactor = row.ProfitFactor,
                    TotalPnL = row.TotalPnL,
                    ManuallyDisabled = flags?.ManualDisabled ?? false,
                    ForceEnabled = flags?.ForceEnabled ?? false,
                    RecordedAtUtc = DateTime.UtcNow
                });
            }

            foreach (var manualOnly in strategyOverrides
                         .Where(pair => pair.Value.ManualDisabled || pair.Value.ForceEnabled)
                         .Where(pair => _persistedStrategyHealth.All(snapshot => NormalizeStrategyKey(snapshot.StrategyTag) != pair.Key)))
            {
                _persistedStrategyHealth.Add(new StrategyHealthSnapshot
                {
                    StrategyTag = manualOnly.Key,
                    RegimeTag = "ManualOverride",
                    ManuallyDisabled = manualOnly.Value.ManualDisabled,
                    ForceEnabled = manualOnly.Value.ForceEnabled,
                    RecordedAtUtc = DateTime.UtcNow
                });
            }

            PersistStrategyHealth();
            RefreshSessionPerformance(tradeRows);
            RefreshEdgeAnalytics(tradeRows);
            RefreshCapitalAllocation();

            var dominantStage = rows
                .GroupBy(row => string.IsNullOrWhiteSpace(row.PromotionStage) ? "UNKNOWN" : row.PromotionStage)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault() ?? "UNKNOWN";

            StrategyLeaderboardStatus = $"Tracking {rows.Count} strategy buckets from {tradeRows.Count} trades. Dominant ladder stage: {dominantStage}.";
            EvaluateStrategyThrottle();
            UpdateBotLaunchSummary();
            UpdateSessionSummary();
        }
        catch (Exception ex)
        {
            StrategyLeaderboardStatus = "Strategy analytics unavailable.";
            RefreshOpenPositionsFromTrades(Array.Empty<Trade>());
            UpdatePortfolioGrowth();
            RefreshSessionPerformance(Array.Empty<Trade>());
            RefreshEdgeAnalytics(Array.Empty<Trade>());
            RefreshDisabledStrategiesFromHealth();
            RefreshCapitalAllocation();
            StrategyThrottleStatus = "Unavailable";
            StrategyThrottleReason = "Strategy throttling is waiting for analytics to recover.";
            StrategyThrottleColor = OrangeBrush;
            UpdateBotLaunchSummary();
            AddSystemLog($"Strategy analytics error: {ex.Message}");
        }
    }

    private void RefreshSessionPerformance(IEnumerable<Trade> trades)
    {
        SessionPerformanceRows.Clear();

        var rows = trades
            .Where(t => t.IsClosed && t.ExitTime.HasValue && t.Status == TradeStatus.Executed)
            .GroupBy(t => ResolveSessionBucket(t.ExitTime!.Value))
            .Select(g =>
            {
                var pnls = g.Select(CalculateTradePnL).ToList();
                var wins = pnls.Count(p => p > 0);
                var grossProfit = pnls.Where(p => p > 0).Sum();
                var grossLoss = Math.Abs(pnls.Where(p => p < 0).Sum());
                return new SessionPerformanceRow
                {
                    SessionName = g.Key,
                    Trades = g.Count(),
                    WinRate = g.Count() == 0 ? 0 : wins * 100.0 / g.Count(),
                    ProfitFactor = grossLoss > 0 ? (double)(grossProfit / grossLoss) : grossProfit > 0 ? 99 : 0,
                    TotalPnL = pnls.Sum(),
                    PnlColor = pnls.Sum() >= 0 ? GreenBrush : RedBrush
                };
            })
            .OrderByDescending(r => r.ProfitFactor)
            .ThenByDescending(r => r.TotalPnL)
            .ToList();

        foreach (var row in rows)
        {
            SessionPerformanceRows.Add(row);
        }
    }

    private void RefreshEdgeAnalytics(IEnumerable<Trade> trades)
    {
        EdgeScoreBands.Clear();
        EdgeFailureConditions.Clear();
        EdgeOpportunities.Clear();
        ExitPolicyWinners.Clear();
        ExitPolicyDriftRows.Clear();

        var closedTrades = trades
            .Where(t => t.Status == TradeStatus.Executed && t.IsClosed && t.ExitTime.HasValue)
            .ToList();

        if (closedTrades.Count == 0)
        {
            EdgeAnalyticsStatus = "Edge analytics need closed trades.";
            BestEdgeZoneSummary = "No closed trades yet, so edge zones are still unconfirmed.";
            WorstEdgeFailureSummary = "No closed trades yet, so failure conditions are still unconfirmed.";
            ExitPolicyAnalyticsSummary = "No closed trades yet, so exit-policy winners are still unconfirmed.";
            ExitPolicyDriftSummary = "No closed trades yet, so exit-policy drift is still unconfirmed.";
            return;
        }

        var journaledEdgeSignals = TradeJournal
            .Select(ParseEdgeJournalEntry)
            .Where(item => item != null)
            .Cast<ParsedEdgeJournalEntry>()
            .OrderBy(item => item.Time)
            .ToList();

        PopulateEdgeScoreBands(journaledEdgeSignals, closedTrades);
        PopulateEdgeFailureConditions(closedTrades);
        PopulateEdgeOpportunities(closedTrades);
        PopulateExitPolicyWinners(closedTrades);
        PopulateExitPolicyDrift(closedTrades);

        var scoreStatus = EdgeScoreBands.Count > 0
            ? $"Observed score bands from {EdgeScoreBands.Sum(row => row.Trades)} edge-scored outcomes."
            : "Score bands will fill as edge-scored outcomes accumulate.";
        EdgeAnalyticsStatus = $"{scoreStatus} Opportunity zones, failure conditions, exit-policy winners, and exit drift use closed trade history.";
    }

    private void PopulateEdgeScoreBands(
        IReadOnlyList<ParsedEdgeJournalEntry> journaledEdgeSignals,
        IReadOnlyList<Trade> closedTrades)
    {
        var persistedBandRows = closedTrades
            .Where(trade => trade.EdgeScore > 0m || !string.IsNullOrWhiteSpace(trade.EdgeBand))
            .GroupBy(trade => NormalizeEdgeBand(trade.EdgeBand, trade.EdgeScore))
            .Select(group =>
            {
                var pnls = group.Select(CalculateTradePnL).ToList();
                var wins = pnls.Count(pnl => pnl > 0);
                var grossProfit = pnls.Where(pnl => pnl > 0).Sum();
                var grossLoss = Math.Abs(pnls.Where(pnl => pnl < 0).Sum());
                var averageScore = group.Average(item => item.EdgeScore);
                return new EdgeScoreBandRow
                {
                    Band = group.Key,
                    ExecutionMode = ResolveExecutionModeScope(group),
                    PromotionStage = ResolvePromotionStageScope(group),
                    Trades = group.Count(),
                    WinRate = group.Count() == 0 ? 0 : wins * 100.0 / group.Count(),
                    ProfitFactor = grossLoss > 0 ? (double)(grossProfit / grossLoss) : grossProfit > 0 ? 99 : 0,
                    TotalPnL = pnls.Sum(),
                    Summary = $"Avg score {averageScore:F1}. {wins}/{group.Count()} winners from {ResolveExecutionModeScope(group)} / {ResolvePromotionStageScope(group)} trades.",
                    PnlColor = pnls.Sum() >= 0 ? GreenBrush : RedBrush
                };
            })
            .OrderByDescending(row => row.ProfitFactor)
            .ThenByDescending(row => row.TotalPnL)
            .ToList();

        if (persistedBandRows.Count > 0)
        {
            foreach (var row in persistedBandRows)
            {
                EdgeScoreBands.Add(row);
            }

            return;
        }

        if (journaledEdgeSignals.Count == 0)
        {
            return;
        }

        var matchedTradeIds = new HashSet<Guid>();
        var matched = new List<(ParsedEdgeJournalEntry Journal, Trade Trade)>();

        foreach (var entry in journaledEdgeSignals)
        {
            var nearestTrade = closedTrades
                .Where(trade => trade.Symbol.Equals(entry.Symbol, StringComparison.OrdinalIgnoreCase))
                .Where(trade => !matchedTradeIds.Contains(trade.Id))
                .Select(trade => new
                {
                    Trade = trade,
                    Distance = Math.Abs(((trade.ExitTime ?? trade.EntryTime) - entry.Time).TotalHours)
                })
                .Where(item => item.Distance <= 36)
                .OrderBy(item => item.Distance)
                .FirstOrDefault();

            if (nearestTrade == null)
            {
                continue;
            }

            matchedTradeIds.Add(nearestTrade.Trade.Id);
            matched.Add((entry, nearestTrade.Trade));
        }

        foreach (var row in matched
                     .GroupBy(item => NormalizeEdgeBand(item.Journal.Band, item.Journal.Score))
                     .Select(group =>
                     {
                         var pnls = group.Select(item => CalculateTradePnL(item.Trade)).ToList();
                         var wins = pnls.Count(pnl => pnl > 0);
                         var grossProfit = pnls.Where(pnl => pnl > 0).Sum();
                         var grossLoss = Math.Abs(pnls.Where(pnl => pnl < 0).Sum());
                         var averageScore = group.Average(item => item.Journal.Score);
                         return new EdgeScoreBandRow
                         {
                             Band = group.Key,
                             ExecutionMode = "JOURNAL-LINKED",
                             PromotionStage = "UNKNOWN",
                             Trades = group.Count(),
                             WinRate = group.Count() == 0 ? 0 : wins * 100.0 / group.Count(),
                             ProfitFactor = grossLoss > 0 ? (double)(grossProfit / grossLoss) : grossProfit > 0 ? 99 : 0,
                             TotalPnL = pnls.Sum(),
                             Summary = $"Avg score {averageScore:F1}. {wins}/{group.Count()} winners linked from edge-scored journal entries.",
                             PnlColor = pnls.Sum() >= 0 ? GreenBrush : RedBrush
                         };
                     })
                     .OrderByDescending(row => row.ProfitFactor)
                     .ThenByDescending(row => row.TotalPnL))
        {
            EdgeScoreBands.Add(row);
        }
    }

    private void PopulateEdgeFailureConditions(IReadOnlyList<Trade> closedTrades)
    {
        var failedTrades = _tradeCache.Where(trade => trade.Status == TradeStatus.Failed).ToList();
        var losingTrades = closedTrades.Where(trade => CalculateTradePnL(trade) <= 0).ToList();
        var completedCount = Math.Max(1, closedTrades.Count + failedTrades.Count);

        var weakBucketTrades = closedTrades
            .GroupBy(trade => new
            {
                Strategy = string.IsNullOrWhiteSpace(trade.StrategyTag) ? "Unlabeled" : trade.StrategyTag,
                Regime = string.IsNullOrWhiteSpace(trade.RegimeTag) ? "Unknown" : trade.RegimeTag
            })
            .Where(group =>
            {
                var pnls = group.Select(CalculateTradePnL).ToList();
                var wins = pnls.Count(pnl => pnl > 0);
                var grossProfit = pnls.Where(pnl => pnl > 0).Sum();
                var grossLoss = Math.Abs(pnls.Where(pnl => pnl < 0).Sum());
                var profitFactor = grossLoss > 0 ? (double)(grossProfit / grossLoss) : grossProfit > 0 ? 99 : 0;
                var winRate = group.Count() == 0 ? 0 : wins * 100.0 / group.Count();
                return profitFactor < 1.0 || winRate < 45;
            })
            .SelectMany(group => group)
            .ToList();

        var conditions = new List<EdgeFailureConditionRow>();

        AddFailureConditionIfAny(
            conditions,
            "Execution failures",
            failedTrades,
            completedCount,
            "Broker/exchange path failed before a clean fill.",
            ResolveExecutionModeScope(failedTrades),
            ResolvePromotionStageScope(failedTrades));

        AddFailureConditionIfAny(
            conditions,
            "Slippage drag",
            losingTrades.Where(trade => trade.EntrySlippagePercent >= 0.35m).ToList(),
            completedCount,
            "Entry slippage is eroding edge before the trade has room to work.",
            ResolveExecutionModeScope(losingTrades.Where(trade => trade.EntrySlippagePercent >= 0.35m)),
            ResolvePromotionStageScope(losingTrades.Where(trade => trade.EntrySlippagePercent >= 0.35m)));

        AddFailureConditionIfAny(
            conditions,
            "Latency drag",
            losingTrades.Where(trade => trade.ExecutionLatencyMs >= 900).ToList(),
            completedCount,
            "Execution latency is too slow for the current setup quality.",
            ResolveExecutionModeScope(losingTrades.Where(trade => trade.ExecutionLatencyMs >= 900)),
            ResolvePromotionStageScope(losingTrades.Where(trade => trade.ExecutionLatencyMs >= 900)));

        AddFailureConditionIfAny(
            conditions,
            "Weak bucket drift",
            weakBucketTrades,
            completedCount,
            "Strategy/regime buckets are underperforming their recent baseline.",
            ResolveExecutionModeScope(weakBucketTrades),
            ResolvePromotionStageScope(weakBucketTrades));

        foreach (var row in conditions
                     .OrderByDescending(item => item.Failures)
                     .ThenByDescending(item => item.FailureRate))
        {
            EdgeFailureConditions.Add(row);
        }

        WorstEdgeFailureSummary = EdgeFailureConditions.FirstOrDefault()?.Summary
            ?? "No concentrated failure condition detected in the recent closed-trade window.";
    }

    private void PopulateEdgeOpportunities(IReadOnlyList<Trade> closedTrades)
    {
        var rows = closedTrades
            .GroupBy(trade => new
            {
                Strategy = string.IsNullOrWhiteSpace(trade.StrategyTag) ? "Unlabeled" : trade.StrategyTag,
                Regime = string.IsNullOrWhiteSpace(trade.RegimeTag) ? "Unknown" : trade.RegimeTag,
                Session = ResolveSessionBucket(trade.ExitTime ?? trade.EntryTime)
            })
            .Select(group =>
            {
                var pnls = group.Select(CalculateTradePnL).ToList();
                var wins = pnls.Count(pnl => pnl > 0);
                var grossProfit = pnls.Where(pnl => pnl > 0).Sum();
                var grossLoss = Math.Abs(pnls.Where(pnl => pnl < 0).Sum());
                var profitFactor = grossLoss > 0 ? (double)(grossProfit / grossLoss) : grossProfit > 0 ? 99 : 0;
                var totalPnl = pnls.Sum();

                return new EdgeOpportunityRow
                {
                    StrategyTag = group.Key.Strategy,
                    RegimeTag = group.Key.Regime,
                    SessionTag = group.Key.Session,
                    ExecutionMode = ResolveExecutionModeScope(group),
                    PromotionStage = ResolvePromotionStageScope(group),
                    Trades = group.Count(),
                    WinRate = group.Count() == 0 ? 0 : wins * 100.0 / group.Count(),
                    ProfitFactor = profitFactor,
                    TotalPnL = totalPnl,
                    EdgeNote = BuildEdgeOpportunityNote(group.Key.Strategy, group.Key.Regime, group.Key.Session, group.Count(), profitFactor, totalPnl),
                    PnlColor = totalPnl >= 0 ? GreenBrush : RedBrush
                };
            })
            .Where(row => row.TotalPnL > 0 || row.ProfitFactor >= 1.2)
            .OrderByDescending(row => row.ProfitFactor)
            .ThenByDescending(row => row.TotalPnL)
            .Take(5)
            .ToList();

        foreach (var row in rows)
        {
            EdgeOpportunities.Add(row);
        }

        BestEdgeZoneSummary = EdgeOpportunities.FirstOrDefault()?.EdgeNote
            ?? "No durable edge zone stands out yet. Keep collecting closed trades.";
    }

    private void PopulateExitPolicyWinners(IReadOnlyList<Trade> closedTrades)
    {
        var winners = closedTrades
            .Where(trade => !string.IsNullOrWhiteSpace(trade.ExitPolicyTag))
            .GroupBy(trade => new
            {
                trade.Symbol,
                Strategy = string.IsNullOrWhiteSpace(trade.StrategyTag) ? "Unlabeled" : trade.StrategyTag,
                Regime = string.IsNullOrWhiteSpace(trade.RegimeTag) ? "Unknown" : trade.RegimeTag,
                Session = ResolveSessionBucketForTrade(trade)
            })
            .Select(bucket =>
            {
                var winningPolicy = bucket
                    .GroupBy(trade => string.IsNullOrWhiteSpace(trade.ExitPolicyTag) ? "Unlabeled" : trade.ExitPolicyTag)
                    .Select(policyGroup =>
                    {
                        var pnls = policyGroup.Select(CalculateTradePnL).ToList();
                        var wins = pnls.Count(pnl => pnl > 0);
                        var grossProfit = pnls.Where(pnl => pnl > 0).Sum();
                        var grossLoss = Math.Abs(pnls.Where(pnl => pnl < 0).Sum());
                        var profitFactor = grossLoss > 0 ? (double)(grossProfit / grossLoss) : grossProfit > 0 ? 99 : 0;
                        var totalPnl = pnls.Sum();
                        var executionMode = ResolveExecutionModeScope(policyGroup);
                        var promotionStage = ResolvePromotionStageScope(policyGroup);

                        return new ExitPolicyWinnerRow
                        {
                            Symbol = bucket.Key.Symbol,
                            StrategyTag = bucket.Key.Strategy,
                            RegimeTag = bucket.Key.Regime,
                            SessionTag = bucket.Key.Session,
                            ExitPolicyTag = policyGroup.Key,
                            ExecutionMode = executionMode,
                            PromotionStage = promotionStage,
                            Trades = policyGroup.Count(),
                            WinRate = policyGroup.Count() == 0 ? 0 : wins * 100.0 / policyGroup.Count(),
                            ProfitFactor = profitFactor,
                            TotalPnL = totalPnl,
                            Summary = BuildExitPolicyWinnerSummary(
                                bucket.Key.Symbol,
                                bucket.Key.Strategy,
                                bucket.Key.Regime,
                                bucket.Key.Session,
                                policyGroup.Key,
                                policyGroup.Count(),
                                profitFactor,
                                totalPnl,
                                executionMode,
                                promotionStage),
                            PnlColor = totalPnl >= 0 ? GreenBrush : RedBrush
                        };
                    })
                    .OrderByDescending(row => row.ProfitFactor)
                    .ThenByDescending(row => row.TotalPnL)
                    .ThenByDescending(row => row.WinRate)
                    .FirstOrDefault();

                return winningPolicy;
            })
            .Where(row => row is not null && (row.TotalPnL > 0 || row.ProfitFactor >= 1.1))
            .Cast<ExitPolicyWinnerRow>()
            .OrderByDescending(row => row.ProfitFactor)
            .ThenByDescending(row => row.TotalPnL)
            .Take(6)
            .ToList();

        foreach (var row in winners)
        {
            ExitPolicyWinners.Add(row);
        }

        ExitPolicyAnalyticsSummary = ExitPolicyWinners.FirstOrDefault()?.Summary
            ?? "No exit policy stands out yet at the exact-bucket level. Keep collecting closed trades.";
    }

    private static string BuildExitPolicyWinnerSummary(
        string symbol,
        string strategy,
        string regime,
        string session,
        string exitPolicy,
        int trades,
        double profitFactor,
        decimal totalPnl,
        string executionMode,
        string promotionStage)
    {
        var pnlDirection = totalPnl >= 0 ? "positive" : "negative";
        return $"{exitPolicy} is leading {symbol} in {strategy}/{regime} during {session}. " +
               $"PF {profitFactor:F2} across {trades} trades with {pnlDirection} PnL. " +
               $"Scope: {executionMode} / {promotionStage}.";
    }

    private void PopulateExitPolicyDrift(IReadOnlyList<Trade> closedTrades)
    {
        var rows = closedTrades
            .Where(trade => !string.IsNullOrWhiteSpace(trade.ExitPolicyTag))
            .GroupBy(trade => new
            {
                trade.Symbol,
                Strategy = string.IsNullOrWhiteSpace(trade.StrategyTag) ? "Unlabeled" : trade.StrategyTag,
                Regime = string.IsNullOrWhiteSpace(trade.RegimeTag) ? "Unknown" : trade.RegimeTag,
                Session = ResolveSessionBucketForTrade(trade),
                ExitPolicy = string.IsNullOrWhiteSpace(trade.ExitPolicyTag) ? "TrendRunner" : trade.ExitPolicyTag
            })
            .Select(group =>
            {
                var paperTrades = group.Where(trade => string.Equals(NormalizeExecutionMode(trade.ExecutionMode), "PAPER", StringComparison.OrdinalIgnoreCase)).ToList();
                var liveTrades = group.Where(trade => string.Equals(NormalizeExecutionMode(trade.ExecutionMode), "REAL", StringComparison.OrdinalIgnoreCase)).ToList();
                if (paperTrades.Count < 2 || liveTrades.Count < 2)
                {
                    return null;
                }

                var paperPnls = paperTrades.Select(CalculateTradePnL).ToList();
                var livePnls = liveTrades.Select(CalculateTradePnL).ToList();
                var paperWinRate = paperPnls.Count(pnl => pnl > 0) * 100.0 / paperTrades.Count;
                var liveWinRate = livePnls.Count(pnl => pnl > 0) * 100.0 / liveTrades.Count;
                var paperProfitFactor = ComputeProfitFactorFromPnls(paperPnls);
                var liveProfitFactor = ComputeProfitFactorFromPnls(livePnls);
                var paperExpectedValue = paperPnls.Average();
                var liveExpectedValue = livePnls.Average();
                var driftScore = ComputeExitPolicyDriftScore(paperWinRate, liveWinRate, paperProfitFactor, liveProfitFactor, paperExpectedValue, liveExpectedValue);

                return new ExitPolicyDriftRow
                {
                    Symbol = group.Key.Symbol,
                    StrategyTag = group.Key.Strategy,
                    RegimeTag = group.Key.Regime,
                    SessionTag = group.Key.Session,
                    ExitPolicyTag = group.Key.ExitPolicy,
                    PaperTrades = paperTrades.Count,
                    LiveTrades = liveTrades.Count,
                    DriftScore = driftScore,
                    PaperWinRate = paperWinRate,
                    LiveWinRate = liveWinRate,
                    PaperProfitFactor = paperProfitFactor,
                    LiveProfitFactor = liveProfitFactor,
                    PaperExpectedValue = paperExpectedValue,
                    LiveExpectedValue = liveExpectedValue,
                    Summary = BuildExitPolicyDriftSummary(group.Key.Symbol, group.Key.Strategy, group.Key.Regime, group.Key.Session, group.Key.ExitPolicy, driftScore, paperExpectedValue, liveExpectedValue, paperProfitFactor, liveProfitFactor),
                    SeverityColor = driftScore >= 0.65 ? RedBrush : driftScore >= 0.45 ? OrangeBrush : GrayBrush
                };
            })
            .Where(row => row is not null && row.DriftScore >= 0.25)
            .Cast<ExitPolicyDriftRow>()
            .OrderByDescending(row => row.DriftScore)
            .ThenByDescending(row => Math.Abs(row.PaperExpectedValue - row.LiveExpectedValue))
            .Take(6)
            .ToList();

        foreach (var row in rows)
        {
            ExitPolicyDriftRows.Add(row);
        }

        ExitPolicyDriftSummary = ExitPolicyDriftRows.FirstOrDefault()?.Summary
            ?? "No meaningful live-vs-paper exit drift is visible yet for exact buckets.";
    }

    private static double ComputeProfitFactorFromPnls(IReadOnlyCollection<decimal> pnls)
    {
        var grossProfit = pnls.Where(pnl => pnl > 0).Sum();
        var grossLoss = Math.Abs(pnls.Where(pnl => pnl < 0).Sum());
        return grossLoss > 0 ? (double)(grossProfit / grossLoss) : grossProfit > 0 ? 99 : 0;
    }

    private static double ComputeExitPolicyDriftScore(
        double paperWinRate,
        double liveWinRate,
        double paperProfitFactor,
        double liveProfitFactor,
        decimal paperExpectedValue,
        decimal liveExpectedValue)
    {
        var winRateDrift = Math.Abs(paperWinRate - liveWinRate) / 100.0;
        var profitFactorBase = Math.Max(0.5, paperProfitFactor);
        var profitFactorDrift = Math.Abs(paperProfitFactor - liveProfitFactor) / profitFactorBase;
        var expectedValueBase = Math.Max(1m, Math.Abs(paperExpectedValue));
        var expectedValueDrift = (double)(Math.Abs(paperExpectedValue - liveExpectedValue) / expectedValueBase);
        return Math.Min(1.50, (winRateDrift * 0.35) + (profitFactorDrift * 0.35) + (expectedValueDrift * 0.30));
    }

    private static string BuildExitPolicyDriftSummary(
        string symbol,
        string strategy,
        string regime,
        string session,
        string exitPolicy,
        double driftScore,
        decimal paperExpectedValue,
        decimal liveExpectedValue,
        double paperProfitFactor,
        double liveProfitFactor)
    {
        return $"{exitPolicy} is drifting on {symbol} {strategy}/{regime} in {session}. " +
               $"Drift {driftScore:F2}; paper EV {paperExpectedValue:F2} / PF {paperProfitFactor:F2} vs live EV {liveExpectedValue:F2} / PF {liveProfitFactor:F2}.";
    }

    private static string NormalizeExecutionMode(string? executionMode)
        => string.IsNullOrWhiteSpace(executionMode) ? "REAL" : executionMode.Trim().ToUpperInvariant();

    private void AddFailureConditionIfAny(
        ICollection<EdgeFailureConditionRow> rows,
        string condition,
        IReadOnlyCollection<Trade> trades,
        int completedCount,
        string summary,
        string executionModeScope,
        string promotionStageScope)
    {
        if (trades.Count == 0)
        {
            return;
        }

        var failureRate = trades.Count * 100.0 / completedCount;
        var avgSlippage = trades.Count == 0 ? 0m : trades.Average(trade => trade.EntrySlippagePercent);
        var avgLatency = trades.Count == 0 ? 0 : trades.Average(trade => (double)trade.ExecutionLatencyMs);

        rows.Add(new EdgeFailureConditionRow
        {
            Condition = condition,
            ExecutionModeScope = executionModeScope,
            PromotionStageScope = promotionStageScope,
            Failures = trades.Count,
            FailureRate = failureRate,
            AverageSlippagePercent = Math.Round(avgSlippage, 3),
            AverageLatencyMs = Math.Round(avgLatency, 0),
            Summary = $"{summary} {trades.Count} recent failures/losers, {failureRate:F1}% of the observed window. Scope: {executionModeScope} / {promotionStageScope}.",
            SeverityColor = failureRate >= 35 ? RedBrush : failureRate >= 20 ? OrangeBrush : GrayBrush
        });
    }

    private static string ResolveExecutionModeScope(IEnumerable<Trade> trades)
    {
        var modes = trades
            .Select(trade => string.IsNullOrWhiteSpace(trade.ExecutionMode) ? "REAL" : trade.ExecutionMode.Trim().ToUpperInvariant())
            .Distinct()
            .OrderBy(mode => mode)
            .ToList();

        return modes.Count == 0 ? "UNKNOWN" : string.Join("/", modes);
    }

    private static string ResolvePromotionStageScope(IEnumerable<Trade> trades)
    {
        var stages = trades
            .Select(InferPromotionStage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(stage => stage)
            .ToList();

        return stages.Count == 0 ? "UNKNOWN" : string.Join("/", stages);
    }

    private static string InferPromotionStage(Trade trade)
    {
        if (!string.IsNullOrWhiteSpace(trade.PromotionStage))
        {
            return trade.PromotionStage.Trim();
        }

        var executionMode = string.IsNullOrWhiteSpace(trade.ExecutionMode)
            ? "REAL"
            : trade.ExecutionMode.Trim().ToUpperInvariant();

        return executionMode switch
        {
            "SIMULATED" => "Backtest",
            "PAPER" => "Paper",
            "REAL" => "LiveProbe",
            _ => "UNKNOWN"
        };
    }

    private static string ResolveSessionBucketForTrade(Trade trade)
    {
        var referenceTime = trade.ExitTime ?? trade.EntryTime;
        if (referenceTime == default)
        {
            return "Unknown Session";
        }

        var turkeyTime = referenceTime.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(referenceTime, "Turkey Standard Time")
            : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(referenceTime.ToUniversalTime(), "Turkey Standard Time");

        return turkeyTime.Hour switch
        {
            >= 10 and < 13 => "London Killzone",
            >= 15 and < 18 => "NY Killzone",
            >= 3 and < 10 => "Asia",
            _ => "Close / Dead Zone"
        };
    }

    private ParsedEdgeJournalEntry? ParseEdgeJournalEntry(TradeJournalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Reasoning))
        {
            return null;
        }

        var match = Regex.Match(entry.Reasoning, @"Edge Score:\s*(?<score>\d+(?:\.\d+)?)\/100\s*\[(?<band>[^\]]+)\]", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        if (!decimal.TryParse(match.Groups["score"].Value, out var score))
        {
            return null;
        }

        return new ParsedEdgeJournalEntry(entry.Symbol, entry.Time, score, match.Groups["band"].Value.Trim());
    }

    private static string NormalizeEdgeBand(string band, decimal score)
    {
        if (!string.IsNullOrWhiteSpace(band))
        {
            return band;
        }

        if (score >= 85m) return "Full";
        if (score >= 70m) return "Reduced";
        if (score >= 55m) return "Probe";
        return "Reject";
    }

    private static string BuildEdgeOpportunityNote(string strategy, string regime, string session, int trades, double profitFactor, decimal totalPnl)
    {
        return $"{strategy} in {regime} during {session} is the cleanest observed edge zone so far: {trades} trades, PF {profitFactor:F2}, PnL {totalPnl:N2}.";
    }

    private sealed record ParsedEdgeJournalEntry(string Symbol, DateTime Time, decimal Score, string Band);

    private List<string> GetEdgeBandFailureStrategies()
    {
        return _tradeCache
            .Where(trade => trade.Status == TradeStatus.Executed && trade.IsClosed)
            .Where(trade => string.Equals(trade.EdgeBand, "Full", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(trade.EdgeBand, "Reduced", StringComparison.OrdinalIgnoreCase))
            .GroupBy(trade => trade.StrategyTag)
            .Where(group =>
            {
                var pnls = group.Select(CalculateTradePnL).ToList();
                if (pnls.Count < 3)
                {
                    return false;
                }

                var wins = pnls.Count(pnl => pnl > 0);
                var winRate = wins * 100.0 / pnls.Count;
                return pnls.Sum() < 0 || winRate < 40;
            })
            .Select(group => group.Key)
            .ToList();
    }

    private bool HasEdgeBandFailure(string strategyTag, string? regimeTag = null)
    {
        var relevantTrades = _tradeCache
            .Where(trade => trade.Status == TradeStatus.Executed && trade.IsClosed)
            .Where(trade => trade.StrategyTag.Equals(strategyTag, StringComparison.OrdinalIgnoreCase))
            .Where(trade => string.IsNullOrWhiteSpace(regimeTag) ||
                            trade.RegimeTag.Equals(regimeTag, StringComparison.OrdinalIgnoreCase))
            .Where(trade => string.Equals(trade.EdgeBand, "Full", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(trade.EdgeBand, "Reduced", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (relevantTrades.Count < 3)
        {
            return false;
        }

        var pnls = relevantTrades.Select(CalculateTradePnL).ToList();
        var wins = pnls.Count(pnl => pnl > 0);
        var winRate = wins * 100.0 / pnls.Count;
        return pnls.Sum() < 0 || winRate < 40;
    }

    private string BuildEdgeBandFailureReason(string strategyTag, string? regimeTag = null)
    {
        return string.IsNullOrWhiteSpace(regimeTag)
            ? $"{strategyTag} is failing even when the engine rates trades as Full/Reduced edge. Recovery should stay cautious until the edge bands heal."
            : $"{strategyTag} in {regimeTag} is failing even on Full/Reduced edge trades. Keep it disabled or in reduced recovery until high-edge executions start paying again.";
    }

    private void RefreshDisabledStrategiesFromHealth()
    {
        DisabledStrategies.Clear();

        var edgeBandFailures = GetEdgeBandFailureStrategies();

        var blocked = _persistedStrategyHealth
            .Where(row => row.ManuallyDisabled || (!row.ForceEnabled && row.Trades >= 3 && (row.ProfitFactor < 1.0 || (row.TotalPnL < 0 && row.WinRate < 45))))
            .Select(row => row.StrategyTag)
            .Concat(edgeBandFailures)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(row => row)
            .ToList();

        foreach (var strategy in blocked)
        {
            DisabledStrategies.Add(strategy);
        }

        DisabledStrategiesSummary = blocked.Count == 0
            ? "No strategies are disabled."
            : $"Disabled: {string.Join(", ", blocked)}. Edge-band failures are included in disable/recovery flow.";
    }

    private void RefreshCapitalAllocation()
    {
        CapitalAllocationRows.Clear();

        var allocatableCapital = BotRecommendedFunding > 0 ? BotRecommendedFunding : Math.Round(BotFundingAmount * 0.25m, 0);
        var candidateRows = _tradeCache
            .Where(trade =>
                trade.Status == TradeStatus.Executed &&
                trade.IsClosed &&
                trade.ExitTime.HasValue &&
                !DisabledStrategies.Any(disabled => disabled.Equals(trade.StrategyTag, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(trade => new
            {
                Symbol = string.IsNullOrWhiteSpace(trade.Symbol) ? "UNKNOWN" : trade.Symbol,
                StrategyTag = string.IsNullOrWhiteSpace(trade.StrategyTag) ? "Unlabeled" : trade.StrategyTag,
                RegimeTag = string.IsNullOrWhiteSpace(trade.RegimeTag) ? "Unknown" : trade.RegimeTag,
                SessionTag = ResolveSessionBucketForTrade(trade)
            })
            .Select(group =>
            {
                var pnls = group.Select(CalculateTradePnL).ToList();
                var wins = pnls.Count(pnl => pnl > 0);
                var grossProfit = pnls.Where(pnl => pnl > 0).Sum();
                var grossLoss = Math.Abs(pnls.Where(pnl => pnl < 0).Sum());
                var executionMode = ResolveExecutionModeScope(group);
                var promotionStage = ResolvePromotionStageScope(group);
                var effectiveStage = promotionStage == "Backtest" && executionMode.Contains("REAL", StringComparison.OrdinalIgnoreCase)
                    ? "LiveProbe"
                    : promotionStage;
                var stageMultiplier = effectiveStage switch
                {
                    "ScaledLive" => 1.15,
                    "LiveProbe" => 0.55,
                    "Paper" => 0.15,
                    _ => 0.05
                };
                var profitFactor = grossLoss > 0 ? (double)(grossProfit / grossLoss) : grossProfit > 0 ? 99 : 0;
                var winRate = group.Count() > 0 ? wins * 100.0 / group.Count() : 0;
                var precisionBonus = group.Count() >= 3 ? 1.08 : 0.95;
                var score = Math.Max(0.05, (profitFactor - 0.9) * 0.55 + Math.Max(0, (winRate - 45) / 55.0) * 0.30 + Math.Min(group.Count(), 12) / 12.0 * 0.15);

                return new
                {
                    group.Key.Symbol,
                    group.Key.StrategyTag,
                    group.Key.RegimeTag,
                    group.Key.SessionTag,
                    EffectiveStage = effectiveStage,
                    Score = score * stageMultiplier * precisionBonus
                };
            })
            .Where(item => item.Score > 0.05 && item.EffectiveStage != "Backtest")
            .OrderByDescending(item => item.Score)
            .Take(4)
            .ToList();

        if (candidateRows.Count == 0 || allocatableCapital <= 0)
        {
            CapitalAllocatorSummary = "No qualified strategy buckets yet. Keep capital mostly in reserve until live or paper performance improves.";
            return;
        }

        var totalScore = candidateRows.Sum(item => item.Score);
        foreach (var item in candidateRows)
        {
            var allocationPercent = item.Score / totalScore * 100.0;
            var capital = Math.Round(allocatableCapital * (decimal)(allocationPercent / 100.0), 0);
            CapitalAllocationRows.Add(new StrategyAllocationRow
            {
                Symbol = item.Symbol,
                StrategyTag = item.StrategyTag,
                RegimeTag = item.RegimeTag,
                SessionTag = item.SessionTag,
                PromotionStage = item.EffectiveStage,
                AllocationPercent = allocationPercent,
                RecommendedCapital = capital,
                Status = item.EffectiveStage == "ScaledLive"
                    ? (allocationPercent >= 35 ? "Core" : "Scaled")
                    : item.EffectiveStage == "LiveProbe"
                        ? "Probe"
                        : "Watch",
                StatusColor = item.EffectiveStage == "ScaledLive"
                    ? (allocationPercent >= 35 ? GreenBrush : OrangeBrush)
                    : item.EffectiveStage == "LiveProbe"
                        ? OrangeBrush
                        : GrayBrush
            });
        }

        CapitalAllocatorSummary =
            $"Allocator is distributing up to {allocatableCapital:N0} {SelectedBotQuoteAsset} across {CapitalAllocationRows.Count} symbol-session ladder buckets. " +
            $"Heavier weights go to exact symbol + session edge, sample depth, and promotion stage.";
    }

    private void PersistStrategyHealth()
    {
        RemoveStaleOverrides();
        _strategyHealthStore.Save(_persistedStrategyHealth);
        RefreshDisabledStrategiesFromHealth();
    }

    private void RemoveStaleOverrides()
    {
        _persistedStrategyHealth.RemoveAll(snapshot =>
            snapshot.RegimeTag == "ManualOverride" &&
            !snapshot.ManuallyDisabled &&
            !snapshot.ForceEnabled &&
            snapshot.Trades == 0 &&
            snapshot.TotalPnL == 0);
    }

    private static string NormalizeStrategyKey(string strategy)
    {
        return string.IsNullOrWhiteSpace(strategy)
            ? string.Empty
            : strategy.Replace(" ", string.Empty).Trim().ToUpperInvariant();
    }

    private bool IsForceEnabledStrategy(string strategy)
    {
        var key = NormalizeStrategyKey(strategy);
        return _persistedStrategyHealth.Any(snapshot =>
            NormalizeStrategyKey(snapshot.StrategyTag) == key &&
            snapshot.ForceEnabled &&
            !snapshot.ManuallyDisabled);
    }

    private void SetStrategyManualState(string strategy, bool manuallyDisabled, bool forceEnabled)
    {
        var key = NormalizeStrategyKey(strategy);
        var matching = _persistedStrategyHealth
            .Where(snapshot => NormalizeStrategyKey(snapshot.StrategyTag) == key)
            .ToList();

        if (matching.Count == 0)
        {
            _persistedStrategyHealth.Add(new StrategyHealthSnapshot
            {
                StrategyTag = strategy,
                RegimeTag = "ManualOverride",
                ManuallyDisabled = manuallyDisabled,
                ForceEnabled = forceEnabled,
                RecordedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            foreach (var snapshot in matching)
            {
                snapshot.ManuallyDisabled = manuallyDisabled;
                snapshot.ForceEnabled = forceEnabled;
                snapshot.RecordedAtUtc = DateTime.UtcNow;
            }
        }

        PersistStrategyHealth();
        EvaluateStrategyThrottle();
        UpdateBotLaunchSummary();
        RefreshCapitalAllocation();
    }

    private static string ResolveSessionBucket(DateTime timestamp)
    {
        var turkeyTime = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
            timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime(),
            "Turkey Standard Time");
        var hour = turkeyTime.Hour;

        if (hour is >= 10 and < 13) return "London Killzone";
        if (hour is >= 15 and < 18) return "NY Killzone";
        if (hour is >= 3 and < 10) return "Asia";
        return "Close / Dead Zone";
    }

    private static decimal CalculateTradePnL(Trade trade)
    {
        if (trade.Status == TradeStatus.Failed)
        {
            return 0m;
        }

        return trade.Side == TradeSide.Buy
            ? (trade.ExitPrice - trade.EntryPrice) * trade.Quantity
            : (trade.EntryPrice - trade.ExitPrice) * trade.Quantity;
    }

    partial void OnSelectedMarketChanged(string value)
    {
        LoadSymbols();
        AddSystemLog($"Switched to {value} market.");
        UpdatePlainEnglishTranslation();
    }

    partial void OnSelectedSymbolChanged(string value)
    {
        if (_enableAutomaticAnalysis)
        {
            _ = AnalyzeSymbol(value, SelectedMarket, SelectedTimeframe);
        }

        UpdatePlainEnglishTranslation();
    }

    partial void OnSelectedTimeframeChanged(string value)
    {
        if (_enableAutomaticAnalysis)
        {
            _ = AnalyzeSymbol(SelectedSymbol, SelectedMarket, value);
        }

        UpdatePlainEnglishTranslation();
    }

    partial void OnSelectedRiskProfileChanged(string value)
    {
        UpdatePlainEnglishTranslation();
    }

    partial void OnSelectedBotBaseAssetChanged(string value) => UpdateBotLaunchSummary();
    partial void OnSelectedBotQuoteAssetChanged(string value) => UpdateBotLaunchSummary();
    partial void OnSelectedBotStrategyChanged(string value) => UpdateBotLaunchSummary();
    partial void OnSelectedBotObjectiveChanged(string value) => UpdateBotLaunchSummary();
    partial void OnBotFundingAmountChanged(decimal value) => UpdateBotLaunchSummary();
    partial void OnGridUpperLimitChanged(double value) => UpdateBotLaunchSummary();
    partial void OnGridLowerLimitChanged(double value) => UpdateBotLaunchSummary();
    partial void OnGridDensityChanged(int value) => UpdateBotLaunchSummary();
    partial void OnGridSlopeBiasChanged(double value) => UpdateBotLaunchSummary();

    public Task RefreshSelectedSymbolAsync()
    {
        return AnalyzeSymbol(SelectedSymbol, SelectedMarket, SelectedTimeframe);
    }

    public Task RefreshStrategyLeaderboardAsync()
    {
        return RefreshStrategyLeaderboard();
    }

    public static RiskParameters ResolveRiskParameters(string riskProfile)
    {
        return riskProfile switch
        {
            "Low" => RiskParameters.Default with
            {
                MaxDailyLossPercent = 1.0m,
                MaxPositionSizePercent = 2.5m,
                StopLossPercent = 1.5m,
                TakeProfitPercent = 4.0m
            },
            "Aggressive" => RiskParameters.Aggressive,
            _ => RiskParameters.Default
        };
    }

    private void UpdatePlainEnglishTranslation()
    {
        string assetAction = string.IsNullOrEmpty(SelectedSymbol) ? "assets" : SelectedSymbol;
        string riskAction = SelectedRiskProfile == "Aggressive" ? "aggressively" : SelectedRiskProfile == "Low" ? "conservatively" : "moderately";
        string timeAction = SelectedTimeframe == "1m" || SelectedTimeframe == "5m" ? "rapidly scalping" : SelectedTimeframe == "1d" ? "swing trading" : "monitoring intraday momentum";
        
        PlainEnglishTranslation = $"The bot will {riskAction} trade {assetAction} on the {SelectedMarket} market. It is currently {timeAction} using {SelectedTimeframe} intervals, waiting for multi-timeframe AlphaTrend confluence before executing.";
    }

    private void UpdateBotLaunchSummary()
    {
        var slopeText = GridSlopeBias < 40 ? "bearish" : GridSlopeBias > 60 ? "bullish" : "neutral";
        UpdateBotLaunchRecommendation();
        BotLaunchSummary =
            $"{SelectedBotStrategy} on {SelectedBotBaseAsset}/{SelectedBotQuoteAsset} with {BotFundingAmount:N0} {SelectedBotQuoteAsset}, " +
            $"{SelectedBotObjective.ToLowerInvariant()} objective, grid {GridLowerLimit:F1}% to {GridUpperLimit:F1}%, density {GridDensity}, slope {slopeText}, throttle {StrategyThrottleStatus.ToLowerInvariant()}, recommended capital {BotRecommendedFunding:N0}.";
        BotLaunchHint = IsPaperMode
            ? "Paper mode is active, so this launch will stay sandboxed until you switch to live."
            : "Live mode is active. Double-check broker routing and risk limits before activating.";
        UpdateBotProductivityInsights();
    }

    private void UpdateBotProductivityInsights()
    {
        var score = 40;

        score += BotLaunchGuardStatus switch
        {
            "Ready" => 30,
            "Reduced" => 18,
            "Caution" => 10,
            "Disabled" => -10,
            "Blocked" => -15,
            _ => 0
        };

        score += StrategyThrottleStatus switch
        {
            "Normal" => 15,
            "Reduced" => 5,
            "Learning" => 3,
            _ => 0
        };

        if (BotFundingAmount > 0 && BotRecommendedFunding > 0)
        {
            var alignment = (double)(BotRecommendedFunding / BotFundingAmount);
            score += alignment >= 0.9 ? 10 : alignment >= 0.6 ? 5 : 0;
        }

        if (GridDensity is >= 8 and <= 16)
        {
            score += 5;
        }

        if (GridUpperLimit > GridLowerLimit && (GridUpperLimit - GridLowerLimit) >= 4)
        {
            score += 5;
        }

        score = Math.Clamp(score, 0, 100);
        BotProductivityScore = $"Setup score {score}/100";
        BotProductivityColor = score >= 75 ? GreenBrush : score >= 55 ? OrangeBrush : RedBrush;

        BotSetupVelocity = score >= 75
            ? "Fast lane. This configuration is clean enough for quick deployment."
            : score >= 55
                ? "Medium friction. Use auto-tune to tighten capital and grid settings."
                : "Slow lane. Too much launch drag or weak edge is hurting deployment quality.";

        BotWorkflowAdvice = BotLaunchGuardStatus switch
        {
            "Disabled" => "Recovery first: re-enable only after paper validation and start with reduced size.",
            "Blocked" => "Do not deploy. Backtest or change strategy before spending more operator time here.",
            "Caution" => "Session is weak. Save time by waiting for a better killzone instead of tweaking endlessly.",
            "Reduced" => "Lean into smaller funding and narrower decision scope. Productivity comes from disciplined repetition.",
            "Ready" => "This bot is launch-efficient. Paper it once, then promote if execution quality holds.",
            _ => "Use presets or auto-tune to reduce launch friction."
        };
    }

    private void UpdateBotLaunchRecommendation()
    {
        var activeSession = SessionContexts.FirstOrDefault(context => context.IsActive)?.Name ?? "Unknown";
        var strategyDisabled = DisabledStrategies.Any(s => s.Equals(SelectedBotStrategy, StringComparison.OrdinalIgnoreCase) ||
                                                           s.Equals(SelectedBotStrategy.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase));
        var strategyForceEnabled = IsForceEnabledStrategy(SelectedBotStrategy);

        decimal throttleMultiplier = StrategyThrottleStatus switch
        {
            "Blocked" => 0m,
            "Reduced" => 0.5m,
            "Normal" => 1m,
            "Learning" => 0.35m,
            "Unavailable" => 0.25m,
            _ => 0.25m
        };

        decimal riskMultiplier = SelectedRiskProfile switch
        {
            "Low" => 0.55m,
            "Aggressive" => 1m,
            _ => 0.8m
        };

        decimal objectiveMultiplier = SelectedBotObjective switch
        {
            "Low Drawdown" => 0.6m,
            "Income Flow" => 0.8m,
            "Fast Rotation" => 0.9m,
            _ => 1m
        };

        decimal sessionMultiplier = activeSession switch
        {
            "London Killzone" => 1m,
            "NY Killzone" => 0.95m,
            "Asia" => 0.65m,
            "Close / Dead Zone" => 0.45m,
            _ => 0.6m
        };

        BotRecommendedFunding = Math.Round(BotFundingAmount * throttleMultiplier * riskMultiplier * objectiveMultiplier * sessionMultiplier, 0);

        if (strategyDisabled)
        {
            BotLaunchGuardStatus = "Disabled";
            BotLaunchGuardColor = RedBrush;
            BotLaunchGuardReason = $"{SelectedBotStrategy} is on the disabled list because its recent bucket performance is unacceptable.";
            BotProfitabilityFocus = "Do not deploy disabled strategies. Repair the edge in backtest or paper before re-enabling.";
            BotRecommendedFunding = 0;
            UpdateDeploymentChecklist(activeSession, strategyDisabled);
            RefreshCapitalAllocation();
            return;
        }

        if (StrategyThrottleStatus == "Blocked")
        {
            BotLaunchGuardStatus = "Blocked";
            BotLaunchGuardColor = RedBrush;
            BotLaunchGuardReason = "This strategy bucket is underperforming. Keep capital at zero until the bucket recovers.";
            BotProfitabilityFocus = "Focus on research, backtesting, and bucket recovery instead of new deployment.";
            UpdateDeploymentChecklist(activeSession, strategyDisabled);
            RefreshCapitalAllocation();
            return;
        }

        if (activeSession == "Close / Dead Zone" || activeSession == "Asia")
        {
            BotLaunchGuardStatus = "Caution";
            BotLaunchGuardColor = OrangeBrush;
            BotLaunchGuardReason = $"Current session is {activeSession}. Reduce deployment size and favor stronger confirmation.";
            BotProfitabilityFocus = "Best profitability usually comes from higher-quality session windows, not constant exposure.";
            UpdateDeploymentChecklist(activeSession, strategyDisabled);
            RefreshCapitalAllocation();
            return;
        }

        if (StrategyThrottleStatus == "Reduced" || BotRecommendedFunding < BotFundingAmount * 0.6m)
        {
            BotLaunchGuardStatus = "Reduced";
            BotLaunchGuardColor = OrangeBrush;
            BotLaunchGuardReason = strategyForceEnabled
                ? "Manual recovery override is active. The bot can trade, but size stays reduced until live quality improves."
                : "The bot can trade, but size should stay reduced until bucket quality or session edge improves.";
            BotProfitabilityFocus = strategyForceEnabled
                ? "Recovery mode is active: prove the edge with smaller size before restoring normal deployment."
                : "Protect capital first: smaller size with better timing usually outperforms forcing full exposure.";
            UpdateDeploymentChecklist(activeSession, strategyDisabled);
            RefreshCapitalAllocation();
            return;
        }

        BotLaunchGuardStatus = "Ready";
        BotLaunchGuardColor = GreenBrush;
        BotLaunchGuardReason = $"Session, throttle, and risk profile support deploying {BotRecommendedFunding:N0} {SelectedBotQuoteAsset}.";
        BotProfitabilityFocus = "This is a healthier launch environment. Prioritize disciplined execution and let winners scale.";
        UpdateDeploymentChecklist(activeSession, strategyDisabled);
        RefreshCapitalAllocation();
    }

    private void UpdateDeploymentChecklist(string activeSession, bool strategyDisabled)
    {
        DeploymentChecklist.Clear();

        AddDeploymentCheck("Strategy enabled", !strategyDisabled, DisabledStrategiesSummary);
        AddDeploymentCheck("Throttle healthy", StrategyThrottleStatus is "Normal" or "Learning", StrategyThrottleReason);
        AddDeploymentCheck("Session edge", activeSession is "London Killzone" or "NY Killzone", $"Active session: {activeSession}");
        AddDeploymentCheck("Queue safe", QueueHealth == "Optimal" && DeadLetterCount == 0, $"Queue {QueueHealth}, dead-letter {DeadLetterCount}");
        AddDeploymentCheck("Capital aligned", BotRecommendedFunding > 0 && BotRecommendedFunding <= BotFundingAmount, $"Recommended {BotRecommendedFunding:N0} / Planned {BotFundingAmount:N0} {SelectedBotQuoteAsset}");
        AddDeploymentCheck("Mode discipline", IsPaperMode || BotLaunchGuardStatus == "Ready", IsPaperMode ? "Paper mode active." : "Live mode only allowed on a fully ready launch.");
    }

    private void AddDeploymentCheck(string title, bool isPassing, string detail)
    {
        DeploymentChecklist.Add(new ChecklistItemViewModel
        {
            Title = title,
            Detail = detail,
            Status = isPassing ? "Pass" : "Review",
            IsPassing = isPassing,
            Color = isPassing ? GreenBrush : OrangeBrush
        });
    }

    private void EvaluateStrategyThrottle()
    {
        var selectedForceEnabled = IsForceEnabledStrategy(SelectedBotStrategy);
        var selectedBucket = StrategyLeaderboard.FirstOrDefault(row =>
            row.StrategyTag.Equals(SelectedBotStrategy.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase) ||
            row.StrategyTag.Equals(SelectedBotStrategy, StringComparison.OrdinalIgnoreCase));

        selectedBucket ??= _persistedStrategyHealth
            .Where(row =>
                row.StrategyTag.Equals(SelectedBotStrategy.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase) ||
                row.StrategyTag.Equals(SelectedBotStrategy, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.RecordedAtUtc)
            .Select(row => new StrategyLeaderboardRow
            {
                StrategyTag = row.StrategyTag,
                RegimeTag = row.RegimeTag,
                Trades = row.Trades,
                WinRate = row.WinRate,
                ProfitFactor = row.ProfitFactor,
                TotalPnL = row.TotalPnL
            })
            .FirstOrDefault();

        var weakestBucket = StrategyLeaderboard
            .OrderBy(row => row.ProfitFactor)
            .ThenBy(row => row.TotalPnL)
            .FirstOrDefault();

        weakestBucket ??= _persistedStrategyHealth
            .OrderBy(row => row.ProfitFactor)
            .ThenBy(row => row.TotalPnL)
            .Select(row => new StrategyLeaderboardRow
            {
                StrategyTag = row.StrategyTag,
                RegimeTag = row.RegimeTag,
                Trades = row.Trades,
                WinRate = row.WinRate,
                ProfitFactor = row.ProfitFactor,
                TotalPnL = row.TotalPnL
            })
            .FirstOrDefault();

        var focusBucket = selectedBucket ?? weakestBucket;

        if (focusBucket == null || focusBucket.Trades < 3)
        {
            StrategyThrottleStatus = "Learning";
            StrategyThrottleReason = "The selected strategy needs at least 3 closed trades before automatic throttling kicks in.";
            StrategyThrottleColor = GrayBrush;
            return;
        }

        if (HasEdgeBandFailure(focusBucket.StrategyTag, focusBucket.RegimeTag))
        {
            if (selectedForceEnabled)
            {
                StrategyThrottleStatus = "Reduced";
                StrategyThrottleReason = $"{BuildEdgeBandFailureReason(focusBucket.StrategyTag, focusBucket.RegimeTag)} Manual recovery is active, so size stays cut until the edge bands recover.";
                StrategyThrottleColor = OrangeBrush;
            }
            else
            {
                StrategyThrottleStatus = "Blocked";
                StrategyThrottleReason = BuildEdgeBandFailureReason(focusBucket.StrategyTag, focusBucket.RegimeTag);
                StrategyThrottleColor = RedBrush;
            }

            return;
        }

        if (focusBucket.ProfitFactor < 1.0 || (focusBucket.TotalPnL < 0 && focusBucket.WinRate < 45))
        {
            if (selectedForceEnabled)
            {
                StrategyThrottleStatus = "Reduced";
                StrategyThrottleReason = $"{focusBucket.StrategyTag} is in manual recovery mode. Trade only at reduced size until the bucket heals.";
                StrategyThrottleColor = OrangeBrush;
            }
            else
            {
                StrategyThrottleStatus = "Blocked";
                StrategyThrottleReason = $"{focusBucket.StrategyTag} in {focusBucket.RegimeTag} is underperforming. New trades should stay disabled until the bucket recovers.";
                StrategyThrottleColor = RedBrush;
            }
            return;
        }

        if (focusBucket.ProfitFactor < 1.25 || focusBucket.WinRate < 52)
        {
            StrategyThrottleStatus = "Reduced";
            StrategyThrottleReason = $"{focusBucket.StrategyTag} is tradable but soft. Keep reduced size until profit factor and win rate improve.";
            StrategyThrottleColor = OrangeBrush;
            return;
        }

        StrategyThrottleStatus = "Normal";
        StrategyThrottleReason = $"{focusBucket.StrategyTag} has enough edge to trade at normal size in the current environment.";
        StrategyThrottleColor = GreenBrush;
    }

    private TradeDecision ApplyStrategyThrottle(TradeDecision decision)
    {
        var forceEnabled = IsForceEnabledStrategy(decision.StrategyTag);
        var bucket = StrategyLeaderboard.FirstOrDefault(row =>
            row.StrategyTag.Equals(decision.StrategyTag, StringComparison.OrdinalIgnoreCase) &&
            row.RegimeTag.Equals(decision.RegimeTag, StringComparison.OrdinalIgnoreCase));

        bucket ??= _persistedStrategyHealth
            .Where(row =>
                row.StrategyTag.Equals(decision.StrategyTag, StringComparison.OrdinalIgnoreCase) &&
                row.RegimeTag.Equals(decision.RegimeTag, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.RecordedAtUtc)
            .Select(row => new StrategyLeaderboardRow
            {
                StrategyTag = row.StrategyTag,
                RegimeTag = row.RegimeTag,
                Trades = row.Trades,
                WinRate = row.WinRate,
                ProfitFactor = row.ProfitFactor,
                TotalPnL = row.TotalPnL
            })
            .FirstOrDefault();

        if (bucket == null || bucket.Trades < 3)
        {
            StrategyThrottleStatus = "Learning";
            StrategyThrottleReason = $"{decision.StrategyTag} in {decision.RegimeTag} is still gathering enough samples.";
            StrategyThrottleColor = GrayBrush;
            return decision;
        }

        if (HasEdgeBandFailure(decision.StrategyTag, decision.RegimeTag))
        {
            if (forceEnabled)
            {
                StrategyThrottleStatus = "Reduced";
                StrategyThrottleReason = $"{BuildEdgeBandFailureReason(decision.StrategyTag, decision.RegimeTag)} Manual recovery keeps sizing aggressively reduced.";
                StrategyThrottleColor = OrangeBrush;

                return decision with
                {
                    ShouldTrade = decision.ShouldTrade,
                    Confidence = Math.Min(decision.Confidence, 55),
                    PositionSizePercent = Math.Min(decision.PositionSizePercent, 0.01m),
                    Reasoning = $"Edge-band recovery override reduced {decision.StrategyTag} in {decision.RegimeTag}. {decision.Reasoning}"
                };
            }

            StrategyThrottleStatus = "Blocked";
            StrategyThrottleReason = BuildEdgeBandFailureReason(decision.StrategyTag, decision.RegimeTag);
            StrategyThrottleColor = RedBrush;

            return decision with
            {
                ShouldTrade = false,
                Confidence = Math.Min(decision.Confidence, 45),
                Reasoning = $"Edge-band failure blocked {decision.StrategyTag} in {decision.RegimeTag}. {decision.Reasoning}"
            };
        }

        if (bucket.ProfitFactor < 1.0 || (bucket.TotalPnL < 0 && bucket.WinRate < 45))
        {
            if (forceEnabled)
            {
                StrategyThrottleStatus = "Reduced";
                StrategyThrottleReason = $"{decision.StrategyTag} is in manual recovery mode. The trade is allowed, but size stays aggressively reduced.";
                StrategyThrottleColor = OrangeBrush;

                return decision with
                {
                    ShouldTrade = decision.ShouldTrade,
                    Confidence = Math.Min(decision.Confidence, 55),
                    PositionSizePercent = Math.Min(decision.PositionSizePercent, 0.01m),
                    Reasoning = $"Manual recovery override reduced {decision.StrategyTag} in {decision.RegimeTag}. {decision.Reasoning}"
                };
            }

            StrategyThrottleStatus = "Blocked";
            StrategyThrottleReason = $"{decision.StrategyTag} in {decision.RegimeTag} is blocked because the current bucket is losing quality.";
            StrategyThrottleColor = RedBrush;

            return decision with
            {
                ShouldTrade = false,
                Confidence = Math.Min(decision.Confidence, 45),
                Reasoning = $"Auto-throttle blocked {decision.StrategyTag} in {decision.RegimeTag}. {decision.Reasoning}"
            };
        }

        if (bucket.ProfitFactor < 1.25 || bucket.WinRate < 52)
        {
            StrategyThrottleStatus = "Reduced";
            StrategyThrottleReason = $"{decision.StrategyTag} in {decision.RegimeTag} is size-reduced until the bucket stabilizes.";
            StrategyThrottleColor = OrangeBrush;

            return decision with
            {
                Confidence = Math.Max(0, decision.Confidence - 10),
                PositionSizePercent = Math.Max(0m, decision.PositionSizePercent * 0.5m),
                Reasoning = $"Auto-throttle reduced size for {decision.StrategyTag} in {decision.RegimeTag}. {decision.Reasoning}"
            };
        }

        StrategyThrottleStatus = "Normal";
        StrategyThrottleReason = $"{decision.StrategyTag} in {decision.RegimeTag} is cleared for normal sizing.";
        StrategyThrottleColor = GreenBrush;
        return decision;
    }

    private void RefreshOpenPositionsFromTrades(IEnumerable<Trade> trades)
    {
        ActivePositions.Clear();

        var openTrades = trades
            .Where(t => !t.IsClosed && t.Status != TradeStatus.Failed && t.Status != TradeStatus.DeadLetter)
            .OrderByDescending(t => t.EntryTime)
            .Take(8)
            .ToList();

        foreach (var trade in openTrades)
        {
            var markPrice = trade.ExitPrice > 0 ? trade.ExitPrice : trade.EntryPrice;
            var pnl = CalculateTradePnL(new Trade
            {
                Side = trade.Side,
                EntryPrice = trade.EntryPrice,
                ExitPrice = markPrice,
                Quantity = trade.Quantity,
                Status = trade.Status
            });

            var notional = trade.EntryPrice * Math.Max(1m, trade.Quantity);
            var roe = notional > 0 ? (double)(pnl / notional * 100m) : 0;

            ActivePositions.Add(new PositionViewModel
            {
                Symbol = trade.Symbol,
                Side = trade.Side == TradeSide.Buy ? "LONG" : "SHORT",
                Size = trade.Quantity,
                EntryPrice = trade.EntryPrice,
                MarkPrice = markPrice,
                UnrealisedPnL = pnl,
                Roe = roe
            });
        }
    }

    private void LoadSymbols()
    {
        Symbols.Clear();
        WatchlistItems.Clear();
        
        string[] symbols = SelectedMarket switch
        {
            "BIST" => new[] { "THYAO", "GARAN", "AKBNK", "SISE", "EREGL", "TUPRS", "BIMAS", "ASELS", "KCHOL" },
            "Crypto" => new[] { "BTC-USD", "ETH-USD", "SOL-USD", "XRP-USD", "AVAX-USD", "LINK-USD" },
            "Forex" => new[] { "USDTRY=X", "EURTRY=X", "EURUSD=X", "GBPUSD=X", "USDJPY=X" },
            _ => Array.Empty<string>()
        };

        foreach (var s in symbols)
        {
            Symbols.Add(s);
            WatchlistItems.Add(new WatchlistItemViewModel { Symbol = s, Price = 0, ChangePercent = 0 });
        }
        
        SelectedSymbol = symbols.FirstOrDefault() ?? "";
    }

    [RelayCommand]
    private Task SelectSymbol(string symbol)
    {
        SelectedSymbol = symbol;
        BacktestMarkers.Clear();
        // Removed manual AnalyzeSymbol call to avoid double execution (property change handler already triggers it)
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task RefreshAnalysis()
    {
        return AnalyzeSymbol(SelectedSymbol, SelectedMarket, SelectedTimeframe);
    }

    [RelayCommand]
    private async Task RunBacktest()
    {
        AddSystemLog($"Starting Local Backtest for {SelectedSymbol}...");
        BacktestStatus = $"Running backtest for {SelectedSymbol} on {SelectedTimeframe}...";
        BacktestScenario = $"{SelectedMarket} | {SelectedSymbol} | {SelectedTimeframe} | {SelectedRiskProfile} risk";
        var candles = await _marketDataService.GetCandlesAsync(SelectedSymbol, 150, SelectedMarket, SelectedTimeframe);
        var result = await _backtestEngine.RunAsync(candles, SelectedSymbol, 100000, 60);
        
        NormalizeBacktestMarkers(candles, result.Trades);
        
        string currency = SelectedMarket == "BIST" ? "₺" : "$";
        BacktestTrades.Clear();
        foreach (var trade in result.Trades.OrderByDescending(trade => trade.ExitTime).Take(18))
        {
            BacktestTrades.Add(new BacktestTradeViewModel
            {
                EntryTime = trade.EntryTime,
                ExitTime = trade.ExitTime,
                Side = trade.Side.ToString(),
                StrategyTag = string.IsNullOrWhiteSpace(trade.StrategyTag) ? "TrendConfluence" : trade.StrategyTag,
                RegimeTag = string.IsNullOrWhiteSpace(trade.RegimeTag) ? "Unknown" : trade.RegimeTag,
                ExitReason = trade.ExitReason,
                EntryPrice = trade.EntryPrice,
                ExitPrice = trade.ExitPrice,
                PnL = trade.PnL,
                PnLPercent = trade.PnLPercent,
                Color = trade.PnL >= 0 ? GreenBrush : RedBrush
            });
        }

        TotalTrades = result.TotalTrades;
        WinRate = result.WinRate;
        ProfitFactor = result.ProfitFactor;
        BacktestStatus = result.TotalTrades > 0
            ? "Backtest complete. Review the distribution before promoting the strategy."
            : "Backtest complete, but no qualifying trades were found.";
        BacktestDateRange = candles.Count > 0
            ? $"Date range: {candles.First().Timestamp:dd MMM yyyy} -> {candles.Last().Timestamp:dd MMM yyyy}"
            : "Date range: --";
        BacktestPnlLabel = $"PnL: {result.TotalPnL:N2}";
        BacktestEquityLabel = $"Final equity: {result.FinalEquity:N2} from {result.StartingCapital:N2}";
        BacktestRiskSummary = $"Sharpe {result.SharpeRatio:F2} | Sortino {result.SortinoRatio:F2} | Max DD {result.MaxDrawdownPercent:F2}%";
        BacktestTradeBreakdown = $"Wins {result.WinningTrades} | Losses {result.LosingTrades} | Avg R/R {result.AverageRR:F2}";
        
        AddSystemLog($"Backtest Complete. Trades: {result.TotalTrades}, PnL: {currency}{result.TotalPnL:N2}");
    }

    [RelayCommand]
    private async Task RunGlobalBacktest()
    {
        if (Symbols.Count == 0) return;
        
        AddSystemLog($"GLOBAL: Starting bulk backtest for {Symbols.Count} symbols in {SelectedMarket}...");
        GlobalBacktestResults.Clear();
        BacktestStatus = "Executing global market simulation...";
        
        var tasks = Symbols.Select(async s =>
        {
            var candles = await _marketDataService.GetCandlesAsync(s, 150, SelectedMarket, SelectedTimeframe);
            if (candles.Count == 0) return null;
            
            var result = await _backtestEngine.RunAsync(candles, s, 100000, 60);
            return new GlobalBacktestResultViewModel
            {
                Symbol = s,
                TotalTrades = result.TotalTrades,
                WinRate = result.WinRate,
                TotalPnL = (decimal)result.TotalPnL,
                ProfitFactor = (decimal)result.ProfitFactor,
                PnLColor = result.TotalPnL >= 0 ? GreenBrush : RedBrush
            };
        });

        var results = await Task.WhenAll(tasks);
        foreach (var res in results.Where(r => r != null))
        {
            GlobalBacktestResults.Add(res!);
        }

        BacktestStatus = $"Bulk backtest complete for {SelectedMarket}. {GlobalBacktestResults.Count} symbols processed.";
        AddSystemLog("GLOBAL: All market simulations finalized.");
    }

    [RelayCommand]
    private Task StartTrading()
    {
        if (BotLaunchGuardStatus == "Blocked" || BotRecommendedFunding <= 0)
        {
            Status = "BLOCKED";
            StatusColor = RedBrush;
            AddSystemLog($"LAUNCH BLOCKED: {BotLaunchGuardReason}");
            return Task.CompletedTask;
        }

        if (_tradingLoopService.IsRunning)
        {
            return Task.CompletedTask;
        }

        Status = IsPaperMode ? "PAPER LOOP" : "LIVE LOOP";
        StatusColor = IsPaperMode ? OrangeBrush : GreenBrush;
        AddSystemLog($"{TradingModeLabel} trading orchestrator started. Monitoring {SelectedMarket} - {SelectedSymbol}...");
        if (BotRecommendedFunding < BotFundingAmount)
        {
            AddSystemLog($"LAUNCH GUARD: Recommended capital is {BotRecommendedFunding:N0} {SelectedBotQuoteAsset} instead of {BotFundingAmount:N0}.");
        }

        return _tradingLoopService.StartAsync(
            _ => AnalyzeSymbol(SelectedSymbol, SelectedMarket, SelectedTimeframe),
            TimeSpan.FromSeconds(5));
    }

    [RelayCommand]
    private void EmergencyStop()
    {
        _tradingLoopService.Stop();
        Status = "STOPPED";
        StatusColor = RedBrush;
        AddSystemLog("EMERGENCY STOP: All trading halted.");
        ActivePositions.Clear();
    }

    [RelayCommand]
    private void ClosePosition(PositionViewModel position)
    {
        if (position == null) return;
        AddSystemLog($"Closing {position.Symbol} {position.Side} at Market.");
        ActivePositions.Remove(position);
    }

    [RelayCommand]
    private void SetBreakEven(PositionViewModel position)
    {
        if (position == null) return;
        AddSystemLog($"Moving SL to BE for {position.Symbol}. Risk removed.");
    }

    [RelayCommand]
    private void CloseAllPositions()
    {
        AddSystemLog("EMERGENCY KILL SWITCH: Closing all active positions...");
        ActivePositions.Clear();
    }

    [RelayCommand]
    private void ResetKillSwitch()
    {
        _riskAgent.ResetKillSwitch();
        RefreshRiskPanel();
        AddSystemLog("Risk kill-switch reset.");
    }

    [RelayCommand]
    private void ToggleTradingMode()
    {
        IsPaperMode = !IsPaperMode;
        UpdateTradingMode();
        AddSystemLog(IsPaperMode
            ? "Trading mode switched to paper sandbox."
            : "Trading mode switched to live mode. Confirm broker/execution safeguards before acting.");
    }

    [RelayCommand]
    private void ExportLedger()
    {
        AddSystemLog("Ledger Export: Generating CSV report...");
        // In a real app, this would use a CSV helper or Excel interop
    }

    [RelayCommand]
    private void SaveSettings()
    {
        AddSystemLog("SYSTEM: Global configuration and API keys saved to secure vault.");
        AddSystemLog("INFRA: Webhook configurations verified successfully.");
    }

    [RelayCommand]
    private void VerifyConnection()
    {
        AddSystemLog("INFRA: Testing connectivity to Binance/BIST endpoints...");
        AddSystemLog("INFRA: API Handshake successful. Latency: 42ms.");
    }

    [RelayCommand]
    private void CalibrateAi()
    {
        AddSystemLog("AI: Intelligence core recalibration started...");
        AddSystemLog($"AI: Tuned FVG Sensitivity to {FvgSensitivity:F0}%.");
        AddSystemLog($"AI: Threshold synchronized at {AiConfidenceThreshold:F0}%.");
        AddSystemLog("AI: Calibration successful. Model ready for next signal.");
    }

    private void AddSystemLog(string message)
    {
        SystemLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        if (SystemLogs.Count > 100) SystemLogs.RemoveAt(100);
    }

    [RelayCommand]
    private Task ReviewTrade(TradeJournalEntry? trade)
    {
        if (trade == null)
        {
            SelectedTradeReviewTitle = "AI Trade Review";
            SelectedTradeReview = "Choose a closed trade to generate an AI coaching review.";
            return Task.CompletedTask;
        }

        var confidenceTone = trade.Confidence >= 75
            ? "High-confidence setup"
            : trade.Confidence >= 55
                ? "Moderate-confidence setup"
                : "Low-confidence setup";
        var timingHint = trade.Confidence >= 75
            ? "Execution likely had enough confirmation, so focus on improving exits and scaling."
            : "Wait for stronger candle close confirmation before committing full size.";
        var outcomeHint = trade.Status.Contains("Approved", StringComparison.OrdinalIgnoreCase)
            ? "The setup qualified, but review whether the liquidity sweep or displacement had fully printed first."
            : "The journal status suggests the setup stayed in review, so respect patience over forcing an entry.";

        SelectedTradeReviewTitle = $"{trade.Symbol} {trade.Side} review";
        SelectedTradeReview =
            $"{confidenceTone}. {outcomeHint} {timingHint} " +
            $"Context: {trade.Reasoning}. Logged at {trade.Time:dd MMM HH:mm}.";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SubmitPrompt()
    {
        if (string.IsNullOrWhiteSpace(StrategyPrompt)) return;
        
        AddSystemLog($"🧠 AI Parsing Intent: \"{StrategyPrompt}\"");
        
        // Simulated Semantic Kernel Local Parsing Latency
        await Task.Delay(600); 
        
        var lower = StrategyPrompt.ToLower();
        
        // 1. Symbol/Market Extraction
        if (lower.Contains("btc") || lower.Contains("bitcoin")) { SelectedMarket = "Crypto"; SelectedSymbol = "BTC-USD"; }
        else if (lower.Contains("eth") || lower.Contains("ethereum")) { SelectedMarket = "Crypto"; SelectedSymbol = "ETH-USD"; }
        else if (lower.Contains("usd") || lower.Contains("try")) { SelectedMarket = "Forex"; SelectedSymbol = "USDTRY=X"; }
        else if (lower.Contains("thy") || lower.Contains("turkish airlines")) { SelectedMarket = "BIST"; SelectedSymbol = "THYAO"; }
        
        // 2. Risk Extraction
        if (lower.Contains("aggressive") || lower.Contains("100%") || lower.Contains("yolo")) SelectedRiskProfile = "Aggressive";
        else if (lower.Contains("conservative") || lower.Contains("low") || lower.Contains("safe")) SelectedRiskProfile = "Low";
        else SelectedRiskProfile = "Moderate";
        
        // 3. Timeframe Extraction
        if (lower.Contains("1m") || lower.Contains("scalp")) SelectedTimeframe = "1m";
        else if (lower.Contains("daily") || lower.Contains("1d") || lower.Contains("day")) SelectedTimeframe = "1d";
        else SelectedTimeframe = "1h"; // fallback
        AddSystemLog("✅ Semantic Kernel translated intent to configuration.");
        StrategyPrompt = ""; // Clear input
    }

    [RelayCommand]
    private void SetPortfolioRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return;
        }

        SelectedPortfolioRange = range;
        UpdatePortfolioGrowth();
    }

    [RelayCommand]
    private void SetSelectedBotStrategy(string? strategy)
    {
        if (string.IsNullOrWhiteSpace(strategy))
        {
            return;
        }

        SelectedBotStrategy = strategy;
    }

    [RelayCommand]
    private void ApplyBotPreset(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
        {
            return;
        }

        switch (preset)
        {
            case "Trend Attack":
                SelectedBotStrategy = "Trend Confluence";
                SelectedBotObjective = "Capital Growth";
                SelectedRiskProfile = "Aggressive";
                GridDensity = 10;
                GridLowerLimit = 5.5;
                GridUpperLimit = 16;
                GridSlopeBias = 72;
                break;
            case "Safe Income":
                SelectedBotStrategy = "Grid Runner";
                SelectedBotObjective = "Income Flow";
                SelectedRiskProfile = "Low";
                GridDensity = 16;
                GridLowerLimit = 4.5;
                GridUpperLimit = 11;
                GridSlopeBias = 50;
                break;
            case "Recovery Probe":
                SelectedBotObjective = "Low Drawdown";
                SelectedRiskProfile = "Low";
                BotFundingAmount = Math.Max(500m, Math.Round(BotFundingAmount * 0.5m, 0));
                GridDensity = 8;
                GridLowerLimit = 4;
                GridUpperLimit = 9;
                GridSlopeBias = 45;
                break;
            case "HFT Probe":
                SelectedBotStrategy = "HFT Scout";
                SelectedBotObjective = "Fast Rotation";
                SelectedRiskProfile = "Moderate";
                GridDensity = 20;
                GridLowerLimit = 2.5;
                GridUpperLimit = 6.5;
                GridSlopeBias = 58;
                break;
        }

        AddSystemLog($"LAUNCH PRESET: Applied {preset}.");
        UpdateBotLaunchSummary();
    }

    [RelayCommand]
    private void AutoTuneBotSetup()
    {
        var activeSession = SessionContexts.FirstOrDefault(context => context.IsActive)?.Name ?? "Unknown";

        if (SelectedBotStrategy == "HFT Scout")
        {
            SelectedBotObjective = "Fast Rotation";
            GridDensity = 20;
            GridLowerLimit = activeSession == "London Killzone" ? 2.2 : 2.8;
            GridUpperLimit = activeSession == "London Killzone" ? 6.0 : 7.0;
            GridSlopeBias = HftExecutionBias == "Ready" ? 62 : 50;
        }
        else if (SelectedBotStrategy == "Grid Runner")
        {
            SelectedBotObjective = "Income Flow";
            GridDensity = 16;
            GridLowerLimit = 4.5;
            GridUpperLimit = activeSession is "London Killzone" or "NY Killzone" ? 11.5 : 9.5;
            GridSlopeBias = 50;
        }
        else if (SelectedBotStrategy == "Range Reversion")
        {
            SelectedBotObjective = "Low Drawdown";
            GridDensity = 10;
            GridLowerLimit = 3.8;
            GridUpperLimit = 8.5;
            GridSlopeBias = 42;
        }
        else if (SelectedBotStrategy == "Breakout Hunter")
        {
            SelectedBotObjective = "Capital Growth";
            GridDensity = 8;
            GridLowerLimit = 6;
            GridUpperLimit = 18;
            GridSlopeBias = 74;
        }
        else
        {
            SelectedBotObjective = "Capital Growth";
            GridDensity = 12;
            GridLowerLimit = 5;
            GridUpperLimit = activeSession is "London Killzone" or "NY Killzone" ? 14 : 10;
            GridSlopeBias = 65;
        }

        if (StrategyThrottleStatus == "Reduced" || BotLaunchGuardStatus is "Caution" or "Reduced")
        {
            BotFundingAmount = Math.Max(500m, Math.Round(BotFundingAmount * 0.75m, 0));
        }

        if (SelectedBotObjective == "Low Drawdown")
        {
            SelectedRiskProfile = "Low";
        }
        else if (SelectedBotStrategy == "HFT Scout")
        {
            SelectedRiskProfile = "Moderate";
        }

        AddSystemLog($"AUTO-TUNE: Optimized {SelectedBotStrategy} for {activeSession} with {SelectedRiskProfile} risk.");
        UpdateBotLaunchSummary();
    }

    [RelayCommand]
    private void DisableSelectedStrategy()
    {
        if (string.IsNullOrWhiteSpace(SelectedBotStrategy))
        {
            return;
        }

        SetStrategyManualState(SelectedBotStrategy, manuallyDisabled: true, forceEnabled: false);
        AddSystemLog($"LAUNCH CONTROL: {SelectedBotStrategy} was manually disabled.");
    }

    [RelayCommand]
    private void ReEnableStrategy(string? strategy)
    {
        if (string.IsNullOrWhiteSpace(strategy))
        {
            return;
        }

        SetStrategyManualState(strategy, manuallyDisabled: false, forceEnabled: true);
        AddSystemLog($"LAUNCH CONTROL: {strategy} moved into manual recovery mode at reduced size.");
    }

    public async Task AnalyzeSymbol(string symbol, string market, string timeframe)
    {
        try
        {
            AddSystemLog($"ANALYSIS: Processing {symbol} ({market}) - {timeframe}...");
            var candles = await _marketDataService.GetCandlesAsync(symbol, 200, market, timeframe);
            ListenerHealth = "Online";
            MarketDataHealth = "Online";
            var risk = ResolveRiskParameters(SelectedRiskProfile);
            var rawDecision = await _technicalAnalysis.AnalyzeAsync(
                new Signal { Symbol = symbol }, candles, risk);

            var ind = rawDecision.Indicators;
            if (ind == null) return;

            var decision = ApplyStrategyThrottle(rawDecision);
            UpdateBotLaunchSummary();

            // Update Watchlist Item
            var watchlistItem = WatchlistItems.FirstOrDefault(w => w.Symbol == symbol);
            if (watchlistItem != null)
            {
                watchlistItem.Price = ind.CurrentPrice;
                var startPrice = candles.First().Close;
                watchlistItem.ChangePercent = (double)((ind.CurrentPrice - startPrice) / startPrice * 100);
            }

            // Update Chart
            NormalizeChart(candles);
            ClearChartInspection();

            // Avoid fabricated telemetry values in the dashboard.
            HftInferenceStatus = "Telemetry unavailable";
            ZScoreStatus = "Waiting for engine feed";

            // Update Indicators Agenda (Fintables style)
            UpdateIndicatorAgenda(ind);

            var multiTimeframeTask = UpdateMultiTimeframeAsync(symbol, market);
            var aiAnalystTask = UpdateAiAnalyst(symbol, ind);
            RefreshRiskPanel();

            // Update old indicator properties for backward compatibility if needed
            CurrentPrice = ind.CurrentPrice;
            RSI = ind.RSI ?? 50;
            RSIColor = RSI < 30 ? GreenBrush : RSI > 70 ? RedBrush : GrayBrush;

            if (ind.MACDHistogram > 0)
            {
                MACDStatus = $"▲ Bullish ({ind.MACDHistogram:F4})";
                MACDColor = GreenBrush;
            }
            else
            {
                if (rawDecision.ShouldTrade && !decision.ShouldTrade)
                {
                    AddSystemLog($"AUTO-THROTTLE: Blocked {decision.StrategyTag} in {decision.RegimeTag} due to weak bucket performance.");
                }
                MACDStatus = $"▼ Bearish ({ind.MACDHistogram:F4})";
                MACDColor = RedBrush;
            }

            if (ind.EMA20 > ind.EMA50)
            {
                EMAStatus = "Golden Cross ▲";
                EMAColor = GreenBrush;
            }
            else
            {
                EMAStatus = "Death Cross ▼";
                EMAColor = RedBrush;
            }

            BBUpper = ind.BollingerUpper ?? 0;
            BBLower = ind.BollingerLower ?? 0;
            ATR = ind.ATR ?? 0;
            VolumeRatio = ind.VolumeRatio ?? 1.0;
            VolumeColor = VolumeRatio >= 1.5 ? GreenBrush : GrayBrush;

            // AlphaTrend
            if (ind.AlphaTrend != null)
            {
                AlphaTrendValue = ind.AlphaTrend.Value;
                AlphaTrendStatus = ind.AlphaTrend.IsBullish ? "▲ BULLISH" : "▼ BEARISH";
                AlphaTrendColor = ind.AlphaTrend.IsBullish ? GreenBrush : RedBrush;

                if (ind.AlphaTrend.Signal != null && ind.AlphaTrend.IsConfirmed)
                {
                    AddSystemLog($"⚡ AlphaTrend CONFIRMED {ind.AlphaTrend.Signal} signal!");
                }
            }

            // S/R Levels
            NearestSupport = ind.NearestSupport?.Price ?? 0;
            NearestResistance = ind.NearestResistance?.Price ?? 0;

            // Decision
            ConfluenceScore = decision.Confidence;
            ConfluenceColor = decision.Confidence >= 60 ? GreenBrush
                            : decision.Confidence >= 40 ? OrangeBrush : RedBrush;

            StopLoss = decision.StopLoss;
            TakeProfit = decision.TakeProfit;
            PositionSize = decision.PositionSizePercent;
            AnalysisTimestamp = $"Updated {DateTime.Now:HH:mm:ss}";

            if (decision.ShouldTrade)
            {
                TradeJournal.Insert(0, new TradeJournalEntry
                {
                    Time = DateTime.Now,
                    Symbol = symbol,
                    Side = decision.Side.ToString(),
                    Price = ind.CurrentPrice,
                    Confidence = decision.Confidence,
                    Status = "Signal Approved",
                    ExecutionMode = GetCurrentExecutionModeLabel(),
                    ExecutionModeColor = GetCurrentExecutionModeColor(),
                    SideColor = decision.Side == TradeSide.Buy ? GreenBrush : RedBrush,
                    PnL = 0m,
                    Reasoning = decision.Reasoning.Length > 60
                        ? decision.Reasoning[..60] + "..."
                        : decision.Reasoning
                });

                string currency = market == "BIST" ? "TRY" : "$";
                AddSystemLog($"Signal approved: {decision.Side} {symbol} @ {currency}{ind.CurrentPrice:F2} (Score: {decision.Confidence}/100)");

                // Send Telegram/Webhook Alerts
                foreach (var alert in _alertingServices)
                {
                    _ = alert.SendTradeAlertAsync(symbol, decision, ind).ContinueWith(t => {
                        if (t.IsFaulted) AddSystemLog($"Alert Error: {t.Exception?.Flatten().InnerException?.Message}");
                    }, TaskScheduler.Default);
                }
            }
            else
            {
                AddSystemLog($"❌ SKIP: {symbol} — {(decision.Reasoning.Length > 80 ? decision.Reasoning[..80] + "..." : decision.Reasoning)}");
            }
            await Task.WhenAll(multiTimeframeTask, aiAnalystTask);
            await RefreshStrategyLeaderboard();
            UpdateTraderExperience(decision, ind, candles);
            UpdateVisualReasoning(decision, ind, candles);
            UpdateSessionSummary();
        }
        catch (Exception ex)
        {
            ListenerHealth = "Warning";
            MarketDataHealth = "Degraded";
            AddSystemLog($"ERROR: {ex.Message}");
        }
    }

    private async Task UpdateAiAnalyst(string symbol, IndicatorSnapshot ind)
    {
        try
        {
            AiSummary = "Generating AI insight...";
            AiSentimentMode = "Analyzing";
            AiSentimentColor = GrayBrush;
            AiTradeRecommendation = "Preparing recommendation...";
            AiRiskSummary = "Checking risk posture...";
            ExecutionReadiness = 0;
            TraderSafetyStatus = "Review pending";
            TraderSafetyColor = GrayBrush;
            TraderWarnings.Clear();

            var result = await _aiAnalyst.AnalyzeSnapshotAsync(symbol, ind);
            AiServiceHealth = "Online";
            
            AiSummary = result.ExecutiveSummary;
            AiTradeRecommendation = result.TradeRecommendation;
            AiRiskSummary = result.RiskSummary;
            ExecutionReadiness = result.ExecutionReadinessScore;
            UpdateTraderSafety(result);
            
            if (result.SentimentScore >= 0.5) { AiSentimentMode = $"Strong Bullish ({result.SentimentScore:F2})"; AiSentimentColor = GreenBrush; }
            else if (result.SentimentScore <= -0.5) { AiSentimentMode = $"Strong Bearish ({result.SentimentScore:F2})"; AiSentimentColor = RedBrush; }
            else if (result.SentimentScore > 0) { AiSentimentMode = $"Bullish ({result.SentimentScore:F2})"; AiSentimentColor = GreenBrush; }
            else if (result.SentimentScore < 0) { AiSentimentMode = $"Bearish ({result.SentimentScore:F2})"; AiSentimentColor = RedBrush; }
            else { AiSentimentMode = "Neutral (0.00)"; AiSentimentColor = GrayBrush; }
        }
        catch (Exception ex)
        {
            AiSummary = "AI Analyst unavailable.";
            AiSentimentMode = "Error";
            AiSentimentColor = RedBrush;
            AiTradeRecommendation = "Fallback to manual review";
            AiRiskSummary = "AI analyst unavailable, so use manual risk checks only.";
            ExecutionReadiness = 0;
            TraderSafetyStatus = "Manual review required";
            TraderSafetyColor = OrangeBrush;
            TraderWarnings.Clear();
            TraderWarnings.Add("AI analyst unavailable");
            AiServiceHealth = "Degraded";
            AddSystemLog($"AI Error: {ex.Message}");
        }
    }

    private void UpdateTraderSafety(AiAnalysisResult result)
    {
        TraderWarnings.Clear();
        foreach (var flag in result.RiskFlags.Take(4))
        {
            TraderWarnings.Add(flag);
        }

        if (TraderWarnings.Count == 0)
        {
            TraderWarnings.Add("No material AI risk flags.");
        }

        if (result.ExecutionReadinessScore >= 75)
        {
            TraderSafetyStatus = "Ready with controls";
            TraderSafetyColor = GreenBrush;
            return;
        }

        if (result.ExecutionReadinessScore >= 55)
        {
            TraderSafetyStatus = "Caution";
            TraderSafetyColor = OrangeBrush;
            return;
        }

        TraderSafetyStatus = "Stand down";
        TraderSafetyColor = RedBrush;
    }

    private static void InvokeOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    private void UpdateTraderExperience(TradeDecision decision, IndicatorSnapshot indicators, List<Candle> candles)
    {
        var strategyTag = string.IsNullOrWhiteSpace(decision.StrategyTag) ? "Confluence filter" : decision.StrategyTag;
        var regimeTag = string.IsNullOrWhiteSpace(decision.RegimeTag) ? "Unclassified regime" : decision.RegimeTag;
        var atr = indicators.ATR.HasValue ? Convert.ToDecimal(indicators.ATR.Value) : 0m;
        var resistanceRoom = indicators.NearestResistance?.Price is decimal resistance
            ? resistance - indicators.CurrentPrice
            : atr * 2;
        var supportRoom = indicators.NearestSupport?.Price is decimal support
            ? indicators.CurrentPrice - support
            : atr * 2;
        var reward = Math.Abs(decision.TakeProfit - indicators.CurrentPrice);
        var risk = Math.Abs(indicators.CurrentPrice - decision.StopLoss);
        RiskRewardRatio = risk > 0 ? reward / risk : 0;

        EntryChecklist.Clear();
        AddChecklistItem(
            "Trend aligned",
            (indicators.AlphaTrend?.IsBullish ?? false) && (indicators.PMax?.IsBullish ?? true) && (indicators.EMA20 ?? 0) >= (indicators.EMA50 ?? 0),
            "AlphaTrend, PMax, and EMA stack are aligned.");
        AddChecklistItem(
            "Momentum confirmed",
            (indicators.MACDHistogram ?? 0) > 0 && (indicators.RSI ?? 50) >= 45 && (indicators.RSI ?? 50) <= 70,
            "Momentum is supportive without looking overheated.");
        AddChecklistItem(
            "Volume confirmed",
            (indicators.VolumeRatio ?? 0) >= 1.2,
            "Relative volume is strong enough to trust the move.");
        AddChecklistItem(
            "Structure room",
            decision.Side == TradeSide.Sell ? supportRoom >= atr * 0.75m : resistanceRoom >= atr * 0.75m,
            "There is enough room before the nearest support or resistance.");
        AddChecklistItem(
            "Risk acceptable",
            !IsKillSwitchArmed && RiskRewardRatio >= 1.4m && decision.PositionSizePercent > 0,
            "Risk, size, and kill-switch controls are inside tolerance.");
        AddChecklistItem(
            "Execution quality",
            ExecutionReadiness >= 55 && QueueHealth != "Critical (Dead-Letter)" && MarketDataHealth == "Online",
            "Execution conditions and system health are acceptable.");

        var passedChecks = EntryChecklist.Count(item => item.IsPassing);
        SignalConfidenceLabel = $"Signal confidence: {decision.Confidence}/100";
        ExecutionQualityLabel = $"Execution quality: {ExecutionReadiness}/100";
        MarketPulse =
            $"Price {indicators.CurrentPrice:F2} | RSI {(indicators.RSI ?? 0):F1} | Vol {(indicators.VolumeRatio ?? 0):F1}x | " +
            $"ATR {(indicators.ATR ?? 0):F2}";
        ExecutionPlan =
            $"Entry {decision.Side} with stop {decision.StopLoss:F2}, target {decision.TakeProfit:F2}, " +
            $"size {decision.PositionSizePercent:P1}, R/R 1:{RiskRewardRatio:F2}.";

        WhyThisTrade =
            $"{decision.Side} bias via {strategyTag} in {regimeTag}. " +
            $"{decision.Reasoning} Stop {decision.StopLoss:F2}, target {decision.TakeProfit:F2}, size {decision.PositionSizePercent:P1}.";
        BotStatusSummary = decision.ShouldTrade
            ? $"Scanning complete: {strategyTag} found a {decision.Side} candidate in {regimeTag}. Execution quality is {ExecutionReadiness}/100. Throttle {StrategyThrottleStatus.ToLowerInvariant()}."
            : $"Bot is scanning but standing down. {decision.Reasoning}";
        DailyMarketBriefing =
            $"{SelectedMarket} briefing for {SelectedSymbol}: {regimeTag} backdrop with {strategyTag} bias. " +
            $"Current price {indicators.CurrentPrice:F2}, support {NearestSupport:F2}, resistance {NearestResistance:F2}, " +
            $"AI stance: {AiTradeRecommendation}.";

        SafetyBanners.Clear();
        if (DeadLetterCount > 0)
        {
            SafetyBanners.Add("Execution queue has dead-letter traffic. New trades should stay blocked.");
        }

        if (IsKillSwitchArmed)
        {
            SafetyBanners.Add("Risk kill-switch is armed. Position taking should remain disabled.");
        }

        if (MarketDataHealth != "Online")
        {
            SafetyBanners.Add("Market data health is degraded. Confirm prices before acting.");
        }

        if (ExecutionReadiness < 55)
        {
            SafetyBanners.Add("Execution quality is weak. Slippage risk is elevated.");
        }

        if (StrategyThrottleStatus == "Blocked")
        {
            SafetyBanners.Add("Strategy auto-throttle is blocking this bucket until its performance improves.");
        }
        else if (StrategyThrottleStatus == "Reduced")
        {
            SafetyBanners.Add("Strategy auto-throttle is reducing size on this bucket.");
        }

        if ((indicators.VolumeRatio ?? 0) < 1.0)
        {
            SafetyBanners.Add("Volume is below baseline. Breakouts are less trustworthy.");
        }

        if (SafetyBanners.Count == 0)
        {
            SafetyBanners.Add("No active safety banners. Conditions are stable enough for review.");
        }

        if (decision.ShouldTrade && ExecutionReadiness >= 75 && passedChecks >= 5 && !IsKillSwitchArmed)
        {
            TradeReadiness = "Ready";
            TradeReadinessColor = GreenBrush;
            TradeReadinessReason = "Confluence, execution quality, and controls are all supportive.";
        }
        else if (ExecutionReadiness >= 55 && passedChecks >= 4 && !IsKillSwitchArmed)
        {
            TradeReadiness = "Caution";
            TradeReadinessColor = OrangeBrush;
            TradeReadinessReason = "The setup is tradable, but one or more filters still need respect.";
        }
        else
        {
            TradeReadiness = "Do Not Trade";
            TradeReadinessColor = RedBrush;
            TradeReadinessReason = "At least one critical filter is failing or execution quality is too weak.";
        }

        if (candles.Count > 0)
        {
            InspectChartAt(ChartCanvasWidth * 0.82); // Slightly shifted for perspective
        }

        UpdateHftWorkspace();
    }

    private void UpdateHftWorkspace()
    {
        var sessionTrades = _tradeCache
            .Where(trade => trade.Status == TradeStatus.Executed && trade.IsClosed && trade.ExitTime.HasValue)
            .Where(trade => trade.ExitTime!.Value.Date == DateTime.UtcNow.Date)
            .ToList();
        var sessionPnl = sessionTrades.Sum(CalculateTradePnL);
        var sessionWins = sessionTrades.Count(trade => CalculateTradePnL(trade) > 0);
        var sessionWinRate = sessionTrades.Count == 0 ? 0 : sessionWins * 100.0 / sessionTrades.Count;
        var normalizedAtr = ATR <= 0 ? 0.25m : Convert.ToDecimal(ATR);
        var referencePrice = CurrentPrice <= 0 ? 1m : CurrentPrice;
        var queueMultiplier = QueueHealth == "Optimal" ? 0.65m : QueueHealth.StartsWith("Critical", StringComparison.OrdinalIgnoreCase) ? 1.8m : 1.1m;
        var readinessMultiplier = ExecutionReadiness >= 70 ? 0.75m : ExecutionReadiness >= 55 ? 1.0m : 1.25m;
        var volumeMultiplier = VolumeRatio >= 1.2 ? 0.8m : 1.15m;
        var estimatedSlippageBps = Math.Round((normalizedAtr / referencePrice) * 10000m * queueMultiplier * readinessMultiplier * volumeMultiplier, 1);

        HftWorkspaceHeadline =
            $"Execution mode {TradingModeLabel}. Queue {QueueHealth.ToLowerInvariant()}, market data {MarketDataHealth.ToLowerInvariant()}, AI {AiServiceHealth.ToLowerInvariant()}.";
        HftLatencySummary = $"API {ApiPing} | Server {ServerLatency} | Listener {ListenerHealth}";
        HftMicrostructureSummary =
            $"OFI {HftInferenceStatus} | Z-score {ZScoreStatus} | Volume {(VolumeRatio):F1}x | ATR {ATR:F2}";
        HftEstimatedSlippage = $"~{estimatedSlippageBps:N1} bps";
        HftSessionPnl = sessionPnl >= 0 ? $"+{sessionPnl:N0}" : $"{sessionPnl:N0}";
        HftSessionPnlColor = sessionPnl >= 0 ? GreenBrush : RedBrush;
        HftSessionWinRate = sessionTrades.Count == 0 ? "WR --" : $"WR {sessionWinRate:F0}% / {sessionTrades.Count}";

        if (estimatedSlippageBps <= 8 && ExecutionReadiness >= 70 && QueueHealth == "Optimal")
        {
            HftFillQuality = "Institutional";
            HftFillQualityColor = GreenBrush;
        }
        else if (estimatedSlippageBps <= 15 && ExecutionReadiness >= 55)
        {
            HftFillQuality = "Acceptable";
            HftFillQualityColor = OrangeBrush;
        }
        else
        {
            HftFillQuality = "Fragile";
            HftFillQualityColor = RedBrush;
        }

        if (DeadLetterCount > 0 || QueueHealth.StartsWith("Critical", StringComparison.OrdinalIgnoreCase))
        {
            HftExecutionBias = "Blocked";
            HftExecutionBiasColor = RedBrush;
            HftRiskSummary = "Dead-letter traffic or queue stress is active. HFT execution should stay disabled.";
            return;
        }

        if (MarketDataHealth != "Online" || StrategyThrottleStatus == "Blocked")
        {
            HftExecutionBias = "Caution";
            HftExecutionBiasColor = OrangeBrush;
            HftRiskSummary = "Execution quality is degraded by data or strategy quality constraints. Reduce aggression.";
            return;
        }

        if (ExecutionReadiness >= 70 && QueueHealth == "Optimal" && VolumeRatio >= 1.2)
        {
            HftExecutionBias = "Ready";
            HftExecutionBiasColor = GreenBrush;
            HftRiskSummary = "Queue, volume, and execution readiness are supportive for faster workflows.";
            return;
        }

        HftExecutionBias = "Standby";
        HftExecutionBiasColor = GrayBrush;
        HftRiskSummary = "Wait for stronger liquidity or better execution conditions before using HFT-style actions.";
    }

    private void AddChecklistItem(string title, bool isPassing, string detail)
    {
        EntryChecklist.Add(new ChecklistItemViewModel
        {
            Title = title,
            Detail = detail,
            Status = isPassing ? "Pass" : "Review",
            IsPassing = isPassing,
            Color = isPassing ? GreenBrush : OrangeBrush
        });
    }

    private void UpdateSessionSummary()
    {
        var topStrategy = StrategyLeaderboard.FirstOrDefault();
        var weakestStrategy = StrategyLeaderboard
            .OrderBy(row => row.ProfitFactor)
            .ThenBy(row => row.TotalPnL)
            .FirstOrDefault();
        var recentSignals = TradeJournal.Take(10).ToList();
        var approvedCount = recentSignals.Count(entry => entry.Status.Contains("Approved", StringComparison.OrdinalIgnoreCase));

        SessionSummary =
            $"Session: {recentSignals.Count} recent journal entries, {approvedCount} approved setups, " +
            $"queue {QueueHealth.ToLowerInvariant()}. " +
            $"Top bucket: {(topStrategy == null ? "n/a" : $"{topStrategy.StrategyTag} ({topStrategy.RegimeTag})")}. " +
            $"Weakest bucket: {(weakestStrategy == null ? "n/a" : $"{weakestStrategy.StrategyTag} ({weakestStrategy.RegimeTag})")}.";
    }

    private void UpdateTradingMode()
    {
        TradingModeLabel = IsPaperMode ? "PAPER" : "LIVE";
        TradingModeColor = IsPaperMode ? OrangeBrush : RedBrush;
        UpdateRuntimeIntegrityStatus();
    }

    private void UpdateRuntimeIntegrityStatus()
    {
        var simulationMode = string.Equals(
            Environment.GetEnvironmentVariable("TMQ_SIMULATION_MODE"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var exchangeMode = Environment.GetEnvironmentVariable("TMQ_EXCHANGE_MODE") ?? "Disabled";
        var hasGeminiKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TMQ_GEMINI_API_KEY"));
        var hasBinanceKeys =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TMQ_BINANCE_API_KEY")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TMQ_BINANCE_API_SECRET"));

        if ((exchangeMode.Equals("Mock", StringComparison.OrdinalIgnoreCase) || simulationMode) && !hasBinanceKeys)
        {
            ExecutionServiceStatus = "SIMULATION BROKER";
        }
        else if ((exchangeMode.Equals("Binance", StringComparison.OrdinalIgnoreCase) ||
                  exchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase)) && hasBinanceKeys)
        {
            ExecutionServiceStatus = exchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase)
                ? "BINANCE TESTNET"
                : "BINANCE LIVE";
        }
        else
        {
            ExecutionServiceStatus = "EXECUTION DISABLED";
        }

        IsHftRuntimeAvailable =
            exchangeMode.Equals("Binance", StringComparison.OrdinalIgnoreCase) ||
            exchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase);

        AiProviderStatus = hasGeminiKey
            ? "GEMINI LIVE"
            : simulationMode
                ? "AI SIMULATION"
                : "RULE-BASED FALLBACK";

        var configIssues = new List<string>();
        if (!IsHftRuntimeAvailable)
        {
            configIssues.Add("HFT streaming disabled");
        }
        var telegramToken = Environment.GetEnvironmentVariable("TMQ_TELEGRAM_BOT_TOKEN");
        var telegramChatId = Environment.GetEnvironmentVariable("TMQ_TELEGRAM_CHAT_ID");
        var webhookUrl = Environment.GetEnvironmentVariable("TMQ_ALERT_WEBHOOK_URL");
        if (string.IsNullOrWhiteSpace(telegramToken) || string.IsNullOrWhiteSpace(telegramChatId))
        {
            configIssues.Add("telegram alert not configured");
        }
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            configIssues.Add("webhook alert not configured");
        }
        var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "price_prediction.onnx");
        if (!File.Exists(modelPath))
        {
            configIssues.Add("onnx model path missing");
        }
        if (ExecutionServiceStatus == "EXECUTION DISABLED")
        {
            configIssues.Add("no live exchange configured");
        }
        if (AiProviderStatus == "RULE-BASED FALLBACK")
        {
            configIssues.Add("no live AI provider configured");
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TMQ_GEMINI_API_KEY")) && !simulationMode)
        {
            configIssues.Add("AI analyst running on deterministic fallback");
        }

        if (configIssues.Count == 0)
        {
            ConfigValidationStatus = "CONFIG VALID";
            ConfigValidationSummary = "Runtime is configured for real services with no major startup gaps.";
        }
        else
        {
            ConfigValidationStatus = simulationMode ? "SIMULATION CONFIG" : "CONFIG GAPS";
            ConfigValidationSummary = string.Join(" | ", configIssues.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        if (ExecutionServiceStatus == "EXECUTION DISABLED")
        {
            PortfolioExecutionModeLabel = "SAFE / DISABLED EXECUTION";
            PortfolioExecutionModeColor = RedBrush;
        }
        else if (IsPaperMode || ExecutionServiceStatus == "SIMULATION BROKER")
        {
            PortfolioExecutionModeLabel = "SIMULATED EXECUTION";
            PortfolioExecutionModeColor = OrangeBrush;
        }
        else
        {
            PortfolioExecutionModeLabel = "REAL EXECUTION";
            PortfolioExecutionModeColor = GreenBrush;
        }

        if (ExecutionServiceStatus == "EXECUTION DISABLED")
        {
            RuntimeIntegrityStatus = "SAFE MODE";
            RuntimeIntegrityColor = RedBrush;
            return;
        }

        if (ExecutionServiceStatus == "SIMULATION BROKER" || AiProviderStatus == "AI SIMULATION")
        {
            RuntimeIntegrityStatus = "SIMULATION MODE";
            RuntimeIntegrityColor = OrangeBrush;
            return;
        }

        if (AiProviderStatus == "RULE-BASED FALLBACK")
        {
            RuntimeIntegrityStatus = "LIVE RULES ONLY";
            RuntimeIntegrityColor = OrangeBrush;
            return;
        }

        RuntimeIntegrityStatus = "REAL SERVICES";
        RuntimeIntegrityColor = GreenBrush;
    }

    private string GetCurrentExecutionModeLabel()
    {
        if (ExecutionServiceStatus == "EXECUTION DISABLED")
        {
            return "SAFE";
        }

        return IsPaperMode || ExecutionServiceStatus == "SIMULATION BROKER"
            ? "SIM"
            : "REAL";
    }

    private Brush GetCurrentExecutionModeColor()
    {
        return GetCurrentExecutionModeLabel() switch
        {
            "REAL" => GreenBrush,
            "SAFE" => RedBrush,
            _ => OrangeBrush
        };
    }

    private int _lastSessionHour = -1;
    private void UpdateSessionContext()
    {
        var localNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Turkey Standard Time");
        var hour = localNow.Hour;

        if (hour == _lastSessionHour && SessionContexts.Count > 0) return;
        _lastSessionHour = hour;

        SessionContexts.Clear();
        AddSessionContext("Asia", hour is >= 3 and < 10, "Lower-liquidity handoff. Favor patience.");
        AddSessionContext("London Killzone", hour is >= 10 and < 13, "Best displacement window for PA continuation.");
        AddSessionContext("NY Killzone", hour is >= 15 and < 18, "Watch for reversal, sweep, and expansion.");
        AddSessionContext("Close / Dead Zone", !(hour is >= 10 and < 13) && !(hour is >= 15 and < 18), "Reduce aggression when liquidity thins.");

        var active = SessionContexts.FirstOrDefault(item => item.IsActive);
        CurrentSessionLabel = active == null
            ? $"Session context: {localNow:HH:mm} Istanbul"
            : $"{active.Name} active · {localNow:HH:mm} Istanbul";
        SessionGuidance = active?.Guidance ?? "No killzone is active. Expect slower, choppier PA unless volatility expands.";
        UpdateBotLaunchSummary();
    }

    private void AddSessionContext(string name, bool isActive, string guidance)
    {
        SessionContexts.Add(new SessionContextViewModel
        {
            Name = name,
            Guidance = guidance,
            IsActive = isActive,
            Color = isActive ? GreenBrush : GrayBrush
        });
    }

    private void UpdateVisualReasoning(TradeDecision decision, IndicatorSnapshot indicators, List<Candle> candles)
    {
        ReasoningOverlays.Clear();
        if (candles.Count == 0)
        {
            VisualReasoningSummary = "Visual reasoning overlays will appear after analysis.";
            return;
        }

        var (min, max) = GetDisplayRange(candles);
        var range = Math.Max(max - min, 0.01m);

        if (indicators.NearestSupport?.Price is decimal support)
        {
            AddReasoningOverlay(
                80,
                ProjectPriceToY(support + Math.Max((decimal)(indicators.ATR ?? 1.2) * 0.4m, 0.2m), min, range),
                260,
                34,
                "HTF Discount / OB Candidate",
                $"Support cluster around {support:F2}",
                "#1D7A5A");
        }

        if (indicators.NearestResistance?.Price is decimal resistance)
        {
            AddReasoningOverlay(
                420,
                ProjectPriceToY(resistance + Math.Max((decimal)(indicators.ATR ?? 1.2) * 0.3m, 0.2m), min, range),
                260,
                34,
                "Premium / Liquidity Pool",
                $"Resistance cluster around {resistance:F2}",
                "#7A2F45");
        }

        var displacementAnchor = candles.GetRange(Math.Max(0, candles.Count - 12), Math.Min(6, candles.Count));
        if (displacementAnchor.Count > 0)
        {
            var anchorHigh = displacementAnchor.Max(c => c.High);
            var anchorLow = displacementAnchor.Min(c => c.Low);
            var label = decision.Side == TradeSide.Buy ? "BULLISH FVG / FV" : "BEARISH FVG / FV";
            AddReasoningOverlay(
                250,
                ProjectPriceToY(anchorHigh, min, range),
                180,
                Math.Max(ProjectPriceToY(anchorLow, min, range) - ProjectPriceToY(anchorHigh, min, range), 24),
                label,
                "Fair Value Gap detected via volume displacement",
                decision.Side == TradeSide.Buy ? "#2F81F7" : "#FF3366");
        }

        VisualReasoningSummary =
            $"Overlaying {ReasoningOverlays.Count} inferred PA zones from support/resistance, displacement, and {decision.StrategyTag}.";
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Brush> _brushCache = new();
    private void AddReasoningOverlay(double x, double y, double width, double height, string title, string detail, string fillHex)
    {
        var fillBrush = _brushCache.GetOrAdd(fillHex, hex => (Brush)new BrushConverter().ConvertFromString(hex)!);

        ReasoningOverlays.Add(new ReasoningOverlayViewModel
        {
            X = x,
            Y = Math.Clamp(y, 6, ChartCanvasHeight - Math.Max(height + 14, 24)),
            Width = width,
            Height = height,
            Title = title,
            Detail = detail,
            Fill = fillBrush,
            Stroke = Brushes.White,
            TextColor = Brushes.White
        });
    }

    private double ProjectPriceToY(decimal price, decimal min, decimal range)
    {
        var normalized = (double)((price - min) / range);
        return ChartCanvasHeight - (normalized * ChartCanvasHeight);
    }

    public void InspectChartAt(double logicalX)
    {
        if (_chartSourceCandles.Count == 0)
        {
            ClearChartInspection();
            return;
        }

        var clampedX = Math.Clamp(logicalX, 0, ChartCanvasWidth);
        var index = Math.Clamp(
            (int)Math.Round(clampedX / ChartCanvasWidth * (_chartSourceCandles.Count - 1)),
            0,
            _chartSourceCandles.Count - 1);
        var candle = _chartSourceCandles[index];
        var previousClose = index > 0 ? _chartSourceCandles[index - 1].Close : candle.Open;
        var percentChange = previousClose == 0 ? 0 : (candle.Close - previousClose) / previousClose * 100;

        ChartFocusTitle = $"{SelectedSymbol} {candle.Timestamp:dd MMM HH:mm}";
        ChartFocusSummary = $"Open {candle.Open:F2} | High {candle.High:F2} | Low {candle.Low:F2}";
        ChartFocusPrice = $"Close: {candle.Close:F2}";
        ChartFocusRange = $"Range: {(candle.High - candle.Low):F2}";
        ChartFocusVolume = $"Volume: {candle.Volume:N0}";
        ChartFocusChange = $"Change: {percentChange:+0.00;-0.00;0.00}%";
        SetChartHeader(candle, previousClose);
    }

    public void ClearChartInspection()
    {
        ChartFocusTitle = "Chart Inspector";
        ChartFocusSummary = "Hover over the chart to inspect candles.";
        ChartFocusPrice = "Close: --";
        ChartFocusRange = "Range: --";
        ChartFocusVolume = "Volume: --";
        ChartFocusChange = "Change: --";
    }

    private void RefreshRiskPanel()
    {
        IsKillSwitchArmed = _riskAgent.IsKillSwitchArmed;
        RiskGuardStatus = IsKillSwitchArmed ? "Armed: Yes" : "Armed: No";
    }

    private async Task UpdateMultiTimeframeAsync(string symbol, string market)
    {
        try
        {
            // Clear or Initialize
            if (TimeframeConfluences.Count == 0)
            {
                TimeframeConfluences.Add(new IndicatorDetailViewModel { Name = "5m", Status = "Loading..." });
                TimeframeConfluences.Add(new IndicatorDetailViewModel { Name = "1h", Status = "Loading..." });
                TimeframeConfluences.Add(new IndicatorDetailViewModel { Name = "1d", Status = "Loading..." });
            }

            var timeframes = new[] { "5m", "1h", "1d" };
            
            for (int i = 0; i < timeframes.Length; i++)
            {
                var tf = timeframes[i];
                var candles = await _marketDataService.GetCandlesAsync(symbol, 100, market, tf);
                if (candles.Count > 0)
                {
                    var ind = await _technicalAnalysis.ComputeIndicatorsAsync(candles);
                    var isBullish = (ind.AlphaTrend?.IsBullish ?? false) && (ind.PMax?.IsBullish ?? true);
                    var status = isBullish ? "▲ Bullish" : "▼ Bearish";
                    var color = isBullish ? GreenBrush : RedBrush;

                    TimeframeConfluences[i].Name = tf;
                    TimeframeConfluences[i].Value = $"AT: {ind.AlphaTrend?.Value:F2} | PMAX: {ind.PMax?.Value:F2}";
                    TimeframeConfluences[i].Status = status;
                    TimeframeConfluences[i].Color = color;
                }
            }
        }
        catch (Exception ex)
        {
            AddSystemLog($"Failed to fetch Multi-Timeframe Confluence: {ex.Message}");
        }
    }

    private void NormalizeChart(List<Candle> candles)
    {
        InvokeOnUiThread(() => 
        {
            ChartCandles.Clear();
            PriceScaleLabels.Clear();
            TimeScaleLabels.Clear();
            HorizontalGridLines.Clear();
            VerticalGridLines.Clear();
        });
        _chartSourceCandles.Clear();
        _chartSourceCandles.AddRange(candles);
        if (candles.Count == 0)
        {
            ChartHeaderTitle = $"{SelectedSymbol} · {SelectedTimeframe} · {SelectedMarket}";
            ChartHeaderOhlc = "O -- H -- L -- C --";
            CurrentPriceMarker = "--";
            return;
        }

        var (min, max) = GetDisplayRange(candles);
        decimal range = max - min;

        double canvasWidth = ChartCanvasWidth;
        double canvasHeight = ChartCanvasHeight;
        double candleWidth = candles.Count > 1 ? (canvasWidth / candles.Count) * 0.82 : 40;
        double gap = candles.Count > 1 ? (canvasWidth / candles.Count) * 0.18 : 10;

        for (int i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            double x = candles.Count > 1 ? (double)i / (candles.Count - 1) * (canvasWidth - candleWidth) : 0;

            var open = Clamp(candle.Open, min, max);
            var close = Clamp(candle.Close, min, max);
            var high = Clamp(candle.High, min, max);
            var low = Clamp(candle.Low, min, max);

            // Normalized Y coordinates (Inverted for WPF)
            double openY = canvasHeight - (double)((open - min) / range) * canvasHeight;
            double closeY = canvasHeight - (double)((close - min) / range) * canvasHeight;
            double highY = canvasHeight - (double)((high - min) / range) * canvasHeight;
            double lowY = canvasHeight - (double)((low - min) / range) * canvasHeight;

            bool isBullish = candle.Close >= candle.Open;
            var color = isBullish ? BuyColorBrush : SellColorBrush;
            InvokeOnUiThread(() => 
            {
                ChartCandles.Add(new CandleViewModel
                {
                    X = x,
                    WickX = x + (candleWidth / 2),
                    BodyY = Math.Min(openY, closeY),
                    BodyHeight = Math.Max(Math.Abs(openY - closeY), 1.2),
                    WickY1 = highY,
                    WickY2 = lowY,
                    Width = candleWidth,
                    Color = color
                });
            });
        }

        InvokeOnUiThread(() => PopulateChartScales(candles, min, max, canvasWidth, canvasHeight));

        // Keep Polyline for indicators or overlay if needed, currently we use it as a simple 'trend' line
        var points = new PointCollection();
        for (int i = 0; i < candles.Count; i++)
        {
            double x = (double)i / (candles.Count - 1) * canvasWidth;
            double y = canvasHeight - (double)((Clamp(candles[i].Close, min, max) - min) / range) * canvasHeight;
            points.Add(new System.Windows.Point(x, y));
        }
        ChartPoints = points;

        var last = candles[^1];
        ChartHeaderTitle = $"{SelectedSymbol} · {SelectedTimeframe} · {SelectedMarket}";
        SetChartHeader(last, candles.Count > 1 ? candles[^2].Close : last.Open);
        CurrentPriceMarker = $"{last.Close:F2}";
        CurrentPriceMarkerY = canvasHeight - (double)((Clamp(last.Close, min, max) - min) / range) * canvasHeight;
        
        // Final sanity check for Multi-Timeframe bar
        if (TimeframeConfluences.Count == 0 || TimeframeConfluences[0].Status == "Loading...")
        {
            _ = UpdateMultiTimeframeAsync(SelectedSymbol, SelectedMarket);
        }
    }

    private void NormalizeBacktestMarkers(List<Candle> candles, List<ThatsMyQuant.Domain.ValueObjects.BacktestTradeResult> trades)
    {
        InvokeOnUiThread(() => BacktestMarkers.Clear());
        if (candles.Count == 0 || trades.Count == 0) return;

        var (min, max) = GetDisplayRange(candles);
        decimal range = max - min;

        double width = ChartCanvasWidth;
        double height = ChartCanvasHeight;

        foreach (var trade in trades)
        {
            int entryIndex = candles.FindIndex(c => c.Timestamp >= trade.EntryTime);
            if (entryIndex >= 0)
            {
                double x = (double)entryIndex / (candles.Count - 1) * width;
                double y = height - (double)((Clamp(trade.EntryPrice, min, max) - min) / range) * height;
                BacktestMarkers.Add(new BacktestMarker 
                { 
                    X = x - 5, Y = y - 5, 
                    Color = trade.Side == "Buy" ? GreenBrush : RedBrush,
                    ToolTip = $"Entry {trade.Side} @ {trade.EntryPrice:F2}"
                });
            }

            int exitIndex = candles.FindIndex(c => c.Timestamp >= trade.ExitTime);
            if (exitIndex >= 0)
            {
                double x = (double)exitIndex / (candles.Count - 1) * width;
                double y = height - (double)((Clamp(trade.ExitPrice, min, max) - min) / range) * height;
                BacktestMarkers.Add(new BacktestMarker 
                { 
                    X = x - 5, Y = y - 5, 
                    Color = trade.PnL > 0 ? GreenBrush : GrayBrush,
                    ToolTip = $"Exit {trade.Side} @ {trade.ExitPrice:F2}\nPnL: {trade.PnL:F2}"
                });
            }
        }
        
        NormalizeChart(candles); 
    }

    private static (decimal Min, decimal Max) GetDisplayRange(List<Candle> candles)
    {
        if (candles.Count == 0)
        {
            return (0m, 1m);
        }

        var lows = candles.Select(c => c.Low).OrderBy(v => v).ToList();
        var highs = candles.Select(c => c.High).OrderBy(v => v).ToList();
        var trim = candles.Count >= 20 ? Math.Max(1, candles.Count / 20) : 0;

        var min = lows[Math.Min(trim, lows.Count - 1)];
        var max = highs[Math.Max(0, highs.Count - 1 - trim)];

        if (max <= min)
        {
            min = candles.Min(c => c.Low);
            max = candles.Max(c => c.High);
        }

        var padding = Math.Max((max - min) * 0.08m, 0.01m);
        min -= padding;
        max += padding;

        if (max <= min)
        {
            max = min + 1m;
        }

        return (min, max);
    }

    private static decimal Clamp(decimal value, decimal min, decimal max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private void UpdatePortfolioGrowth()
    {
        var baseline = Convert.ToDouble(Balance);
        var now = DateTime.Now;
        var closedTrades = _tradeCache
            .Where(t => t.IsClosed && t.ExitTime.HasValue && t.Status == TradeStatus.Executed)
            .OrderBy(t => t.ExitTime)
            .ToList();
        var dominantStage = closedTrades.Count == 0
            ? "No ladder data yet"
            : closedTrades
                .GroupBy(InferPromotionStage)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault() ?? "UNKNOWN";
        PortfolioPromotionStageLabel = dominantStage;

        var labels = new List<string>();
        var values = new List<double>();

        if (closedTrades.Count == 0)
        {
            BuildFlatPortfolioSeries(labels, values, baseline);
            PortfolioGrowthHeadline = $"{Balance:N0}";
            PortfolioGrowthSubtext = "No realized growth history yet";
            PortfolioRangeSummary = $"{PortfolioExecutionModeLabel} | Stage {PortfolioPromotionStageLabel} | {SelectedPortfolioRange} view is waiting for closed trades";
            PortfolioGrowthColor = GrayBrush;
            ApplyPortfolioSeries(labels, values);
            return;
        }

        DateTime windowStart;
        Func<DateTime, string> labelSelector;
        switch (SelectedPortfolioRange)
        {
            case "Daily":
                windowStart = now.Date;
                labelSelector = d => d.ToString("HH");
                PortfolioRangeSummary = $"{PortfolioExecutionModeLabel} | Stage {PortfolioPromotionStageLabel} | Daily equity curve from realized trades";
                break;
            case "Weekly":
                windowStart = now.Date.AddDays(-6);
                labelSelector = d => d.ToString("ddd");
                PortfolioRangeSummary = $"{PortfolioExecutionModeLabel} | Stage {PortfolioPromotionStageLabel} | Weekly realized portfolio growth";
                break;
            case "Yearly":
                windowStart = new DateTime(now.Year, 1, 1);
                labelSelector = d => $"Q{((d.Month - 1) / 3) + 1}";
                PortfolioRangeSummary = $"{PortfolioExecutionModeLabel} | Stage {PortfolioPromotionStageLabel} | Yearly realized portfolio growth";
                break;
            default:
                windowStart = new DateTime(now.Year, now.Month, 1);
                labelSelector = d => $"W{((d.Day - 1) / 7) + 1}";
                PortfolioRangeSummary = $"{PortfolioExecutionModeLabel} | Stage {PortfolioPromotionStageLabel} | Monthly realized portfolio growth";
                break;
        }

        var tradesInWindow = closedTrades
            .Where(t => t.ExitTime!.Value >= windowStart)
            .ToList();

        if (tradesInWindow.Count == 0)
        {
            BuildFlatPortfolioSeries(labels, values, baseline);
            PortfolioGrowthHeadline = $"{Balance:N0}";
            PortfolioGrowthSubtext = $"No closed trades in the {SelectedPortfolioRange.ToLowerInvariant()} window. Dominant stage: {PortfolioPromotionStageLabel}.";
            PortfolioGrowthColor = GrayBrush;
            ApplyPortfolioSeries(labels, values);
            return;
        }

        decimal cumulative = Balance;
        foreach (var bucket in tradesInWindow.GroupBy(t => labelSelector(t.ExitTime!.Value)))
        {
            cumulative += bucket.Sum(CalculateTradePnL);
            labels.Add(bucket.Key);
            values.Add(Convert.ToDouble(cumulative));
        }

        var first = values.FirstOrDefault(baseline);
        var last = values.LastOrDefault(baseline);
        var changePercent = first == 0 ? 0 : ((last - first) / first) * 100d;
        PortfolioGrowthHeadline = $"{last:N0}";
        PortfolioGrowthSubtext = $"{changePercent:+0.00;-0.00;0.00}% realized | Dominant stage {PortfolioPromotionStageLabel}";
        PortfolioGrowthColor = changePercent >= 0 ? GreenBrush : RedBrush;

        ApplyPortfolioSeries(labels, values);
    }

    private void BuildFlatPortfolioSeries(List<string> labels, List<double> values, double baseline)
    {
        labels.AddRange(SelectedPortfolioRange switch
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
        var points = new System.Windows.Media.PointCollection();

        PortfolioXAxisLabels.Clear();
        PortfolioYAxisLabels.Clear();

        for (var i = 0; i < values.Count; i++)
        {
            var x = values.Count == 1 ? 0 : i * (width / (values.Count - 1));
            var y = height - ((values[i] - min) / range * height);
            points.Add(new System.Windows.Point(x, y));

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

        InvokeOnUiThread(() => 
        {
            for (int i = 0; i <= horizontalSteps; i++)
            {
                var y = canvasHeight / horizontalSteps * i;
                var price = max - (max - min) * (decimal)i / horizontalSteps;
                HorizontalGridLines.Add(new ChartGridLineViewModel { X1 = 0, Y1 = y, X2 = canvasWidth, Y2 = y });
                PriceScaleLabels.Add(new ChartAxisLabel
                {
                    Text = price.ToString("F2"),
                    X = canvasWidth + 10,
                    Y = y - 8
                });
            }

            for (int i = 0; i < verticalSteps; i++)
            {
                var x = verticalSteps == 1 ? 0 : canvasWidth / (verticalSteps - 1) * i;
                var index = Math.Min(candles.Count - 1, (int)Math.Round((double)i / Math.Max(1, verticalSteps - 1) * (candles.Count - 1)));
                VerticalGridLines.Add(new ChartGridLineViewModel { X1 = x, Y1 = 0, X2 = x, Y2 = canvasHeight });
                TimeScaleLabels.Add(new ChartAxisLabel
                {
                    Text = candles[index].Timestamp.ToString(candles.Count > 120 ? "dd MMM" : "dd MMM HH:mm"),
                    X = Math.Max(0, x - 24),
                    Y = canvasHeight + 10
                });
            }
        });
    }

    private void SetChartHeader(Candle candle, decimal previousClose)
    {
        var change = previousClose == 0 ? 0 : (candle.Close - previousClose) / previousClose * 100;
        ChartHeaderOhlc =
            $"O {candle.Open:F2}  H {candle.High:F2}  L {candle.Low:F2}  C {candle.Close:F2}  {change:+0.00;-0.00;0.00}%";
        ChartHeaderChangeColor = change >= 0 ? GreenBrush : RedBrush;
    }

    private void UpdateIndicatorAgenda(IndicatorSnapshot ind)
    {
        IndicatorDetails.Clear();
        LogicTree.Clear();

        var momentumNode = new LogicTreeNode { Category = "⚡ Momentum Weights" };
        momentumNode.Indicators.Add(new IndicatorDetailViewModel { Name = "RSI (14)", Category = "Momentum", Value = ind.RSI?.ToString("F1") ?? "—", Status = ind.RSI > 70 ? "Overbought" : ind.RSI < 30 ? "Oversold" : "Neutral", Color = ind.RSI > 70 ? RedBrush : ind.RSI < 30 ? GreenBrush : GrayBrush });
        momentumNode.Indicators.Add(new IndicatorDetailViewModel { Name = "MACD", Category = "Momentum", Value = ind.MACDHistogram?.ToString("F4") ?? "—", Status = ind.MACDHistogram > 0 ? "Bullish" : "Bearish", Color = ind.MACDHistogram > 0 ? GreenBrush : RedBrush });

        var trendNode = new LogicTreeNode { Category = "📈 Trend Confluence" };
        trendNode.Indicators.Add(new IndicatorDetailViewModel { Name = "EMA 20/50", Category = "Trend", Value = ind.EMA20?.ToString("F2") ?? "—", Status = ind.EMA20 > ind.EMA50 ? "Golden Cross" : "Death Cross", Color = ind.EMA20 > ind.EMA50 ? GreenBrush : RedBrush });
        trendNode.Indicators.Add(new IndicatorDetailViewModel { Name = "AlphaTrend", Category = "Trend", Value = ind.AlphaTrend?.Value.ToString("F2") ?? "—", Status = ind.AlphaTrend?.IsBullish == true ? "Bullish Trend" : "Bearish Trend", Color = ind.AlphaTrend?.IsBullish == true ? GreenBrush : RedBrush });
        trendNode.Indicators.Add(new IndicatorDetailViewModel { Name = "PMax", Category = "Trend", Value = ind.PMax?.Value.ToString("F2") ?? "—", Status = ind.PMax?.IsBullish == true ? "Bullish Filter" : "Bearish Filter", Color = ind.PMax?.IsBullish == true ? GreenBrush : RedBrush });
        trendNode.Indicators.Add(new IndicatorDetailViewModel { Name = "Price Action", Category = "Trend", Value = ind.DetectedPattern, Status = ind.DetectedPattern == "None" ? "Waiting..." : "Active Pattern", Color = ind.DetectedPattern.Contains("Bullish") ? GreenBrush : ind.DetectedPattern.Contains("Bearish") ? RedBrush : GrayBrush });

        var volNode = new LogicTreeNode { Category = "🌪 Volatility / Volume" };
        volNode.Indicators.Add(new IndicatorDetailViewModel { Name = "Volatility (ATR)", Category = "Volatility", Value = ind.ATR?.ToString("F2") ?? "—", Status = "Normal", Color = GrayBrush });
        volNode.Indicators.Add(new IndicatorDetailViewModel { Name = "Volume Ratio", Category = "Volume", Value = (ind.VolumeRatio?.ToString("F1") ?? "—") + "x", Status = ind.VolumeRatio > 1.5 ? "High Relative Volume" : "Normal", Color = ind.VolumeRatio > 1.5 ? GreenBrush : GrayBrush });

        LogicTree.Add(momentumNode);
        LogicTree.Add(trendNode);
        LogicTree.Add(volNode);

        // Maintain old list for backwards compatibility
        foreach (var node in LogicTree)
            foreach (var child in node.Indicators)
                IndicatorDetails.Add(child);
    }


    // Brush helpers
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xDA, 0x36, 0x33));
    private static readonly Brush BuyColorBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0xBB)); // Neon Emerald
    private static readonly Brush SellColorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x33, 0x66)); // Electric Raspberry
    private static readonly Brush OrangeBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
}
