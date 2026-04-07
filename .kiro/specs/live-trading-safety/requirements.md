# Requirements Document

## Introduction

ThatsMyQuant is a C# HFT trading system that routes live orders through Binance and OKX. This document captures the Phase 1 safety-critical fixes required before the system can be trusted with real funds. The eight items below represent gaps where the safety infrastructure exists in code but is either commented out, stubbed, fire-and-forgotten, or bypassed entirely. Left unaddressed, these gaps mean the emergency halt does not cancel open orders, drawdown monitoring never fires, the circuit breaker is dead code in the execution hot path, OKX fill quantities are always zero, and exchange credentials sit in plaintext memory or hardcoded constants.

---

## Glossary

- **RiskSupervisorService**: The autonomous risk supervisor that enforces rate limits, drawdown circuit breakers, and emergency kill-switches. Lives in `02_Core/Application/Risk/`.
- **CircuitBreakerService**: The institutional-grade circuit breaker that enforces sliding-window rate limits, drawdown thresholds, and manual halt checks before every order. Lives in `02_Core/Application/Strategy/`.
- **ExecutionWorker**: The `BackgroundService` that polls for queued signals and dispatches them to the exchange via MediatR. Lives in `01_Services/Execution.Worker/`.
- **IExchangeService**: The contract interface for exchange adapters (`BinanceExecutionService`, `OkxExecutionService`). Lives in `06_Contracts/Interfaces/`.
- **BinanceExecutionService**: The Binance REST v3 execution adapter. Accepts `apiKey` and `apiSecret` as raw constructor strings.
- **OkxExecutionService**: The OKX REST v5 execution adapter. Uses `IConfiguration` for credentials and `ApiVault` for in-memory AES-256-GCM encryption.
- **ApiVault**: The in-memory AES-256-GCM key vault that prevents plaintext API keys from appearing in memory dumps. Lives in `02_Core/Application/Security/`.
- **TelegramAlertService**: The alerting service that sends trade notifications via the Telegram Bot API. Lives in `03_Infrastructure/Data.Persistence/Services/`.
- **TelegramOptions**: A strongly-typed options class that binds `Telegram:BotToken` and `Telegram:ChatId` from `IConfiguration`.
- **IHostedService**: The .NET hosted service lifecycle interface (`StartAsync` / `StopAsync`) used for background workers with proper `CancellationToken` support.
- **DrawdownPercent**: The percentage loss from the peak observed balance. Formula: `(PeakBalance - CurrentBalance) / PeakBalance * 100`.
- **InitialBalance**: The USDT balance recorded at service startup, used as the baseline for drawdown computation.
- **PeakBalance**: The highest USDT balance observed since service startup, updated on every reconciliation.
- **EmergencyHalt**: The system state where `_isHalted = true` and all open exchange orders have been cancelled via `CancelAllOrdersAsync`.
- **IPortfolioManager**: The interface abstracting balance and position tracking. Lives in `06_Contracts/Interfaces/`.
- **VenueKind**: The enum identifying the exchange venue (Binance, OKX, Mock).

---

## Requirements

### Requirement 1: Emergency Halt Must Cancel Open Orders

**User Story:** As a risk manager, I want the emergency halt to actually cancel all open orders on the exchange, so that a triggered halt stops real financial exposure immediately rather than just setting a flag.

#### Acceptance Criteria

1. WHEN `RiskSupervisorService.TriggerEmergencyHaltAsync` is called, THE `RiskSupervisorService` SHALL set `_isHalted = true` AND await `IExchangeService.CancelAllOrdersAsync` before returning.
2. WHEN `IExchangeService.CancelAllOrdersAsync` throws an exception during an emergency halt, THE `RiskSupervisorService` SHALL log the exception at Critical level and continue — the halt flag SHALL remain `true`.
3. WHEN `ExecutionWorker` detects `_isHalted = true` at the start of its polling loop, THE `ExecutionWorker` SHALL skip signal dispatch and log a warning without throwing.
4. THE `BinanceExecutionService` SHALL implement `IExchangeService.CancelAllOrdersAsync` by calling the Binance `DELETE /api/v3/openOrders` endpoint for each active symbol and returning the count of cancelled orders.
5. THE `OkxExecutionService` SHALL implement `IExchangeService.CancelAllOrdersAsync` by calling the OKX `POST /api/v5/trade/cancel-batch-orders` endpoint and returning the count of cancelled orders.
6. IF `CancelAllOrdersAsync` receives a cancellation token that is already cancelled, THEN THE `IExchangeService` SHALL return `0` without making any HTTP calls.

#### Correctness Properties

