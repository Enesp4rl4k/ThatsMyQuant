# TMQ Edge Plan v4 — 2026-05-10

**Supersedes:** MASTER_PLAN_v3_2026_05_07.md
**Companion (read-only):** ../../Quantlab/PLAN_A_PLUS_2026_05_09.md, ../../Quantlab/reports/deploy_runbook_2026_05_10/TMQ_INTEGRATION_CONTRACT.md
**Bu plan:** TMQ-worker tarafı. Edge'leri Quantlab üretiyor, TMQ taşıyıp execute ediyor.
**Last updated:** 2026-05-10 ~08:00 UTC (post-flip-to-live)

---

## TL;DR — bugün ne yaşandı

Sabah 06:43: 1 verified edge (fee_panic), 2 soft-promote, hepsi dry-run yakını.
Akşam 08:00: **13 strateji live (OKX demo)**, Sprint 1-6 tamamlandı, 0 fill (Quantlab "flat" sinyali, beklenen).

| Saat | Olay |
|---|---|
| 06:43 | Sprint 0: durum tespiti, Track B Phase 1 zaten config'te ama maker_fill=0% |
| 07:14 | Sprint 1 ship: post-only price nudge + reject retry (`MakerEdgeBps=1.0`) |
| 07:14 | Sprint 2 ship: per-strategy override (Weight + PromotionStatus + MinPaperCyclesForLive) |
| 07:14 | Sprint 3 ship: kritik bug fix — StrategyTag/SignalPrice/ExecutionSource propagation |
| 07:14 | Sprint 4 ship: MarkoutLedger + MarkoutSnapshotHostedService (TCA) |
| 07:14 | Sprint 4 ship: PerStrategyDigestHostedService (daily 23:30 UTC) |
| 07:24 | Sprint 5 ship: BasisSanityCheck + Paper:InitialBalance cap ($96K → $2K) |
| 07:24 | Sprint 5 ship: B3 — Telegram on every live EdgeLab fill + fail |
| 07:41 | Sprint 6 ship: Quantlab audit contract — 9 yeni metadata field |
| 07:41 | Sprint 6 ship: EdgeLabKillSwitch + state JSON + hosted service (multiplier {1.0, 0.5, 0.0}) |
| 07:50 | Operator decision: `MinPaperCyclesForLive=10→0` for all 11 demoted strategies |
| 07:50 | **13/13 strategies LIVE on OKX demo** (verified pickup) |

Quantlab bu süreçte: 8 yeni 4h breakout edge dropladı (Round 47-50), composite backtest Sharpe +3.30 equal-weight / +3.55 risk-parity. Stack 3 → 13'e çıktı. İlk SHORT edge'leri (ETH/OP/ARB/NEAR + ada short).

---

## 1. Active edge stack (live on OKX demo)

| # | Strategy | Class | Status | Backtest Sharpe | Freq | Dir | Weight | KS Hard |
|---|---|---|---|---|---|---|---|---|
| 1 | fee_panic_btc_long | event 1d | verified | 0.65 | 4/yr | LONG | 8% | -1.0 |
| 2 | stablecoin_30d_btc_long | event 1d | soft-promote | 0.71 | 2-3/yr | LONG | 8% | -1.0 |
| 3 | rv_breakout_btc_long | event 1d | soft-promote | 0.68 | ~1/yr | LONG | 7% | -1.0 |
| 4 | btc_breakout_4h_long | breakout 4h | soft-promote | 0.75 | 62/yr | LONG | 8% | -1.0 |
| 5 | eth_breakout_4h_long | breakout 4h | soft-promote | 0.88 | 31/yr | LONG | 10% | -1.0 |
| 6 | eth_breakout_4h_short | breakout 4h | soft-promote | 0.67 | ~30/yr | **SHORT** | 7% | -1.0 |
| 7 | doge_breakout_4h_long | breakout 4h | soft-promote | 0.78 | ~25/yr | LONG | 8% | -1.0 |
| 8 | bnb_breakout_4h_long | breakout 4h | soft-promote | 0.79 | ~30/yr | LONG | 8% | -1.0 |
| 9 | op_breakout_4h_short | breakout 4h | soft-promote | 1.05 | ~25/yr | **SHORT** | 11% | -1.0 |
| 10 | arb_breakout_4h_short | breakout 4h | soft-promote | 1.01 | ~10/yr | **SHORT** | 10% | -1.0 |
| 11 | near_breakout_4h_short | breakout 4h | soft-promote | 0.85 | ~15/yr | **SHORT** | 9% | -1.0 |
| 12 | ada_breakout_4h_long | breakout 4h | soft-promote | ~0.7 | ~25/yr | LONG | 8% | -1.0 |
| 13 | ada_breakout_4h_short | breakout 4h | soft-promote | ~0.7 | ~25/yr | **SHORT** | 10% | -1.0 |

