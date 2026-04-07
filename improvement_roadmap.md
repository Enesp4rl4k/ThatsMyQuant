# ThatsMyQuant — Production Hardening Roadmap

> **Project Level:** Level 3.5 — Advanced HFT MVP with real ML inference, multi-agent consensus, and institutional-grade risk governance.
>
> **Target Level:** Level 5 — Live-deployable with real capital protection guarantees.

---

## What Already Works (Do NOT Rebuild)

These components are solid and should be preserved. Any "improvement plan" that tells you to build these is lying to you.

| Component | File(s) | Status |
|:--|:--|:--|
| Lock-free LMAX Ring Buffer | `HftInfrastructure/HftRingBuffer.cs` | ✅ Cache-line padded, power-of-2 bitwise modulo |
| O(1) Online Tick Math | `MachineLearning/TickRingBuffer.cs` | ✅ Running sum, no LINQ |
| SIMD Vectorized Math | `Strategy/VectorizedMathUtils.cs` | ✅ AVX `Vector<float>` sum + batch SMA |
| ML Inference Pool | `MachineLearning/InferenceAgent.cs` | ✅ ConcurrentStack pool, per-core JIT warmup |
| Feature Drift Monitor | `MachineLearning/FeatureDriftMonitor.cs` | ✅ Z-score based, critical threshold gating |
| L2 Tick Replay Engine | `Strategy/TickReplayEngine.cs` | ✅ OFI injection, order book walk-through, sqrt slippage |
| Dead Man's Switch | `HftInfrastructure/CircuitBreakers.cs` | ✅ Heartbeat monitor + liquidation evacuator |
| Circuit Breaker (3-layer) | `Strategy/CircuitBreakerService.cs` | ✅ Rate limiter + drawdown + error cascade |
| Token Bucket Rate Limiter | `Strategy/RiskAgent.cs` (line 722) | ✅ Inside RiskAgent |
| Portfolio Manager (full) | `Strategy/MemoryPortfolioManager.cs` | ✅ MAFE, balance locking, Interlocked decimals, reconciliation |
| Risk Governor (757 lines) | `Strategy/RiskAgent.cs` | ✅ Kelly, heat caps, correlation, execution health, VPIN |
| Swarm Orchestrator | `Strategy/SwarmOrchestrator.cs` | ✅ Multi-agent concurrent consensus with emergency shutdown |
| Strategy Promotion Ladder | `Strategy/StrategyPromotionService.cs` | ✅ Backtest → Paper → LiveProbe → ScaledLive |
| GC + ThreadPool Tuning | `Common/PerformanceBootstrapper.cs` | ✅ SustainedLowLatency, 64+ min threads |

---

## Sprint 0: Compilation & Structural Fixes (You Own This)

- [ ] Fix `ProcessSignalHandler.cs` — missing `_marketDataService` field reference
- [ ] Fix `StrategyPromotionService.cs` — 10× nullable `PromotionGuardResult?` → `PromotionGuardResult` conversion errors  
- [ ] Fix `PaperTradingService.cs` — `IReadOnlyList<Candle>` → `List<Candle>` type mismatch
- [ ] Delete or merge the dead `02_Core/Application/Risk/MemoryPortfolioManager.cs` (65-line skeleton) — the real one is in `Strategy/`

---

## Sprint 1: Wiring & Safety Integration

**Goal:** Activate existing safety components that are currently dead code.

### 1.1 Register CircuitBreakerService in DI

`CircuitBreakerService` exists but isn't in `Program.cs`. It needs to wrap the execution path.

```csharp
// In QuantEngine.Worker/Program.cs
builder.Services.AddSingleton<CircuitBreakerService>();
```

Then inject it into the execution handler so every order passes through `CheckOrderAllowed()` before dispatch.

### 1.2 Wire DeadMansSwitch + HeartbeatMonitor

These exist in `HftInfrastructure/CircuitBreakers.cs` but are never instantiated. Wire them to the `BinanceWebSocketService` tick stream:

