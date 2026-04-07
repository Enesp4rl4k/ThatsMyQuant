# Implementation Plan: Execution Hardening

## Overview

Incremental implementation of four production hardening improvements: exponential backoff for WebSocket reconnection, database-backed audit logging, OpenTelemetry OTLP wiring, and expanded HftExecutionService test coverage.

## Tasks

- [x] 1. BackoffCalculator + BinanceWebSocketService reconnect fix
  - [x] 1.1 Create `BackoffCalculator` static class
    - Create `02_Core/Application/Resilience/BackoffCalculator.cs`
    - Implement `Compute(int attempt, double jitterFactor = 0.3)` using `Math.Min(Math.Pow(2, attempt), 60.0)` base + `Random.Shared` jitter
    - _Requirements: 1.1, 1.2, 1.3, 1.4, 1.5, 1.6_

  - [x] 1.2 Write property test for backoff bounds (P1)
    - Use `[Property(MaxTest = 20)]` with `Gen.Choose(0, 10)` for attempt number
    - Assert `delay >= TimeSpan.FromSeconds(1)` and `delay <= TimeSpan.FromSeconds(78)`
    - Tag: `// Feature: execution-hardening, Property 1: backoff delay bounds`
    - _Requirements: 1.1, 1.2, 1.6, 1.10_

  - [x] 1.3 Write unit tests for BackoffCalculator
    - `BackoffCalculator_Attempt0_Returns1Second` (jitterFactor=0)
    - `BackoffCalculator_Attempt1_Returns2Seconds` (jitterFactor=0)
    - `BackoffCalculator_Attempt4_Returns16Seconds` (jitterFactor=0)
    - `BackoffCalculator_HighAttempt_CapsAt60Seconds` (jitterFactor=0)
    - _Requirements: 1.3, 1.4, 1.5, 1.6_

  - [x] 1.4 Update `BinanceWebSocketService` to use exponential backoff
    - Add `int _reconnectAttempt` field and optional `IAlertingService? _alertingService` constructor parameter
    - Extract `ConnectCoreAsync` from existing `ConnectAsync` body
    - Add `RunWithReconnectAsync` wrapping the connect loop with `BackoffCalculator.Compute(_reconnectAttempt)` delay
    - Reset `_reconnectAttempt = 0` on successful connect; increment on failure
    - Call `_alertingService?.SendTradeAlertAsync(...)` when `attempt >= _maxReconnectAttempts`
    - Log each attempt at `Warning` level with attempt number and delay
    - _Requirements: 1.1, 1.2, 1.7, 1.8, 1.9_

  - [-] 1.5 Write unit tests for BinanceWebSocketService reconnect behaviour
    - `WebSocketReconnector_ExhaustsAttempts_SendsAlert` — verifies alerting service is called
    - `WebSocketReconnector_LogsWarning_OnEachAttempt` — verifies warning log per attempt
    - _Requirements: 1.7, 1.9_