- Weight toplamı: ~1.02 (basis cap = $2K → max single-strategy exposure ~$220, max sum bounded)
- Soft-kill threshold: realized 30c Sharpe < -0.3 → multiplier 0.5×
- Hard-kill threshold: realized 30c Sharpe < -1.0 → multiplier 0× (forced dry-run)
- Composite backtest: ~415 cyc/yr ≈ 8 saatte 1 fill (ortalama)

---

## 2. TMQ guarantees → Quantlab (sözleşme)

| # | Garanti | Sprint | Doğrulama |
|---|---|---|---|
| 1 | Per-strategy attribution (StrategyTag) | S3 | Trade.StrategyTag = signal.StrategyTag |
| 2 | Per-strategy basis (Weight × $2K) | S2 + S5 | basis cap aktif, weight × kill_mult uygulanıyor |
| 3 | Promotion gate (MinPaperCyclesForLive) | S2 | Per-strategy pure-function gate, transition-aware |
| 4 | Maker fill ratio (post-only edge nudge) | S1 | `bestBid - 1bp` BUY / `bestAsk + 1bp` SELL + retry |
| 5 | BacktestExpectedPnL → Trade | S6 | `mean_pct/100 × notional` from signal.metadata |
| 6 | Telegram visibility (fill, fail, gate, basis re-anchor) | S3+5 | Her live fill + fail + transition |
| 7 | Kill switch (realized 30c Sharpe → multiplier) | S6 | Saatlik state JSON, EdgeLab cycle'da okunuyor |

---

## 3. Observability stack (yeni)

| Servis | Cadence | Output | Sprint |
|---|---|---|---|
| `MarkoutSnapshotHostedService` | 1s tick + 5s scan | `markouts.jsonl` (+1s/+10s/+60s/+300s adverse bp) | S4 |
| `PerStrategyDigestHostedService` | 23:30 UTC daily | JSON + log table + Telegram | S3 |
| `EdgeLabKillSwitchHostedService` | 1h | `edge_kill_switch.json` (per-strategy multiplier) | S6 |
| `BasisSanityCheckHostedService` | one-shot at boot | log + warning | S5 |
| `MakerFillRatioMonitor` (existing) | 1h | OKX harvest, ratio + alert | pre-existing |
| `TcaLiveAnalyzer` (existing) | 30m | rolling cost leakage | pre-existing |
| `EdgeLabRealizedPnLDumper` (existing) | 23:30 UTC | CSV → Quantlab/data/realized_pnl/ | pre-existing |

---

## 4. North Star (gerçekçi, 90 gün)

| Metrik | Backtest claim | 30-day expected | 90-day "başardık" eşiği |
|---|---|---|---|
| Composite Sharpe (net) | +3.30 (eq-wt) | +1.0 - 1.5 | ≥ 0.8 |
| Edges yaşayan (KS≥0.5×) | 13/13 | 6-8/13 | ≥ 3 verified live |
| Maker fill ratio | hedef ≥%50 | ≥%30 | ≥%50 sustained |
| Drawdown | -%14 | -%5-10 | < -%15 |
| First fill ETA | 4h breakouts ≤4h | 24h | 24h |
| First gate transition | n/a | n/a | observed |
| First kill switch transition (if any) | n/a | maybe 1-2 demote | hard-kill ≤1 strategy |

**Gerçek "başardık" anı**: 90 günde **en az 3 strateji N≥30 trade ile realized Sharpe ≥0.5 net**.

---

## 5. Bekleme protokolü (operator self-discipline)

Live phase başladığında bunlara DOKUNMA:
- Per-strategy `Weight` değerleri
- `MinPaperCyclesForLive`, `PromotionStatus`
- `kill_switch_realized_sharpe_*` thresholds
- `MakerEdgeBps`, `PostOnlyRejectRetries`
- `BasisCap` (Paper:InitialBalance)
- Strategy add/remove (Quantlab tarafı yapacak)

