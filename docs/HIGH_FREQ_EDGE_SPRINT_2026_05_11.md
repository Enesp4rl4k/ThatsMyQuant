# High-Frequency Edge Sprint — TMQ Side Mirror

**Drafted:** 2026-05-11
**Master plan:** [`Quantlab/SPRINT_HIGH_FREQ_EDGES_2026_05_11.md`](C:\Users\parlak\Downloads\Quantlab\SPRINT_HIGH_FREQ_EDGES_2026_05_11.md)
**Trigger:** 13-edge stack went live 2026-05-10. 0 fills in first 24h. 4h-cadence edges can't produce enough samples for SPRT/learning within sprint horizons.

This is the TMQ-side action list. The Quantlab side does the validation + emitter work; TMQ side is config + (in 1 case) a small code change.

## Today's interim deploy (2026-05-11, awaiting restart)

Already edited in [`01_Services/QuantEngine.Worker/appsettings.Paper.json`](../01_Services/QuantEngine.Worker/appsettings.Paper.json):

- `Funding:Long:HybridMode`: `off` → **`live`**, AllocationCsv `LTCUSDT:0.17` → **`LTCUSDT:0.08`** (trim weight to share basis with 13 EdgeLab edges)
- `Funding:Short:HybridMode`: `off` → **`dry-run`** (resume AAVE shadow validation, paused for now-defunct SOL-AVAX focus)

**Restart pending** — operator decision. Per CLAUDE.md hard rule, use `Launch-PaperTrade-Clean.ps1 -SkipBuild`. Expected +12 fires/month from LTC funding alone.

## Sprint deliverables — 3 specs in Quantlab/integration/

The deep specs live in Quantlab repo (closer to validation work). Mirror here for operator reference.

### Spec #1 — `funding_settlement_basis_snap` (ETA 3 days)

📄 [`Quantlab/integration/TMQ_INTEGRATION_funding_settlement_basis_snap.md`](C:\Users\parlak\Downloads\Quantlab\integration\TMQ_INTEGRATION_funding_settlement_basis_snap.md)

**TMQ side action when ship-ready:**
- Add 15 per-symbol entries to `EdgeLabSingleAsset:Strategies[]`
- Add 15 per-symbol `StrategyOverrides` (Weight 0.03-0.04, StaleAlertHours 3, soft-promote)
- Restart worker
- Expected: +90 fires/month

**No TMQ code change** — existing `EdgeLabSingleAssetHostedService` consumes any registered single-asset strategy.

### Spec #2 — `liquidation_fade` (ETA 11 days, moderate-to-high risk)

📄 [`Quantlab/integration/TMQ_INTEGRATION_liquidation_fade.md`](C:\Users\parlak\Downloads\Quantlab\integration\TMQ_INTEGRATION_liquidation_fade.md)

**TMQ side action when ship-ready:**
- ⚠️ **Code change needed:** per-strategy `OrderType` (currently global on `EdgeLabSingleAsset`) OR split-service `EdgeLabFastAsset` for 30s polling + Market order. Half-day work, defer until validator passes.
- `PollIntervalSeconds`: 60 → 30 for the new service
- Add N per-symbol `liquidation_fade_*` entries
- Expected: +80-150 fires/month

**Risk:** Pre-reg's own failure plan flags 30s-latency stress as a kill criterion. If validator says HFT-only, TMQ code change is wasted. **Defer code change until Quantlab Day 7 verdict.**

### Spec #3 — `composite_vol_carry_deploy` (ETA 2-4 days post-2026-05-22)

📄 [`Quantlab/integration/TMQ_INTEGRATION_composite_vol_carry.md`](C:\Users\parlak\Downloads\Quantlab\integration\TMQ_INTEGRATION_composite_vol_carry.md)

**Gated on maker-fill ratio verdict 2026-05-22.** Decision matrix is locked in pre-reg — operator does not re-decide.

**Most likely branch:** Variant B (1-leg BTC vol_carry).
- Add `vol_carry_btc_long` to `EdgeLabSingleAsset:Strategies[]` with Weight 0.08, verified
- Restart
- Expected: +60 fires/month

**Contingent branch:** Variant A (3-leg composite, requires maker_fill ≥80%).
- ⚠️ **Code change needed:** `PerSymbolCap` infrastructure to prevent BTC over-concentration (5 strategies all on BTC = 0.55 of basis without cap)
- Add 3 entries

## Watch items between now and 2026-05-22

1. **`MakerFillRatioMonitor` output** at `%LOCALAPPDATA%\ThatsMyQuant\maker_fill_ratio.json` — operator sample every 2-3 days to anticipate which Spec #3 branch will fire
2. **`maker_fill_test_dow` fill count** — pre-reg needs N≥100 maker-attempts for statistically meaningful 80% threshold
3. **Sprint 1.2 alarm state** — if MakerFillRatioMonitor fires `CRITICAL` (<0.5) before verdict, investigate the post-only fix rather than waiting

## What this sprint does NOT do

- No threshold-lowering on existing edges (breakout 20-bar → 5-bar = noise-fitting)
- No new exchange integrations
- No ad-hoc intraday strategies without Quantlab validation
- No sub-minute strategies (OKX demo fill latency caps practical cadence at 5min)

## Frequency math post-sprint

| Stage | Fires/month |
|---|---|
| Now (13 EdgeLab + 0 fills observed in 24h) | claim ~35, observed 0 |
| + Funding:Long re-enable (today's interim, awaits restart) | +12 |
| + #1 settlement-snap | +90 |
| + #3 vol_carry (most likely Variant B) | +60 |
| + #2 liquidation_fade (if validator passes) | +80-150 |
| **Total target** | **~250-300/month ≈ 8-10/day** |

Sample density target: from <1/day → 8-10/day, enabling SPRT convergence in ~30 days instead of 12-18 months.

## Operator decision queue

1. **Today:** restart worker to activate today's Funding re-enable? Per CLAUDE.md → `Launch-PaperTrade-Clean.ps1 -SkipBuild`
2. **Day 1 of sprint:** run Quantlab validator for #1 (`python scripts/validate_funding_settlement.py --days 1825`)
3. **Day 1 of sprint:** sign up Coinalyze for #2 backfill (5 min, free tier)
4. **2026-05-22:** read maker_fill verdict, ship #3 variant accordingly
5. **Post-sprint:** measure fires/day delta — if still <3/day, escalate (the sprint failed, real diagnosis needed)
