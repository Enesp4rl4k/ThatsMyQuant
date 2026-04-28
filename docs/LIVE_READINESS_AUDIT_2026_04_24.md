# Live Trading Readiness Audit — 2026-04-24

## Summary: **Paper-grade READY ✅ · HFT NOT ready 🔴**

Our current stack is **production-ready for swing/position trading** (1h-4h timeframes,
funding alpha every 8h, pair stat-arb on daily candles). It is **NOT ready for HFT**
(sub-millisecond latency, co-location, tick-by-tick market making).

Given our validated strategies operate on 1h+ cadence, this is the right balance.

## Performance Audit Results

### ✅ Strengths

| Area | Finding |
|------|---------|
| Real-time data | OKX WebSocket (`OkxWebSocketService`) + Binance WebSocket (`BinanceWebSocketService`) registered in 3 worker projects (Execution, Listener, QuantEngine). Not REST polling for prices. |
| HFT infra | `BinanceWebsocketAdapter`, `TickReplayEngine`, `OFIExecutionAgent` all present. Wired for future HFT use. |
| Timer hygiene | Only 5 DispatcherTimers in Dashboard (clock 1s, queue 5s, Pro Mode 60s, Funding auto-refresh 60s, session timeout). No timer explosion. |
| Async patterns | Only 2 `async void` (both event handlers — correct). No fire-and-forget abuse. |
| Non-blocking scans | Pro Mode wraps `ScanCycle` in `Task.Run` — UI thread protected during scan. |
| Thread safety | Locks in `CircuitBreakerMonitor`, `ProModeOrchestrator`. `ConcurrentDictionary` in `CandidatePool`. No obvious race conditions. |
| Resource cleanup | `IDisposable` implemented where needed (`ProModeViewModel`, timers stopped on Closing). |
| Build | 0 errors / 0 warnings in Release (Dashboard + Application projects). |

### 🟡 Moderate Concerns

| Area | Finding | Impact | Fix |
|------|---------|--------|-----|
| VM size | `MainViewModel.cs` 5,063 lines + 11 partials = 11,490 total. | Long UI init (~100-300ms), WPF memory ~200MB baseline | Already using partial class split. Acceptable for desktop terminal. |
| ObservableCollections | 88 collections, 139 `.Add` calls. Some capped (opportunity feed 100, recent 50), others not. | Slow memory growth over days of continuous run | Add periodic prune for TradeJournal, CandidatePool |
| `.GetAwaiter().GetResult()` | Used in `OkxFundingRateProvider.GetRate` (sync → async bridge) | Minor blocking call on hot loop (backtest only, not live) | Acceptable — only hit once per symbol on first access (cached thereafter) |
| WPF Canvas charts | Polyline / ItemsControl canvas rendering (not SciChart) | Rendering CPU on big datasets (1000+ points) | Our datasets are <1000 points; fine |
| SQLite | Single-writer limit under load | Fine up to ~100 trades/minute; HFT needs Postgres | Migrate later if needed |

### 🔴 HFT Blockers (not relevant for current strategies)

| Area | Blocker | When Relevant |
|------|---------|---------------|
| Execution latency | REST order routing ~200ms; no co-location | Only matters for market-making / sub-second arbitrage |
| L2 tick ingestion | `L2TickReplayEngine` exists but not wired to live feed | HFT mode only |
| Order book depth | OFI agent present but not running in live path | Market-making only |
| Sub-ms clocks | System clock; no hardware timestamp | Microstructure latency arb |

---

## Live Trading Scorecard

**For our validated strategies (4h pairs + 8h funding alpha):**

| Criterion | Status | Evidence |
|---|---|---|
| Price data stream | ✅ Live | OKX/Binance WebSocket services registered |
| Strategy execution latency budget | ✅ OK | We trade on hour boundaries, 200ms execution is invisible |
| Circuit breaker | ✅ Implemented | 5% DD trip, manual reset |
| Position leverage cap | ✅ Hard-capped | 15x absolute max in runners |
| DD-based auto-throttle | ✅ Implemented | Halves leverage at 60% of DD budget |
| Kill switch | ✅ Implemented | Pro Mode tab 🛑 button |
| Real-time regime awareness | 🟡 Partial | Regime computed, but live feed to throttle needs hosted service |
| Automatic reconnect on WS disconnect | 🟡 Unverified | Need to test network interruption recovery |
| Persistent state on restart | ✅ OK | SQLite persists positions, candidate pool |
| Audit logging | ✅ OK | `IAuditLogger` wired, structured logs |
| Paper-to-live parity | ✅ Implemented | `ExecutionParityService` tracks divergence |

## Resource Usage Estimate

**Expected live-trading footprint (for validated portfolio, 6 positions, Moderate mode):**

| Resource | Estimate |
|----------|----------|
| RAM (baseline) | 180–250 MB |
| RAM (8h runtime) | 280–350 MB (gradual growth from trade history) |
| RAM (7d runtime) | 400–500 MB (should stabilize; periodic prune if not) |
| CPU (idle) | 1–2% average, 5–8% during refresh ticks |
| CPU (backtest) | 30–60% single-core during run, 2–3 min for 365d |
| Network (live) | 50–100 KB/s per active WebSocket (BTC/ETH/SOL dominant) |
| Disk (SQLite) | ~2 MB/day at 30 trades/day cadence |

**Multi-day live run (expected):** stable. No identified memory leak with current bounded collections. Will grow slowly via trade journal — recommend periodic manual export/archive or automated prune at 30-day retention.

## Known Issues to Monitor in Live

1. **WebSocket disconnect recovery** — code uses `BackgroundService` with try/catch loop, but actual reconnect behavior during OKX downtime hasn't been stress-tested.
2. **Clock drift** — system clock used for timestamps; if machine clock drifts significantly vs exchange server, funding rate matching could be 1-period off.
3. **Partial fills** — `ExecuteTradeHandler` handles happy path well; partial fill + subsequent cancel under heavy volatility untested.
4. **Exchange rate limits** — OKX allows ~20 req/sec per IP. Our aggressive mode could approach this on a market-wide event.

## Recommendations Before Going Live

### Must-do (safety):
1. **30-day paper trade** of final config before any live capital
2. **Manual kill-switch test** — verify 🛑 button immediately closes open positions (not just blocks new entries)
3. **Network interrupt test** — pull Wi-Fi cable for 60s, verify reconnect + no lost state
4. **Low-balance canary** — start live with $100-$500, not full capital

### Should-do (optimization):
1. Add periodic `CandidatePool` prune (30-day retention default)
2. Add `TradeJournal` rolling window (last 30 days in UI, archive older)
3. Profile `MainViewModel` initialization (if >500ms, consider lazy-load tabs)
4. Add OKX rate-limit monitoring (if >15 req/sec sustained, circuit-break)

### Nice-to-have (pro polish):
1. Per-position mark-to-market chart in Terminal tab
2. Live P&L with 1s update tick (already possible via WebSocket price stream + trade events)
3. Drawdown curve stand-alone panel (currently inline in Alpha Portfolio)
4. Discord/Telegram alert on circuit breaker trip or kill switch

---

## Verdict

**You can go live safely on the validated 6-position Moderate portfolio after 30 days of paper trading.** Infrastructure is production-grade for swing/position strategies. Performance will not be a bottleneck.

**Do NOT go live with:** Aggressive mode (7x) until paper-validated, any HFT-style strategy (code present but untested path), any untested pair outside the validated 6.

Build: clean. Tests: not re-run this audit. Dashboard + Launcher shortcuts on desktop ready to use.