```
BinanceWebSocketService.OnTickReceived → HeartbeatMonitor.RecordHeartbeat()
Timer (5ms) → HeartbeatMonitor.AuditConnection()
```

### 1.3 Consolidate MemoryPortfolioManager

Kill the 65-line version at `Application/Risk/MemoryPortfolioManager.cs`. Keep only the 289-line version at `Application/Strategy/MemoryPortfolioManager.cs`. Extract an `IPortfolioManager` interface so both `RiskAgent` and `CircuitBreakerService` reference the contract, not the concrete class.

---

## Sprint 2: Test Coverage for Money-Critical Paths

**Goal:** Zero untested money-handling code.

### 2.1 MemoryPortfolioManager Tests (HIGH PRIORITY)

Test cases needed:
- `SetBalance` / `GetAvailableBalance` round-trip
- `Lock` / `Unlock` correctly adjusts available balance
- `OpenPosition` + `ClosePosition` P&L calculation (long and short)
- `UpdateFromExecutionReport` handles partial fills, full fills, rejections
- `Reconcile` syncs with external source and removes stale assets
- Thread-safety: concurrent `AddBalance` from multiple threads converges correctly
- `GetDrawdownPercent` calculation after series of wins/losses

### 2.2 FeatureEngineeringAgent Tests

Test cases needed:
- `ProcessTickNatively` produces valid RSI [0,1], EMA deviation, MA bias
- `SafeFloat` returns fallback for NaN/Infinity 
- `ApplyFoundationData` correctly maps BidAskImbalance and TickDirectionRatio
- `CopyTo` produces identical snapshot to `CurrentTensor`

### 2.3 Full Pipeline Integration Test

Signal → ProcessSignalHandler → SwarmOrchestrator → ExecuteTradeCommand → Trade result

Use `MockBrokerService` + `MockChatCompletionService` + in-memory SQLite. Verify the entire pipeline end-to-end without external dependencies.

---

## Sprint 3: Hot-Path Performance Fixes

**Goal:** Remove kernel-level bottlenecks from the tick processing pipeline.

### 3.1 Replace `lock` in FeatureEngineeringAgent

The `lock(_syncLock)` on every tick is a kernel mutex in your HFT hot path. Replace with a **double-buffer swap pattern**:

```csharp
private float[] _writeBuffer = new float[5];
private float[] _readBuffer = new float[5];

public void ProcessTickNatively(double price)
{
    // Write to write buffer (single writer, no lock needed)
    _writeBuffer[0] = SafeFloat(rsi / 100.0);
    // ... compute all features ...
    
    // Atomic swap: reader instantly sees new tensor
    Interlocked.Exchange(ref _readBuffer, _writeBuffer);
    _writeBuffer = new float[5]; // or pool
}

public void CopyTo(Span<float> destination)
{
    // No lock — just read the current buffer
    Volatile.Read(ref _readBuffer).AsSpan().CopyTo(destination);
}
```

### 3.2 Expand VectorizedMathUtils

`CalculateBatchSMA` has a comment "For brevity, we focus on the core concept" — the body is a naive O(N×W) loop calling `SumVectorized` per window. Implement proper sliding-window with O(1) per step:

```csharp
public static void CalculateBatchSMA_Optimized(ReadOnlySpan<float> prices, int windowSize, Span<float> results)
{
    float windowSum = SumVectorized(prices[..windowSize]);
    results[0] = windowSum / windowSize;
    
    for (int i = 1; i <= prices.Length - windowSize; i++)
    {
        windowSum += prices[i + windowSize - 1] - prices[i - 1]; // O(1) per step
        results[i] = windowSum / windowSize;
    }
}
```

### 3.3 Replace DateTime.UtcNow in Hot Paths

`HeartbeatMonitor`, `TokenBucketRateLimiter`, and daily loss checks use `DateTime.UtcNow` which is a syscall. Use `Environment.TickCount64` (already used in `CircuitBreakerService.CheckOrderAllowed`) or inject `TimeProvider` (.NET 8+) for testability.

