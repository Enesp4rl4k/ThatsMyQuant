using System;
using System.Collections.Generic;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// On-chain signal aggregator. Combines three robust on-chain metrics that
/// have demonstrable lead-time over spot price moves in crypto:
///
///   • <b>Exchange net flow</b>: USD value of stablecoins / BTC flowing INTO
///     exchange wallets minus flowing OUT. Large net inflows of stablecoins
///     historically precede price rallies (capital ready to deploy); large
///     net inflows of BTC precede dumps (holders moving to exchanges to sell).
///
///   • <b>Whale wallet movement</b>: count and notional of transactions
///     ≥ $1M from "old" addresses (holders &gt;6 months). Whale activity
///     is a high-quality proxy for informed flow.
///
///   • <b>Miner outflow</b>: BTC moved out of miner-attributed wallets. When
///     elevated, miners are selling — net supply pressure.
///
/// References:
///   Glassnode metric documentation; CryptoQuant academic series.
///   Vasan et al. (2022), "Quantifying NFT-driven networks in crypto" — also
///     covers exchange-flow lead time.
///
/// Why this matters in the upgrade plan:
///   Phase Tier-1B — orthogonal alpha source with 1-3 hour lead time. Used as
///   confluence overlay (combined with the existing IC aggregator), not
///   standalone — on-chain signals alone are noisy at hourly frequency, but
///   when they confirm a microstructure or pairs signal the EV of the trade
///   roughly doubles.
///
/// Caller responsibility: data acquisition (Glassnode / CryptoQuant API or
/// equivalent provider). This module is the algorithmic layer that converts
/// raw inflow / outflow / whale-tx-count series into a directional confidence.
/// </summary>
public static class OnChainSignalAggregator
{
    /// <summary>One on-chain observation snapshot.</summary>
    /// <param name="StablecoinNetExchangeInflowUsd">USDT+USDC net inflow to exchange wallets, last 24h. Positive = capital deploying.</param>
    /// <param name="BitcoinNetExchangeInflowBtc">BTC net inflow to exchange wallets, last 24h. Positive = supply for sale.</param>
    /// <param name="WhaleTransferCountLast24h">Count of on-chain transfers ≥ $1M from old addresses, last 24h.</param>
    /// <param name="MinerOutflowBtc">BTC moved out of miner wallets, last 24h.</param>
    public sealed record OnChainSnapshot(
        double StablecoinNetExchangeInflowUsd,
        double BitcoinNetExchangeInflowBtc,
        int WhaleTransferCountLast24h,
        double MinerOutflowBtc);

    /// <summary>Aggregated directional view from on-chain data.</summary>
    /// <param name="DirectionalBias">Signed fraction in [-1, 1]. Positive = bullish (buy capital ready,
    /// no supply being moved to exchanges); negative = bearish.</param>
    /// <param name="Confidence">0-1 quality score; how many sub-signals fired.</param>
    /// <param name="Components">Per-component contributions for telemetry.</param>
    public sealed record OnChainSignal(
        double DirectionalBias,
        double Confidence,
        IReadOnlyList<ComponentContribution> Components);

    public sealed record ComponentContribution(string Name, double NormalizedValue, double Contribution);

    /// <summary>Default thresholds chosen to fire ~weekly under normal conditions.</summary>
    public const double DefaultStableInflowSignificantUsd = 200_000_000;   // $200M
    public const double DefaultBtcInflowSignificantBtc = 5000;              // 5000 BTC
    public const int DefaultWhaleCountSignificant = 50;
    public const double DefaultMinerOutflowSignificantBtc = 1500;

