# Implementation Tasks — Production Hardening

---

## Sprint 0 — Structural Fixes

- [x] 1. Write bug condition exploration tests (Sprint 0)
  - File: `05_Tests/ThatsMyQuant.Tests/Structural/Sprint0BugConditionTests.cs`
  - **Test B1:** Use `Directory.GetFiles` recursively on the solution root to find all `.cs` files whose content matches `class\s+MemoryPortfolioManager`. Assert count >= 2. This proves the naming collision exists across namespaces.
  - **Test B3:** Build a `ServiceCollection`, call `AddThatsMyQuantApplication()`, build the provider, and assert both `sp.GetService<DeadMansSwitch>()` and `sp.GetService<IAuditLogger>()` return null.
  - **Test B4:** Read the content of all four files — `01_Services/QuantEngine.Worker/Program.cs`, `01_Services/Execution.Worker/Program.cs`, `01_Services/Listener.Api/Program.cs`, `04_UI/ThatsMyQuant.Dashboard/App.xaml.cs` — and assert each contains the string `C:\\Users\\parlak`.
  - All three tests must PASS on unfixed code (they assert the bug condition is present).
  - _Requirements: 1.1, 1.3, 1.4_

- [x] 2. Write preservation property tests for Sprint 0
  - File: `05_Tests/ThatsMyQuant.Tests/Structural/Sprint0PreservationTests.cs`
  - Build a `ServiceCollection`, call `AddThatsMyQuantApplication()`, build the provider.
  - Assert `CircuitBreakerService` is resolvable (registered at `DependencyInjection.cs:31`).
  - Assert `MemoryPortfolioManager` (Strategy) is resolvable (registered at `DependencyInjection.cs:22`).
  - Assert `TickPipeline` is resolvable (registered at `DependencyInjection.cs:29`).
  - Assert `FeatureEngineeringAgent` is resolvable (registered at `DependencyInjection.cs:21`).
  - All tests must PASS on unfixed code.
  - _Requirements: 3.1, 3.7_

- [x] 3. Delete the 65-line MemoryPortfolioManager skeleton
  - Delete `02_Core/Application/Risk/MemoryPortfolioManager.cs` (namespace `ThatsMyQuant.Application.Risk`, 65 lines).
  - Keep `02_Core/Application/Strategy/MemoryPortfolioManager.cs` (289 lines — DI-registered, used by `CircuitBreakerService` and `RiskAgent`).
  - Keep `03_Infrastructure/Data.Persistence/Services/MemoryPortfolioManager.cs` (268 lines — accessed via `MemoryPortfolioManager.Instance`, implements `ISharedPortfolioStateManager`). Document in a code comment that this will be renamed to `VenuePortfolioManager` in Sprint 4.
  - Check `02_Core/Application/Strategy/RiskAgent.cs` line 6 — if `using ThatsMyQuant.Application.Risk;` is now unused after deletion, remove it.
  - Run `dotnet build` — assert 0 errors, 0 warnings.
  - _Requirements: 1.1, 2.1, 3.1_

- [x] 4. Fix hardcoded SQLite database path in all four files
  - [x] 4.1 Add `ConnectionStrings` section to each service's `appsettings.json`
    - Files: `01_Services/QuantEngine.Worker/appsettings.json`, `01_Services/Execution.Worker/appsettings.json`, `01_Services/Listener.Api/appsettings.json`
    - Add: `"ConnectionStrings": { "Quant": "Data Source=quant.db;Cache=Shared" }`
    - SQLite resolves relative paths against `Directory.GetCurrentDirectory()` at runtime.
  - [x] 4.2 Update `QuantEngine.Worker/Program.cs` (line 66)
    - Replace the hardcoded string with: `builder.Configuration.GetConnectionString("Quant") ?? "Data Source=quant.db;Cache=Shared"`
  - [x] 4.3 Update `Execution.Worker/Program.cs` (line 66) — same replacement as 4.2.
  - [x] 4.4 Update `Listener.Api/Program.cs` (line 43) — same replacement as 4.2.
  - [x] 4.5 Update `04_UI/ThatsMyQuant.Dashboard/App.xaml.cs` (line 145)
    - Dashboard does not use `builder.Configuration`. Replace with the literal: `"Data Source=quant.db;Cache=Shared"`
  - Run `dotnet build` — assert 0 errors, 0 warnings.
  - _Requirements: 1.4, 2.4_

