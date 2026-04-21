# ThatsMyQuant — Stratejik odak (edge, execution, risk)

Bu doküman, teknik borçları kapatmanın ötesinde **ticari kanıt** üretmek için öncelik sırasını sabitler. Kod “doğru çalışır” ile “para kazanır” ayrımını net tutar.

## 1. Kanıt ve kar (öncelik 1)

**Amaç:** Edge’in gerçekten var olup olmadığını ölçmek; maliyet sonrası net performans.

| Eylem | Çıktı |
|--------|--------|
| Paper veya küçük sermaye ile aynı metrik seti | Net PnL değil; **maliyet sonrası** getiri, doldurma kalitesi |
| Backtest → paper → canlı **tutarlı** promotion (`StrategyPromotionService`, config) | Overfitting azaltma |
| Walk-forward / out-of-sample (BacktestOrchestrator + PBO zaten var) | Parametre stabilitesi |
| Dashboard / JSON performans deposu (`IStrategyPerformanceStore`) düzenli inceleme | Karar döngüsü |

**Başarı ölçütü:** Belirli bir sembol/strateji/regime için **paper ve canlı** bucket’larda beklenen değer ve profit factor’ün tanımlı eşiklerin üzerinde kalması (eşikler `appsettings` ile).

## 2. Execution gerçekliği (öncelik 2)

**Amaç:** Simülasyon ile borsa davranışının uyumu.

| Eylem | Çıktı |
|--------|--------|
| Giriş kayması: sinyal fiyatı vs gerçek `EntryPrice` (`EntrySlippagePercent`) | Prometheus: ortalama / son mutlak slip % |
| Venue HTTP hataları (`tmq_venue_http_errors_total`) | API sağlığı |
| Execution gecikmesi (`tmq_execution_latency_*`) | Latency SLO |
| Execution sync / rapor dedupe | Tutarlı pozisyon durumu |

**Başarı ölçütü:** Canlıda ortalama |slippage| ve latency’nin `MarketExecutionProfile` / risk seçeneklerindeki hedeflere yakın olması.

## 3. Risk ve sermaye bütçesi (öncelik 3)

**Amaç:** Kaybı bilinçli sınırlamak.

| Eylem | Çıktı |
|--------|--------|
| `RiskSupervisorService`, `CircuitBreakerService`, drawdown limitleri | Patlayıcı zarar azalır |
| Pozisyon yüzdesi, venue başına maruz kalma | Konsantrasyon riski kontrolü |
| Promotion aşamaları (Backtest → Paper → LiveProbe → …) | Aşamalı sermaye |

**Başarı ölçütü:** Max drawdown ve ardışık hata eşikleri ihlal edilmeden operasyon.

## 4. Güvenilirlik ve gözlem (sürekli)

CI, testler, `/metrics`, log correlation — **kaybı erken görmek** için; tek başına alpha üretmez.

---

## 90 günlük pratik döngü (özet)

1. **Hafta 1–2:** Paper’da tek strateji + tek sembol; günlük performans + slippage/latency tablosu.
2. **Hafta 3–4:** Aynı metriklerle küçük canlı; promotion kurallarını gözlemle düzelt.
3. **Hafta 5–12:** Regime değişiminde bucket performansı; gereksiz strateji kapatma.

---

## İlgili kod / yapılandırma

- Performans durumu: `IStrategyPerformanceStore`, `StrategyPromotionService`, `Promotion:*` in `appsettings`.
- Risk: `Risk:Supervisor:*`, circuit breaker.
- Telemetri: `OperationalTelemetry`, `PrometheusMetricsFormatter`, `/metrics` (servislerde).

README’de secret ve çok süreçli çalıştırma için: [README.md](../README.md).

---

## Edge skoru, paper ve backtest hattı

**Amaç:** `EdgeScoreService` ile expectancy, sembol, promotion ve risk oylarını tek skorda birleştirip sinyal metadata’sına (`EdgeScore`, `EdgeBand`, `EdgeReasons`, …) yazmak; canlıda zayıf edge’i durdurmak; paper/backtest’te **probe** bandını araştırma boyutuyla açmak.