- [~] 2. DbAuditLogger + AuditEntry entity
  - [~] 2.1 Create `AuditEntry` entity
    - Create `03_Infrastructure/Data.Persistence/AuditEntry.cs`
    - Properties: `Guid Id`, `string EventType`, `string CorrelationId`, `DateTime TimestampUtc`, `string Payload`
    - _Requirements: 2.3, 2.4, 2.5, 2.6_

  - [~] 2.2 Update `QuantDbContext` to include `AuditLog` DbSet
    - Add `public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();`
    - Add `OnModelCreating` configuration: PK, `HasMaxLength(30)` on EventType, `HasMaxLength(120)` on CorrelationId, `HasColumnType("TEXT")` on Payload, indexes on `CorrelationId` and `TimestampUtc`
    - _Requirements: 2.5, 2.6_

  - [~] 2.3 Create `DbAuditLogger`
    - Create `03_Infrastructure/Data.Persistence/Services/DbAuditLogger.cs`
    - Implement `IAuditLogger` using `IDbContextFactory<QuantDbContext>` (one context per write)
    - Implement `LogOrderExecutionAsync`: serialize `{orderId, strategyId, justification}` as JSON payload, EventType = `"OrderExecution"`
    - Implement `LogSecurityEventAsync`: serialize `{principalId, role, actionType, details}` as JSON payload, EventType = `"SecurityEvent"`
    - Implement `QueryByCorrelationIdAsync` and `QueryByTimeRangeAsync` returning results ordered by `TimestampUtc` ascending
    - On DB write failure: log at `Error` level and rethrow
    - _Requirements: 2.1, 2.2, 2.3, 2.4, 2.7, 2.9, 2.10_

  - [~] 2.4 Update DI registration in `Data.Persistence`
    - In `03_Infrastructure/Data.Persistence/DependencyInjection.cs`, replace `services.AddSingleton<IAuditLogger, ConsoleAuditLogger>()` with `services.AddSingleton<IAuditLogger, DbAuditLogger>()`
    - Ensure `AddDbContextFactory<QuantDbContext>` is registered (add if absent)
    - _Requirements: 2.8_

  - [~] 2.5 Write property test for order execution audit round-trip (P3)
    - Use `[Property(MaxTest = 20)]` with `Arb.Generate<string>` for orderId, strategyId, justification
    - Use in-memory SQLite (`UseInMemoryDatabase` or SQLite in-memory connection string)
    - Assert row exists with `EventType = "OrderExecution"`, `CorrelationId = orderId`, payload contains all three values
    - Tag: `// Feature: execution-hardening, Property 3: order execution audit round-trip`
    - _Requirements: 2.1, 2.3_

  - [~] 2.6 Write property test for security event audit round-trip (P4)
    - Use `[Property(MaxTest = 20)]` with `Arb.Generate<string>` for principalId, role, actionType, details
    - Assert row exists with `EventType = "SecurityEvent"`, `CorrelationId = principalId`, payload contains all four values
    - Tag: `// Feature: execution-hardening, Property 4: security event audit round-trip`
    - _Requirements: 2.2, 2.4_

  - [~] 2.7 Write property test for CorrelationId query ordering (P5)
    - Use `[Property(MaxTest = 20)]` with random lists of `AuditEntry` with varying CorrelationIds
    - Assert query returns only matching entries, ordered by `TimestampUtc` ascending
    - Tag: `// Feature: execution-hardening, Property 5: CorrelationId query returns only matching entries in order`
    - _Requirements: 2.9_

  - [~] 2.8 Write property test for time range query ordering (P6)
    - Use `[Property(MaxTest = 20)]` with random `AuditEntry` lists and random `[from, to]` ranges
    - Assert only entries where `TimestampUtc >= from && TimestampUtc <= to`, ordered ascending
    - Tag: `// Feature: execution-hardening, Property 6: time range query returns only in-range entries in order`
    - _Requirements: 2.10_

  - [~] 2.9 Write unit tests for DbAuditLogger
    - `DbAuditLogger_WriteFailure_LogsErrorAndRethrows` — mock factory throws, verify error log and exception propagation
    - `DbAuditLogger_IsRegisteredAsSingleton` — verify DI registration lifetime
    - _Requirements: 2.7, 2.8_

- [~] 3. OpenTelemetry OTLP wiring
  - [~] 3.1 Add NuGet packages to the infrastructure/application project
    - Add to the relevant `.csproj`: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Exporter.Prometheus.AspNetCore` (all `Version="1.*"`)
    - _Requirements: 3.1, 3.2_

  - [~] 3.2 Add `ActivitySource` and spans to `HftExecutionService`
    - Add `private static readonly ActivitySource _activitySource = new ActivitySource("ThatsMyQuant.Execution");`
    - In `ExecuteAsync`: wrap with `_activitySource.StartActivity("hft.execute")`, set tag `signal.symbol`, set `Error` status and record exception on throw
    - In `ExecuteLimitOrderAsync`: wrap with `_activitySource.StartActivity("hft.execute.limit")`, set tag `signal.symbol`
    - _Requirements: 3.4, 3.5, 3.6_

  - [~] 3.3 Create `AddThatsMyQuantTelemetry` extension in `DependencyInjection.cs`
    - Add to `02_Core/Application/Common/DependencyInjection.cs`
    - Configure `MeterProvider` with `AddMeter("ThatsMyQuant.*")`
    - Configure `TracerProvider` with `AddSource("ThatsMyQuant.Execution")`
    - If `OpenTelemetry:OtlpEndpoint` is set: add OTLP exporter for both metrics and traces, protocol from `OpenTelemetry:OtlpProtocol` (default `Grpc`)
    - If endpoint is absent: fall back to `AddPrometheusExporter()` on port 9464
    - Update `AddThatsMyQuantApplication` to accept optional `IConfiguration?` and call `AddThatsMyQuantTelemetry` when non-null
    - _Requirements: 3.1, 3.2, 3.3, 3.7, 3.8, 3.9_

  - [~] 3.4 Write property test for ExecuteAsync span symbol tag (P7)
    - Use `[Property(MaxTest = 20)]` with random non-empty `Symbol` strings
    - Use `ActivityListener` to capture activities from `"ThatsMyQuant.Execution"` source
    - Assert activity name is `"hft.execute"` and tag `signal.symbol` equals the signal's symbol
    - Tag: `// Feature: execution-hardening, Property 7: ExecuteAsync span carries signal symbol`
    - _Requirements: 3.4_

  - [~] 3.5 Write property test for ExecuteLimitOrderAsync span symbol tag (P8)
    - Use `[Property(MaxTest = 20)]` with random non-empty `Symbol` strings
    - Assert activity name is `"hft.execute.limit"` and tag `signal.symbol` equals the signal's symbol
    - Tag: `// Feature: execution-hardening, Property 8: ExecuteLimitOrderAsync span carries signal symbol`
    - _Requirements: 3.5_

  - [~] 3.6 Write unit tests for telemetry wiring
    - `Telemetry_FallsBackToPrometheus_WhenOtlpEndpointMissing` — verify Prometheus exporter registered when config key absent
    - `Telemetry_SetsGrpcProtocol_ByDefault` — verify default OTLP protocol is Grpc
    - `Telemetry_SetsSpanStatusError_WhenExchangeThrows` — mock exchange throws, verify span status set to Error
    - _Requirements: 3.3, 3.6, 3.9_

