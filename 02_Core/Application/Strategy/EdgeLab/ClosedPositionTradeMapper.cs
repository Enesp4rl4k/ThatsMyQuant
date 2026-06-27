using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// Pure mapping from a closed <see cref="PaperPosition"/> to a persisted <see cref="Trade"/>
/// row, used by the worker's closed-position → db.Trades bridge (2026-06-12 ledger-drift fix).
///
/// Kept side-effect-free and in the Application layer (mirrors the
/// <see cref="EdgeLabAltShortCapEvaluator"/> convention) so the invariants can be unit-tested
/// without a DbContext or hosted-service host:
///   1. <see cref="MirrorId"/> is content-addressed → idempotent inserts.
///   2. <see cref="MapToTrade"/> chooses fields so the consumers' price-based PnL
///      (sideMul·(exit−entry)·qty − fees + funding) reproduces the position's realized net.
/// </summary>
public static class ClosedPositionTradeMapper
{
    /// <summary>ExecutionMode sentinel that <c>DbToPortfolioSyncHostedService.OkxMirrorMarker</c>
    /// stamps on positions hydrated from OKX. Mirrored here (Application can't reference the
    /// worker exe) — must stay in sync with that constant.</summary>
    public const string LiveExecutionMarker = "LIVE_OKX";

    /// <summary>ExecutionSource stamped on Trade rows mirrored from paper-only closes.</summary>
    public const string PaperMirrorSource = "PAPER_MIRROR";

    /// <summary>Deterministic, content-addressed id so the same close maps to the same Trade
    /// row no matter how many times — or from which freshly-built object — we see it.</summary>
    public static Guid MirrorId(PaperPosition p)
    {
        var key = string.Join("|",
            p.StrategyTag,
            p.Symbol,
            p.EntryTime.Ticks.ToString(CultureInfo.InvariantCulture),
            (p.ExitTime?.Ticks ?? 0L).ToString(CultureInfo.InvariantCulture),
            p.Quantity.ToString(CultureInfo.InvariantCulture));
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }

    /// <summary>Map a closed <see cref="PaperPosition"/> onto a <see cref="Trade"/> row.</summary>
    public static Trade MapToTrade(Guid id, PaperPosition p)
    {
        var isLive = string.Equals(p.ExecutionMode, LiveExecutionMarker, StringComparison.OrdinalIgnoreCase);
        return new Trade
        {
            Id = id,
            // db.Trades has a UNIQUE constraint on SignalId. Paper-only closes carry no
            // originating signal (p.SignalId == null) — mapping them all to Guid.Empty made
            // the 2nd such mirror insert throw "UNIQUE constraint failed: Trades.SignalId",
            // failing the whole tick (observed every 5 min, 2026-06). Fall back to the
            // content-addressed MirrorId, which is unique per close and deterministic, so the
            // UNIQUE invariant holds and re-mirroring stays idempotent. A real, non-empty
            // SignalId is preserved (keeps Trade→Signal linkage for the live-path closes).
            SignalId = p.SignalId is { } sid && sid != Guid.Empty ? sid : id,
            Symbol = p.Symbol,
            Side = p.Side,
            EntryPrice = p.EntryPrice,
            ExitPrice = p.CurrentPrice,
            AverageExitPrice = p.CurrentPrice,
            Quantity = p.Quantity,
            RequestedQuantity = p.Quantity,
            FilledQuantity = p.Quantity,
            ClosedQuantity = p.Quantity,
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.Closed,
            StrategyTag = p.StrategyTag,
            RegimeTag = p.RegimeTag,
            ExitPolicyTag = p.ExitPolicyTag,
            ExecutionMode = p.ExecutionMode,
            PromotionStage = p.PromotionStage,
            TotalFeesPaid = p.RealFeePaid ?? 0m,
            NetFunding = p.NetFunding,
            Leverage = p.Leverage,
            MarginMode = p.MarginMode,
            ExitReason = p.ExitReason,
            BarsHeld = p.BarsHeld,
            MaxHoldingBars = p.MaxHoldingBars,
            ExecutionSource = isLive ? "LIVE" : PaperMirrorSource,
            EntryTime = p.EntryTime,
            SubmittedTime = p.EntryTime,
            ExitTime = p.ExitTime,
            LastUpdatedTime = p.ExitTime,
        };
    }
}