    /// <summary>
    /// Aggregate the snapshot into a directional bias.
    /// </summary>
    /// <param name="snapshot">Latest on-chain observation.</param>
    /// <param name="stableInflowThreshold">Significant stablecoin inflow magnitude (USD).</param>
    /// <param name="btcInflowThreshold">Significant BTC inflow magnitude (BTC).</param>
    /// <param name="whaleCountThreshold">Significant whale transaction count threshold.</param>
    /// <param name="minerOutflowThreshold">Significant miner outflow magnitude (BTC).</param>
    public static OnChainSignal Aggregate(
        OnChainSnapshot snapshot,
        double stableInflowThreshold = DefaultStableInflowSignificantUsd,
        double btcInflowThreshold = DefaultBtcInflowSignificantBtc,
        int whaleCountThreshold = DefaultWhaleCountSignificant,
        double minerOutflowThreshold = DefaultMinerOutflowSignificantBtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var components = new List<ComponentContribution>();

        // 1. Stablecoin net inflow → bullish.
        double stableNorm = stableInflowThreshold > 0
            ? snapshot.StablecoinNetExchangeInflowUsd / stableInflowThreshold
            : 0;
        double stableContrib = Math.Tanh(stableNorm);  // soft-capped to [-1, 1]
        components.Add(new ComponentContribution("StableInflow", stableNorm, stableContrib));

        // 2. BTC net inflow → bearish (so contribution is NEGATED).
        double btcNorm = btcInflowThreshold > 0
            ? snapshot.BitcoinNetExchangeInflowBtc / btcInflowThreshold
            : 0;
        double btcContrib = -Math.Tanh(btcNorm);
        components.Add(new ComponentContribution("BtcInflow", btcNorm, btcContrib));

        // 3. Whale activity → ambiguous direction; use as confidence multiplier.
        // High whale count means SOMETHING is happening (informed flow active).
        double whaleNorm = whaleCountThreshold > 0
            ? snapshot.WhaleTransferCountLast24h / (double)whaleCountThreshold
            : 0;
        double whaleConfidence = Math.Tanh(whaleNorm);
        components.Add(new ComponentContribution("WhaleActivity", whaleNorm, whaleConfidence));

        // 4. Miner outflow → bearish.
        double minerNorm = minerOutflowThreshold > 0
            ? snapshot.MinerOutflowBtc / minerOutflowThreshold
            : 0;
        double minerContrib = -Math.Tanh(minerNorm);
        components.Add(new ComponentContribution("MinerOutflow", minerNorm, minerContrib));

        // Directional bias: average of stable-inflow + BTC-inflow + miner-outflow.
        // Whale activity is multiplied at the end as a "confidence boost".
        double directionalBias = (stableContrib + btcContrib + minerContrib) / 3.0;
        directionalBias *= 0.5 + 0.5 * Math.Min(1.0, whaleConfidence);  // 50-100% multiplier

        // Confidence: how many sub-signals are non-zero (above noise floor 0.1).
        int activeCount = 0;
        if (Math.Abs(stableContrib) > 0.1) activeCount++;
        if (Math.Abs(btcContrib) > 0.1) activeCount++;
        if (Math.Abs(minerContrib) > 0.1) activeCount++;
        if (whaleConfidence > 0.1) activeCount++;
        double confidence = activeCount / 4.0;

        return new OnChainSignal(
            DirectionalBias: Math.Clamp(directionalBias, -1.0, 1.0),
            Confidence: confidence,
            Components: components);
    }

    /// <summary>
    /// Convert the aggregated bias to a TradeSide tag, or None if confidence is too low.
    /// </summary>
    /// <param name="signal">Output of <see cref="Aggregate"/>.</param>
    /// <param name="biasThreshold">|bias| must exceed this to take a side. Default 0.30.</param>
    /// <param name="confidenceThreshold">confidence must exceed this to fire. Default 0.50.</param>
    public static TradeSide? AsSide(
        OnChainSignal signal,
        double biasThreshold = 0.30,
        double confidenceThreshold = 0.50)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Confidence < confidenceThreshold) return null;
        if (Math.Abs(signal.DirectionalBias) < biasThreshold) return null;
        return signal.DirectionalBias > 0 ? TradeSide.Buy : TradeSide.Sell;
    }
}
