using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// Extracts ML features specific to the Funding-Alpha strategy. These are emitted
/// at trade close time into the per-strategy <see cref="StrategySampleStore"/> so a
/// future trainer (post-Tier-A) has rich context: not just RSI/EMA tensor (which is
/// directional) but the actual funding-rate dynamics that the strategy bets on.
///
/// Schema (v1) — funding-rate context at entry + position lifecycle:
///   funding_rate_at_entry         : raw rate, signed
///   funding_rate_abs              : |rate|, threshold-relative
///   funding_threshold_excess      : |rate| − configured threshold (how strong was the signal?)
///   side_long                     : 1 if long, 0 if short
///   hold_duration_min             : minutes held
///   hold_vs_max_ratio             : duration / config.MaxHoldingBars × 60min (was it held to max?)
///   entry_price                   : reference for downstream price-feature builds
///   net_pnl_pct                   : signed PnL as fraction of entry notional
///   exit_reason_funding_norm      : 1 if exit was FundingNormalized
///   exit_reason_take_profit       : 1 if TP fired
///   exit_reason_stop_loss         : 1 if SL fired
///   exit_reason_time_stop         : 1 if max-hold fired
///   exit_reason_bracket_fired     : 1 if OKX-side bracket TP/SL fired (ghost-close)
///   real_fee_to_gross_ratio       : actual leakage measure (when RealFeePaid present)
///   real_slippage_to_gross_ratio  : execution-quality measure (when measured)
///   regime_funding_strong         : 1 if funding excess &gt; 2× threshold (strong regime)
///
/// All features are normalized fractions or 0/1 indicators so they survive z-score
/// normalization at training time without unit collisions.
/// </summary>
public static class FundingFeatureExtractor
{
    /// <summary>Extract funding-strategy features for a closed paper position.</summary>
    public static Dictionary<string, double> Extract(PaperPosition position, double entryThresholdRate)
    {
        var f = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // Funding rate at entry — stored on PaperPosition.NetFunding (we use this as the
        // entry-time funding rate proxy; ideally a dedicated FundingRateAtEntry field but
        // we want zero-impact instrumentation right now).
        var fundingRate = (double)position.NetFunding;
        f["funding_rate_at_entry"]    = fundingRate;
        f["funding_rate_abs"]         = Math.Abs(fundingRate);
        f["funding_threshold_excess"] = Math.Max(0.0, Math.Abs(fundingRate) - entryThresholdRate);

        f["side_long"] = position.Side == TradeSide.Buy ? 1.0 : 0.0;

        // Hold duration
        var hold = position.ExitTime - position.EntryTime ?? TimeSpan.Zero;
        f["hold_duration_min"] = hold.TotalMinutes;
        // 20h default max hold for Long tier; ratio = held / max
        f["hold_vs_max_ratio"] = hold.TotalMinutes / 1200.0; // 20h × 60min

        f["entry_price"] = (double)position.EntryPrice;

        var entryNotional = (double)(position.EntryPrice * position.Quantity);
        var grossPnl = entryNotional > 0
            ? (double)position.RealizedPnL / entryNotional
            : 0.0;
        f["net_pnl_pct"] = grossPnl;

        // Exit-reason indicators
        var reason = (position.ExitReason ?? "").ToLowerInvariant();
        f["exit_reason_funding_norm"]  = reason.Contains("funding") || reason.Contains("normalized") ? 1.0 : 0.0;
        f["exit_reason_take_profit"]   = reason.Contains("takeprofit") || reason.Contains("tp") ? 1.0 : 0.0;
        f["exit_reason_stop_loss"]     = reason.Contains("stoploss") || reason.Contains("sl") ? 1.0 : 0.0;
        f["exit_reason_time_stop"]     = reason.Contains("timestop") || reason.Contains("maxhold") ? 1.0 : 0.0;
        f["exit_reason_bracket_fired"] = reason.Contains("bracket") ? 1.0 : 0.0;

        // Cost-quality measures (when measured)
        if (position.RealFeePaid.HasValue && Math.Abs(grossPnl) > 1e-9)
        {
            f["real_fee_to_gross_ratio"] = (double)position.RealFeePaid.Value / Math.Max(Math.Abs(entryNotional), 1.0);
        }
        if (position.RealSlippagePaid.HasValue && Math.Abs(grossPnl) > 1e-9)
        {
            f["real_slippage_to_gross_ratio"] = (double)position.RealSlippagePaid.Value / Math.Max(Math.Abs(entryNotional), 1.0);
        }

        // Regime indicator: strong funding signal when |rate| ≥ 2× threshold
        f["regime_funding_strong"] = Math.Abs(fundingRate) >= 2.0 * entryThresholdRate ? 1.0 : 0.0;

        return f;
    }
}
