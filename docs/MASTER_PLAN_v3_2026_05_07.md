# MASTER PLAN v3 — ThatsMyQuant + Quantlab

**Tarih:** 2026-05-07
**Önceki:** UNIFIED_MASTER_PLAN_v2 (2026-05-01) — supersede ediliyor
**Kalibrasyon:** Senior engineer + PhD quant lens, premortem-validated

---

## 0. Reality Check — bugünkü durumun dürüst fotoğrafı

| Boyut | Değer | Yorum |
|---|---|---|
| Live strateji sayısı | 1 (LTC Funding-Long) | N=7 trade, Sharpe 1.6 (CI çok geniş) |
| Shadow strateji | 1 (AAVE Funding) | N=8/30, daha uzun yol |
| Quantlab edge factory | Ölü | Son 30 günde 1 commit |
| ThatsMyQuant maturity | 5/10 | Reconciliation stub, allocator unwired, VaR read-only |
| Quantlab maturity | 4/10 | Statistical ML iyi, throughput sıfır |
| Lifetime PnL | -$0.01 | 54 trade, esasen flat |
| Drought | 6 gün | Eşik aşılmıyor (regime), bug değil |
| Operatör | 1 kişi | Burnout riski yüksek |

---

## 1. North Star — Sharpe 2.5 COMMITMENT (2026-05-07 revised)

**Operatör seçimi: Sharpe 2.5 stretch target.** Plan SIKILAŞIR, gevşemez.

### Faz hedefleri

| Faz | Süre | Sharpe net | Edge sayısı | Capital | Annual return |
|---|---|---|---|---|---|
| Yıl 1 | 0-12 ay | 1.5-2.0 | 4-5 | $5K → $8K paper, $1-5K canary | %15-25 |
| Yıl 2 | 12-24 ay | 2.0-2.5 | 6-8 uncorrelated | $10-25K real | %20-30 |
| Yıl 3+ | 24-36 ay | 2.5+ sustained | 8-10 cadence-diverse | $25-50K | %25-35 |

### Mathematical recipe (no fudging)

```
Portfolio Sharpe = √N_eff × avg_strategy_Sharpe
N_eff = N / (1 + (N-1) × avg_correlation)

Sharpe 2.5 için pratik recipe:
  7-8 strategy × avg Sharpe 1.0-1.3 × pairwise corr < 0.3
  → N_eff ≈ 5-6 → portfolio Sharpe 2.2-2.6 ✓
```

### Sıkılaşan gate'ler (was Sharpe 1.0-1.5 plan)

| Gate | Eski (1.5) | YENİ (2.5) |
|---|---|---|
| Pairwise correlation | < 0.4 | **< 0.3** |
| Bayesian promotion | P(SR≥0.5) ≥ 0.7 | **P(SR≥0.7) ≥ 0.7** |
| Cost survival | net Sharpe > 0.5 | **net Sharpe > 0.7** |
| HRP Phase 2 wire-in | Deferred | **Required before strategy #3** |
| Edge factory throughput | Vague | **4 tested+validated/ay** |

### Required investments

- **Alt-data subscriptions** ($80-100/ay):
  - Glassnode paid tier — on-chain (BTC active addresses, exchange netflow)
  - Kaiko — orderflow + cross-venue funding skew
  - Deribit DVOL feed (free for spot, paid for term structure)
- **Cadence diversity** (kod, ücretsiz):
  - 5m: liquidation_fade
  - 1h: funding_zscore_meanrev
  - 4h: vol_carry, oi_divergence
  - daily: basis_term_structure
- **Yıl 2 infrastructure** (~$50/ay):
  - VPS Singapore/Tokyo (OKX co-location proxy, ~30-50ms savings)
  - WebSocket execution path (Python REST baseline kalır research için)

### NOT istediklerimiz

