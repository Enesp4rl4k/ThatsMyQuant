using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ThatsMyQuant.Contracts.Notifications;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Fires the AI analyst (Gemini / DeepSeek via Microsoft.SemanticKernel) on every closed
/// position to produce a 2-3 sentence "post-mortem" review. The review is appended to
/// <c>%LOCALAPPDATA%\ThatsMyQuant\ai_trade_reviews.jsonl</c> so the daily Telegram digest
/// can pick up the last 24h's reviews and show them in-line with the trade attribution.
///
/// Cooldown: max 1 review per minute (60s sliding window) to avoid hammering the LLM
/// when many trades close in burst. Hot trades (NetPnlPercent ≥ 1% or ≤ -1%) bypass the
/// cooldown so we never miss reviewing a meaningful outcome.
/// </summary>
public sealed class AiTradeReviewHandler : INotificationHandler<PositionClosedNotification>
{
    private readonly ILogger<AiTradeReviewHandler> _logger;
    private readonly IChatCompletionService? _chat;
    private readonly string _reviewLogPath;
    private static readonly ConcurrentQueue<DateTime> _recentCalls = new();
    private const int CallsPerMinuteLimit = 5;
    // Threshold above which a trade ALWAYS gets reviewed regardless of cooldown.
    private const decimal HotPnlThresholdPct = 1.0m;

    public AiTradeReviewHandler(
        ILogger<AiTradeReviewHandler> logger,
        Kernel? kernel = null)
    {
        _logger = logger;
        _chat = kernel?.Services.GetService(typeof(IChatCompletionService)) as IChatCompletionService;

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThatsMyQuant");
        Directory.CreateDirectory(dir);
        _reviewLogPath = Path.Combine(dir, "ai_trade_reviews.jsonl");
    }

    public async Task Handle(PositionClosedNotification note, CancellationToken ct)
    {
        if (_chat is null)
        {
            // No LLM available — skip silently (paper-safe, costs nothing).
            return;
        }

        // Cooldown gate.
        var nowUtc = DateTime.UtcNow;
        var isHot = Math.Abs(note.NetPnlPercent) >= HotPnlThresholdPct;
        if (!isHot && !TryReserveSlot(nowUtc))
        {
            _logger.LogDebug("AiReview skipped (cooldown): {Sym} {Strat} pnl={Pnl:F2}", note.Symbol, note.StrategyTag, note.NetPnlPercent);
            return;
        }

        var holdingMinutes = (note.ExitTimeUtc - note.EntryTimeUtc).TotalMinutes;
        var dirSign = note.NetPnl >= 0 ? "WIN" : "LOSS";

        var prompt = $@"You are a quant trading coach reviewing a just-closed position. Be brutally concise (max 3 sentences). Focus on:
1. Was the EXIT REASON appropriate given the strategy's edge?
2. Was the HOLD TIME consistent with the strategy archetype?
3. ONE sharp actionable improvement for the next setup.

TRADE:
Symbol: {note.Symbol}  Side: {note.Side}  Outcome: {dirSign} ${note.NetPnl:+0.00;-0.00}  ({note.NetPnlPercent:+0.00;-0.00}%)
Entry→Exit: {note.EntryPrice:F4} → {note.ExitPrice:F4}  qty={note.Quantity:F4}
Hold: {holdingMinutes:F0} min   ExitReason: {note.ExitReason}
Strategy tag: {note.StrategyTag}    Regime: {note.RegimeTag}    ExecMode: {note.ExecutionMode}

Output: 3 short sentences max, no fluff, no markdown headers.";

        string review;
        try
        {
            var resp = await _chat.GetChatMessageContentsAsync(prompt, cancellationToken: ct).ConfigureAwait(false);
            review = resp.FirstOrDefault()?.Content?.Trim() ?? "(no AI response)";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI trade review LLM call failed for {Sym}", note.Symbol);
            return;
        }

        // Persist to JSONL for daily digest pickup.
        try
        {
            var entry = new
            {
                ts = nowUtc,
                symbol = note.Symbol,
                strategy = note.StrategyTag,
                regime = note.RegimeTag,
                side = note.Side.ToString(),
                netPnl = note.NetPnl,
                netPnlPct = note.NetPnlPercent,
                holdMinutes = (int)holdingMinutes,
                exitReason = note.ExitReason,
                executionMode = note.ExecutionMode,
                review
            };
            var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            await File.AppendAllTextAsync(_reviewLogPath, line, Encoding.UTF8, ct).ConfigureAwait(false);
            _logger.LogInformation("AI review written for {Sym} {Strat} pnl={Pnl:+0.00;-0.00}: {Excerpt}",
                note.Symbol, note.StrategyTag, note.NetPnl,
                review.Length > 120 ? review.Substring(0, 120) + "…" : review);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI trade review persist failed for {Sym}", note.Symbol);
        }
    }

    /// <summary>Returns true if a call slot is available within the last 60s window.</summary>
    private static bool TryReserveSlot(DateTime nowUtc)
    {
        // Drain expired entries.
        var cutoff = nowUtc.AddSeconds(-60);
        while (_recentCalls.TryPeek(out var oldest) && oldest < cutoff)
            _recentCalls.TryDequeue(out _);

        if (_recentCalls.Count >= CallsPerMinuteLimit) return false;
        _recentCalls.Enqueue(nowUtc);
        return true;
    }
}
