# Requirements Document

## Introduction

This feature covers the remaining production hardening items for ThatsMyQuant, a C# HFT trading system. Four areas are addressed: (1) replacing the fixed 3-second WebSocket reconnect delay in `BinanceWebSocketService` with exponential backoff and jitter; (2) replacing `ConsoleAuditLogger` with a database-backed audit trail that survives restarts and is queryable; (3) wiring the existing `OperationalTelemetry` metrics and adding distributed tracing via OpenTelemetry OTLP export; and (4) expanding `HftExecutionService` test coverage from 3 tests to cover market-order fallback on timeout, queue position tracking, slippage projection accuracy, and urgency-based mode selection.

## Glossary

- **WebSocket_Reconnector**: The reconnection logic within `BinanceWebSocketService` responsible for re-establishing dropped WebSocket connections.
- **Backoff_Calculator**: The component that computes the delay before each reconnect attempt using exponential backoff with jitter.
- **DbAuditLogger**: The new database-backed implementation of `IAuditLogger` that replaces `ConsoleAuditLogger` in production.
- **AuditEntry**: A persisted record in the `AuditLog` database table representing a single security event or order execution event.
- **Telemetry_Exporter**: The OpenTelemetry SDK pipeline that exports metrics and traces from `OperationalTelemetry` to an OTLP endpoint.
- **ActivitySource**: The `System.Diagnostics.ActivitySource` instance used to create distributed trace spans for the signal→trade pipeline.
- **HftExecutionService**: The existing service in `02_Core/Application/Strategy/HftExecutionService.cs` that routes signals to limit or market execution.
- **LimitOrderExecutionConfig**: The configuration record controlling urgency threshold, max spread tolerance, max order age, and retry limits for `HftExecutionService`.

---

## Requirements

### Requirement 1: WebSocket Reconnection with Exponential Backoff

**User Story:** As a system operator, I want the Binance WebSocket connection to reconnect automatically with exponential backoff and jitter, so that transient network failures are recovered without thundering-herd effects and persistent failures trigger an alert.

#### Acceptance Criteria

1. WHEN a WebSocket connection is lost, THE `WebSocket_Reconnector` SHALL wait before attempting to reconnect using a delay computed as `min(baseDelay * 2^attempt, 60s)` where `baseDelay` is 1 second.
2. WHEN computing a reconnect delay, THE `Backoff_Calculator` SHALL add a random jitter in the range `[0, delay * 0.3]` to the base exponential delay before returning the final delay value.
3. WHEN the reconnect attempt number is 0, THE `Backoff_Calculator` SHALL return a delay of 1 second (before jitter).
4. WHEN the reconnect attempt number is 1, THE `Backoff_Calculator` SHALL return a delay of 2 seconds (before jitter).
5. WHEN the reconnect attempt number is 4, THE `Backoff_Calculator` SHALL return a delay of 16 seconds (before jitter).
6. WHEN the computed exponential delay exceeds 60 seconds, THE `Backoff_Calculator` SHALL cap the delay at 60 seconds (before jitter).
7. WHEN the number of consecutive failed reconnect attempts reaches the configured `MaxReconnectAttempts` value, THE `WebSocket_Reconnector` SHALL invoke `IAlertingService.SendTradeAlertAsync` with a message indicating the WebSocket is unrecoverable.
8. WHEN a reconnect attempt succeeds, THE `WebSocket_Reconnector` SHALL reset the consecutive failure counter to 0.
9. WHILE a reconnect is in progress, THE `WebSocket_Reconnector` SHALL log each attempt at Warning level including the attempt number and computed delay.
10. FOR ALL attempt numbers `n` in the range `[0, 10]`, the delay computed by `Backoff_Calculator` SHALL satisfy `delay >= 1s` and `delay <= 60s + 60s * 0.3` (cap plus maximum jitter).

### Requirement 2: Persistent Audit Trail

**User Story:** As a compliance officer, I want all order executions and security events to be persisted to the database, so that the audit trail survives service restarts and can be queried by signal ID, trade ID, or time range.

#### Acceptance Criteria

1. THE `DbAuditLogger` SHALL implement `IAuditLogger` and persist each call to `LogOrderExecutionAsync` as an `AuditEntry` row in the `AuditLog` database table.
2. THE `DbAuditLogger` SHALL persist each call to `LogSecurityEventAsync` as an `AuditEntry` row in the `AuditLog` database table.
3. WHEN `LogOrderExecutionAsync` is called, THE `DbAuditLogger` SHALL store the `orderId`, `strategyId`, `justification`, event type `"OrderExecution"`, and a UTC timestamp in the `AuditEntry`.
4. WHEN `LogSecurityEventAsync` is called, THE `DbAuditLogger` SHALL store the `principalId`, `role`, `actionType`, `details`, event type `"SecurityEvent"`, and a UTC timestamp in the `AuditEntry`.
5. THE `AuditEntry` table SHALL include an indexed column on `CorrelationId` to support queries by signal ID or trade ID.
6. THE `AuditEntry` table SHALL include an indexed column on `TimestampUtc` to support queries by time range.
7. WHEN the database write fails, THE `DbAuditLogger` SHALL log the failure at Error level and rethrow the exception so the caller is aware of the persistence failure.
8. THE `DbAuditLogger` SHALL be registered in the DI container as the singleton implementation of `IAuditLogger`, replacing `ConsoleAuditLogger` in the `Data.Persistence` `DependencyInjection` registration.
9. WHEN `DbAuditLogger` is queried by `CorrelationId`, THE `DbAuditLogger` SHALL return all `AuditEntry` records matching that `CorrelationId` ordered by `TimestampUtc` ascending.
10. WHEN `DbAuditLogger` is queried by time range `[from, to]`, THE `DbAuditLogger` SHALL return all `AuditEntry` records where `TimestampUtc >= from` and `TimestampUtc <= to`, ordered by `TimestampUtc` ascending.