- ❌ Sharpe 4-7 (HFT institutional, retail erişilemez)
- ❌ Eşik düşürme via "promote eşiği yumuşat" (overfit, Sharpe 2.5 IMKANSIZ olur)
- ❌ Eski 28bp cost varsayımı (ölçüldü 10bp, dokunma)
- ❌ HRP'siz multi-strategy promotion (factor concentration → Sharpe 0.4 = premortem #5)

---

## 2. Why We're Stuck — kök neden

Premortem 6 farklı senaryodan **3 kök sebebe** indirgenebilir:

1. **Framework cadence mismatch** — institutional rigor (Bonferroni m=3, N=50, DSR p>0.7) ile retail trade-rate (4h cadence × 1-3 sembol) bir araya gelmez. N=50'ye ulaşmadan regime değişir, framework path-dependent restart ister, sonsuz döngü.

2. **Edge factory throughput zero** — Quantlab 30 günde 1 commit. Hipotez yazımı production-line değil insight-bound; 4-8 saat/hipotez × 95% reject rate = haftada 6 saatte 0.5 yeni edge → ayda 2 deneme → yılda 24 → istatistiksel olarak yetersiz.

3. **Solo operator capacity** — 91 agent + 42 command + 6 hook + 2 repo + execution + research + ops + statistics = 1 kişiye fazla. Burnout reçetesi.

**Bu üçü çözülmeden infra düzeltmeleri PnL üretmiyor.**

---

## 3. Strategic Principles — taktiklerden önce kararlar

Bu prensiplere `Master Plan v3 commitment` diyoruz. Sonraki sprint'ler bunlardan üretilecek.

### 3.1 Discipline'i AT, parametrelerini DEĞİŞTİR
- ❌ Bonferroni m=3 (path-dependent family) → ✅ **Bayesian sequential update** (path-independent)
- ❌ N=50 frequentist stopping → ✅ **SPRT (Sequential Probability Ratio Test)** adaptive
- ❌ DSR p>0.7 fixed threshold → ✅ **Bayes Factor with explicit prior**

### 3.2 Throughput > Validation perfection
- 5 yeni hipotez/ay'lık throughput hedef. 95% reject olur, sorun değil.
- Pre-registration HER hipotez için, ama family-wise değil hypothesis-wise.
- Failed hypotheses **logged** (bilgi birikiyor, bir sonraki tasarımı besliyor).

### 3.3 Multi-cadence > 4h-only
- 4h carry (mevcut) + 1h vol + daily basis = N hızlı birikir, regime shifts arası farklı strateji ayakta kalır.

### 3.4 Alt-data > pure price/funding
- 2026 crypto edge'i on-chain + sentiment + orderflow'da. Sadece price/funding aynı 1000 botla rekabet.
- Glassnode free tier (BTC active addresses, exchange netflow), OKX long-short ratio, OKX OI history — minimum başlangıç.