| Yapılandırma | Anlamı |
|----------------|--------|
| `Edge:Enabled` | `true` iken swarm onayından sonra edge hesaplanır (varsayılan `false` — açıkça aç). |
| `Edge:PipelineKind` | `Live` (canlı sermaye), `Paper` (kağıt), `Backtest` (tarihsel simülasyon). Paper/Backtest’te düşük “probe” bandı canlıdaki gibi reddedilmez; `Edge:ProbeSimulatedMultiplier` ile boyutlanır. |
| `Edge:ProbeSimulatedMultiplier` | Paper/Backtest probe bandında pozisyon kesiti (ör. `0.45`). |

**Başlangıç:** `Listener.Api` ve `QuantEngine.Worker` açılışında `IPortfolioStateCache.InitializeAsync` ve `StrategyPromotionService.InitializeAsync` çağrılır; edge ve promotion guard’ları güncel trade özetine dayanır.

**Not:** `TMQ_MODE=paper` hâlâ `PaperTradingHostedService` ile ayrı döngüyü kullanır; webhook/tick üzerinden gelen sinyallerde pipeline türünü `Edge:PipelineKind` ile seçersiniz. Backtest CLI şu an ayrı motor kullanır; aynı edge metadata’sını orada da üretmek için ileride paylaşılan bir host yapılandırması eklenebilir.

---

## Operasyon checklist (Launcher / canlı)

1. **Sıra:** Launcher’da önce **Listener**, **Execution**, **QuantEngine** (Dashboard isteğe bağlı). Hepsi aynı `quant.db` connection string’ini kullanmalı (`ConnectionStrings:Quant`).
2. **Execution.Worker:** WebSocket + `TickPipeline` (order book → `ProcessSignal`), kuyruk + `ExecuteTrade`, `LivePositionLifecycleService` periyodik pozisyon yönetimi. Açılışta `PortfolioStateCache` + **`StrategyPromotionService.InitializeAsync`** yüklenir.
3. **QuantEngine.Worker:** Reconciliation + (isteğe bağlı) **`QuantEngine:LiveSignalScan`** — mum çekip tam `ProcessSignal` hattını çalıştırır; varsayılan **kapalı**. Açmak için `Enabled=true` ve `Symbols` CSV (örn. `BTCUSDT,ETHUSDT` veya `THYAO`). Tick/HFT yine **Execution**’dadır.
4. **Webhook:** Listener → `ProcessSignal` → `Execution:UseWorkerHandoff=true` ise kuyruk/DB → Execution emir.
5. **Edge:** `Edge:Enabled` ve `Edge:PipelineKind` — servis başına `appsettings` veya user-secrets. API anahtarları canlı emir için yeterli değildir; `TMQ_ALLOW_LIVE_TRADING`, venue modu ve `StartupSafetyPolicy` kurallarına uy.
6. **Gateway.Api:** Opsiyonel gRPC; zorunlu sinyal zincirinde değil.

---

## Dashboard UI — institutional WPF plan

**Product language:** All user-visible strings in the Dashboard (XAML, tooltips, `MainViewModel` copy surfaced in the shell) are **English**. This section is kept in **English** so it matches the app.

**Current shell:** Persistent **ops / risk strip** (`OpsStripExecutionLine`, `OpsStripRiskLine`) — queue summary, drawdown / governor / open-work counts; strip stays visible in Zen (only the decorative header collapses).

| Phase | Scope | Status |
|-------|--------|--------|
| 0 | Critical flows + accessibility spot-check | Ongoing |
| 1 | Risk strip, queue visibility, async feedback (critical actions) | Strip **done**; button polish incremental |
| 2 | Dock-lite / mini blotter; keyboard shortcuts | **Done** (splitter, ops mini blotter, Ctrl+1–0 / PgUp–Dn / F5 / Esc / Z, Settings summary) |
| 3 | Icon set, reduced motion, heavy grid virtualization | **Done** (MDL2 deck icons, `UiMotionPreferences`, grid recycling, Settings copy) |
| 4 | See **P4 (backlog)** below — not part of the baseline P0–P3 delivery | Backlog |

