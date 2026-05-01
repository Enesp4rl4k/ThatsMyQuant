# Pre-registered Hypothesis Test — Pairs Long Tier

**Filed:** 2026-05-01
**Author:** Operator + Claude (architect)
**Status:** ACTIVE — running
**Independent of:** `PRE_REGISTERED_HYPOTHESIS_funding_long.md` (separate Bonferroni family)

This is the SECOND pre-registered test running in parallel with the funding-alpha
hypothesis. Each test maintains its OWN multiple-comparison family. Attribution
is via `PaperPosition.StrategyTag` — funding trades carry `FundingAlpha-Hybrid-*`,
pairs trades carry `PairsTrading-Hybrid-*`.

---

## Hypothesis

**H₀ (null):** The Pairs Long tier (3 cointegrated pairs at z-entry 2.5σ, exit 0.3σ,
stop 3.5σ on 4h candles, configured 2026-05-01) has zero true edge over OKX VIP-0
fees + slippage. Per-trade net PnL is centered at zero with all observed deviation
explainable by sampling variance.

**H₁ (alternative):** The strategy has positive edge net of measured costs (10bp
roundtrip fee + measured slippage + spread). Mean per-trade net PnL > 0 with
statistical significance after multiple-comparison correction across the 3 pairs.

---

## Test design

| Parameter | Value | Rationale |
|---|---|---|
| Sample size N (stopping rule) | **50 closed trades** (across 3 pairs combined) | Pairs trade ~1×/3-5 days per pair on 4h with strict z=2.5 entry. 50 trades ETA ~10-14 weeks in calm regime. Lower than funding's 200 because pairs trade SLOWER and we accept lower power per pair. |
| Family size m (Bonferroni) | **3** | Universe: ADA-XRP, BCH-LTC, TRX-XLM. Long tier was pruned 2026-05-01 to these three high-Sharpe historical pairs. SOL-AVAX, SOL-LINK, LINK-UNI excluded from Long tier per this filing — they remain in Short tier (paused). |
| Family-wise alpha α | **0.05** | Conventional. |
| Per-test alpha (corrected) | α / m = **0.01667** | Two-sided. |
| Required `\|t-stat\|` | **≥ 2.39** | Inverse-normal at 1 - α/(2m) for m=3. |
| Cost basis | **Real-fee + real-slippage + spread** | Per-trade RealFeePaid + RealSlippagePaid (when populated by hybrid executor). Spread cost via HistoricalSpreadReader nearest-neighbor lookup. |
| Annualization | **√(actual trades/year)** | Sample-window trade frequency, NOT a hardcoded constant. |
| Significance gate | **DSR ≥ 0.95** AND **bootstrap Sharpe 95% CI lower > 0** | Same gates as funding test. |

---

## Stopping rule

Sampling continues until ANY of:

1. **Primary stop:** Per-tier closed-trade count reaches **50** (across all 3 pairs).
2. **Early-fail stop:** At N=25, if **deflated Sharpe probability** for any single pair
   falls below 0.20 AND its bootstrap CI upper bound is below zero, that pair is
   demoted to dry-run. Test continues on the remaining pairs IF total ≥ 2 pairs left.
3. **Cointegration-break stop:** If a pair's 60-bar rolling cointegration test fails
   (γ ≥ -0.15 OR adfT ≥ -2.86 OR `|corr|` < 0.50) for **3 consecutive rebalances**, pair is
   auto-demoted (this is independent of statistical edge — structural relationship broke).
4. **Operational fail:** Drawdown breaches **-10% basis** at any point. ALL pairs demoted
   to dry-run. Cross-strategy fail (independent of pairs-only PnL).

---

## Decision matrix (committed up-front)

| Outcome at stop | Verdict | Action |
|---|---|---|
| ≥1 pair passes Bonferroni `\|t\|≥2.39` AND DSR≥0.95 AND CI-low>0 | **PROVEN edge** | Promote that pair to canary live. Other pairs continue paper. |
| Pooled portfolio passes (DSR≥0.95) but no single pair does | **PORTFOLIO edge** | Keep portfolio composition fixed. Promote canary live ALL 3 pairs at 1/4 size. |
| All pairs DSR<0.50, pooled t-stat in `[-1.96, +1.96]` | **NO edge proven** | Pairs Long retired as primary. Demote to dry-run. Pivot to Edge Lab discoveries. |
| Pooled DSR ∈ [0.50, 0.95], individual mixed | **CANDIDATE — insufficient data** | Continue paper trading another 25 trades. |
| Operational fail (drawdown > -10%) | **OPS FAIL** | 14-day cooling-off, post-mortem before re-enable. |

**Anti-rationalization clauses:**

- "We'll add SOL-AVAX back retroactively" → forbidden during test. Family m=3 fixed.
- "z-entry too strict, lower to 2.0" → forbidden. Threshold committed.
- "Cointegration broke but it'll come back" → cointegration-break stop is automatic
  and not subject to operator override mid-test.

---

## Cross-strategy isolation

This test is INDEPENDENT of the funding-alpha test:

- Bonferroni family is local (m=3 here, m=7 there). Tests do NOT share α budget.
- Attribution: `PaperPosition.StrategyTag` distinguishes pairs from funding.
- `analyze-edge --by-strategy` (added 2026-05-01) reports per-strategy stats
  separately. NEVER pool funding + pairs trades into a single significance test.
- Capital pool: `NotionalExposureGuard` reservation key `Pairs:Long:*` is separate
  from `Funding:Long:*`. Drawdown breach on either tier triggers DrawdownLadderGate
  on BOTH (cross-tier safety) but PnL attribution stays per-strategy.

---

## What evidence WOULD change this hypothesis

Pre-committed adjustments:

1. **Cointegration relationship breaks structurally** (per cointegration-break stop).
2. **OKX delists a pair leg** → pair removed, m updates, required `|t|` recomputes.
3. **A new pair from PairsDiscoveryHostedService passes its OWN pre-registered test**
   (not added to this family) → eligible for separate test.

---

## Operational discipline

While running:

- **No threshold tweaks** (z-entry, exit, stop committed).
- **No pair additions** (m=3 fixed; new pairs need own pre-registration).
- **No retroactive trade exclusions.**
- **`analyze-edge --by-strategy` per 10 closed pairs trades** for monitoring only.
  No mid-test decisions.

---

## Sample state at filing

```
Pairs Long tier (HybridMode at filing): live (flipped from dry-run on this commit)
Pairs configured: ADA-XRP (0.15), BCH-LTC (0.15), TRX-XLM (0.05)
Closed paper pairs trades pre-2026-05-01: 0 (legacy pre-instrumentation)
Worker: PID will rotate after this commit's restart
```

Test starts effectively from 2026-05-01 with sample N=0.

---

## Filed by

Git commit hash: TBD on first commit. Once committed, reference that hash in all
future discussions of this test.

DO NOT EDIT THIS FILE except to record the FINAL outcome (after stop fires).