Müdahale için kapı:
- 30+ trade biriktikten sonra realized Sharpe analizi
- Bonferroni / multiple-testing düzeltilmiş p-value < 0.01 ile threshold change
- "1 strateji catastrophic decay" durumunda elle hard-kill (kill switch zaten yapacak ama)

Otomatik aksiyonlar (operatör elle yapmaz):
- Kill switch demote (1.0 → 0.5 → 0)
- Gate transition (DryRun → Live)
- Basis re-anchor (stale snapshot detect)

---

## 6. Quantlab haftalık tempo (sustainable)

Quantlab tarafı haftada 1 hipotez deneme tempo'da. Her hipotez:
1. Pre-reg `.md` dosyası (mekanizma + data citation)
2. Backtest + permutation + bootstrap + stres test
3. `BACKTEST_STATS` registry'ye stat ekle
4. `appsettings.Paper.json:Strategies` array + emit cron güncelle

TMQ tarafı bu workflow'da pasif: yeni strateji geldiğinde
- Promotion gate'ten geçer (`MinPaperCyclesForLive=10` ya da `=0` operatör seçimi)
- Kill switch ile sürekli izlenir
- PerStrategyDigest'e otomatik düşer

---

## 7. Açık deferred bug'lar (Sprint 7+)

| # | Bug | Notu | Sprint |
|---|---|---|---|
| B4 | OKX exec sync 1× disconnect handling | Reconnect logic edge-case | S7 |
| B5 | Bybit rate-limit %50 hardening | Bybit-trigger'a kadar deferred | n/a |
| - | Per-strategy circuit breaker (beyond kill switch) | Daily loss cap per strategy | S7 |
| - | Worker auto-restart watchdog | Crash recovery | S7 |
| - | Realized vs backtest divergence digest | Sprint 4 markout already gives raw data; digest layer eksik | S7 |
| - | Morning briefing (09:00 UTC Telegram) | Sabah özet | S7 |

---

## 8. Sprint geçmiş özet

| Sprint | Date | Theme | Critical bug fix? |
|---|---|---|---|
| S1 | 2026-05-10 AM | Track B post-only edge nudge + retry | (Track B Phase 1'in eksiğini tamamladı) |
| S2 | 2026-05-10 AM | Per-strategy override + promotion gate | Yeni edge stack için **zorunluydu** |
| S3 | 2026-05-10 AM | Attribution bug fix + signal context capture | **KRİTİK**: 13 stratejinin tek bucket'a düşmesini engelledi |
| S4 | 2026-05-10 AM | Markout TCA + per-strategy digest | TCA blind spot kapatıldı |
| S5 | 2026-05-10 PM | Basis cap + Telegram visibility | $96K → $2K cap, 48× yanlış sizing engelledi |
| S6 | 2026-05-10 PM | Quantlab audit contract + kill switch | Toxic edge auto-kill devrede |

**Toplam shipped:** 13 yeni dosya, ~1500 satır kod, 48 unit test, 0 regression fail.

---

## 9. Sıradaki ne (eylem matrisi)

**Şimdi (live phase başlangıcı, 0 fill):**
- Beklemek doğru aksiyon
- Memory + plan doc consolidation = yapıldı

**24h içinde:**
- İlk fill izlemek (Telegram + log)
- Markout ledger doluyor mu kontrol
- KillSwitch state file ilk row'unu yazıyor mu
- 23:30 UTC PerStrategyDigest tablosu ilk halini

**1 hafta:**
- Maker fill ratio anlamlı sample (≥10 fill)
- Drawdown ladder davranışı (eğer tetiklenirse)
- Concurrent fire çakışması (13 strateji aynı bar'da)

**30 gün:**
- N≥30 trade per strategy (en azından bazılarında)
- Realized Sharpe vs expected divergence
- Hangi 3 edge yaşadı, hangileri kill switch'e gitti

**90 gün:**
- North Star verdict
- Sermaye genişletme kararı ($2K → $5K → $10K kademeli)
- Master Plan v5 (gerekirse)

---

**Sonraki revizyon:** İlk fill geldiğinde, ya da 7 gün sonra (hangisi önce).
