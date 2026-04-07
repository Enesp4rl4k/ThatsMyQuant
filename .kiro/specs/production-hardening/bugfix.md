# Bugfix Requirements Document

## Introduction

ThatsMyQuant is a C# HFT trading system currently in a clean build state (0 errors, 0 warnings, 227 tests passing). This document captures the production hardening requirements across four sprints. The bugs range from structural dead code and missing abstractions (Sprint 0), to unwired safety components (Sprint 1), missing test coverage on money-critical paths (Sprint 2), and hot-path performance regressions (Sprint 3). Left unaddressed, these issues create namespace ambiguity risks, silent safety bypasses, untested balance-tracking logic, and kernel-level contention on every market tick.

---

## Bug Analysis

### Current Behavior (Defect)

**Sprint 0 — Structural Bugs**

1.1 WHEN both `ThatsMyQuant.Application.Risk` and `ThatsMyQuant.Application.Strategy` namespaces are imported in the same file THEN the system fails to compile due to ambiguous reference to `MemoryPortfolioManager`, because `02_Core/Application/Risk/MemoryPortfolioManager.cs` (65-line skeleton) duplicates the class name of the full 289-line implementation in `02_Core/Application/Strategy/MemoryPortfolioManager.cs`

1.2 WHEN `CircuitBreakerService` or `RiskAgent` need to be tested in isolation THEN the system cannot mock the portfolio dependency, because both constructors accept the concrete `MemoryPortfolioManager` class directly rather than an interface, and no `IPortfolioManager` interface exists in `06_Contracts/Interfaces/`

1.3 WHEN the market data WebSocket receives a tick THEN the system does not record a heartbeat, because `BinanceWebSocketService.OnTickReceived` never calls `HeartbeatMonitor.RecordHeartbeat()`, and neither `HeartbeatMonitor` nor `DeadMansSwitch` are registered in the DI container (`DependencyInjection.cs`), making both classes permanently dead code

1.4 WHEN the application starts on any machine other than the developer's laptop THEN the system fails to open the SQLite database, because `Program.cs` hardcodes the absolute path `C:\Users\parlak\Downloads\ThatsMyQuant\quant.db`

**Sprint 1 — Safety Wiring Gaps**

1.5 WHEN `ExecuteTradeHandler` calls `CircuitBreakerService.CheckOrderAllowed()` THEN the circuit breaker evaluates drawdown via `_portfolio.GetDrawdownPercent()` on a concrete `MemoryPortfolioManager` instance, meaning any future substitution or mock of the portfolio manager requires changing the constructor signature of `CircuitBreakerService`, because the dependency is not abstracted behind `IPortfolioManager`

**Sprint 2 — Missing Test Coverage**

1.6 WHEN `MemoryPortfolioManager` (Strategy namespace, 289 lines) processes balance updates, lock/unlock cycles, position open/close, execution reports, or reconciliation THEN the system has no automated verification of correctness, because zero unit tests exist for this class in `05_Tests/ThatsMyQuant.Tests/`

1.7 WHEN `FeatureEngineeringAgent.ProcessTickNatively()` computes RSI, EMA deviation, momentum, book imbalance, and volatility features THEN the system has no automated verification that feature values are mathematically correct or within valid ranges, because zero unit tests exist for the full feature computation contract

1.8 WHEN a signal flows through the full pipeline from `ProcessSignalHandler` → `SwarmOrchestrator` → `ExecuteTradeCommand` → trade result THEN the system has no end-to-end integration test validating the complete path with in-memory infrastructure

**Sprint 3 — Hot-Path Performance Bugs**

1.9 WHEN `FeatureEngineeringAgent.ProcessTickNatively()` is called on every market tick THEN the system acquires a kernel-level `lock(_syncLock)` mutex, causing thread contention and latency spikes under concurrent load, because the implementation uses `lock` instead of a lock-free double-buffer pattern

1.10 WHEN `VectorizedMathUtils.CalculateBatchSMA` computes a sliding window SMA over N prices with window size W THEN the system performs O(N×W) work by calling `SumVectorized` (which iterates the full window) for every position, instead of maintaining a running sum for O(N) total complexity