### 3.5 HRP allocator default
- Equal-weight = factor concentration riski (premortem #5). HRP zaten kodda var, default haline gel.

### 3.6 Operator hard limit
- Haftada ≤6 evening-saat. Daha fazlası burnout debt = ileride %100 sistem çöküşü.
- Aylık 1 hafta full-off (zorunlu). Sistem kendi başına çalışacak.

---

## 4. Execution — 4 Sprint × 1 Hafta × 6 Saat

Her sprint: 6 evening-saat. "Done" criteria katı. Geçemezsen dur, replan.

### Sprint 0 (Bu hafta — DEVAM EDİYOR)
**Goal: Stop the bleeding**

| Görev | Durum |
|---|---|
| Drought alarm | ✅ Done (2026-05-07) |
| Pairs disabled (regime) | ✅ Done |
| EF SQL log spam fix | ✅ Done |
| Fail-fast guard empty-TMQ_MODE hole | ✅ Done |
| **7-gün unattended çalışma testi** | ⏳ Started 2026-05-07 15:55 |

**Done = 2026-05-14 15:55'e kadar worker manuel müdahale olmadan çalışmaya devam ediyor + günlük heartbeat'ler kesintisiz**

---

### Sprint 1 (Hafta 1 — 2026-05-08 → 05-14): Early-Warning Instrumentation
**Goal: premortem'in 6 erken-uyarı sinyalini operasyonel hale getir**

#### Tasks (priority order)

1. **Funding/Fee Ratio Dashboard** (premortem #1 detection)
   - Her closed shadow/live trade için: `realized_funding_received / realized_fees_paid` hesapla
   - Rolling 10-trade ratio'yu state'e yaz
   - `< 1.5` → Telegram alarm, promotion freeze
   - **File:** `01_Services/QuantEngine.Worker/FundingHybridExecutor.cs` (PersistFundingCloseAsync method)
   - **Tahmin:** 2 saat

2. **Maker Fill Ratio Tracker** (premortem #4 detection)
   - Her order için: was filled as maker (post-only) or taker?
   - Rolling 30d MakerFillPct heartbeat satırına ekle
   - `< 75%` → günlük Telegram digest'te warning
   - **File:** `03_Infrastructure/Data.Persistence/Services/OkxExecutionService.cs`
   - **Tahmin:** 2 saat

3. **Pairwise PnL Correlation Matrix** (premortem #5 detection)
   - Daily strategy PnL → 60d rolling correlation matrix
   - Off-diagonal `> 0.4` → ekran kırmızı + Telegram
   - **File:** Yeni `02_Core/Application/Observability/PortfolioCorrelationService.cs`
   - **Tahmin:** 2 saat

**Sprint 1 Done = 3 metrik Telegram'da görünüyor + state dosyalarında persist ediliyor**

---

### Sprint 2 (Hafta 2 — 2026-05-15 → 05-21): Bayesian Framework Swap
**Goal: Pre-registration trap'inden çık (premortem #6 root fix)**

#### Tasks

1. **SPRT (Sequential Probability Ratio Test) implementation**
   - H0: Sharpe ≤ 0, H1: Sharpe ≥ 0.5
   - Each new trade updates likelihood ratio
   - Promote when LR > 8 (≈ p > 0.95), kill when LR < 1/8
   - **File:** Yeni `02_Core/Application/Statistics/SequentialDecision.cs`
   - **Replace:** `FormalPromotionGate.EvaluatePaperToLive`
   - **Tahmin:** 4 saat

2. **Bayesian Sharpe estimator (replace frequentist Sharpe CI)**
   - Prior: Beta(2, 5) on Sharpe (slightly skeptical, room for surprise)
   - Posterior update per trade
   - Strategy promote when posterior P(Sharpe > 0.5) > 0.7
   - **File:** `02_Core/Application/Statistics/BayesianSharpe.cs`
   - **Tahmin:** 2 saat

**Sprint 2 Done = LTC Funding-Long'ın live kararı yeni framework'le hesaplanıyor + sonuç eski ile karşılaştırma logged**

---

### Sprint 3 (Hafta 3 — 2026-05-22 → 05-28): Wire the Dormant Code
**Goal: Mevcut kodda var ama kullanılmayan capabilities'i aktif hale getir**

#### Tasks

1. **HRP Allocator wire**
   - `HierarchicalRiskParity` zaten kodda var (`02_Core/Application/...`), executor'a bağlı değil
   - Per-strategy weight'leri HRP'den al, equal-weight'i değiştir
   - **File:** `01_Services/QuantEngine.Worker/FundingHybridExecutor.cs` (sizing logic)
   - **Tahmin:** 3 saat

2. **PortfolioRiskLive enforcing yap**
   - Şu an READ-ONLY (yorum line 16: "Does not block trades")
   - VaR/CVaR breach → `BlockNewEntries` flag set, RiskAgent guardrails check
   - **File:** `01_Services/QuantEngine.Worker/PortfolioRiskLiveHostedService.cs`
   - **Tahmin:** 2 saat

3. **Real Reconciliation (replace stub)**
   - Şu an `ReconciliationAgent` "Simplified for this demo" comment'i ile stub
   - `/api/v5/account/positions` + `/api/v5/trade/fills-history` → state-diff
   - Drift `> 1%` → HARD halt + Telegram
   - **File:** `03_Infrastructure/Data.Persistence/Services/ReconciliationAgent.cs`
   - **Tahmin:** 1 saat (basit version, tam iteration sonra)

**Sprint 3 Done = 3 dormant capability live + bir test: kasıtlı drift create et, halt tetiklenmeli**

---

### Sprint 4 (Hafta 4 — 2026-05-29 → 06-04): Edge Factory Revival
**Goal: Quantlab throughput'u 0/ay'dan 4/ay'a çıkar**

#### Tasks

1. **Bootstrap CI implementation in Quantlab**
   - README'de iddia ediliyor ama kod yok (audit confirmed)
   - 5000 iterations, 24-bar block bootstrap
   - **File:** `Quantlab/edgelab/bootstrap.py` (new)
   - **Tahmin:** 2 saat

2. **Regime classifier (3-state HMM)**
   - Features: realized vol 24h, BTC funding mean, BTC-ETH correlation 7d
   - 3 states: low-vol/quiet, mid-vol/trending, high-vol/dislocation
   - Backtests run regime-conditional
   - **File:** `Quantlab/edgelab/regime.py` (new)
   - **Tahmin:** 3 saat

3. **5 yeni hipotez yaz** (1/gün, hafta sonu boş günler için buffer)
   - **Hipotez 1:** `vol_carry` — short realized vol, long implied vol proxy via OI delta
   - **Hipotez 2:** `funding_zscore_meanrev` — extreme funding rate'ler mean reverts
   - **Hipotez 3:** `oi_divergence` — OI ↑ + price ↓ = bearish
   - **Hipotez 4:** `basis_term_structure` — quarterly futures vs perp mispricing
   - **Hipotez 5:** `liquidation_fade` — büyük liquidation cascade'inden sonra mean revert

**Sprint 4 Done = 5 hipotez Quantlab'de yazılmış + en az 1'i bootstrap CI + regime-conditional backtest tamamlamış. Tier-A approval önemli değil bu hafta — output önemli.**

---

## 5. Sprint 5-12 — REAL EDGE HUNTING (2 ay)

Sprint 1-4 altyapı kurar. Sprint 5+ **gerçek edge avı** — mevcut funding-only carry'den çık, 4 farklı kategoriye yayıl.

### Neden Mevcut Edge'ler Yetersiz
- Tek-factor concentration (hepsi short-vol carry)
- Sample size yetersiz (LTC N=7, AAVE N=8 live)
- Compressed regime — funding rate'leri 1bp eşiğinin altında
- No alt-data / microstructure — 1000 retail bot ile aynı sinyal kaynağı
- No cross-venue arbitrage

### 4 Edge Kategorisi (her biri farklı factor)

#### Kategori A: Microstructure / Orderflow
| Hipotez | Veri | Beklenti |
|---|---|---|
| OFI (Order Flow Imbalance) scalp | OKX L2 100ms | 5-15bp/trade, Sharpe 0.8-1.2 |
| Queue position arb | Maker fill rate per level | 3-8bp/trade |
| Sweep-following / sweep-fade | Aggressive market sweep detection | 8-20bp on rare events |

#### Kategori B: Cross-Venue Basis
| Hipotez | Veri | Beklenti |
|---|---|---|
| Funding skew arb | OKX vs Binance funding (same symbol) | 5-15bp/8h on dislocations |
| Quarterly futures-perp basis | OKX delivery vs perp | 20-50bp/month |
| Stablecoin flow signal | USDT/USDC dominance shifts | Macro, weekly cadence |

#### Kategori C: On-Chain & Sentiment
| Hipotez | Veri | Beklenti |
|---|---|---|
| Exchange netflow → BTC direction | Glassnode free tier | Sharpe 0.5-1.0, daily |
| Long-short account ratio mean-rev | OKX `/long-short-account-ratio` | 2-5bp/4h, high N |
| Stablecoin supply expansion | Etherscan + DefiLlama | Macro, weekly |

#### Kategori D: Volatility Surface (advanced, sonra)
| Hipotez | Veri | Beklenti |
|---|---|---|
| DVOL term structure | Deribit DVOL futures | Sharpe 0.7-1.0 |
| Skew z-score mean-rev | Deribit skew historical | Tail-event |
| Realized-implied vol gap | OKX realized + Deribit IV | Classic vol-carry |

### Sprint 5-12 Schedule

| Sprint | Edge | Kategori | Done = |
|---|---|---|---|
| 5 | OFI scalp | A | Backtest + decision |
| 6 | Funding skew arb | B | Backtest + paper-shadow |
| 7 | Sprint 5 winner shadow N=20 monitor | A maintenance | İlk B-tier validation |
| 8 | Exchange netflow → BTC | C | Backtest |
| 9 | Quarterly basis arb | B | Live shadow if Sprint 6 passed |
| 10 | Sweep-fade | A | Backtest |
| 11 | DVOL term structure | D | Backtest |
| 12 | Portfolio review GO/NO-GO | — | 12-hafta verdict |

### Sprint 12 Sonu Hedef Portföy
| Edge | Kategori | Sharpe Hedef | Capital % |
|---|---|---|---|
| Funding-Long (LTC + AAVE) | Carry | 0.7 | 25% |
| Funding skew arb | Cross-venue | 1.0 | 25% |
| OFI scalp | Microstructure | 0.8 | 25% |
| Exchange netflow | On-chain | 0.6 | 15% |
| Cash buffer | — | — | 10% |

**Portfolio Sharpe = sqrt(4) × 0.78 ≈ 1.5** (uncorrelated assumption — Sprint 1's correlation matrix bunu verify edecek)

### Sprint 5+ "Done" Kriterleri
- En az 1 edge Tier-A geçmeli (yeni Bayesian framework altında)
- Pairwise correlation ≤ 0.4 (HRP allocator working olduğunu kanıtlar)
- Real capital canary $1-5K (paper Sharpe 1.0+ sustained 30d olduktan sonra)

### After-Sprint Cycle (Sprint 12 sonrası, 4-hafta tekrar)
| Hafta | Aktivite |
|---|---|
| W1 | Yeni hipotez yazımı (kategori rotasyonu) |
| W2 | Backtest + validate |
| W3 | Promote-to-shadow olanları monitor + maintenance |
| W4 | Operatör kontrol haftası — sistem kendine başa |

---

## 6. What We Won't Do — Negative Roadmap

Bunları yapmamak için bilinçli kararlar:

| ❌ Yapmıyoruz | Sebep |
|---|---|
| AAVE'yi N<30'da promote | Operator's own discipline + premortem #1 |
| Pairs'i regime kötüyken zorla | 0 candidate 6 gün, regime sinyali |
| Pairs threshold düşür | Pre-reg invalidate + signal-to-noise erodes |
| Sharpe 4-7 hedeflemek | Institutional fantazi, retail erişilemez |
| 10+ strateji live | Factor concentration without HRP = -%11 in 18 days |
| Postgres migration şimdi | Premature, SQLite OK 3 strateji'ye kadar |
| Web UI feature ekleme | Bottleneck DEĞİL, distraction |
| ML overlay | N<200, training data yok |
| Yeni MCP/agent kurulumu | Mevcut infra zaten over-engineered |
| Live capital $5K paper Sharpe < 1.0 olmadan | Risk control kurulu değil |

---

## 7. Hard Exit Criteria — Ne zaman PIVOT/STOP

| Eşik | Aksiyon |
|---|---|
| Sprint 1 sonunda 3 dashboard tamamlanmamışsa | 1 hafta pause, replan |
| Sprint 4 sonunda 5 hipotez yazılmamışsa | 2 hafta pause, throughput root cause |
| 8 hafta paper Sharpe < 0.3 sustained | Strateji tamamen değiştir, factor pivot |
| 12 hafta operator burnout signal (terse commits, Telegram unread > 50, 5+ gün) | 2 hafta zorunlu off |
| 6 ay portfolio Sharpe < 0.7 | Quitting threshold — öğrenmeleri sakla, sermayeyi koru |
| Drawdown > %15 | Anında halt, full review |

---

## 8. Most-Profitable-Path — Dürüst Cevap

**Soru:** "En çok kar yaratan bot nasıl?"

**Dürüst cevap:** $5K paper'da 1.5 strateji ile **dünya değiştiren** para kazanmıyorsun. Yapabileceğin şey:

1. **Ay 1-3:** Sistem ayakta tutulur, 2 edge validate edilir → portfolio Sharpe 0.7-1.0 → +%5-15 paper
2. **Ay 4-6:** AAVE promote edilirse, küçük real capital ($1K) canary → +$50-150
3. **Ay 7-12:** 3-4 edge live, $10K capital, Sharpe 1.0-1.5 → +%15-30 = $1500-3000 / yıl
4. **Yıl 2:** Eğer edges holds → $25K-50K capital, $5K-15K / yıl
5. **Yıl 3+:** Part-time research analyst hire, edge factory output 2x → meaningful PnL

**Asymmetric bet:** Şimdi 4 hafta yatırım yap (bu plan), 6 ay disiplinli uygula. İşe yararsa nadir bir şeye sahip olursun. İşe yaramazsa retail'in en temiz dökümante edilmiş trading sistemine sahipsin (career capital).

**Alternatif (aktif olarak ÖNERMİYORUM):**
- Eşikleri aç (1bp → 0.5bp), trade gör, hızlı PnL (overfit'ten 6 ay sonra ölür)
- Strateji çoğalt, equal-weight (premortem #5 fires)
- Live capital'a hızlı geç (paper Sharpe doğrulanmadan = sermaye yakar)

---

## 9. Decision Log — Bu plana commit ettiğin anlamına gelen şeyler

Bu planı kabul edersen:
- [x] AAVE N=30'a kadar shadow'da kalır (kendi disiplinin korundu)
- [x] Pairs şimdilik kapalı, regime düzelene kadar
- [x] Bonferroni → Bayesian sequential (Sprint 2)
- [x] HRP allocator default (Sprint 3)
- [x] Quantlab throughput hedefi 4 hipotez/ay (Sprint 4 onwards)
- [x] Operator weekly limit 6 evening-saat (NEGOTIATION yok)
- [x] Aylık 1 hafta full-off (NEGOTIATION yok)
- [x] Live capital paper Sharpe > 1.0 sustained 30 days olmadan dokunulmaz

---

## 10. Sprint 0 verification — bugün bitmiş olanlar (kanıt)

Drought alarm: `[15:34:31 INF] [HB DROUGHT] Telegram alert sent (drought=6d).`
Pairs disabled: `appsettings.Paper.json` "Strategy.PairsTrading.Enabled": false, "Pairs.Discovery.Enabled": false
EF spam: "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
Guard hardened: `Program.cs:279` `string.IsNullOrEmpty(tmqModeGuard)` ek koşulu

Worker durumu:
- PID 32492 (or current)
- Boot signature: `[CRED] OKX ApiKey=36c (Demo=True)`, `OKX execution service registered`, `TMQ_MODE=paper`
- Heartbeat 60s düzenli

---

**Onay durumunda:** Sprint 1'e bu hafta sonu başlanır (cumartesi/pazar 6 saat). 2026-05-14'te Sprint 1 done = 3 dashboard live.

**Onay vermezsen:** Sistem mevcut haliyle stabil çalışır, drought alarmı görev başında. 1-2 gün düşün, dön.