- **Halt idempotence**: FOR ALL sequences of `TriggerEmergencyHaltAsync` calls, calling it N times SHALL result in exactly the same halted state as calling it once — `_isHalted` remains `true` and `CancelAllOrdersAsync` is not called redundantly on subsequent invocations after the first.
- **No-order-after-halt**: FOR ALL signals processed after `TriggerEmergencyHaltAsync` has been called, THE `ExecutionWorker` SHALL dispatch zero orders to the exchange — `IExchangeService.ExecuteOrderAsync` call count SHALL remain unchanged.

---

### Requirement 2: Drawdown Monitoring Must Be Implemented

**User Story:** As a risk manager, I want the 1-second monitoring loop to actually compute and enforce the drawdown threshold, so that the system halts automatically when cumulative losses exceed the configured limit.

#### Acceptance Criteria

1. WHEN `RiskSupervisorService` starts, THE `RiskSupervisorService` SHALL record the current USDT balance from `IPortfolioManager.GetAvailableBalance("USDT")` as `_initialBalance`.
2. WHILE the monitoring loop is running, THE `RiskSupervisorService` SHALL update `_peakBalance` to `Max(_peakBalance, currentBalance)` on every iteration.
3. WHEN the computed `DrawdownPercent` exceeds `_maxDrawdownPercent`, THE `RiskSupervisorService` SHALL call `TriggerEmergencyHaltAsync` with a descriptive reason string that includes the current drawdown percentage and the threshold.
4. THE `RiskSupervisorService` SHALL compute `DrawdownPercent` as `(_peakBalance - currentBalance) / _peakBalance * 100` where `currentBalance = IPortfolioManager.GetAvailableBalance("USDT")`.
5. IF `_peakBalance` is zero or negative, THEN THE `RiskSupervisorService` SHALL skip the drawdown check for that iteration and log a warning.
6. THE `RiskSupervisorService` SHALL expose a `decimal CurrentDrawdownPercent` property that returns the most recently computed drawdown value for monitoring and diagnostics.

#### Correctness Properties

- **Drawdown formula invariant**: FOR ALL values of `peakBalance > 0` and `currentBalance` in `[0, peakBalance]`, `DrawdownPercent` SHALL be in `[0, 100]`.
- **Monotone peak**: FOR ALL sequences of balance observations `b1, b2, ..., bN`, `_peakBalance` after processing the sequence SHALL equal `Max(b1, b2, ..., bN)` — the peak never decreases.
- **Halt threshold**: FOR ALL `currentBalance` values where `(peakBalance - currentBalance) / peakBalance * 100 >= maxDrawdownPercent`, the system SHALL be halted — `_isHalted = true`.

---

### Requirement 3: RiskSupervisorService Must Use IHostedService Lifecycle

**User Story:** As a platform engineer, I want `RiskSupervisorService` to implement `IHostedService` with proper cancellation and exception handling, so that the monitoring loop does not silently die and the service shuts down cleanly without orphaned background tasks.

#### Acceptance Criteria

1. THE `RiskSupervisorService` SHALL implement `IHostedService` (or extend `BackgroundService`) with `StartAsync(CancellationToken)` and `StopAsync(CancellationToken)` methods.
2. WHEN `StartAsync` is called, THE `RiskSupervisorService` SHALL begin the monitoring loop using the provided `CancellationToken` — the loop SHALL exit when the token is cancelled.
3. THE `RiskSupervisorService` SHALL NOT use fire-and-forget task assignment (`_ = StartMonitoringLoop()`) — the monitoring task SHALL be stored and awaited during `StopAsync`.
4. WHEN an unhandled exception occurs inside the monitoring loop, THE `RiskSupervisorService` SHALL log the exception at Critical level and continue the loop — a single iteration failure SHALL NOT terminate the monitoring loop.
5. WHEN `StopAsync` is called, THE `RiskSupervisorService` SHALL cancel the monitoring loop's `CancellationToken` and await the monitoring task with a 5-second timeout before returning.
6. THE `RiskSupervisorService` SHALL be registered in the DI container as an `IHostedService` via `services.AddHostedService<RiskSupervisorService>()`.

#### Correctness Properties

- **Loop resilience**: FOR ALL exceptions thrown by `IPortfolioManager.GetAvailableBalance` during a monitoring iteration, the loop SHALL continue to the next iteration — the exception SHALL be caught, logged, and not re-thrown.
- **Clean shutdown**: WHEN `StopAsync` is called after `StartAsync`, THE monitoring loop SHALL terminate within 5 seconds — no `TaskCanceledException` SHALL propagate to the host.

---

### Requirement 4: CircuitBreakerService Must Be Wired into the Execution Hot Path

**User Story:** As a risk manager, I want every order to pass through the circuit breaker before reaching the exchange, so that the rate limit, drawdown threshold, and manual halt checks are enforced on every trade rather than being dead code.

#### Acceptance Criteria

