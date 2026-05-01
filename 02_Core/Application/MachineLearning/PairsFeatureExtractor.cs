using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// Extracts ML features specific to the Pairs (cointegration mean-reversion) strategy.
/// Funding-strategy features (FundingFeatureExtractor) are useless here — we need the
/// cointegration / spread dynamics: z-score, half-life, hedge ratio, ADF t-stat, etc.
/// Emitted at pair-close into per-strategy StrategySampleStore for future training.
///
/// Schema (v1) — pair-level cointegration + execution context at close time:
///   z_at_entry              : signal z-score that triggered the entry (parsed from Reasoning)
///   half_life_bars          : OU half-life at entry (parsed from Reasoning)
///   hedge_ratio_beta        : β at entry (parsed from Reasoning)
///   spread_side_long        : 1 if LongSpread, 0 if ShortSpread
///   hold_duration_hours     : pair held duration
///   net_pnl_pct_basis       : signed return as fraction of basis (using Quantity × EntryPrice)
///   exit_reason_z_revert    : 1 if exit was z-score reversion to band
///   exit_reason_stop_loss   : 1 if z-stop fired
///   exit_reason_coint_break : 1 if cointegration broke mid-trade
///   exit_reason_time_stop   : 1 if max-hold fired
///   real_fee_to_notional    : actual leakage measure (when RealFeePaid present)
///   real_slippage_to_not    : execution-quality measure
///
/// Features are extracted from PaperPosition fields + the Reasoning/RegimeTag strings
/// that PairsHybridExecutor stamps at close time. Schema-version bump (v2) will land
/// when we wire dedicated fields on PaperPosition for z/β/HL — for now reasoning-string
/// parsing is the lowest-disruption way to capture the data.
/// </summary>
public static class PairsFeatureExtractor
{
    public static Dictionary<string, double> Extract(PaperPosition position)
    {
        var f = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // Hedge ratio + entry z + half-life are stamped by PairsHybridExecutor in
        // Reasoning string: `Hybrid pairs close. β=1.234 entryZ=2.50` (cf. line 494).
        // A dedicated field is the proper fix; for now we parse — best-effort, default 0.
        var reasoning = position.Reasoning ?? "";
        f["z_at_entry"]       = ParseAfterToken(reasoning, "entryZ=");
        f["hedge_ratio_beta"] = ParseAfterToken(reasoning, "β=");
        f["half_life_bars"]   = ParseAfterToken(reasoning, "HL=");

        f["spread_side_long"] = position.Side == TradeSide.Buy ? 1.0 : 0.0;

        var hold = position.ExitTime - position.EntryTime ?? TimeSpan.Zero;
        f["hold_duration_hours"] = hold.TotalHours;

        // Pair PnL is stored on PaperPosition.RealizedPnL; basis approximation
        // uses EntryPrice × Quantity (Quantity is 0 on pair-level mirror, so fall
        // back to a 1.0 placeholder — trainer can re-derive from leg legs later).
        var basisProxy = position.Quantity > 0
            ? (double)(position.EntryPrice * position.Quantity)
            : 1.0;
        f["net_pnl_pct_basis"] = basisProxy > 0 ? (double)position.RealizedPnL / basisProxy : 0.0;

        var reason = (position.ExitReason ?? "").ToLowerInvariant();
        f["exit_reason_z_revert"]    = reason.Contains("zrevert") || reason.Contains("z<=") || reason.Contains("revert") ? 1.0 : 0.0;
        f["exit_reason_stop_loss"]   = reason.Contains("stop") ? 1.0 : 0.0;
        f["exit_reason_coint_break"] = reason.Contains("cointbreak") || reason.Contains("cointegrationbreak") ? 1.0 : 0.0;
        f["exit_reason_time_stop"]   = reason.Contains("time") || reason.Contains("maxhold") ? 1.0 : 0.0;

        if (position.RealFeePaid.HasValue && basisProxy > 0)
            f["real_fee_to_notional"] = (double)position.RealFeePaid.Value / basisProxy;
        if (position.RealSlippagePaid.HasValue && basisProxy > 0)
            f["real_slippage_to_not"] = (double)position.RealSlippagePaid.Value / basisProxy;

        return f;
    }

    /// <summary>Best-effort extract numeric value following a token in a free-form string.
    /// Returns 0 when not found / unparseable. Tolerant of comma vs dot decimal separators.</summary>
    private static double ParseAfterToken(string text, string token)
    {
        var idx = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0.0;
        var rest = text.AsSpan(idx + token.Length);
        // Read the next contiguous "number-like" run: digits / sign / decimal sep
        int end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] is '-' or '+' or '.' or ',')) end++;
        if (end == 0) return 0.0;
        var slice = rest.Slice(0, end).ToString().Replace(',', '.');
        return double.TryParse(slice, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.0;
    }
}
