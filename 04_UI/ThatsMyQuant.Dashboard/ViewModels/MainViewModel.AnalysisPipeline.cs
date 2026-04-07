using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel
{
    public async Task AnalyzeSymbol(string symbol, string market, string timeframe)
    {
        try
        {
            AddSystemLog($"ANALYSIS: Processing {symbol} ({market}) - {timeframe}...");
            var candles = await _marketDataService.GetCandlesAsync(symbol, 200, market, timeframe);
            SetMarketHealth("Online");

            var risk = ResolveRiskParameters(Studio.SelectedRiskProfile);
            var rawDecision = await _technicalAnalysis.AnalyzeAsync(new Signal { Symbol = symbol }, candles, risk);
            if (rawDecision.Indicators == null)
            {
                return;
            }

            var decision = ApplyStrategyThrottle(rawDecision);
            UpdateBotLaunchSummary();

            await UpdateAnalysisResultsAsync(symbol, market, candles, decision);

            var snapshot = await _agentCoordinator.RunFullPipelineAsync(
                new Signal { Symbol = symbol, Price = rawDecision.Indicators.CurrentPrice },
                new List<MarketTick>(),
                candles,
                MarketRegime.Trending,
                _tradeCache,
                _tradeCache.Where(t => t.IsClosed).ToList());

            UpdateTelemetry(snapshot);
            await ExecutePostAnalysisTasksAsync(symbol, market, candles, decision);
        }
        catch (Exception ex)
        {
            SetMarketHealth("Degraded");
            AddSystemLog($"ERROR: {ex.Message}");
        }
    }

    private void SetMarketHealth(string status)
    {
        ListenerHealth = status == "Online" ? "Online" : "Warning";
        MarketDataHealth = status;
    }

    private async Task UpdateAnalysisResultsAsync(string symbol, string market, List<Candle> candles, TradeDecision decision)
    {
        var indicators = decision.Indicators!;

        InvokeOnUi(() =>
        {
            UpdateWatchlistAndChart(symbol, indicators, candles);
            RefreshIndicatorProperties(indicators);
            RefreshDecisionProperties(decision);

            if (decision.ShouldTrade)
            {
                LogAnalysisResults(symbol, decision, indicators, market);
            }
            else
            {
                var reasoning = decision.Reasoning.Length > 80 ? decision.Reasoning[..80] + "..." : decision.Reasoning;
                AddSystemLog($"SKIP: {symbol} - {reasoning}");
            }
        });

        await Task.CompletedTask;
    }

    private async Task ExecutePostAnalysisTasksAsync(string symbol, string market, List<Candle> candles, TradeDecision decision)
    {
        var indicators = decision.Indicators!;
        var multiTimeframeTask = UpdateMultiTimeframeAsync(symbol, market);
        var aiAnalystTask = UpdateAiAnalyst(symbol, indicators);

        RefreshRiskPanel();

        if (decision.ShouldTrade)
        {
            _ = DispatchTradeAlertsAsync(symbol, decision, indicators);
        }

        await Task.WhenAll(multiTimeframeTask, aiAnalystTask);
        await RefreshStrategyLeaderboard();

        UpdateTraderExperience(decision, indicators, candles);
        UpdateVisualReasoning(decision, indicators, candles);
        UpdateSessionSummary();
    }

    private async Task DispatchTradeAlertsAsync(string symbol, TradeDecision decision, IndicatorSnapshot indicators)
    {
        foreach (var alert in _alertingServices)
        {
            var audit = BuildAlertAuditContext(symbol, decision, indicators);

            try
            {
                await alert.SendTradeAlertAsync(symbol, decision, indicators, audit);
            }
            catch (Exception ex)
            {
                AddSystemLog($"Alert Error: {ex.Message}");
            }
        }
    }

    private void UpdateWatchlistAndChart(string symbol, IndicatorSnapshot indicators, List<Candle> candles)
    {
        var watchlistItem = WatchlistItems.FirstOrDefault(w => w.Symbol == symbol);
        if (watchlistItem != null)
        {
            watchlistItem.Price = indicators.CurrentPrice;
            var startPrice = candles.First().Close;
            watchlistItem.ChangePercent = (double)((indicators.CurrentPrice - startPrice) / startPrice * 100);
        }

        MarketOverview.NormalizeChart(candles);
        UpdateIndicatorAgenda(indicators);
    }

    private void UpdateTelemetry(FiveTierSnapshot snapshot)
    {
        InvokeOnUi(() =>
        {
            Telemetry.OrderBookRatio = snapshot.Foundation.BidAskImbalance;
            Telemetry.TickDirectionRatio = snapshot.Foundation.TickDirectionRatio;
            Telemetry.VwapSigma = (double)snapshot.Patterns.VwapDeviationSigmas;
            Telemetry.IchimokuCloudStatus = snapshot.Patterns.IchimokuState.ToString();
            Telemetry.BollingerWidth = snapshot.Patterns.BollingerPercentB;

            foreach (var vote in snapshot.Votes)
            {
                switch (vote.StrategyTag)
                {
                    case "Momentum":
                        Studio.MomentumVote = $"{vote.Vote} ({vote.ConfidenceAdjustment:P0})";
                        break;
                    case "Breakout":
                        Studio.BreakoutVote = $"{vote.Vote} ({vote.ConfidenceAdjustment:P0})";
                        break;
                    case "StatArb":
                        Studio.StatArbVote = $"{vote.Vote} ({vote.ConfidenceAdjustment:P0})";
                        break;
                    case "Scalp":
                        Studio.ScalpVote = $"{vote.Vote} ({vote.ConfidenceAdjustment:P0})";
                        break;
                }
            }

            Studio.RiskGauntletSummary = snapshot.RiskGauntlet.Approved
                ? "PASSED"
                : $"VETO: {snapshot.RiskGauntlet.Reason}";
            Studio.KellyFraction = snapshot.RiskGauntlet.SizeMultiplier;

            AiAnalystView.MarketRegime = snapshot.Meta.StrategyWeights
                .OrderByDescending(x => x.Value)
                .FirstOrDefault()
                .Key ?? "Determining...";
            AiAnalystView.RLReward = snapshot.Meta.ConfidenceScalar;
            AiAnalystView.OnlineLearningWeights = string.Join(
                " | ",
                snapshot.Meta.StrategyWeights.Select(sw => $"{sw.Key}: {sw.Value:P0}"));
        });
    }

    private void RefreshIndicatorProperties(IndicatorSnapshot indicators)
    {
        CurrentPrice = indicators.CurrentPrice;
        UpdateRsiUi(indicators.RSI);
        UpdateMacdUi(indicators.MACDHistogram ?? 0);
        UpdateEmaUi(indicators.EMA20 ?? 0, indicators.EMA50 ?? 0);
        UpdateVitalsUi(indicators.BollingerUpper, indicators.BollingerLower, indicators.ATR, indicators.VolumeRatio);
        UpdateAlphaTrendUi(indicators.AlphaTrend);

        NearestSupport = indicators.NearestSupport?.Price ?? 0;
        NearestResistance = indicators.NearestResistance?.Price ?? 0;
    }

    private void UpdateRsiUi(double? rsi)
    {
        RSI = rsi ?? 50;
        RSIColor = RSI < 30 ? GreenBrush : RSI > 70 ? RedBrush : GrayBrush;
    }

    private void UpdateMacdUi(double macdHistogram)
    {
        if (macdHistogram > 0)
        {
            MACDStatus = $"Bullish ({macdHistogram:F4})";
            MACDColor = GreenBrush;
        }
        else
        {
            MACDStatus = $"Bearish ({macdHistogram:F4})";
            MACDColor = RedBrush;
        }
    }

    private void UpdateEmaUi(double ema20, double ema50)
    {
        if (ema20 > ema50)
        {
            EMAStatus = "Golden Cross";
            EMAColor = GreenBrush;
        }
        else
        {
            EMAStatus = "Death Cross";
            EMAColor = RedBrush;
        }
    }

    private void UpdateVitalsUi(double? bbUpper, double? bbLower, double? atr, double? volumeRatio)
    {
        BBUpper = bbUpper ?? 0;
        BBLower = bbLower ?? 0;
        ATR = atr ?? 0;
        VolumeRatio = volumeRatio ?? 1.0;
        VolumeColor = VolumeRatio >= 1.5 ? GreenBrush : GrayBrush;
    }

    private void UpdateAlphaTrendUi(AlphaTrendResult? alpha)
    {
        if (alpha == null)
        {
            return;
        }

        AlphaTrendValue = (decimal?)alpha.Value;
        AlphaTrendStatus = alpha.IsBullish ? "BULLISH" : "BEARISH";
        AlphaTrendColor = alpha.IsBullish ? GreenBrush : RedBrush;

        if (alpha.Signal != null && alpha.IsConfirmed)
        {
            AddSystemLog($"AlphaTrend CONFIRMED {alpha.Signal} signal.");
        }
    }

    private void RefreshDecisionProperties(TradeDecision decision)
    {
        ConfluenceScore = decision.Confidence;
        ConfluenceColor = decision.Confidence >= 60
            ? GreenBrush
            : decision.Confidence >= 40
                ? OrangeBrush
                : RedBrush;

        StopLoss = decision.StopLoss;
        TakeProfit = decision.TakeProfit;
        PositionSize = decision.PositionSizePercent;
        Telemetry.AnalysisTimestamp = $"Updated {DateTime.Now:HH:mm:ss}";
    }

    private void LogAnalysisResults(string symbol, TradeDecision decision, IndicatorSnapshot indicators, string market)
    {
        var alertAudit = BuildAlertAuditContext(symbol, decision, indicators);

        TradeJournal.Insert(0, new TradeJournalEntry
        {
            Time = DateTime.Now,
            Symbol = symbol,
            Side = decision.Side.ToString(),
            Price = indicators.CurrentPrice,
            Confidence = decision.Confidence,
            Status = "Signal Approved",
            ExecutionMode = alertAudit.ExecutionMode,
            PromotionStage = alertAudit.PromotionStage,
            StageExplanation = alertAudit.StageExplanation,
            ExactBucketImpact = alertAudit.ExactBucketImpact,
            ExactBucketSummary = alertAudit.ExactBucketSummary,
            VenueImpact = alertAudit.VenueImpact,
            AllocatorImpact = alertAudit.AllocatorImpact,
            EdgeSummary = alertAudit.EdgeSummary,
            ExitSummary = alertAudit.ExitSummary,
            MlDriftImpact = alertAudit.MlDriftImpact,
            MlDriftSummary = alertAudit.MlDriftSummary,
            VenueImpactColor = ResolveJournalVenueImpactColor(decision, indicators),
            MlDriftImpactColor = GrayBrush,
            ExecutionModeColor = GetCurrentExecutionModeColor(),
            SideColor = decision.Side == TradeSide.Buy ? GreenBrush : RedBrush,
            PnL = 0m,
            Reasoning = decision.Reasoning.Length > 60 ? decision.Reasoning[..60] + "..." : decision.Reasoning
        });

        RefreshOperatorAuditAlerts();

        var currency = market == "BIST" ? "TRY" : "$";
        AddSystemLog($"Signal approved: {decision.Side} {symbol} @ {currency}{indicators.CurrentPrice:F2} (Score: {decision.Confidence}/100)");
    }

    private async Task UpdateAiAnalyst(string symbol, IndicatorSnapshot indicators)
    {
        try
        {
            InvokeOnUi(() =>
            {
                AiAnalystView.AiSummary = "Generating AI insight...";
                AiAnalystView.AiSentimentMode = "Analyzing";
                AiAnalystView.AiSentimentColor = GrayBrush;
                AiAnalystView.AiTradeRecommendation = "Preparing recommendation...";
                AiAnalystView.AiRiskSummary = "Checking risk posture...";
                AiAnalystView.ExecutionReadiness = 0;
                AiAnalystView.TraderSafetyStatus = "Review pending";
                AiAnalystView.TraderSafetyColor = GrayBrush;
                TraderWarnings.Clear();
            });

            var result = await _aiAnalyst.AnalyzeSnapshotAsync(symbol, indicators);

            InvokeOnUi(() =>
            {
                AiServiceHealth = "Online";
                AiAnalystView.AiSummary = result.ExecutiveSummary;
                AiAnalystView.AiTradeRecommendation = result.TradeRecommendation;
                AiAnalystView.AiRiskSummary = result.RiskSummary;
                AiAnalystView.ExecutionReadiness = result.ExecutionReadinessScore;
                UpdateTraderSafety(result);

                if (result.SentimentScore >= 0.5)
                {
                    AiAnalystView.AiSentimentMode = $"Strong Bullish ({result.SentimentScore:F2})";
                    AiAnalystView.AiSentimentColor = GreenBrush;
                }
                else if (result.SentimentScore <= -0.5)
                {
                    AiAnalystView.AiSentimentMode = $"Strong Bearish ({result.SentimentScore:F2})";
                    AiAnalystView.AiSentimentColor = RedBrush;
                }
                else if (result.SentimentScore > 0)
                {
                    AiAnalystView.AiSentimentMode = $"Bullish ({result.SentimentScore:F2})";
                    AiAnalystView.AiSentimentColor = GreenBrush;
                }
                else if (result.SentimentScore < 0)
                {
                    AiAnalystView.AiSentimentMode = $"Bearish ({result.SentimentScore:F2})";
                    AiAnalystView.AiSentimentColor = RedBrush;
                }
                else
                {
                    AiAnalystView.AiSentimentMode = "Neutral (0.00)";
                    AiAnalystView.AiSentimentColor = GrayBrush;
                }
            });
        }
        catch (Exception ex)
        {
            InvokeOnUi(() =>
            {
                AiAnalystView.AiSummary = "AI Analyst unavailable.";
                AiAnalystView.AiSentimentMode = "Error";
                AiAnalystView.AiSentimentColor = RedBrush;
                AiAnalystView.AiTradeRecommendation = "Fallback to manual review";
                AiAnalystView.AiRiskSummary = "AI analyst unavailable, so use manual risk checks only.";
                AiAnalystView.ExecutionReadiness = 0;
                AiAnalystView.TraderSafetyStatus = "Manual review required";
                AiAnalystView.TraderSafetyColor = OrangeBrush;
                TraderWarnings.Clear();
                TraderWarnings.Add("AI analyst unavailable");
                AiServiceHealth = "Degraded";
            });

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
            AiAnalystView.TraderSafetyStatus = "Ready with controls";
            AiAnalystView.TraderSafetyColor = GreenBrush;
            return;
        }

        if (result.ExecutionReadinessScore >= 55)
        {
            AiAnalystView.TraderSafetyStatus = "Caution";
            AiAnalystView.TraderSafetyColor = OrangeBrush;
            return;
        }

        AiAnalystView.TraderSafetyStatus = "Stand down";
        AiAnalystView.TraderSafetyColor = RedBrush;
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
        var resistanceRoom = indicators.NearestResistance?.Price is decimal resistance ? resistance - indicators.CurrentPrice : atr * 2;
        var supportRoom = indicators.NearestSupport?.Price is decimal support ? indicators.CurrentPrice - support : atr * 2;
        var reward = Math.Abs(decision.TakeProfit - indicators.CurrentPrice);
        var risk = Math.Abs(indicators.CurrentPrice - decision.StopLoss);
        RiskRewardRatio = risk > 0 ? reward / risk : 0;

        InvokeOnUi(() =>
        {
            PopulateEntryChecklist(indicators, decision, supportRoom, resistanceRoom, atr);
            UpdateTraderStatusLabels(decision, indicators, strategyTag, regimeTag);
            PopulateSafetyBanners(indicators);
            DetermineTradeReadiness(decision, indicators);
        });

        if (candles.Count > 0)
        {
            MarketOverview.InspectChartAt(MarketOverview.ChartCanvasWidth * 0.82);
        }

        UpdateHftWorkspace();
    }

    private void PopulateEntryChecklist(IndicatorSnapshot indicators, TradeDecision decision, decimal supportRoom, decimal resistanceRoom, decimal atr)
    {
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
            !IsSoftPauseArmed && RiskRewardRatio >= 1.4m && decision.PositionSizePercent > 0,
            "Risk, size, and soft-pause controls are inside tolerance.");
        AddChecklistItem(
            "Execution quality",
            AiAnalystView.ExecutionReadiness >= 55 && QueueHealth != "Critical (Dead-Letter)" && MarketDataHealth == "Online",
            "Execution conditions and system health are acceptable.");
    }

    private void UpdateTraderStatusLabels(TradeDecision decision, IndicatorSnapshot indicators, string strategyTag, string regimeTag)
    {
        AiAnalystView.SignalConfidenceLabel = $"Signal confidence: {decision.Confidence}/100";
        AiAnalystView.ExecutionQualityLabel = $"Execution quality: {AiAnalystView.ExecutionReadiness}/100";

        var exactBucketPressure = ResolveSelectedExactBucketPressure();
        var concordanceSignal = ResolveSelectedConcordanceSignal();
        var exactBucketExecutionText = exactBucketPressure is null
            ? "Exact bucket: pressure is still unconfirmed."
            : $"Exact bucket: {exactBucketPressure.Value.Summary}";
        var concordanceExecutionText = concordanceSignal is null
            ? "Concordance: edge and venue execution are still unconfirmed."
            : $"Concordance: {concordanceSignal.Value.Summary}";

        MarketOverview.MarketPulse =
            $"Price {indicators.CurrentPrice:F2} | RSI {(indicators.RSI ?? 0):F1} | Vol {(indicators.VolumeRatio ?? 0):F1}x | " +
            $"ATR {(indicators.ATR ?? 0):F2} | {exactBucketExecutionText} {concordanceExecutionText}";
        AiAnalystView.ExecutionPlan =
            $"Entry {decision.Side} with stop {decision.StopLoss:F2}, target {decision.TakeProfit:F2}, " +
            $"size {decision.PositionSizePercent:P1}, R/R 1:{RiskRewardRatio:F2}. {exactBucketExecutionText} {concordanceExecutionText}";
        AiAnalystView.WhyThisTrade =
            $"{decision.Side} bias via {strategyTag} in {regimeTag}. " +
            $"{decision.Reasoning} Stop {decision.StopLoss:F2}, target {decision.TakeProfit:F2}, size {decision.PositionSizePercent:P1}.";
        MarketOverview.BotStatusSummary = decision.ShouldTrade
            ? $"Scanning complete: {strategyTag} found a {decision.Side} candidate in {regimeTag}. Execution quality is {AiAnalystView.ExecutionReadiness}/100. Throttle {StrategyThrottleStatus.ToLowerInvariant()}. {exactBucketExecutionText} {concordanceExecutionText}"
            : $"Bot is scanning but standing down. {decision.Reasoning}";
        MarketOverview.DailyMarketBriefing =
            $"{MarketOverview.SelectedMarket} briefing for {MarketOverview.SelectedSymbol}: {regimeTag} backdrop with {strategyTag} bias. " +
            $"Current price {indicators.CurrentPrice:F2}, support {NearestSupport:F2}, resistance {NearestResistance:F2}, " +
            $"AI stance: {AiAnalystView.AiTradeRecommendation}. {exactBucketExecutionText} {concordanceExecutionText}";
    }

    private void DetermineTradeReadiness(TradeDecision decision, IndicatorSnapshot indicators)
    {
        var passedChecks = EntryChecklist.Count(item => item.IsPassing);

        if (decision.ShouldTrade && AiAnalystView.ExecutionReadiness >= 75 && passedChecks >= 5 && !IsSoftPauseArmed)
        {
            AiAnalystView.TradeReadiness = "Ready";
            AiAnalystView.TradeReadinessColor = GreenBrush;
            AiAnalystView.TradeReadinessReason = "Confluence, execution quality, and controls are all supportive.";
        }
        else if (AiAnalystView.ExecutionReadiness >= 55 && passedChecks >= 4 && !IsSoftPauseArmed)
        {
            AiAnalystView.TradeReadiness = "Caution";
            AiAnalystView.TradeReadinessColor = OrangeBrush;
            AiAnalystView.TradeReadinessReason = "The setup is tradable, but one or more filters still need respect.";
        }
        else
        {
            AiAnalystView.TradeReadiness = "Do Not Trade";
            AiAnalystView.TradeReadinessColor = RedBrush;
            AiAnalystView.TradeReadinessReason = "At least one critical filter is failing or execution quality is too weak.";
        }
    }

    private void UpdateHftWorkspace()
    {
        var (exactBucketPressure, concordanceSignal, exactBucketHftText, concordanceHftText) = ResolveHftContext();
        var (sessionTrades, sessionPnl, sessionWinRate) = CalculateHftPerformance();
        var estimatedSlippageBps = CalculateEstimatedSlippage();

        InvokeOnUi(() =>
        {
            UpdateHftLabels(sessionTrades.Count, sessionPnl, sessionWinRate, estimatedSlippageBps, exactBucketHftText, concordanceHftText);
            ResolveHftBias(exactBucketPressure, concordanceSignal, exactBucketHftText, concordanceHftText);
        });
    }

    private ((string Impact, string Summary, Brush Color)? Pressure, (string Impact, string Summary, Brush Color)? Concordance, string PressureText, string ConcordanceText) ResolveHftContext()
    {
        var pressure = ResolveSelectedExactBucketPressure();
        var concordance = ResolveSelectedConcordanceSignal();
        var pressureText = pressure is null
            ? "Exact bucket: pressure not confirmed yet."
            : $"Exact bucket: {pressure.Value.Summary}";
        var concordanceText = concordance is null
            ? "Concordance: exact bucket edge and venue execution are not confirmed yet."
            : $"Concordance: {concordance.Value.Summary}";
        return (pressure, concordance, pressureText, concordanceText);
    }

    private (List<Trade> Trades, decimal Pnl, double WinRate) CalculateHftPerformance()
    {
        var trades = _tradeCache
            .Where(trade => trade.Status == TradeStatus.Executed && trade.IsClosed && trade.ExitTime.HasValue)
            .Where(trade => trade.ExitTime!.Value.Date == DateTime.UtcNow.Date)
            .ToList();
        var pnl = trades.Sum(CalculateTradePnL);
        var wins = trades.Count(trade => CalculateTradePnL(trade) > 0);
        var winRate = trades.Count == 0 ? 0 : wins * 100.0 / trades.Count;
        return (trades, pnl, winRate);
    }

    private decimal CalculateEstimatedSlippage()
    {
        var normalizedAtr = ATR <= 0 ? 0.25m : Convert.ToDecimal(ATR);
        var referencePrice = CurrentPrice <= 0 ? 1m : CurrentPrice;
        var queueMultiplier = GetQueueMultiplier(QueueHealth);
        var readinessMultiplier = AiAnalystView.ExecutionReadiness >= 70 ? 0.75m : AiAnalystView.ExecutionReadiness >= 55 ? 1.0m : 1.25m;
        var volumeMultiplier = VolumeRatio >= 1.2 ? 0.8m : 1.15m;
        return Math.Round((normalizedAtr / referencePrice) * 10000m * queueMultiplier * readinessMultiplier * volumeMultiplier, 1);
    }

    private void UpdateHftLabels(int tradeCount, decimal sessionPnl, double sessionWinRate, decimal estimatedSlippageBps, string pressureText, string concordanceText)
    {
        Telemetry.HftWorkspaceHeadline = $"Execution mode {TradingModeLabel}. Queue {QueueHealth.ToLowerInvariant()}, market data {MarketDataHealth.ToLowerInvariant()}, AI {AiServiceHealth.ToLowerInvariant()}. {pressureText} {concordanceText}";
        Telemetry.HftLatencySummary = $"API {ApiPing} | Server {ServerLatency} | Listener {ListenerHealth}";
        Telemetry.HftMicrostructureSummary = $"OFI {Telemetry.HftInferenceStatus} | Z-score {Telemetry.ZScoreStatus} | Volume {VolumeRatio:F1}x | ATR {ATR:F2}";
        Telemetry.HftEstimatedSlippage = $"~{estimatedSlippageBps:N1} bps";
        Telemetry.HftSessionPnl = sessionPnl >= 0 ? $"+{sessionPnl:N0}" : $"{sessionPnl:N0}";
        Telemetry.HftSessionPnlColor = sessionPnl >= 0 ? GreenBrush : RedBrush;
        Telemetry.HftSessionWinRate = tradeCount == 0 ? "WR --" : $"WR {sessionWinRate:F0}% / {tradeCount}";

        ResolveFillQuality(estimatedSlippageBps);
    }

    private void ResolveFillQuality(decimal estimatedSlippageBps)
    {
        if (estimatedSlippageBps <= 8 && AiAnalystView.ExecutionReadiness >= 70 && QueueHealth == "Optimal")
        {
            Telemetry.HftFillQuality = "Institutional";
            Telemetry.HftFillQualityColor = GreenBrush;
        }
        else if (estimatedSlippageBps <= 15 && AiAnalystView.ExecutionReadiness >= 55)
        {
            Telemetry.HftFillQuality = "Acceptable";
            Telemetry.HftFillQualityColor = OrangeBrush;
        }
        else
        {
            Telemetry.HftFillQuality = "Fragile";
            Telemetry.HftFillQualityColor = RedBrush;
        }
    }

    private void ResolveHftBias((string Impact, string Summary, Brush Color)? pressure, (string Impact, string Summary, Brush Color)? concordance, string pressureText, string concordanceText, decimal estimatedSlippageBps = 0m)
    {
        if (CheckHftBlocked(pressure, concordance, pressureText, concordanceText))
        {
            return;
        }

        if (CheckHftProbe(pressure, concordance, concordanceText))
        {
            return;
        }

        if (CheckHftReady(pressure, concordance, pressureText, concordanceText))
        {
            return;
        }

        Telemetry.HftExecutionBias = "Standby";
        Telemetry.HftExecutionBiasColor = GrayBrush;
        Telemetry.HftRiskSummary = $"Wait for stronger liquidity before using HFT-style actions. {pressureText} {concordanceText}";
    }

    private bool CheckHftBlocked((string Impact, string Summary, Brush Color)? pressure, (string Impact, string Summary, Brush Color)? concordance, string pressureText, string concordanceText)
    {
        if (DeadLetterCount > 0 || QueueHealth.StartsWith("Critical", StringComparison.OrdinalIgnoreCase))
        {
            SetHftBias("Blocked", RedBrush, $"Dead-letter traffic/queue stress. HFT disabled. {pressureText} {concordanceText}");
            return true;
        }

        if (MarketDataHealth != "Online" || StrategyThrottleStatus == "Blocked")
        {
            SetHftBias("Caution", OrangeBrush, $"Execution degraded by data/throttle constraints. {pressureText} {concordanceText}");
            return true;
        }

        if (concordance?.Impact.Contains("capping", StringComparison.OrdinalIgnoreCase) == true)
        {
            SetHftBias("Blocked", RedBrush, $"Concordance blocking HFT aggression. {concordance.Value.Summary}");
            return true;
        }

        if (pressure?.Impact.Contains("near Paper", StringComparison.OrdinalIgnoreCase) == true)
        {
            SetHftBias("Blocked", RedBrush, $"Bucket pressure holding near Paper. {pressure.Value.Summary}");
            return true;
        }

        return false;
    }

    private bool CheckHftProbe((string Impact, string Summary, Brush Color)? pressure, (string Impact, string Summary, Brush Color)? concordance, string concordanceText)
    {
        if (pressure?.Impact.Contains("LiveProbe", StringComparison.OrdinalIgnoreCase) == true)
        {
            var summary = concordance?.Impact.Contains("holding LiveProbe", StringComparison.OrdinalIgnoreCase) == true
                ? $"Concordance holding in LiveProbe. {concordance.Value.Summary}"
                : $"Bucket pressure holding in LiveProbe. {pressure.Value.Summary} {concordanceText}";
            SetHftBias("Caution", OrangeBrush, summary);
            return true;
        }

        return false;
    }

    private bool CheckHftReady((string Impact, string Summary, Brush Color)? pressure, (string Impact, string Summary, Brush Color)? concordance, string pressureText, string concordanceText)
    {
        if (AiAnalystView.ExecutionReadiness >= 70 && QueueHealth == "Optimal" && VolumeRatio >= 1.2)
        {
            var summary = ResolveHftReadySummary(pressure, concordance, pressureText, concordanceText);
            SetHftBias("Ready", GreenBrush, summary);
            return true;
        }

        return false;
    }

    private void SetHftBias(string bias, Brush color, string summary)
    {
        Telemetry.HftExecutionBias = bias;
        Telemetry.HftExecutionBiasColor = color;
        Telemetry.HftRiskSummary = summary;
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

    private decimal GetQueueMultiplier(string queueHealth)
    {
        if (queueHealth == "Optimal")
        {
            return 0.65m;
        }

        if (queueHealth.StartsWith("Critical", StringComparison.OrdinalIgnoreCase))
        {
            return 1.8m;
        }

        return 1.1m;
    }

    private string ResolveHftReadySummary((string Impact, string Summary, Brush Color)? pressure, (string Impact, string Summary, Brush Color)? concordance, string pressureText, string concordanceText)
    {
        if (concordance?.Impact.Contains("supports scaling", StringComparison.OrdinalIgnoreCase) == true)
        {
            return $"Concordance supports scaling. {concordance.Value.Summary}";
        }

        if (pressure?.Impact.Contains("supports scaled deployment", StringComparison.OrdinalIgnoreCase) == true)
        {
            return $"Bucket pressure supports scaling. {pressure.Value.Summary} {concordanceText}";
        }

        return $"Conditions supportive for faster workflows. {pressureText} {concordanceText}";
    }
}