1.11 WHEN `HeartbeatMonitor.RecordHeartbeat()` is called on every tick and `TokenBucketRateLimiter` checks rate limits on every order THEN the system issues `DateTime.UtcNow` syscalls in the hot path, adding unnecessary latency, because these components use wall-clock time instead of `Environment.TickCount64` for latency-sensitive comparisons

---

### Expected Behavior (Correct)

**Sprint 0 — Structural Bugs**

2.1 WHEN both application namespaces are imported in the same file THEN the system SHALL compile without ambiguity, because the duplicate `Risk/MemoryPortfolioManager.cs` skeleton SHALL be deleted, leaving only the full `Strategy/MemoryPortfolioManager.cs` implementation

2.2 WHEN `CircuitBreakerService` or `RiskAgent` are constructed in tests THEN the system SHALL accept an `IPortfolioManager` interface, because an `IPortfolioManager` interface SHALL be extracted to `06_Contracts/Interfaces/`, `MemoryPortfolioManager` SHALL implement it, and both consumers SHALL depend on the interface

2.3 WHEN the application starts THEN the system SHALL register `DeadMansSwitch` and `HeartbeatMonitor` in the DI container, and `BinanceWebSocketService.OnTickReceived` SHALL call `HeartbeatMonitor.RecordHeartbeat()` so the safety components are live and operational

2.4 WHEN the application starts on any machine THEN the system SHALL resolve the SQLite database path using `Path.Combine(AppContext.BaseDirectory, "quant.db")` or an equivalent configuration-driven path, eliminating the hardcoded developer path

**Sprint 1 — Safety Wiring Gaps**

2.5 WHEN `CircuitBreakerService` is constructed THEN the system SHALL accept `IPortfolioManager` instead of the concrete `MemoryPortfolioManager`, so the circuit breaker's portfolio dependency is mockable and the safety layer is fully testable in isolation

**Sprint 2 — Missing Test Coverage**

2.6 WHEN `MemoryPortfolioManager` is exercised by tests THEN the system SHALL have unit tests covering: SetBalance/GetAvailableBalance round-trip, Lock/Unlock balance adjustment, OpenPosition/ClosePosition P&L for long and short sides, UpdateFromExecutionReport for partial fills/full fills/rejections, Reconcile syncing with external source and removing stale assets, concurrent AddBalance convergence under multi-thread load, and GetDrawdownPercent after a series of wins and losses

2.7 WHEN `FeatureEngineeringAgent` is exercised by tests THEN the system SHALL have unit tests covering: ProcessTickNatively producing RSI in [0,1], valid EMA deviation and MA bias; SafeFloat returning the fallback value for NaN and Infinity inputs; ApplyFoundationData correctly mapping BidAskImbalance and TickDirectionRatio; and CopyTo producing a snapshot identical to CurrentTensor

2.8 WHEN the full signal pipeline is exercised by an integration test THEN the system SHALL have a test using `MockBrokerService`, `MockChatCompletionService`, and in-memory SQLite that validates the complete path from signal creation through trade result

**Sprint 3 — Hot-Path Performance Bugs**

2.9 WHEN `FeatureEngineeringAgent.ProcessTickNatively()` is called concurrently THEN the system SHALL use a lock-free double-buffer swap pattern (e.g., `Interlocked.Exchange` on buffer references) instead of `lock(_syncLock)`, eliminating kernel mutex contention on the tick hot path

2.10 WHEN `VectorizedMathUtils.CalculateBatchSMA` computes a sliding window SMA THEN the system SHALL use a running-sum sliding window algorithm with O(1) work per step (O(N) total), eliminating the O(N×W) per-call cost of the current `SumVectorized`-per-window approach

2.11 WHEN `HeartbeatMonitor.RecordHeartbeat()` and `TokenBucketRateLimiter` perform time comparisons in the hot path THEN the system SHALL use `Environment.TickCount64` for latency-sensitive elapsed-time checks, and SHALL inject `TimeProvider` for components requiring testable wall-clock access

---

### Unchanged Behavior (Regression Prevention)

3.1 WHEN `MemoryPortfolioManager` (Strategy namespace) is resolved from DI THEN the system SHALL CONTINUE TO resolve the full 289-line implementation as the singleton, with all existing callers (`CircuitBreakerService`, `RiskAgent`) receiving the same instance

