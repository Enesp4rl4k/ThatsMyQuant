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
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IMarketDataService _marketDataService;
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly IAiAnalystService _aiAnalyst;
    private readonly IEnumerable<IAlertingService> _alertingServices;
    private readonly Application.Strategy.BacktestEngine _backtestEngine;
    private readonly Application.Strategy.RiskAgent _riskAgent;
    private readonly ISignalRepository _signalRepository;
    private readonly ITradeRepository _tradeRepository;
    private readonly ILogger<MainViewModel> _logger;

    private CancellationTokenSource? _cts;
    private DispatcherTimer? _uiUpdateTimer;
    private DispatcherTimer? _telemetryTimer;
    private bool _isTrading;
    private bool _disposed;

    // --- KPI & Status ---
    [ObservableProperty] private string _status = "⏸ READY";
    [ObservableProperty] private Brush _statusColor = GrayBrush;
    [ObservableProperty] private decimal _balance = 100_000;
    [ObservableProperty] private int _confluenceScore = 0;
    [ObservableProperty] private Brush _confluenceColor = GrayBrush;
    [ObservableProperty] private string _currentTime = DateTime.Now.ToString("HH:mm:ss");

    // --- AI Analyst & Sentiment ---
    [ObservableProperty] private string _aiSummary = "Waiting for analysis...";
    [ObservableProperty] private string _aiSentimentMode = "N/A";
    [ObservableProperty] private Brush _aiSentimentColor = GrayBrush;
    [ObservableProperty] private int _executionReadiness = 0;
    [ObservableProperty] private string _tradeReadiness = "Review pending";
    [ObservableProperty] private Brush _tradeReadinessColor = GrayBrush;

    // --- Market & Selection ---
    public ObservableCollection<string> Markets { get; } = new() { "BIST", "Crypto", "Forex" };
    [ObservableProperty] private string _selectedMarket = "BIST";
    public ObservableCollection<string> Symbols { get; } = new();
    [ObservableProperty] private string _selectedSymbol = "THYAO";
    [ObservableProperty] private string _selectedTimeframe = "1h";
    [ObservableProperty] private string _selectedRiskProfile = "Moderate";

    // --- Collections (Thread-Safe UI Updates) ---
    public ObservableCollection<WatchlistItemViewModel> WatchlistItems { get; } = new();
    public ObservableCollection<TradeJournalEntry> TradeJournal { get; } = new();
    public ObservableCollection<PositionViewModel> ActivePositions { get; } = new();
    public ObservableCollection<string> SystemLogs { get; } = new();
    public ObservableCollection<ChecklistItemViewModel> EntryChecklist { get; } = new();
    public ObservableCollection<IndicatorDetailViewModel> TimeframeConfluences { get; } = new();

    public MainViewModel(
        IMarketDataService marketDataService, 
        ITechnicalAnalysisService technicalAnalysis,
        IAiAnalystService aiAnalyst,
        IEnumerable<IAlertingService> alertingServices,
        Application.Strategy.BacktestEngine backtestEngine,
        Application.Strategy.RiskAgent riskAgent,
        ISignalRepository signalRepository,
        ITradeRepository tradeRepository,
        ILogger<MainViewModel> logger)
    {
        _marketDataService = marketDataService;
        _technicalAnalysis = technicalAnalysis;
        _aiAnalyst = aiAnalyst;
        _alertingServices = alertingServices;
        _backtestEngine = backtestEngine;
        _riskAgent = riskAgent;
        _signalRepository = signalRepository;
        _tradeRepository = tradeRepository;
        _logger = logger;

        LoadSymbols();
        InitializeTimers();
        AddSystemLog("SYSTEM: ThatsMyQuant Terminal Initialized.");
    }

    private void InitializeTimers()
    {
        _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiUpdateTimer.Tick += (s, e) => CurrentTime = DateTime.Now.ToString("HH:mm:ss");
        _uiUpdateTimer.Start();

        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _telemetryTimer.Tick += async (s, e) => await RefreshTelemetry();
        _telemetryTimer.Start();
    }

    private async Task RefreshTelemetry()
    {
        try 
        {
            var trades = await _tradeRepository.GetAllAsync(50);
            InvokeOnUi(() => RefreshPositions(trades));
        }
        catch (Exception ex) { _logger.LogWarning("Telemetry update failed: {Msg}", ex.Message); }
    }

    public async Task AnalyzeSymbol(string symbol, string market, string timeframe)
    {
        try
        {
            var candles = await _marketDataService.GetCandlesAsync(symbol, 200, market, timeframe);
            var risk = ResolveRiskParameters(SelectedRiskProfile);
            var decision = await _technicalAnalysis.AnalyzeAsync(new Signal { Symbol = symbol }, candles, risk);

            // UI Güncellemeleri
            InvokeOnUi(() => {
                UpdateKPIs(decision);
                UpdateChecklist(decision);
            });

            // AI Analizi
            await UpdateAiInsight(symbol, decision.Indicators);
        }
        catch (Exception ex) { AddSystemLog($"ERROR: {ex.Message}"); }
    }

    private void UpdateKPIs(TradeDecision decision)
    {
        ConfluenceScore = decision.Confidence;
        ConfluenceColor = decision.Confidence >= 70 ? GreenBrush : decision.Confidence >= 40 ? OrangeBrush : RedBrush;
        // Diğer KPI güncellemeleri...
    }

    [RelayCommand]
    private async Task StartTrading()
    {
        if (_isTrading) return;
        _isTrading = true;
        _cts = new CancellationTokenSource();
        Status = "🟢 LIVE TRADING";
        StatusColor = GreenBrush;
        
        AddSystemLog($"Strategy execution started for {SelectedSymbol}");
        
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                await AnalyzeSymbol(SelectedSymbol, SelectedMarket, SelectedTimeframe);
                await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private void EmergencyStop()
    {
        _cts?.Cancel();
        _isTrading = false;
        Status = "🔴 STOPPED";
        StatusColor = RedBrush;
        AddSystemLog("EMERGENCY STOP: All automated execution halted.");
    }

    private void InvokeOnUi(Action action) => System.Windows.Application.Current.Dispatcher.BeginInvoke(action);

    private void AddSystemLog(string message)
    {
        InvokeOnUi(() => {
            SystemLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
            if (SystemLogs.Count > 50) SystemLogs.RemoveAt(50);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _uiUpdateTimer?.Stop();
        _telemetryTimer?.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    // Brushes
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xDA, 0x36, 0x33));
    private static readonly Brush OrangeBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
}