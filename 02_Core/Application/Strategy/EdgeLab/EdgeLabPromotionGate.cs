namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// Three-state execution mode used by the promotion gate. Mirrors the private
/// enum each hosted service maintains today (PairsHybridExecutor, FundingHybridExecutor,
/// EdgeLabSignalHostedService) — kept duplicated here rather than promoted to
/// Contracts because the gate is currently EdgeLab-specific. If a future sprint
/// unifies hybrid-mode handling across all executors, this lives in Contracts
/// and the duplicates collapse.
/// </summary>
public enum EdgeLabHybridMode { Off, DryRun, Live }

/// <summary>
/// Pure-function gate that decides the EFFECTIVE <see cref="EdgeLabHybridMode"/> for a
/// single EdgeLab strategy by combining:
///   1. The operator-declared mode (config: EdgeLabHybridMode = off | dry-run | live).
///   2. The Quantlab-side promotion verdict (verified | soft-promote | observation).
///   3. The number of completed paper (dry-run) reconciliation cycles.
///   4. The minimum paper cycles required to honour a soft-promote → live flip.
///
/// Decision matrix (most-restrictive wins):
///
///                       observation     soft-promote (cycles &lt; min)   soft-promote (cycles &gt;= min)   verified
///   declared=off        off             off                            off                             off
///   declared=dry-run    dry-run         dry-run                        dry-run                         dry-run
///   declared=live       dry-run [obs]   dry-run [gate-pending]         live                            live
///
/// Why this exists (Sprint 2, 2026-05-10): Quantlab's overnight pipeline
/// frequently adds a new "soft-promote" edge — partial paranoid pass, e.g.
/// n &lt; 30 or perm_p &gt; 0.05 (RV-Breakout 2026-05-10 case). Without a gate,
/// the operator must remember to NOT flip <c>EdgeLabHybridMode=live</c> until enough
/// paper data accumulates. With this gate, the config can declare the
/// operator's INTENT (live) while the gate enforces the runtime CONSTRAINT
/// (paper cycles). The gate emits a structured decision the hosted service
/// logs once at startup and on each transition, giving an audit trail of
/// "why is strategy X not trading even though config says live?".
///
/// Pure function: zero allocations, no IO. Tested directly in unit tests.
/// </summary>
public static class EdgeLabPromotionGate
{
    public sealed record Decision(
        EdgeLabHybridMode EffectiveMode,
        string Reason);

    public static Decision Resolve(
        string declaredMode,
        string promotionStatus,
        int paperCyclesCompleted,
        int minPaperCyclesForLive)
    {
        var declared = NormalizeMode(declaredMode);

        // 1. Off short-circuits — operator explicitly disabled.
        if (declared == EdgeLabHybridMode.Off)
            return new Decision(EdgeLabHybridMode.Off, "declared=off");

        // 2. Dry-run can only stay dry-run; gate only restricts live, never expands.
        if (declared == EdgeLabHybridMode.DryRun)
            return new Decision(EdgeLabHybridMode.DryRun, "declared=dry-run");

        // 3. Live declared — apply restrictions.
        var status = (promotionStatus ?? "verified").Trim().ToLowerInvariant();

        if (status == "observation")
            return new Decision(EdgeLabHybridMode.DryRun,
                "declared=live but promotion_status=observation → dry-run forced");

        if (status == "soft-promote")
        {
            if (minPaperCyclesForLive <= 0)
                return new Decision(EdgeLabHybridMode.Live,
                    "declared=live, soft-promote, gate=disabled (min_cycles=0)");

            if (paperCyclesCompleted < minPaperCyclesForLive)
                return new Decision(EdgeLabHybridMode.DryRun,
                    $"declared=live, soft-promote, paper_cycles={paperCyclesCompleted} " +
                    $"< min={minPaperCyclesForLive} → dry-run pending");

            return new Decision(EdgeLabHybridMode.Live,
                $"declared=live, soft-promote, paper_cycles={paperCyclesCompleted} " +
                $">= min={minPaperCyclesForLive} → live promoted");
        }

        // verified (or unknown — treat permissively as verified for back-compat)
        return new Decision(EdgeLabHybridMode.Live, $"declared=live, status={status}");
    }

    /// <summary>
    /// Mirror of <see cref="EdgeLabSingleAssetHostedService"/>'s private
    /// NormalizeMode — copied here as a static so the gate is self-contained
    /// and tests don't have to spin up the hosted service.
    /// </summary>
    public static EdgeLabHybridMode NormalizeMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return EdgeLabHybridMode.DryRun;
        return raw.Trim().ToLowerInvariant() switch
        {
            "off" => EdgeLabHybridMode.Off,
            "live" => EdgeLabHybridMode.Live,
            _ => EdgeLabHybridMode.DryRun,
        };
    }
}
