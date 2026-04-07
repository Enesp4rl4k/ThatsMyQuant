# Phase 2 Execution Hardening Plan

## Scope

This phase picks up the execution-hardening work from `thatsmyquant_analysis.md` without stepping on the Phase 1 safety fixes that are already underway elsewhere in the repo.

## Delivered In This Slice

1. Startup reconciliation against venue open orders.
2. Event-driven execution handoff inside the worker via `Channel<Signal>`.
3. Deterministic idempotency/client-order IDs derived from `Signal.Id`.
4. Trade state projection from venue open-order snapshots and execution reports.
5. Initial `HftExecutionService` unit coverage for market and limit execution paths.

## Implementation Notes

1. `ExecutionSignalQueue` is the new in-process handoff path.
   The repository enqueues `Queued` signals immediately, while the worker keeps a polling fallback to catch anything already persisted or missed during restarts.

2. `ExecutionOrderIdentity` uses `Signal.Id.ToString("N")` as the venue client-order ID.
   This gives deterministic idempotency without adding schema churn to the `Trades` table.

3. `TradeExecutionSyncService` is the Phase 2 state-sync bridge.
   It can reconcile startup open orders today and is ready to consume live execution reports when the exchange sync implementation is promoted beyond the current stub.

## Remaining Phase 2 Backlog

1. Real exchange WebSocket execution-sync implementation with reconnect/backoff.
2. Full partial-fill lifecycle handling for incremental fill events across all venues.
3. Expanded `HftExecutionService` tests for retries, repricing, and cancellation paths.
4. Venue-specific orphan-order remediation beyond logging and state sync.
