# Live-Readiness Hardening Sprints

## Status Legend

- `done`: already implemented in the current repo
- `in_progress`: being implemented in this pass
- `planned`: valid gap, not implemented yet
- `stale`: the original finding is no longer accurate

## Sprint 0

### 0.1 Shared database path strategy

- Status: `in_progress`
- Goal: stop relying on process working directory for SQLite files and make local/desktop runs stable across machines.
- Current pass:
  - move SQLite connection-string resolution into a shared path resolver
  - keep PostgreSQL override behavior intact

### 0.2 Portfolio manager collision cleanup

- Status: `stale`
- Notes:
  - there is only one `MemoryPortfolioManager` class in the repo now
  - the remaining architectural split is between:
    - strategy-level `IPortfolioManager`
    - venue-aware infrastructure `ISharedPortfolioStateManager`
- Follow-up:
  - document ownership boundaries and reduce naming ambiguity later

### 0.3 Console audit logger

- Status: `done`
- Implementations:
  - `ConsoleAuditLogger`
  - `DbAuditLogger`
- Follow-up:
  - keep `DbAuditLogger` as primary infra registration
  - retain console logger as fallback/local surface

## Sprint 1

### 1.1 Circuit breaker in execution path

- Status: `done`
- Notes:
  - already enforced in `ExecuteTradeHandler`
  - already enforced again in `ExecutionWorker`

### 1.2 Heartbeat monitor activation

- Status: `in_progress`
- Goal: make heartbeat auditing active instead of passive registration only.
- Current pass:
  - add watchdog hosted service
  - wire dead-man switch escalation into circuit-breaker halt flow

### 1.3 Dead-man switch audit trail

- Status: `in_progress`
- Goal: ensure heartbeat-triggered safety actions produce an auditable halt event and execution halt.

## Sprint 2

### 2.1 Money-path tests

- Status: `planned`
- Scope:
  - `MemoryPortfolioManager` P&L math
  - drawdown computation
  - reconcile/update flows

## Sprint 3

### 3.1 Hot-path lock reduction

- Status: `planned`
- Scope:
  - `FeatureEngineeringAgent`
  - `RiskAgent` token bucket / tick-facing guards

### 3.2 SMA optimization

- Status: `planned`
- Scope:
  - replace `O(N×W)` SMA implementation with rolling-window accumulation

### 3.3 Time-source cleanup

- Status: `planned`
- Scope:
  - reduce repeated wall-clock syscalls in hot paths
  - prefer monotonic tick-based timing where appropriate

## This Pass

- Add shared SQLite path resolution.
- Add active heartbeat watchdog execution.
- Escalate dead-man switch into circuit-breaker halt.
- Leave Sprint 2/3 queued for a focused follow-up pass.
