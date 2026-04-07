# Implementation Plan: Live Trading Safety

## Overview

Eight surgical fixes to wire existing safety infrastructure into the live execution path. Each task builds on the previous, with property tests placed immediately after the implementation they validate. No new architectural layers — only wiring what exists and filling three stubs.

## Tasks

- [x] 1. Rewrite RiskSupervisorService as BackgroundService with real drawdown monitoring
  - Replace the class declaration: `public sealed class RiskSupervisorService : BackgroundService`
  - Remove the fire-and-forget constructor call `_ = StartMonitoringLoop()`
  - Replace `MemoryPortfolioManager _portfolio` field with `IPortfolioManager _portfolio`
  - Add fields: `_maxDrawdownPercent`, `_isHalted` (volatile bool), `_initialBalance`, `_peakBalance`, `_currentDrawdownPercent`
  - Add public property `decimal CurrentDrawdownPercent => _currentDrawdownPercent`
  - Implement `protected override async Task ExecuteAsync(CancellationToken stoppingToken)` with the drawdown loop from the design
  - Implement `TriggerEmergencyHaltAsync`: set `_isHalted = true`, `await _execution.CancelAllOrdersAsync()`, wrap in try/catch logging Critical on failure
  - Update DI registration in `02_Core/Application/Common/DependencyInjection.cs`: `services.AddHostedService<RiskSupervisorService>()`
  - _Requirements: 1.1, 1.2, 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 3.1, 3.2, 3.3, 3.4, 3.6_

  - [x] 1.1 Write property test for drawdown formula invariant
    - **Property 3: Drawdown formula invariant** — `(peak - current) / peak * 100` always in `[0, 100]`
    - File: `05_Tests/ThatsMyQuant.Tests/Risk/RiskSupervisorServiceTests.cs`
    - Use FsCheck `[Property]` attribute, min 100 iterations
    - **Validates: Requirements 2.4**

  - [x] 1.2 Write property test for monotone peak
    - **Property 4: Monotone peak** — peak never decreases across any balance sequence
    - File: `05_Tests/ThatsMyQuant.Tests/Risk/RiskSupervisorServiceTests.cs`
    - Use FsCheck `[Property]` attribute, min 100 iterations
    - **Validates: Requirements 2.2**

  - [x] 1.3 Write property test for loop resilience
    - **Property 5: Loop resilience** — exception in `GetAvailableBalance` does not kill the monitoring loop
    - File: `05_Tests/ThatsMyQuant.Tests/Risk/RiskSupervisorServiceTests.cs`
    - Use FsCheck `[Property]` attribute, min 100 iterations
    - **Validates: Requirements 3.4**

  - [x] 1.4 Write unit tests for RiskSupervisorService
    - `TriggerEmergencyHaltAsync_SetsHaltedAndCallsCancelAllOrders` — Req 1.1
    - `TriggerEmergencyHaltAsync_WhenCancelThrows_HaltFlagRemainsTrue` — Req 1.2
    - `TriggerEmergencyHaltAsync_CalledTwice_CancelAllOrdersCalledOnce` — Property 1 (idempotence)
    - `MonitoringLoop_WhenDrawdownExceedsThreshold_TriggersHalt` — Req 2.3
    - `MonitoringLoop_WhenPeakIsZero_SkipsDrawdownCheck` — Req 2.5
    - `StartAsync_RecordsInitialBalance` — Req 2.1
    - File: `05_Tests/ThatsMyQuant.Tests/Risk/RiskSupervisorServiceTests.cs`
    - _Requirements: 1.1, 1.2, 2.1, 2.3, 2.5_

- [x] 2. Wire CircuitBreakerService into ExecutionWorker hot path
  - Add `CircuitBreakerService _circuitBreaker` field and inject via constructor (remove `ExecutionSignalQueue` from constructor, add `CircuitBreakerService circuitBreaker`)
  - Add halt guard at top of `PollAndProcessSignalsAndTrades`: if `_circuitBreaker.IsHalted` log warning and return
  - In `ProcessSingleSignal`: call `_circuitBreaker.CheckOrderAllowed()` before MediatR dispatch; if blocked set `signal.Status = TradeStatus.Rejected`, append block reason to `signal.Reasoning`, log warning, `await repo.UpdateAsync(signal)`, return
  - On successful trade: call `_circuitBreaker.RecordSuccess()`
  - On exception in catch block: call `_circuitBreaker.RecordError(ex)`
  - _Requirements: 1.3, 4.1, 4.2, 4.3, 4.4, 4.5, 4.6_

  - [x] 2.1 Write property test for no-bypass
    - **Property 6: No-bypass** — when `CheckOrderAllowed` returns false, `ExecuteOrderAsync` is never called
    - File: `05_Tests/ThatsMyQuant.Tests/Worker/ExecutionWorkerCircuitBreakerTests.cs`
    - Use FsCheck `[Property]` attribute, min 100 iterations
    - **Validates: Requirements 4.1, 4.2**

  - [x] 2.2 Write unit tests for ExecutionWorker circuit breaker integration
    - `ProcessSingleSignal_WhenCircuitBreakerBlocks_SignalRejectedNoMediatR` — Property 6
    - `ProcessSingleSignal_WhenCircuitBreakerAllows_MediatRDispatched` — Req 4.3
    - `ProcessSingleSignal_OnSuccess_RecordsSuccess` — Req 4.4
    - `ProcessSingleSignal_OnException_RecordsError` — Req 4.5
    - `PollAndProcess_WhenHalted_SkipsDispatch` — Req 1.3
    - File: `05_Tests/ThatsMyQuant.Tests/Worker/ExecutionWorkerCircuitBreakerTests.cs`
    - _Requirements: 1.3, 4.1, 4.2, 4.3, 4.4, 4.5_