1. WHEN `ExecutionWorker.ProcessSingleSignal` is called, THE `ExecutionWorker` SHALL call `CircuitBreakerService.CheckOrderAllowed()` before dispatching the signal to MediatR.
2. WHEN `CircuitBreakerService.CheckOrderAllowed()` returns `(false, reason)`, THE `ExecutionWorker` SHALL mark the signal as `TradeStatus.Rejected`, log the block reason, and NOT send the signal to MediatR.
3. WHEN `CircuitBreakerService.CheckOrderAllowed()` returns `(true, null)`, THE `ExecutionWorker` SHALL proceed with normal MediatR dispatch.
4. WHEN `ExecutionWorker` successfully executes a trade, THE `ExecutionWorker` SHALL call `CircuitBreakerService.RecordSuccess()`.
5. WHEN `ExecutionWorker` catches an exception during trade execution, THE `ExecutionWorker` SHALL call `CircuitBreakerService.RecordError(exception)`.
6. THE `CircuitBreakerService` SHALL be injected into `ExecutionWorker` via constructor dependency injection.

#### Correctness Properties

- **No-bypass property**: FOR ALL signals where `CheckOrderAllowed()` returns `(false, _)`, `IExchangeService.ExecuteOrderAsync` SHALL NOT be called — the exchange call count SHALL not increase.
- **Error cascade**: FOR ALL sequences of N consecutive exceptions where N >= `maxConsecutiveErrors`, `CircuitBreakerService.IsHalted` SHALL be `true` after the Nth exception.

---

### Requirement 5: OKX Fill Tracking Must Return Actual Filled Quantity

**User Story:** As a trader, I want `OkxExecutionService.ExecuteOrderAsync` to return the actual filled quantity, so that the portfolio manager and position tracking reflect real exchange state rather than always showing zero.

#### Acceptance Criteria

1. WHEN `OkxExecutionService.ExecuteOrderAsync` places a limit order and the OKX API returns an `ordId`, THE `OkxExecutionService` SHALL poll `GET /api/v5/trade/order?instId={symbol}&ordId={ordId}` to retrieve fill status.
2. WHEN the polled order status is `"filled"`, THE `OkxExecutionService` SHALL set `Trade.FilledQuantity` to the `fillSz` value from the OKX response and `Trade.RemainingQuantity` to `0`.
3. WHEN the polled order status is `"partially_filled"`, THE `OkxExecutionService` SHALL set `Trade.FilledQuantity` to the `fillSz` value and `Trade.RemainingQuantity` to `Trade.RequestedQuantity - Trade.FilledQuantity`.
4. WHEN the polled order status is `"live"` (unfilled), THE `OkxExecutionService` SHALL set `Trade.FilledQuantity = 0`, `Trade.RemainingQuantity = Trade.RequestedQuantity`, and `Trade.Status = TradeStatus.Pending`.
5. THE `OkxExecutionService` SHALL poll for fill status with a maximum of 3 attempts with 500ms delay between attempts before returning the best-known state.
6. IF the fill-status poll fails with an HTTP error, THEN THE `OkxExecutionService` SHALL log the error and return the trade with `FilledQuantity = 0` and `Status = TradeStatus.Pending` — the method SHALL NOT throw.

#### Correctness Properties

- **Fill quantity invariant**: FOR ALL completed `ExecuteOrderAsync` calls where the order was accepted by OKX, `Trade.FilledQuantity + Trade.RemainingQuantity` SHALL equal `Trade.RequestedQuantity`.
- **Fill quantity bounds**: FOR ALL `ExecuteOrderAsync` results, `Trade.FilledQuantity` SHALL be in `[0, Trade.RequestedQuantity]` and `Trade.RemainingQuantity` SHALL be in `[0, Trade.RequestedQuantity]`.
- **No-throw on poll failure**: FOR ALL HTTP errors returned by the fill-status poll endpoint, `ExecuteOrderAsync` SHALL return a valid `Trade` object — it SHALL NOT throw `InvalidOperationException` or `HttpRequestException`.

---

### Requirement 6: All Failing Tests Must Pass Before Live Trading

**User Story:** As a developer, I want the full test suite to pass with zero failures, so that the safety-critical code paths have verified correctness before real funds are at risk.

#### Acceptance Criteria

1. THE test suite SHALL pass with 0 failing tests when run via `dotnet test ThatsMyQuant.sln`.
2. WHEN the 9 currently failing tests are fixed, THE fixes SHALL not introduce regressions in any of the currently passing tests.
3. THE `OkxExecutionServiceTests` in `05_Tests/ThatsMyQuant.Tests/Execution/` SHALL all pass — these 5 tests cover HMAC signature correctness, demo/live mode headers, insufficient balance rejection, and credential hot-reload.
4. THE `RiskSupervisorServiceTests` SHALL be created in `05_Tests/ThatsMyQuant.Tests/Risk/` covering: halt rejection, rate limit halt, drawdown halt, `CancelAllOrdersAsync` invocation on halt, and `IHostedService` lifecycle.
5. WHERE a test failure is caused by a missing interface method or unimplemented stub, THE fix SHALL implement the method correctly rather than suppress the test.