3.2 WHEN `ExecuteTradeHandler` processes a trade command THEN the system SHALL CONTINUE TO call `CircuitBreakerService.CheckOrderAllowed()` before dispatching to the exchange, preserving the existing circuit breaker guard on every order

3.3 WHEN `CircuitBreakerService.CheckOrderAllowed()` is called THEN the system SHALL CONTINUE TO enforce the sliding window rate limit, drawdown threshold, and manual halt checks with identical semantics after the `IPortfolioManager` interface extraction

3.4 WHEN the application starts with a valid Gemini API key THEN the system SHALL CONTINUE TO register the real `IChatCompletionService` via `AddGoogleAIGeminiChatCompletion`, and fall back to `MockChatCompletionService` when the key is absent

3.5 WHEN `FeatureEngineeringAgent.ProcessTickNatively()` is called with a NaN or Infinity price THEN the system SHALL CONTINUE TO guard against tensor corruption, preserving the existing `SafeFloat` protection after the lock-free refactor

3.6 WHEN `VectorizedMathUtils.CalculateBatchSMA` is called with a prices span shorter than the window size THEN the system SHALL CONTINUE TO return without writing results, preserving the existing early-exit guard

3.7 WHEN `BinanceWebSocketService` receives ticks THEN the system SHALL CONTINUE TO feed `FeatureEngineeringAgent.ProcessTickNatively()` via `TickPipeline` with unchanged tick data semantics after the heartbeat wiring is added

3.8 WHEN 227 existing tests are run after all hardening changes THEN the system SHALL CONTINUE TO pass all 227 tests with zero regressions

---

## Bug Condition Pseudocode

```pascal
// Bug 1: Duplicate class — namespace ambiguity
FUNCTION isBugCondition_B1(X: CompilationUnit)
  RETURN X.imports CONTAINS "ThatsMyQuant.Application.Risk"
     AND X.imports CONTAINS "ThatsMyQuant.Application.Strategy"
END FUNCTION

FOR ALL X WHERE isBugCondition_B1(X) DO
  result ← compile(X)
  ASSERT result.errors = 0
END FOR

FOR ALL X WHERE NOT isBugCondition_B1(X) DO
  ASSERT compile(F(X)) = compile(F'(X))  // single-namespace files unaffected
END FOR

// Bug 3: Dead safety components
FUNCTION isBugCondition_B3(X: TickEvent)
  RETURN HeartbeatMonitor NOT IN DI_Container
      OR X.source = BinanceWebSocketService
END FUNCTION

FOR ALL X WHERE isBugCondition_B3(X) DO
  result ← F'(X)
  ASSERT HeartbeatMonitor.LastHeartbeat updated
     AND DeadMansSwitch reachable via DI
END FOR

// Bug 4: Hardcoded DB path
FUNCTION isBugCondition_B4(X: RuntimeEnvironment)
  RETURN X.machine != "parlak-dev-laptop"
END FUNCTION

FOR ALL X WHERE isBugCondition_B4(X) DO
  result ← startApplication(X)
  ASSERT result.dbConnectionSuccess = true
END FOR

// Bug 9: Kernel mutex in hot path
FUNCTION isBugCondition_B9(X: ConcurrentTickBurst)
  RETURN X.concurrentCallers >= 2
END FUNCTION

FOR ALL X WHERE isBugCondition_B9(X) DO
  result ← measureLatency(ProcessTickNatively, X)
  ASSERT result.p99LatencyNs < threshold_no_lock
END FOR

// Bug 10: O(N×W) SMA
FUNCTION isBugCondition_B10(X: SMAInput)
  RETURN X.pricesLength > 1000 AND X.windowSize > 50
END FUNCTION

FOR ALL X WHERE isBugCondition_B10(X) DO
  result ← CalculateBatchSMA(X)
  ASSERT result.computeTime = O(X.pricesLength)  // not O(N×W)
END FOR

// Preservation: all non-buggy inputs
FOR ALL X WHERE NOT isBugCondition(X) DO
  ASSERT F(X) = F'(X)
END FOR
```