- [x] 3. Checkpoint — ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 4. Add OKX fill tracking via PollFillStatusAsync
  - Add private method `PollFillStatusAsync(string ordId, string instId, decimal requestedQty, CancellationToken ct)` returning `(decimal FilledQty, decimal RemainingQty, TradeStatus Status)`
  - Implement poll loop: max 3 attempts, 500ms delay, GET `/api/v5/trade/order?instId={instId}&ordId={ordId}` with full auth headers
  - Parse `fillSz` and `state` from `data[0]`; map `"filled"` → `(fillSz, 0, Executed)`, `"partially_filled"` → `(fillSz, requested-fillSz, Pending)`, `"live"` → `(0, requested, Pending)`
  - On HTTP error or exception: log error, return `(0, requestedQty, Pending)` — do not throw
  - In `ExecuteOrderAsync`: after parsing `ordId` from placement response, call `PollFillStatusAsync` and populate `Trade.FilledQuantity`, `Trade.RemainingQuantity`, `Trade.Quantity`, `Trade.Status`
  - _Requirements: 5.1, 5.2, 5.3, 5.4, 5.5, 5.6_

  - [x] 4.1 Write property test for fill quantity invariant
    - **Property 7: Fill quantity invariant** — `FilledQty + RemainingQty == RequestedQty` for all accepted orders
    - File: `05_Tests/ThatsMyQuant.Tests/Execution/OkxExecutionServiceTests.cs`
    - Use FsCheck `[Property]` attribute, min 100 iterations
    - **Validates: Requirements 5.2, 5.3**

  - [x] 4.2 Write unit tests for OKX fill tracking
    - `ExecuteOrderAsync_WhenOrderFilled_FilledQtyEqualsRequestedQty` — Property 7
    - `ExecuteOrderAsync_WhenOrderPartiallyFilled_QuantitiesSumToRequested` — Property 7
    - `ExecuteOrderAsync_WhenPollFails_ReturnsTradeWithoutThrowing` — Property 8 (no-throw on poll failure)
    - `ExecuteOrderAsync_WhenOrderLive_FilledQtyIsZero` — Req 5.4
    - File: `05_Tests/ThatsMyQuant.Tests/Execution/OkxExecutionServiceTests.cs`
    - _Requirements: 5.2, 5.3, 5.4, 5.6_

- [x] 5. Move Telegram secrets to IOptions<TelegramOptions>
  - Create `06_Contracts/Options/TelegramOptions.cs` with `string BotToken` and `string ChatId` properties (alongside existing `RiskGovernorOptions`)
  - In `TelegramAlertService`: remove `private const string BotToken` and `private const string ChatId` fields
  - Add `IOptions<TelegramOptions> _options` field; inject via constructor
  - Replace `if (string.IsNullOrEmpty(BotToken))` guard with `var opts = _options.Value; if (string.IsNullOrEmpty(opts.BotToken))`
  - Replace all uses of `BotToken` and `ChatId` constants with `opts.BotToken` and `opts.ChatId`
  - Register in `03_Infrastructure/Data.Persistence/DependencyInjection.cs`: `services.Configure<TelegramOptions>(configuration.GetSection("Telegram"))`
  - _Requirements: 7.1, 7.2, 7.3, 7.4, 7.5_

  - [x] 5.1 Write unit tests for TelegramAlertService options injection
    - `SendTradeAlertAsync_WhenBotTokenEmpty_LogsWithoutHttpCall` — Property 12 (no-throw on missing config)
    - `SendTradeAlertAsync_WhenBotTokenSet_SendsHttpRequest` — Req 7.3
    - File: `05_Tests/ThatsMyQuant.Tests/Infrastructure/TelegramAlertServiceTests.cs`
    - _Requirements: 7.3, 7.4_

