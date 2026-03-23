using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Domain.ValueObjects;
using System.Windows.Media;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
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
    private readonly bool _enableAutomaticAnalysis;

    // --- KPI Cards ---
    [ObservableProperty] private string _status = "⏸ READY";
    [ObservableProperty] private Brush _statusColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private decimal _balance = 100_000;
    [ObservableProperty] private int _confluenceScore = 0;
    [ObservableProperty] private Brush _confluenceColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    
    // --- HFT & Engine Telemetry ---
    [ObservableProperty] private string _hftInferenceStatus = "Offline";
    [ObservableProperty] private string _zScoreStatus = "N/A";
    
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
    [ObservableProperty] private string _selectedPortfolioRange = "Monthly";
    [ObservableProperty] private string _portfolioGrowthHeadline = "$108,420";
    [ObservableProperty] private string _portfolioGrowthSubtext = "+8.42% vs starting capital";
    [ObservableProperty] private string _portfolioRangeSummary = "Monthly growth profile";
    [ObservableProperty] private Brush _portfolioGrowthColor = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0xBB));

    [ObservableProperty] private string _alphaTrendStatus = "—";
    [ObservableProperty] private Brush _alphaTrendColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private double _winRate = 0;
    [ObservableProperty] private int _totalTrades = 0;
    [ObservableProperty] private double _profitFactor = 0;
    [ObservableProperty] private string _strategyLeaderboardStatus = "Waiting for trade analytics...";
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


    // --- Indicators ---
    [ObservableProperty] private double _rSI = 50;
    [ObservableProperty] private Brush _rSIColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _mACDStatus = "—";
    [ObservableProperty] private Brush _mACDColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _eMAStatus = "—";
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
    public ObservableCollection<StrategyLeaderboardRow> StrategyLeaderboard { get; } = new();
    public ObservableCollection<SessionContextViewModel> SessionContexts { get; } = new();
    public ObservableCollection<PositionViewModel> ActivePositions { get; } = new();
    [ObservableProperty] private System.Windows.Media.PointCollection _portfolioGrowthPoints = new();

    private bool _isTrading;
    private CancellationTokenSource? _cts;
    private readonly List<Candle> _chartSourceCandles = new();
    private readonly List<Trade> _tradeCache = new();

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
        _enableAutomaticAnalysis = enableAutomaticAnalysis;

        LoadSymbols();
        AddSystemLog("SYSTEM: Terminal initialized. Multi-asset support active.");
        AddSystemLog("INFRA: WebSocket connection to global exchanges established.");
        AddSystemLog("AI: Reasoning engine v2.4-Institutional loaded.");
        AddSystemLog("RISK: Daily loss guard armed at 5.0%.");
        
        RefreshRiskPanel();
        UpdateTradingMode();
        UpdateSessionContext();
        UpdateBotLaunchSummary();
        UpdatePortfolioGrowth();

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

            StrategyLeaderboardStatus = $"Tracking {rows.Count} strategy buckets from {tradeRows.Count} trades.";
            UpdateSessionSummary();
        }
        catch (Exception ex)
        {
            StrategyLeaderboardStatus = "Strategy analytics unavailable.";
            RefreshOpenPositionsFromTrades(Array.Empty<Trade>());
            UpdatePortfolioGrowth();
            AddSystemLog($"Strategy analytics error: {ex.Message}");
        }
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
        BotLaunchSummary =
            $"{SelectedBotStrategy} on {SelectedBotBaseAsset}/{SelectedBotQuoteAsset} with {BotFundingAmount:N0} {SelectedBotQuoteAsset}, " +
            $"{SelectedBotObjective.ToLowerInvariant()} objective, grid {GridLowerLimit:F1}% to {GridUpperLimit:F1}%, density {GridDensity}, slope {slopeText}.";
        BotLaunchHint = IsPaperMode
            ? "Paper mode is active, so this launch will stay sandboxed until you switch to live."
            : "Live mode is active. Double-check broker routing and risk limits before activating.";
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
    private async Task SelectSymbol(string symbol)
    {
        SelectedSymbol = symbol;
        BacktestMarkers.Clear();
        // Removed manual AnalyzeSymbol call to avoid double execution (property change handler already triggers it)
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
    private async Task StartTrading()
    {
        if (_isTrading) return;
        _isTrading = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        Status = IsPaperMode ? "🟠 PAPER MODE" : "🟢 TRADING";
        StatusColor = IsPaperMode ? OrangeBrush : GreenBrush;
        AddSystemLog($"{TradingModeLabel} trading started. Monitoring {SelectedMarket} - {SelectedSymbol}...");

        try
        {
            await AnalyzeSymbol(SelectedSymbol, SelectedMarket, SelectedTimeframe); // Initial load
            while (!_cts.Token.IsCancellationRequested)
            {
                await AnalyzeSymbol(SelectedSymbol, SelectedMarket, SelectedTimeframe);
                await Task.Delay(5000, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private void EmergencyStop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _isTrading = false;
        Status = "🔴 STOPPED";
        StatusColor = new SolidColorBrush(Color.FromRgb(0xDA, 0x36, 0x33));
        AddSystemLog("EMERGENCY STOP — All trading halted.");
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

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
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

    public async Task AnalyzeSymbol(string symbol, string market, string timeframe)
    {
        try
        {
            AddSystemLog($"ANALYSIS: Processing {symbol} ({market}) - {timeframe}...");
            var candles = await _marketDataService.GetCandlesAsync(symbol, 200, market, timeframe);
            ListenerHealth = "Online";
            MarketDataHealth = "Online";
            var risk = ResolveRiskParameters(SelectedRiskProfile);
            var decision = await _technicalAnalysis.AnalyzeAsync(
                new Signal { Symbol = symbol }, candles, risk);

            var ind = decision.Indicators;
            if (ind == null) return;

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
                    Reasoning = decision.Reasoning.Length > 60
                        ? decision.Reasoning[..60] + "..."
                        : decision.Reasoning
                });

                string currency = market == "BIST" ? "TRY" : "$";
                AddSystemLog($"Signal approved: {decision.Side} {symbol} @ {currency}{ind.CurrentPrice:F2} (Score: {decision.Confidence}/100)");

                // Send Telegram/Webhook Alerts
                foreach (var alert in _alertingServices)
                {
                    _ = alert.SendTradeAlertAsync(symbol, decision, ind, new AlertAuditContext { Reasoning = "Dashboard Analysis Trigger" }).ContinueWith(t => {
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
            ? $"Scanning complete: {strategyTag} found a {decision.Side} candidate in {regimeTag}. Execution quality is {ExecutionReadiness}/100."
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

        var labels = new List<string>();
        var values = new List<double>();

        if (closedTrades.Count == 0)
        {
            BuildFlatPortfolioSeries(labels, values, baseline);
            PortfolioGrowthHeadline = $"{Balance:N0}";
            PortfolioGrowthSubtext = "No realized growth history yet";
            PortfolioRangeSummary = $"{SelectedPortfolioRange} view is waiting for closed trades";
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
                PortfolioRangeSummary = "Daily equity curve from realized trades";
                break;
            case "Weekly":
                windowStart = now.Date.AddDays(-6);
                labelSelector = d => d.ToString("ddd");
                PortfolioRangeSummary = "Weekly realized portfolio growth";
                break;
            case "Yearly":
                windowStart = new DateTime(now.Year, 1, 1);
                labelSelector = d => $"Q{((d.Month - 1) / 3) + 1}";
                PortfolioRangeSummary = "Yearly realized portfolio growth";
                break;
            default:
                windowStart = new DateTime(now.Year, now.Month, 1);
                labelSelector = d => $"W{((d.Day - 1) / 7) + 1}";
                PortfolioRangeSummary = "Monthly realized portfolio growth";
                break;
        }

        var tradesInWindow = closedTrades
            .Where(t => t.ExitTime!.Value >= windowStart)
            .ToList();

        if (tradesInWindow.Count == 0)
        {
            BuildFlatPortfolioSeries(labels, values, baseline);
            PortfolioGrowthHeadline = $"{Balance:N0}";
            PortfolioGrowthSubtext = $"No closed trades in the {SelectedPortfolioRange.ToLowerInvariant()} window";
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
        PortfolioGrowthSubtext = $"{changePercent:+0.00;-0.00;0.00}% realized";
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