- [~] 4. HftExecutionService additional tests
  - [~] 4.1 Write property test for slippage formula correctness (P9)
    - Use `[Property(MaxTest = 20)]` with `Gen.Positive<decimal>` for fill price and signal price
    - Assert `trade.EntrySlippagePercent == Math.Abs(fillPrice - signalPrice) / signalPrice * 100`
    - Tag: `// Feature: execution-hardening, Property 9: slippage formula correctness`
    - _Requirements: 4.3_

  - [~] 4.2 Write property test for urgency range invariant (P10)
    - Use `[Property(MaxTest = 20)]` with `Gen.Choose(0, 100)` for confidence, `Gen.Double` in `[0.0, 1.0]` for OFI, `Gen.Choose(0, 10000)` for wall age
    - Expose or test `ComputeUrgency` via a signal constructed with those values
    - Assert urgency result is in `[0.0, 1.0]`
    - Tag: `// Feature: execution-hardening, Property 10: urgency range invariant`
    - _Requirements: 4.6_

  - [~] 4.3 Write property test for max retries removes order (P11)
    - Use `[Property(MaxTest = 20)]` with `Gen.Choose(1, 10)` for `MaxRetries`
    - Call `ManageLimitOrderAsync` exactly `MaxRetries` times for the same order (with age > `MaxOrderAgeMs` to trigger retry path)
    - Assert `GetActiveOrders()` does not contain that order after `MaxRetries` calls
    - Tag: `// Feature: execution-hardening, Property 11: max retries removes order from active set`
    - _Requirements: 4.7_

  - [~] 4.4 Write unit tests for market fallback on order age timeout
    - `HftExecution_FallsBackToMarket_WhenOrderAgeExceedsMax` — set `MaxOrderAgeMs` low, advance time, verify `ExecuteAsync` is called
    - _Requirements: 4.1_

  - [~] 4.5 Write unit tests for queue position price adjustment
    - `HftExecution_AdjustsLimitPrice_WhenQueuePositionBelowMin` — construct order book where queue position percentile < `MinQueuePositionPercentile`, verify submitted price is adjusted toward opposing side
    - _Requirements: 4.2_

  - [~] 4.6 Write unit tests for urgency-based routing
    - `HftExecution_SetsLimitMakerMode_WhenUrgencyBelowThreshold` — low confidence/OFI/wallAge signal, verify `ExecutionMode = "LIMIT_MAKER"`
    - `HftExecution_FallsBackToMarket_WhenUrgencyHigh` — already exists, verify it still passes
    - _Requirements: 4.4, 4.5_

- [~] 5. Final checkpoint — build and run all tests
  - Ensure all tests pass, ask the user if questions arise.
  - Run `dotnet build` and `dotnet test --no-build` to confirm zero build errors and all tests green.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Property tests use `[Property(MaxTest = 20)]` for speed; increase to 100+ for thorough CI runs
- Properties P3–P6 require an in-memory SQLite context — use `Microsoft.EntityFrameworkCore.Sqlite` already present in the test project
- Properties P7–P8 require an `ActivityListener` registered before calling the SUT; dispose it after each test
- Each task references specific requirements for traceability