### Surface map (review tracker)

| Surface | Location |
|---------|----------|
| Terminal | `MainWindow` Terminal tab |
| Portfolio | `MainWindow` Portfolio tab |
| Bot Launcher | `MainWindow` Bot Launcher tab |
| Strategy & AI Studio | `MainWindow` Studio tab |
| Trade Ledger & Analytics | `MainWindow` Ledger tab |
| Right deck / log | `MainWindow` column 2 |
| Login | `LoginWindow.xaml` |
| Market analytics | `MarketAnalyticsControl.xaml` |
| Backtest report | `BacktestReportControl.xaml` |
| Scientific chart | `ScientificChartControl.xaml` |
| Heatmap | `MarketHeatmapControl.xaml` |
| Session / password / users | `Views/*.xaml` |

### P0 / P1 UI (plan ↔ code)

| Item | Description | Status |
|------|-------------|--------|
| P0 | **PANIC / active positions** panel must not disappear in Zen | Done (`MainWindow` Terminal lower ledger) |
| P0 | **Trade Ledger**: AI coaching vs DataGrid — no overlay; side-by-side columns | Done |
| P0 | **System Settings**: venue API fields use `PasswordBox` + VM binding (`Helpers/PasswordBoxAssistant`) | Done |
| P0 | Health card: real **process uptime**, **local persistence** (init result), **runtime snapshot** (memory / queue / ping) | Done |
| P0 | Right deck queue: `QueueHealth` + `QueueHealthColor` (fix wrong `Telemetry.*` binding) | Done |
| P1 | Launcher vs Terminal “start” single narrative; clarify duplicate backtest tabs | Done (`BotLaunchHint`, tooltips, tab headers `Backtest · Studio/Lab/Focus`, short explainer) |
| P1 | Portfolio growth chart responsive min-height | Done (`Viewbox` + row `MinHeight`, card `MinHeight`) |

### P2 UI

| Item | Description | Status |
|------|-------------|--------|
| P2 | `GridSplitter` between main tabs and control deck (hidden in Zen) | Done |
| P2 | Ops strip **mini blotter** (open-position chips, horizontal scroll) | Done |
| P2 | **Keyboard**: Ctrl+1–9 / Ctrl+0, Ctrl+PgUp/PgDn, F5, F11, Z, Esc (Z/F11 suppressed in text fields) | Done (`MainWindow.xaml.cs`) |
| P2 | Keyboard summary in System Settings | Done |

### P3 UI

| Item | Description | Status |
|------|-------------|--------|
| P3 | **Segoe MDL2** icons on right-deck page shortcuts | Done (`App.xaml` `IconFont`, `MainWindow` nav) |
| P3 | **Reduced motion**: `TMQ_REDUCE_MOTION`, `ClientAreaAnimation` hint; header pulse gated; no infinite CTA/panic loops; Combo `PopupAnimation=None` | Done (`UiMotionPreferences`, `MainViewModel.ReduceUiMotion`) |
| P3 | **DataGrid** row/column virtualization + `VirtualizingStackPanel` **Recycling** | Done (`MainWindow`, `BacktestReportControl`, `UserManagementTab`) |
| P3 | Motion / accessibility summary in System Settings | Done |

### P4 (backlog — not scheduled)

Candidate themes if you want a next UI tranche (no committed scope yet):

- **i18n / resources:** move visible strings to `.resx` (or similar) for non-English locales without scattering literals in XAML/VM.
- **Screen reader pass:** AutomationProperties on nav, grids, PANIC; focus order after Zen / splitter.
- **Chart / WebView2:** performance and lifecycle when swapping symbols or long sessions.
- **Tab sprawl:** consolidate or role-based visibility for rarely used tabs.
