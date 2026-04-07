# Phase 3 Observability & Ops Plan

## Scope

This phase starts the operational hardening work needed before unattended running. The first implementation slice focuses on host visibility and operator traceability without forcing the larger persistence migration yet.

## Delivered In This Slice

1. Health and readiness endpoints on the web services plus management endpoints for the worker services.
2. Lightweight runtime metrics snapshots exposed at `/metrics`.
3. Correlation-aware structured logging across the signal and execution path.
4. Runtime readiness state shared by hosts and surfaced through observability endpoints.

## Endpoints

1. `Listener.Api`
   Same-host `/health`, `/ready`, and `/metrics`.
2. `Gateway.Api`
   Same-host `/health`, `/ready`, and `/metrics`.
3. `Execution.Worker`
   Management host on `http://127.0.0.1:18081`.
4. `QuantEngine.Worker`
   Management host on `http://127.0.0.1:18082`.

## Remaining Phase 3 Backlog

1. PostgreSQL production persistence path and migration guidance.
2. OTLP/Prometheus exporter wiring on top of the new meter instrumentation.
3. Dashboard display for health/readiness and circuit-breaker operational state.
4. Correlation propagation into every exchange/websocket hop and outbound alert transport.