#### Correctness Properties

- **Test suite stability**: FOR ALL code changes made to fix failing tests, the count of passing tests SHALL be monotonically non-decreasing — no fix SHALL cause a previously passing test to fail.

---

### Requirement 7: Telegram Secrets Must Be Loaded from Configuration

**User Story:** As a security engineer, I want Telegram bot credentials to come from `IConfiguration` with user secrets support, so that the bot token and chat ID are never hardcoded in source code or committed to version control.

#### Acceptance Criteria

1. THE `TelegramAlertService` SHALL NOT contain `private const string BotToken` or `private const string ChatId` fields — these constants SHALL be removed.
2. THE system SHALL define a `TelegramOptions` class with `string BotToken` and `string ChatId` properties, bound from the `"Telegram"` configuration section.
3. THE `TelegramAlertService` SHALL accept `IOptions<TelegramOptions>` as a constructor parameter and read `BotToken` and `ChatId` from the injected options.
4. WHEN `TelegramOptions.BotToken` is null or empty, THE `TelegramAlertService` SHALL fall back to mock-logging mode (log the alert to `ILogger` at Information level) without throwing.
5. THE `TelegramOptions` SHALL be registered via `services.Configure<TelegramOptions>(configuration.GetSection("Telegram"))` in the DI setup.
6. THE `Telegram:BotToken` and `Telegram:ChatId` values SHALL be stored using .NET user secrets (`dotnet user-secrets set`) for local development and environment variables for production — they SHALL NOT appear in `appsettings.json` or `appsettings.Development.json`.

#### Correctness Properties

- **Config round-trip**: FOR ALL valid `TelegramOptions` instances constructed from `IConfiguration`, `options.BotToken` SHALL equal the value set in the configuration source — the binding SHALL be lossless.
- **No-throw on missing config**: FOR ALL `TelegramAlertService` instances constructed with an empty `TelegramOptions`, `SendTradeAlertAsync` SHALL complete without throwing — it SHALL log the alert instead of sending HTTP requests.

---

### Requirement 8: Binance API Keys Must Use ApiVault Encryption

**User Story:** As a security engineer, I want Binance API keys to be encrypted in memory using the same `ApiVault` AES-256-GCM pattern used by OKX, so that a memory dump of the running process does not expose plaintext exchange credentials.

#### Acceptance Criteria

1. THE `BinanceExecutionService` SHALL NOT store `apiKey` or `apiSecret` as raw `string` or `byte[]` fields after construction — the plaintext values SHALL be zeroed out immediately after being passed to `ApiVault.SecureInboundKey`.
2. THE `BinanceExecutionService` SHALL accept an `ApiVault` instance (or two `ApiVault` instances — one for key, one for secret) as a constructor parameter instead of raw `string apiKey, string apiSecret`.
3. WHEN `BinanceExecutionService` needs to sign a request, THE `BinanceExecutionService` SHALL call `ApiVault.GetTransientDecryptedKey()`, use the returned bytes for HMAC-SHA256 signing, and zero out the decrypted bytes immediately after signing via `Array.Clear`.
4. THE `BinanceExecutionService` constructor SHALL call `ApiVault.SecureInboundKey(apiKey)` and `ApiVault.SecureInboundKey(apiSecret)` during initialization, then zero out the input strings by overwriting with empty values where possible.
5. WHEN `BinanceExecutionService` is disposed, THE `BinanceExecutionService` SHALL call `ApiVault.Dispose()` on all held vault instances to zero out the encrypted key material from memory.
6. THE `BinanceExecutionService` registration in `Program.cs` SHALL read `apiKey` and `apiSecret` from `IConfiguration` (user secrets / environment variables), pass them to `ApiVault`, and NOT store them in any long-lived field outside the vault.

#### Correctness Properties

- **ApiVault round-trip**: FOR ALL valid API key strings `k`, `Encoding.UTF8.GetString(vault.GetTransientDecryptedKey())` after `vault.SecureInboundKey(k)` SHALL equal `k` — the encrypt/decrypt cycle SHALL be lossless.
- **Encrypted bytes differ from plaintext**: FOR ALL API key strings `k` of length >= 1, the `_encryptedKey` bytes stored in `ApiVault` after `SecureInboundKey(k)` SHALL NOT equal `Encoding.UTF8.GetBytes(k)` — AES-GCM encryption SHALL produce ciphertext distinct from plaintext.
- **Post-dispose throws**: WHEN `ApiVault.GetTransientDecryptedKey()` is called after `ApiVault.Dispose()`, THE `ApiVault` SHALL throw `InvalidOperationException` — zeroed key material SHALL not be returned as a valid key.