- [x] 6. Replace raw Binance API key fields with ApiVault
  - Remove `private readonly string _apiKey`, `private readonly byte[] _apiSecretBytes`, and `private readonly HMACSHA256 _hmac` fields
  - Add `private readonly ApiVault _keyVault` and `private readonly ApiVault _secretVault` fields
  - Update constructor signature: replace `string apiKey, string apiSecret` with `ApiVault keyVault, ApiVault secretVault`
  - In constructor: assign vaults, then transiently decrypt key bytes to set `X-MBX-APIKEY` header, zero out immediately via `Array.Clear`
  - Rewrite `ComputeSignature`: call `_secretVault.GetTransientDecryptedKey()`, use `HMACSHA256.HashData(secretBytes, ...)`, zero out via `Array.Clear` in finally block
  - Update `Dispose()`: call `_keyVault?.Dispose()` and `_secretVault?.Dispose()`; remove `_hmac?.Dispose()`
  - Update `Program.cs` registration in `01_Services/Execution.Worker/Program.cs`: read raw strings from `IConfiguration`, create two `ApiVault` instances, call `SecureInboundKey`, pass vaults to constructor
  - _Requirements: 8.1, 8.2, 8.3, 8.4, 8.5, 8.6_

  - [x] 6.1 Write property test for ApiVault round-trip
    - **Property 9: ApiVault round-trip** — `encrypt → decrypt` is lossless for any valid key string
    - File: `05_Tests/ThatsMyQuant.Tests/Risk/ApiVaultTests.cs`
    - Use FsCheck `[Property]` attribute, min 100 iterations
    - **Validates: Requirements 8.1, 8.3**

  - [x] 6.2 Write property test for encrypted bytes differ from plaintext
    - **Property 10: Encrypted bytes differ from plaintext** — ciphertext != UTF8 bytes of original key
    - File: `05_Tests/ThatsMyQuant.Tests/Risk/ApiVaultTests.cs`
    - Use FsCheck `[Property]` attribute, min 100 iterations
    - **Validates: Requirements 8.1**

  - [x] 6.3 Write property test for post-dispose throws
    - **Property 11: Post-dispose throws InvalidOperationException** — `GetTransientDecryptedKey` after `Dispose` must throw
    - File: `05_Tests/ThatsMyQuant.Tests/Risk/ApiVaultTests.cs`
    - Use FsCheck `[Property]` attribute, min 100 iterations
    - **Validates: Requirements 8.5**

  - [x] 6.4 Write unit tests for ApiVault
    - `SecureInboundKey_RoundTrip_ReturnsOriginalKey` — Property 9
    - `SecureInboundKey_EncryptedBytesDifferFromPlaintext` — Property 10
    - `GetTransientDecryptedKey_AfterDispose_ThrowsInvalidOperationException` — Property 11
    - File: `05_Tests/ThatsMyQuant.Tests/Risk/ApiVaultTests.cs`
    - _Requirements: 8.1, 8.3, 8.5_

- [x] 7. Add Sprint 4 structural bug condition tests
  - Create `05_Tests/ThatsMyQuant.Tests/Structural/Sprint4BugConditionTests.cs`
  - **B12**: Assert `RiskSupervisorService.cs` does NOT contain `_ = StartMonitoringLoop()` — fire-and-forget gone
  - **B13**: Assert `RiskSupervisorService.cs` does NOT contain `// await _execution.CancelAllOrdersAsync()` — commented-out call gone
  - **B14**: Assert `TelegramAlertService.cs` does NOT contain `private const string BotToken` — hardcoded constant gone
  - **B15**: Assert `BinanceExecutionService.cs` does NOT contain `private readonly string _apiKey` — raw string field gone
  - Follow the same `FindSolutionRoot()` helper pattern used in `Sprint3BugConditionTests.cs`
  - _Requirements: 1.1, 1.2, 7.1, 8.1_

- [x] 8. Fix the 9 currently failing tests
  - Run `dotnet test ThatsMyQuant.sln` to identify all failing tests
  - For each failing test: trace the failure to its root cause (missing interface method, unimplemented stub, or broken wiring introduced by tasks 1–6)
  - Fix each failure by implementing the missing code correctly — do not suppress or skip tests
  - Verify no previously passing tests regress after each fix
  - _Requirements: 6.1, 6.2, 6.3, 6.5_

- [x] 9. Final checkpoint — build and run all tests
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Property tests use FsCheck with minimum 100 iterations each
- Tag format for property tests: `// Feature: live-trading-safety, Property {N}: {property_text}`
- Structural bug condition tests (B12–B15) assert the bugs are GONE — they pass on fixed code
- All eight requirements must be covered before live trading with real funds