### Requirement 3: OpenTelemetry OTLP Export

**User Story:** As a platform engineer, I want the existing `OperationalTelemetry` metrics and signal→trade spans exported via OTLP, so that Prometheus and Grafana can visualise system health without code changes to the metric recording sites.

#### Acceptance Criteria

1. THE `Telemetry_Exporter` SHALL register the OpenTelemetry SDK with the `MeterProvider` configured to listen to the `ThatsMyQuant.*` meter name prefix used by `OperationalTelemetry`.
2. THE `Telemetry_Exporter` SHALL export metrics to the OTLP endpoint configured in `appsettings.json` under the key `OpenTelemetry:OtlpEndpoint`.
3. WHEN `OpenTelemetry:OtlpEndpoint` is absent or empty in configuration, THE `Telemetry_Exporter` SHALL fall back to the Prometheus HTTP scrape exporter on port 9464.
4. THE `ActivitySource` named `"ThatsMyQuant.Execution"` SHALL create a span when `HftExecutionService.ExecuteAsync` is called, with the span name `"hft.execute"` and a tag `signal.symbol` set to the signal's symbol.
5. THE `ActivitySource` named `"ThatsMyQuant.Execution"` SHALL create a span when `HftExecutionService.ExecuteLimitOrderAsync` is called, with the span name `"hft.execute.limit"` and a tag `signal.symbol` set to the signal's symbol.
6. WHEN a span is created for an execution, THE `ActivitySource` SHALL set the span status to `Error` and record the exception if `IExchangeService.ExecuteOrderAsync` throws.
7. THE `Telemetry_Exporter` SHALL register the `TracerProvider` configured to export traces to the same OTLP endpoint as metrics.
8. THE `Telemetry_Exporter` SHALL be registered via the existing `DependencyInjection.cs` extension in `02_Core/Application/Common/` so no changes to `Program.cs` are required beyond calling the existing extension method.
9. WHEN the OTLP exporter is configured, THE `Telemetry_Exporter` SHALL set the OTLP export protocol to `Grpc` by default, overridable via `OpenTelemetry:OtlpProtocol` in configuration.

### Requirement 4: HftExecutionService Additional Tests

**User Story:** As a developer, I want comprehensive tests for `HftExecutionService` covering timeout-driven market fallback, queue position tracking, slippage projection accuracy, and urgency-based mode selection, so that regressions in execution routing logic are caught before deployment.

#### Acceptance Criteria

1. WHEN a limit order is placed and the order age exceeds `LimitOrderExecutionConfig.MaxOrderAgeMs`, THE `HftExecutionService` SHALL cancel the limit order and fall back to a market execution via `ExecuteAsync`.
2. WHEN `ExecuteLimitOrderAsync` is called with an order book where the computed queue position percentile is below `LimitOrderExecutionConfig.MinQueuePositionPercentile`, THE `HftExecutionService` SHALL adjust the limit price toward the best opposing side before submitting.
3. WHEN `ExecuteAsync` is called and the actual fill price differs from the signal price, THE `HftExecutionService` SHALL record `EntrySlippagePercent` as `|fillPrice - signalPrice| / signalPrice * 100`.
4. WHEN `ExecuteLimitOrderAsync` is called with a signal whose computed urgency is below `LimitOrderExecutionConfig.UrgencyThreshold`, THE `HftExecutionService` SHALL submit a limit order and set `ExecutionMode` to `"LIMIT_MAKER"`.
5. WHEN `ExecuteLimitOrderAsync` is called with a signal whose computed urgency meets or exceeds `LimitOrderExecutionConfig.UrgencyThreshold`, THE `HftExecutionService` SHALL fall back to market execution and set `ExecutionMode` to `"REAL"`.
6. FOR ALL signal confidence values in `[0, 100]`, order flow imbalance values in `[0.0, 1.0]`, and wall age values in `[0, 10000]`, the urgency computed by `HftExecutionService` SHALL be in the range `[0.0, 1.0]`.
7. FOR ALL valid `LimitOrderExecutionConfig` instances with `MaxRetries >= 1`, after `MaxRetries` calls to `ManageLimitOrderAsync` for the same order, THE `HftExecutionService` SHALL remove the order from `GetActiveOrders()`.
