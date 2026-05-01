# Pre-registered Hypothesis Test — Funding Long Tier

**Filed:** 2026-05-01
**Author:** Operator + Claude (architect)
**Status:** ACTIVE — running

This document is committed to git BEFORE the trade sample reaches the size needed to evaluate.
Its purpose: prevent post-hoc rationalization. After the test concludes, the operator's only
decision is to compare the observed result against the rule already written here.

---

## Hypothesis

**H₀ (null):** The Funding Long strategy (LTC at 1bp threshold + DOGE at 0.5bp threshold,
configured 2026-05-01) has zero true edge over OKX VIP-0 fees + slippage on demo perpetual
swaps. Per-trade net PnL is centered at zero with all observed deviation explainable by
sampling variance.

**H₁ (alternative):** The strategy has positive edge net of measured costs (10bp roundtrip
fee + measured slippage). Mean per-trade net PnL > 0 with statistical significance after
multiple-comparison correction.

---

## Test design

| Parameter | Value | Rationale |
|---|---|---|
| Sample size N (stopping rule) | **200 closed trades** | Sufficient power for moderate effect sizes (d≈0.2) under Bonferroni at family size 7. |
| Family size m (Bonferroni) | **7** | Universe scanned: AAVE, ADA, AVAX, DOT, LINK, LTC, XRP — every symbol that received funding-alpha entries during paper. |
| Family-wise alpha α | **0.05** | Conventional. |
| Per-test alpha (corrected) | α / m = **0.00714** | Two-sided. |
| Required `\|t-stat\|` | **≥ 2.69** | Inverse-normal at 1 - α/(2m). |
| Cost basis | **Real-fee + real-slippage** | Per-trade `RealFeePaid` + `RealSlippagePaid` from PaperPosition (post-2026-04-30 instrumentation). |
| Annualization | **√(actual trades/year)** | Not the legacy √1095 (which over-stated reported Sharpe ~3-5×). |
| Significance gate | **DSR ≥ 0.95** AND **bootstrap Sharpe 95% CI lower bound > 0** | Bailey & López de Prado 2014 + standard percentile bootstrap. |

---

## Stopping rule

Sampling continues until ANY of:

1. **Primary stop:** Per-tier closed-trade count reaches 200 (across LTC + DOGE combined).
2. **Early-fail stop:** At N=100 the **deflated Sharpe probability** of any individual symbol
   falls below 0.20 AND its bootstrap CI upper bound is below zero. Strategy retired without
   waiting for full N.
3. **Operational fail:** A single drawdown breaches -10% of basis at any point. Strategy
   demoted to dry-run regardless of statistical state.

Once a stop fires, the operator runs `dotnet run -- analyze-edge` once and applies the
**decision matrix below verbatim**. No additional trades. No "let's see one more bar".

---

## Decision matrix (committed up-front)

| Outcome at stop | Verdict | Action |
|---|---|---|
| ≥1 symbol passes Bonferroni `\|t\|≥2.69` AND DSR≥0.95 AND CI-low>0 | **PROVEN edge** | Promote to canary live with $1K notional. No size scale-up for first 30 trades on real money. |
| Pooled portfolio passes (DSR≥0.95) but no individual symbol does | **PORTFOLIO edge** | Keep portfolio composition fixed. Promote canary live but require continued statistical monitoring. |
| All symbols DSR<0.50, pooled t-stat in `[-1.96, +1.96]` | **NO edge proven** | Retire as primary strategy. Demote to dry-run only. Pivot focus to Edge Lab discoveries. |
| Pooled DSR ∈ [0.50, 0.95], individual symbols mixed | **CANDIDATE — insufficient data** | Continue paper trading another 100 trades. NOT a proof, NOT a refutation. |
| Operational fail (drawdown > -10%) | **OPS FAIL** | Mandatory 14-day cooling-off. Post-mortem written before re-enable. |

**Anti-rationalization clauses:**

- "AAVE/AVAX excluded retroactively" is **not** an allowable adjustment under this test —
  family size m=7 was committed up-front. Selecting the survivors after the fact is the
  classic data-snooping mistake this pre-registration prevents.
- "DOGE was launched mid-test, so its sample is shorter" is acknowledged: DOGE evaluated
  separately on its own N (target 60+ trades) before counted as Bonferroni-clear.
- "The regime was unfavorable; let's wait for vol" is **only** allowed BEFORE first stop
  fires — once stop fires, regime arguments are post-hoc.

---

## What evidence WOULD change this hypothesis

**Pre-committed adjustments** (no rationalization risk because written here):

1. **Real fee changes by venue/tier upgrade:** if user hits VIP-1 (0.05% maker → 0.04%),
   re-run test with new cost basis. Family size unchanged.
2. **OKX adds/removes a symbol from funding eligibility:** universe shrinks, family size
   m updates accordingly, required `\|t\|` recomputes. Document the change.
3. **A new edge candidate from Edge Lab passes its OWN pre-registered test:** added as a
   PARALLEL hypothesis, NOT merged into this one (preserves family-wise alpha).

---

## Operational discipline

While this test is running:

- **No threshold tweaks.** Threshold per symbol committed at test start (LTC 1bp, DOGE 0.5bp).
  Changing during the test invalidates the sample.
- **No symbol additions** (other than what was already in the universe at test start).
  Adding a symbol mid-test = data snooping.
- **No retroactive trade exclusions.** If a trade closes with fee/slippage anomaly, log it
  but include in sample.
- **`analyze-edge` per 20 trades** for monitoring only — DO NOT make decisions on intermediate
  results. Only the final stop triggers the decision matrix.

---

## Sample state at filing

```
Closed trades (paper):     54 (incl. ~12 phantom-artifact pre-fix)
Clean instrumented sample: 0 (instrumentation shipped 2026-05-01)
LTC trades since fix:      pending
DOGE trades since 0.5bp:   pending
Worker:                    PID 10988, OKX demo, $5,000 basis
```

Test starts effectively from 2026-05-01 with sample N=0 of clean instrumented trades.
Pre-fix trades (54 currently) are EXCLUDED — they predate RealFeePaid + RealSlippagePaid.

---

## Filed by

Git commit hash: TBD on first commit. Once committed, reference that hash in all future
discussions of this test.

DO NOT EDIT THIS FILE except to record the FINAL outcome (after stop fires) in a new section
appended below. Mid-test edits = invalidates the pre-registration.