- [x] 5. Create IAuditLogger implementation, register DeadMansSwitch and HeartbeatMonitor in DI
  - [x] 5.1 Create `03_Infrastructure/Data.Persistence/Services/ConsoleAuditLogger.cs`
    - Namespace: `ThatsMyQuant.Infrastructure.Services`
    - Implement `IAuditLogger` from `ThatsMyQuant.Contracts.Interfaces`
    - `LogSecurityEventAsync`: log at Warning level using `ILogger<ConsoleAuditLogger>`
    - `LogOrderExecutionAsync`: log at Information level
  - [x] 5.2 Register in `02_Core/Application/Common/DependencyInjection.cs`
    - Add `using ThatsMyQuant.Application.HftInfrastructure;`
    - Add in order (dependency order matters): `services.AddSingleton<IAuditLogger, ConsoleAuditLogger>()`, `services.AddSingleton<DeadMansSwitch>()`, `services.AddSingleton<HeartbeatMonitor>()`
    - Note: `ConsoleAuditLogger` is in Infrastructure — add a project reference if needed, or register it in `AddThatsMyQuantInfrastructure()` instead and forward the interface.
  - [x] 5.3 Wire heartbeat into `02_Core/Application/Strategy/TickPipeline.cs`
    - Add `HeartbeatMonitor _heartbeatMonitor` field and constructor parameter (after `ITickLogger tickLogger`).
    - In `ProcessTicksAsync`, after `_featureAgent.ProcessTickNatively((double)tick.Price)` (line 85), add: `_heartbeatMonitor.RecordHeartbeat();`
    - Existing tick data semantics and `FeatureEngineeringAgent` feed are unchanged.
  - _Requirements: 1.3, 2.3, 3.7_

- [x] 6. Checkpoint Sprint 0
  - Run `dotnet build ThatsMyQuant.sln` — assert 0 errors, 0 warnings.
  - Run `dotnet test` — assert all existing tests pass.
  - Re-run B1 test: assert it now finds exactly 2 `MemoryPortfolioManager` classes (Risk one deleted).
  - Re-run B3 test: assert `DeadMansSwitch` and `IAuditLogger` are now resolvable from DI.
  - Re-run B4 test: assert 0 occurrences of `C:\\Users\\parlak` across all four files.
  - _Requirements: 3.8_


---

## Sprint 1 — Interface Abstraction

- [x] 7. Write bug condition exploration test (Sprint 1)
  - File: `05_Tests/ThatsMyQuant.Tests/Structural/Sprint1BugConditionTests.cs`
  - Use reflection: load `typeof(CircuitBreakerService)`, get its constructor parameters, assert that the parameter named `portfolio` is of type `MemoryPortfolioManager` (concrete), not an interface.
  - This test must PASS on unfixed code (it asserts the concrete dependency exists).
  - _Requirements: 1.2, 1.5_

- [x] 8. Write preservation property tests for Sprint 1
  - File: `05_Tests/ThatsMyQuant.Tests/Structural/Sprint1PreservationTests.cs`
  - Construct `CircuitBreakerService` with a real `MemoryPortfolioManager` (no drawdown, no halt).
  - Assert `CheckOrderAllowed()` returns `(true, null)` when drawdown < threshold and no halt.
  - Set drawdown above threshold via `portfolio.SetBalance("USDT", 94_000m)` after `SetBalance("USDT", 100_000m)` (peak set), then assert `CheckOrderAllowed()` returns `(false, ...)`.
  - These tests must PASS on unfixed code.
  - _Requirements: 3.2, 3.3_

