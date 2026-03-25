using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Application.Strategy;
using Microsoft.Extensions.DependencyInjection;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public partial class FocusedBacktestViewModel : ObservableObject
{
    private readonly AgentCoordinator _agentCoordinator;
    private readonly IMarketDataService _marketData;
    private readonly IEnumerable<IFocusedAgent> _focusedAgents;

    [ObservableProperty] private string _selectedMarket = "BIST";
    [ObservableProperty] private string _selectedTimeframe = "1h";
    [ObservableProperty] private string _selectedSymbol = "THYAO";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private double _simulationProgress;
    
    public ObservableCollection<string> Symbols { get; } = new() { "THYAO", "BTC-USDT", "ETH-USDT", "GARAN", "AKBNK", "EREGL" };
    public ObservableCollection<string> AgentLogs { get; } = new();

    private CancellationTokenSource? _simCts;

    public FocusedBacktestViewModel(
        AgentCoordinator agentCoordinator, 
        IMarketDataService marketData,
        IEnumerable<IFocusedAgent> focusedAgents)
    {
        _agentCoordinator = agentCoordinator;
        _marketData = marketData;
        _focusedAgents = focusedAgents;
    }

    [RelayCommand]
    private async Task StartSimulation()
    {
        if (IsRunning) return;
        IsRunning = true;
        AgentLogs.Clear();
        SimulationProgress = 0;
        StatusMessage = $"Initializing Focused Backtest for {SelectedSymbol}...";

        _simCts = new CancellationTokenSource();

        try
        {
            // 1. Reset Agents via Coordinator
            await _agentCoordinator.ResetAllAgentsAsync(SelectedSymbol, _simCts.Token);
            AgentLogs.Add($"[SYSTEM] {DateTime.Now:HH:mm:ss} | Engines reset for focus on {SelectedSymbol}.");

            // 2. Fetch Historical Data
            var candles = await _marketData.GetCandlesAsync(SelectedSymbol, 100, SelectedMarket, SelectedTimeframe);
            if (candles == null || candles.Count < 10)
            {
                StatusMessage = "Insufficient data for focused playback.";
                IsRunning = false;
                return;
            }

            // 3. Playback Loop (The "Flow")
            var history = new List<Trade>();
            for (int i = 0; i < candles.Count; i++)
            {
                if (_simCts.Token.IsCancellationRequested) break;

                var candle = candles[i];
                StatusMessage = $"Processing {SelectedSymbol} data point {i + 1}/{candles.Count}...";
                SimulationProgress = (double)(i + 1) / candles.Count * 100;

                // Run Full Pipeline for this step
                var signal = new Signal { Symbol = SelectedSymbol, Price = candle.Close };
                var snapshot = await _agentCoordinator.RunFullPipelineAsync(
                    signal, 
                    new List<MarketTick>(), 
                    new List<Candle> { candle }, // In a real playback, we'd pass a window
                    MarketRegime.Trending, 
                    new List<Trade>(), 
                    history, 
                    _simCts.Token);

                var primaryTag = snapshot.Patterns.PatternTags.FirstOrDefault() ?? "Neutral";
                var voteSummary = snapshot.Votes.Any(v => v.Vote == VoteOutcome.Approve) ? "🔥 APPROVE" : "⚖️ HOLD";
                
                AgentLogs.Add($"[ENGINE] {candle.Timestamp:HH:mm} | {SelectedSymbol}: {candle.Close:N2} | {primaryTag} | {voteSummary}");
                
                if (AgentLogs.Count > 100) AgentLogs.RemoveAt(0);

                await Task.Delay(200, _simCts.Token); // Visual flow speed
            }

            StatusMessage = "Focused Backtest complete.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Simulation stopped by user.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _simCts?.Dispose();
        }
    }

    [RelayCommand]
    private void StopSimulation()
    {
        _simCts?.Cancel();
    }
}