---

## Sprint 4: Strategy/ Folder Decomposition

**Goal:** Break the 55-file god module into proper Clean Architecture boundaries.

### Proposed structure:

```
02_Core/Application/
├── Strategy/           (Swarm, AgentCoordinator, MarketRegimeDetector — core decision logic)
├── Risk/               (RiskAgent, CircuitBreakerService, CorrelationService, CommissionGuard)
├── Execution/          (OFIExecutionAgent, HftExecutionService, ExecutionImpactModel, SlippageSimulator)
├── Backtesting/        (BacktestEngine, TickReplayEngine, PaperTradingService, MarketEventSimulator)
├── Portfolio/          (MemoryPortfolioManager, PortfolioStateCache, ReconciliationAgent, PositionSupervisorService)
├── Promotion/          (StrategyPromotionService, StrategyExpectancyService, StrategyTuningService, ExitPolicyLearningService)
├── MachineLearning/    (keep as-is — already clean)
├── HftInfrastructure/  (keep as-is — already clean)
└── Common/             (keep as-is)
```

Move files, update namespaces, `dotnet build` after each batch. No logic changes — pure organizational refactor.

---

## Sprint 5: Real Exchange Connectivity (OMS)

**Goal:** Replace MockBrokerService with live Binance connectivity.

### 5.1 BinanceExecutionService

The `03_Infrastructure/Exchange.Binance/` folder already exists. Build:
- HMAC-SHA256 request signing
- REST: Place/Cancel/Status for Limit and Market orders
- WebSocket: `executionReport` stream → `MemoryPortfolioManager.UpdateFromExecutionReport()`
- Queue position tracking for limit orders

### 5.2 FIX Protocol Adapter

`HftInfrastructure/FixProtocolAdapter.cs` already exists as a skeleton. Flesh it out for institutional venues if/when you need sub-ms latency beyond REST.

### 5.3 Exchange Info Provider

`BinanceExchangeInfoProvider` already exists and initializes on startup. Extend it with:
- Min notional / lot size enforcement before order submission
- Symbol precision (price/qty decimals) auto-formatting

---

## Sprint 6: Observability

**Goal:** "If you can't measure it, you can't improve it."

### 6.1 OpenTelemetry Traces

Add `ActivitySource` to:
- `SwarmOrchestrator.Handle()` — total consensus time
- Each agent (`SentimentAgent`, `RiskAgent`, `InferenceAgent`) — individual latency
- `IExchangeService.PlaceOrderAsync()` — tick-to-order latency

### 6.2 Structured Metrics

Export to Prometheus/Grafana:
- Orders blocked by circuit breaker (counter)
- Current drawdown % (gauge)
- Portfolio heat (gauge)
- Agent veto rates per agent (counter)
- ML feature drift z-scores (histogram)

### 6.3 Audit Trail

Every trade decision (approved or rejected) should be persisted with full reasoning chain. The `Signal.Metadata` dictionary already carries this — pipe it to a dedicated audit table or append-only log.

---

## Priority Matrix

| Sprint | Focus | Risk if Skipped | Effort |
|:--|:--|:--|:--|
| **0** | Compilation fixes | 🔴 Nothing works | ~30 min |
| **1** | Wire safety into DI | 🔴 Safety components are dead code | ~2 hrs |
| **2** | Money-path tests | 🔴 Silent balance bugs in production | ~4 hrs |
| **3** | Hot-path perf fixes | 🟡 Kernel stalls under load | ~3 hrs |
| **4** | Folder decomposition | 🟡 Growing tech debt, onboarding cost | ~3 hrs |
| **5** | Real OMS | 🟡 Can't trade real money | ~8 hrs |
| **6** | Observability | 🟢 Flying blind but functional | ~4 hrs |

---

> [!WARNING]
> **Do not skip Sprint 2.** A portfolio manager that handles real money with zero test coverage is the fastest path to a "Knight Capital" moment. Write the tests before touching any exchange APIs.