- [x] 9. Extract IPortfolioManager interface and update consumers
  - [x] 9.1 Create `06_Contracts/Interfaces/IPortfolioManager.cs`
    - Namespace: `ThatsMyQuant.Contracts.Interfaces`
    - Include only the methods actually called by `CircuitBreakerService` and `RiskAgent`:
      - `decimal GetDrawdownPercent()` — called by `CircuitBreakerService.CheckOrderAllowed()` (line 70)
      - `decimal GetAvailableBalance(string asset)` — called by `RiskAgent.CheckAvailableBalance()`
      - `void SetBalance(string asset, decimal amount)`
      - `void AddBalance(string asset, decimal delta)`
      - `void Lock(string asset, decimal amount)`
      - `void Unlock(string asset, decimal amount)`
      - `void OpenPosition(string symbol, decimal entryPrice, decimal quantity, bool isLong)`
      - `decimal ClosePosition(string symbol, decimal exitPrice)`
      - `PositionState? GetPosition(string symbol)`
      - `IReadOnlyDictionary<string, PositionState> GetAllPositions()`
      - `void Reconcile(IReadOnlyDictionary<string, (decimal Total, decimal Locked)> externalBalances)`
    - _Requirements: 1.2, 2.2_
  - [x] 9.2 Implement `IPortfolioManager` on `Strategy/MemoryPortfolioManager`
    - File: `02_Core/Application/Strategy/MemoryPortfolioManager.cs`
    - Add `: IPortfolioManager` to the class declaration.
    - Add `using ThatsMyQuant.Contracts.Interfaces;`
    - _Requirements: 2.2_
  - [x] 9.3 Update `CircuitBreakerService` to accept `IPortfolioManager`
    - File: `02_Core/Application/Strategy/CircuitBreakerService.cs`
    - Change constructor parameter `MemoryPortfolioManager portfolio` → `IPortfolioManager portfolio`
    - Change field `private readonly MemoryPortfolioManager _portfolio` → `private readonly IPortfolioManager _portfolio`
    - `CheckOrderAllowed()` semantics are unchanged — it still calls `_portfolio.GetDrawdownPercent()`.
    - _Requirements: 1.5, 2.5, 3.3_
  - [x] 9.4 Update `RiskAgent` to accept `IPortfolioManager?`
    - File: `02_Core/Application/Strategy/RiskAgent.cs`
    - Change constructor parameter `MemoryPortfolioManager? portfolioManager = null` → `IPortfolioManager? portfolioManager = null`
    - Change field `private readonly MemoryPortfolioManager? _portfolioManager` → `private readonly IPortfolioManager? _portfolioManager`
    - _Requirements: 1.2, 2.2_
  - [x] 9.5 Update DI to expose `IPortfolioManager`
    - File: `02_Core/Application/Common/DependencyInjection.cs`
    - After `services.AddSingleton<MemoryPortfolioManager>()`, add: `services.AddSingleton<IPortfolioManager>(sp => sp.GetRequiredService<MemoryPortfolioManager>())`
    - Same singleton instance is returned whether resolved as concrete or interface.
    - _Requirements: 2.2, 3.1_
  - [x] 9.6 Update `CircuitBreakerServiceTests.cs` to use `IPortfolioManager` substitute
    - File: `05_Tests/ThatsMyQuant.Tests/Strategy/CircuitBreakerServiceTests.cs`
    - The existing tests construct `new MemoryPortfolioManager()` directly (4 occurrences). After the interface extraction, `CircuitBreakerService` accepts `IPortfolioManager`.
    - For the drawdown test (`CheckOrderAllowed_ShouldHaltWhenDrawdownThresholdBreached`): keep using a real `MemoryPortfolioManager` instance since it needs actual drawdown calculation.
    - For the halt and rate-limit tests: replace `new MemoryPortfolioManager()` with `Substitute.For<IPortfolioManager>()` — configure `GetDrawdownPercent()` to return `0m`.
    - _Requirements: 2.2, 3.3_

- [x] 10. Checkpoint Sprint 1
  - Run `dotnet build` — assert 0 errors, 0 warnings.
  - Run `dotnet test` — assert all existing tests pass.
  - Re-run Sprint 1 exploration test: assert `CircuitBreakerService` constructor now accepts `IPortfolioManager` (interface type), not `MemoryPortfolioManager`.
  - Re-run Sprint 1 preservation tests: assert `CheckOrderAllowed()` semantics are unchanged.
  - _Requirements: 3.3, 3.8_


---

## Sprint 2 — Test Coverage

- [x] 11. Write bug condition exploration tests (Sprint 2)
  - File: `05_Tests/ThatsMyQuant.Tests/Structural/Sprint2BugConditionTests.cs`
  - **Test B6:** Assert that `05_Tests/ThatsMyQuant.Tests/Strategy/MemoryPortfolioManagerTests.cs` does NOT exist. (The existing `Infrastructure/MemoryPortfolioManagerTests.cs` tests the Infrastructure Singleton — the Strategy version has zero coverage.)
  - **Test B7:** Assert that `05_Tests/ThatsMyQuant.Tests/MachineLearning/FeatureEngineeringAgentTests.cs` does NOT exist.
  - **Test B8:** Assert that `05_Tests/ThatsMyQuant.Tests/Integration/SignalPipelineIntegrationTests.cs` does NOT exist.
  - All three tests must PASS on unfixed code (files don't exist yet).
  - _Requirements: 1.6, 1.7, 1.8_

- [x] 12. Write Strategy MemoryPortfolioManager unit tests
  - File: `05_Tests/ThatsMyQuant.Tests/Strategy/MemoryPortfolioManagerTests.cs`
  - Target class: `ThatsMyQuant.Application.Strategy.MemoryPortfolioManager` (the DI-registered 289-line version, not the Infrastructure Singleton).
  - Test: `SetBalance("USDT", 1000m)` then `GetAvailableBalance("USDT")` returns `1000m`.
  - Test: `Lock("USDT", 300m)` reduces `GetAvailableBalance` to `700m`; `Unlock("USDT", 300m)` restores it to `1000m`; available balance never goes below `0m`.
  - Test: `OpenPosition("BTCUSDT", 50000m, 1m, isLong: true)` then `ClosePosition("BTCUSDT", 51000m)` returns P&L of `1000m` and adds it to USDT balance.
  - Test: `OpenPosition("BTCUSDT", 50000m, 1m, isLong: false)` then `ClosePosition("BTCUSDT", 49000m)` returns P&L of `1000m` (short profit).
  - Test: `UpdateFromExecutionReport` with a `Trade` where `Status = TradeStatus.Executed` and `FilledQuantity = 0.5m` calls `OpenPosition` with quantity `0.5m`.
  - Test: `UpdateFromExecutionReport` with `Status = TradeStatus.Rejected` calls `Unlock` for the estimated notional (`EntryPrice * Quantity`).
  - Test: `Reconcile` with an external source containing `{ "USDT": (2000m, 100m) }` sets total balance to `2000m` and locked to `100m`; assets absent from the external source are removed from `_balances`.
  - Test: 50 concurrent `AddBalance("USDT", 10m)` calls converge to exactly `500m` (thread-safety via `InterlockedAdd`).
  - Test: `GetDrawdownPercent()` returns `0m` before any peak is set; after `SetBalance("USDT", 1000m)` then `SetBalance("USDT", 900m)`, returns `10m`.
  - _Requirements: 1.6, 2.6_

- [x] 13. Write FeatureEngineeringAgent unit tests
  - File: `05_Tests/ThatsMyQuant.Tests/MachineLearning/FeatureEngineeringAgentTests.cs`
  - Test: after 20 calls to `ProcessTickNatively` with ascending prices, `CurrentTensor[0]` (RSI) is in range `[0f, 1f]`.
  - Test: after 10 calls to `ProcessTickNatively` with a constant price, `CurrentTensor[1]` (EMA deviation) is `> 0f`.
  - Test: after 100 calls to `ProcessTickNatively`, `CurrentTensor[2]` (MA bias) is `> 0f`.
  - Test: `SafeFloat(double.NaN)` returns `0f`.
  - Test: `SafeFloat(double.PositiveInfinity)` returns `0f`.
  - Test: `SafeFloat(double.NegativeInfinity, 0.5f)` returns `0.5f`.
  - Test: `ApplyFoundationData(new FoundationSnapshot { BidAskImbalance = 0.3, TickDirectionRatio = 0.7 })` sets `CurrentTensor[3]` to `0.3f` and `CurrentTensor[4]` to `0.7f`.
  - Test: `CopyTo(destination)` produces a `float[5]` array with values identical to `CurrentTensor` at the time of the call.
  - _Requirements: 1.7, 2.7_

- [x] 14. Write signal pipeline integration test
  - File: `05_Tests/ThatsMyQuant.Tests/Integration/SignalPipelineIntegrationTests.cs`
  - [x] 14.1 Set up in-memory test host
    - Build a `ServiceCollection` with `AddThatsMyQuantApplication()` and `AddThatsMyQuantInfrastructure()`.
    - Override `IExchangeService` with `MockBrokerService` (already exists in infrastructure).
    - Override `IChatCompletionService` with `MockChatCompletionService` (already exists).
    - Override `IAuditLogger` with a no-op substitute: `Substitute.For<IAuditLogger>()`.
    - Configure `QuantDbContext` with in-memory SQLite: `UseSqlite("DataSource=:memory:")` and call `EnsureCreatedAsync()`.
    - Register `MediatR` scanning `typeof(ProcessSignalCommand).Assembly`.
    - _Requirements: 1.8, 2.8_
  - [x] 14.2 Write pipeline flow test
    - Create a `Signal` with `Symbol = "BTCUSDT"`, `Price = 60000m`, `Side = TradeSide.Buy`, `Volume24h = 50_000_000m`, `Timestamp = DateTime.UtcNow`.
    - Resolve `IMediator` from the test host and send `new ProcessSignalCommand(signal)`.
    - Assert the command completes without throwing.
    - Assert the signal was persisted: query `QuantDbContext.Signals` and assert at least one record with `Symbol == "BTCUSDT"` exists.
    - _Requirements: 2.8_
  - [x] 14.3 Write pipeline rejection test
    - Create a `Signal` with `Volume24h = 0m` (should fail `MinVolumeValidator`).
    - Send `ProcessSignalCommand(signal)` and assert it returns `false`.
    - Assert the persisted signal has `Status == TradeStatus.Rejected`.
    - _Requirements: 2.8_

- [x] 15. Checkpoint Sprint 2
  - Run `dotnet build` — assert 0 errors, 0 warnings.
  - Run `dotnet test` — assert all existing tests pass plus all new tests in tasks 12, 13, 14.
  - Re-run Sprint 2 exploration tests: assert all three test files now exist (tests now FAIL — confirming coverage was added).
  - _Requirements: 3.8_


---

## Sprint 3 — Hot-Path Performance Fixes

- [x] 16. Write bug condition exploration tests (Sprint 3)
  - File: `05_Tests/ThatsMyQuant.Tests/Structural/Sprint3BugConditionTests.cs`
  - **Test B9 (structural):** Read `02_Core/Application/MachineLearning/FeatureEngineeringAgent.cs` via `File.ReadAllText`. Assert the file contains `lock (` or `lock(`. This confirms the kernel mutex is present.
  - **Test B10 (structural):** Read `02_Core/Application/Strategy/VectorizedMathUtils.cs`. Assert `CalculateBatchSMA` does NOT contain a running-sum subtraction pattern (e.g., does not contain `prices[i - 1]`). This confirms the O(N×W) loop is present.
  - **Test B11 (structural):** Read `02_Core/Application/HftInfrastructure/CircuitBreakers.cs`. Assert `HeartbeatMonitor` contains `DateTime.UtcNow` (not `Environment.TickCount64`). Read `02_Core/Application/Strategy/RiskAgent.cs`. Assert `TokenBucketRateLimiter` contains `DateTime.UtcNow`.
  - All tests must PASS on unfixed code (they assert the bugs are present).
  - _Requirements: 1.9, 1.10, 1.11_

- [x] 17. Write preservation property tests for Sprint 3
  - File: `05_Tests/ThatsMyQuant.Tests/Structural/Sprint3PreservationTests.cs`
  - **P1 (SafeFloat guard):** Call `ProcessTickNatively(double.NaN)` on a fresh `FeatureEngineeringAgent`. Assert `CurrentTensor` values are all `0f` or `1f` (no NaN propagation).
  - **P2 (SMA early-exit):** Call `CalculateBatchSMA(prices: [1f, 2f], windowSize: 5, results: new float[2])`. Assert `results[0]` is `0f` (no write occurred).
  - **P3 (SMA correctness baseline):** Call `CalculateBatchSMA([1f, 2f, 3f, 4f, 5f], windowSize: 3, results: new float[3])`. Record the output as the expected values `[2f, 3f, 4f]` before the fix.
  - All tests must PASS on unfixed code.
  - _Requirements: 3.5, 3.6_

- [x] 18. Replace lock with publish-then-forget pattern in FeatureEngineeringAgent
  - File: `02_Core/Application/MachineLearning/FeatureEngineeringAgent.cs`
  - Replace `public float[] CurrentTensor { get; }` and `private readonly object _syncLock = new()` with `private volatile float[] _currentTensor = new float[5]`.
  - Add a public property: `public float[] CurrentTensor => _currentTensor;`
  - Rewrite `ProcessTickNatively(double price)`:
    - Allocate a fresh `float[5]` local array `tensor`.
    - Compute all 5 features into `tensor` (same math as before, with `SafeFloat` guards).
    - Preserve OFI and foundation data: `var prev = _currentTensor; tensor[3] = prev[3]; tensor[4] = prev[4];` (before overwriting with tick-direction fallback if applicable).
    - Publish atomically: `Volatile.Write(ref _currentTensor, tensor)`.
  - Rewrite `ApplyFoundationData`: allocate fresh array, copy `_currentTensor`, set `[3]` and `[4]`, publish via `Volatile.Write`.
  - Rewrite `ApplyOrderFlowImbalance`: same pattern — copy, set `[3]`, publish.
  - Rewrite `CopyTo(Span<float> destination)`: `Volatile.Read(ref _currentTensor).AsSpan().CopyTo(destination)` — no lock.
  - Remove `_syncLock` field and all `lock(_syncLock)` statements.
  - All `SafeFloat` guards must be preserved on every tensor write.
  - _Requirements: 1.9, 2.9, 3.5_

- [x] 19. Replace O(N×W) SMA with O(N) running-sum sliding window
  - File: `02_Core/Application/Strategy/VectorizedMathUtils.cs`
  - Rewrite `CalculateBatchSMA`:
    - Keep early-exit guard: `if (prices.Length < windowSize) return;`
    - Compute initial window sum: `float windowSum = SumVectorized(prices[..windowSize]);`
    - Set `results[0] = windowSum / windowSize;`
    - Loop `for (int i = 1; i <= prices.Length - windowSize; i++)`: `windowSum += prices[i + windowSize - 1] - prices[i - 1]; results[i] = windowSum / windowSize;`
  - Numerical output must be identical to the O(N×W) version for all valid inputs (verified by Sprint 3 preservation test P3).
  - _Requirements: 1.10, 2.10, 3.6_

- [x] 20. Replace DateTime.UtcNow with Environment.TickCount64 in hot-path components
  - [x] 20.1 Fix `HeartbeatMonitor` in `02_Core/Application/HftInfrastructure/CircuitBreakers.cs`
    - Change `private DateTime _lastHeartbeat = DateTime.UtcNow` → `private long _lastHeartbeatTick = Environment.TickCount64`
    - In `RecordHeartbeat()`: `_lastHeartbeatTick = Environment.TickCount64`
    - In `AuditConnection()`: replace the `DateTime` subtraction with `Environment.TickCount64 - _lastHeartbeatTick > _maxStaleThreshold.TotalMilliseconds`
    - _Requirements: 1.11, 2.11_
  - [x] 20.2 Fix `TokenBucketRateLimiter` in `02_Core/Application/Strategy/RiskAgent.cs`
    - Change `private DateTime _lastRefill` → `private long _lastRefillTick`
    - In constructor: `_lastRefillTick = Environment.TickCount64`
    - In `TryConsume()`: replace `var now = DateTime.UtcNow` and `(now - _lastRefill).TotalSeconds` with `var nowTick = Environment.TickCount64` and `(nowTick - _lastRefillTick) / 1000.0`
    - _Requirements: 1.11, 2.11_

- [x] 21. Checkpoint Sprint 3
  - Run `dotnet build` — assert 0 errors, 0 warnings.
  - Run `dotnet test` — assert all tests pass with zero regressions.
  - Re-run Sprint 3 exploration tests: assert B9 no longer finds `lock(` in `FeatureEngineeringAgent.cs`; B10 finds the running-sum pattern; B11 finds `Environment.TickCount64` in both files.
  - Re-run Sprint 3 preservation tests: assert SafeFloat guard still works, SMA early-exit still works, SMA numerical output matches baseline.
  - _Requirements: 3.5, 3.6, 3.8_

---

## Sprint 4 — Tech Debt (Rename Infrastructure MemoryPortfolioManager)

- [x] 22. Rename Infrastructure MemoryPortfolioManager to VenuePortfolioManager
  - File: `03_Infrastructure/Data.Persistence/Services/MemoryPortfolioManager.cs`
  - Rename class to `VenuePortfolioManager`. Update the `Lazy<MemoryPortfolioManager>` field to `Lazy<VenuePortfolioManager>`. Update `Instance` property type.
  - Rename file to `VenuePortfolioManager.cs`.
  - Update `05_Tests/ThatsMyQuant.Tests/Infrastructure/MemoryPortfolioManagerTests.cs`: rename class to `VenuePortfolioManagerTests`, update `using` and field type.
  - Update any consumers referencing `MemoryPortfolioManager.Instance` (grep for `MemoryPortfolioManager.Instance` in `03_Infrastructure/`).
  - Run `dotnet build` — assert 0 errors, 0 warnings.
  - Run `dotnet test` — assert all tests pass.
  - _Requirements: 2.1 (eliminates remaining class-name collision)_

---

## Sprint 5 — OKX Integration Hardening

- [x] 23. Migrate OKX credentials from plaintext JSON to ApiVault (AES-256-GCM)
  - [x] 23.1 Remove plaintext key storage from `exchange_settings.json`
    - Strip `ApiKey`, `SecretKey`, `Passphrase` fields from the JSON file schema.
    - Keep only non-sensitive fields: `IsDemo` (bool), `Exchange` (string).
  - [x] 23.2 Persist credentials via `ApiVault`
    - File: `04_UI/ThatsMyQuant.Dashboard/ViewModels/ExchangeSettingsViewModel.cs`
    - On save: call `ApiVault.SecureInboundKey(secretKey)` and store `ApiKey`/`Passphrase` via the vault's secure storage mechanism.
    - On load: retrieve credentials from `ApiVault` rather than reading from JSON.
  - [x] 23.3 Update `OkxExecutionService` to read credentials from `ApiVault`
    - File: `01_Services/Execution.Worker/OkxExecutionService.cs` (or equivalent path)
    - Replace `IConfiguration`-based key reads with `ApiVault` lookups.
    - HMAC-SHA256 signing must use the vault-retrieved secret — no plaintext key in memory beyond the signing operation.
  - Run `dotnet build` — assert 0 errors, 0 warnings.
  - _Requirements: OKX-1 (no plaintext credentials on disk)_

- [x] 24. Add Live/Demo mode confirmation guard
  - File: `04_UI/ThatsMyQuant.Dashboard/ViewModels/ExchangeSettingsViewModel.cs`
  - When `IsDemo` is toggled from `true` → `false` (switching to Live), show a confirmation dialog: `"You are switching to LIVE trading. Real funds will be at risk. Confirm?"`
  - If the user cancels, revert `IsDemo` back to `true` without saving.
  - The `x-simulated-trading: 1` header must only be omitted after confirmed Live switch.
  - _Requirements: OKX-2 (accidental live order prevention)_

- [x] 25. Write OkxExecutionService unit tests
  - File: `05_Tests/ThatsMyQuant.Tests/Execution/OkxExecutionServiceTests.cs`
  - Test: HMAC-SHA256 signature is computed correctly for a known timestamp + method + path + body combination (golden-value test).
  - Test: when `IsDemo = true`, the outgoing request contains header `x-simulated-trading: 1`.
  - Test: when `IsDemo = false`, the outgoing request does NOT contain `x-simulated-trading` header.
  - Test: `PlaceOrderAsync` with insufficient balance returns a failed result without throwing.
  - Test: credential hot-reload — after updating keys via `IConfiguration` reload, the next request uses the new HMAC key.
  - _Requirements: OKX-3 (execution correctness under demo/live modes)_

- [x] 26. Checkpoint Sprint 5
  - Run `dotnet build ThatsMyQuant.sln` — assert 0 errors, 0 warnings.
  - Run `dotnet test` — assert all tests pass including new OKX tests.
  - Manually verify: start `Execution.Worker`, set Demo mode in Dashboard, confirm `x-simulated-trading: 1` appears in outgoing OKX requests (check logs).
  - _Requirements: OKX-1, OKX-2, OKX-3_
