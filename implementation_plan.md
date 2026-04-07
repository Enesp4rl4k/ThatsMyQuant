# Implementation Plan — Corrected Against Actual Codebase

> **Corrections applied from code review on 2025-04-05.**
> Every task was cross-referenced against the actual source files.
> ❌ marks original plan errors that were corrected.

---

## Critical Discoveries During Review

Before we start, here's what the code review revealed that **invalidates parts of the original plan**:

### Discovery 1: There are THREE MemoryPortfolioManagers, not two.
| # | File | Namespace | Lines | Pattern |
|:--|:--|:--|:--|:--|
| 1 | `02_Core/Application/Risk/MemoryPortfolioManager.cs` | `ThatsMyQuant.Application.Risk` | 65 | Simple ConcurrentDictionary skeleton |
| 2 | `02_Core/Application/Strategy/MemoryPortfolioManager.cs` | `ThatsMyQuant.Application.Strategy` | 289 | MAFE tracking, balance locking, Interlocked decimals |
| 3 | `03_Infrastructure/Data.Persistence/Services/MemoryPortfolioManager.cs` | `ThatsMyQuant.Infrastructure.Services` | 268 | **Lock-free atomic, venue-aware, scaled longs, Singleton** |

**#3 is the most sophisticated one** — it uses `Interlocked.CompareExchange` spin-loops, scaled-long (10^8) precision, venue isolation, and atomic reservations. It implements `ISharedPortfolioStateManager`. It's the one registered via `MemoryPortfolioManager.Instance` (private constructor, Lazy singleton). **The existing test file (`MemoryPortfolioManagerTests.cs`) tests THIS one, not the Strategy one.**

The DI registration at `DependencyInjection.cs:22` (`services.AddSingleton<MemoryPortfolioManager>()`) registers the **Strategy namespace** one (because the file imports `using ThatsMyQuant.Application.Strategy;`). Meanwhile `CircuitBreakerService` (line 17: `private readonly MemoryPortfolioManager _portfolio;`) is in the **same namespace** — so it resolves to the Strategy one.

**Implication:** We need to decide which is the canonical implementation. The Infrastructure one (#3) is technically superior but is a Singleton accessed via `.Instance` (not DI-friendly). The Strategy one (#2) is DI-registered. Both need to exist, but the naming collision must be resolved.

### Discovery 2: Hardcoded path exists in FOUR files, not one.
```
01_Services/QuantEngine.Worker/Program.cs       (line 66)
01_Services/Execution.Worker/Program.cs          (line 66)
01_Services/Listener.Api/Program.cs              (line 43)
04_UI/ThatsMyQuant.Dashboard/App.xaml.cs          (line 145)
```

### Discovery 3: Test coverage is NOT zero.
`05_Tests/ThatsMyQuant.Tests/Infrastructure/MemoryPortfolioManagerTests.cs` already exists with 5 tests covering the Infrastructure version's atomic operations. The Sprint 2 bug condition test ("verify test file does not exist") would **PASS** on unfixed code, not fail — invalidating the methodology.

### Discovery 4: `CircuitBreakerService` IS already registered in DI.
`DependencyInjection.cs:31`: `services.AddSingleton<CircuitBreakerService>();` — it's live.

### Discovery 5: `DeadMansSwitch` requires `IAuditLogger` — not registered anywhere.
```csharp
// CircuitBreakers.cs:51
public DeadMansSwitch(IAuditLogger auditLogger) { ... }
```
`IAuditLogger` is defined in `06_Contracts/Interfaces/IHsmAndAudit.cs:19` but has NO implementation registered in any DI container.

---

## Sprint 0 — Structural Fixes (CORRECTED)

### Task 1: Bug Condition Exploration Tests

- **Test B1: Duplicate Class Detection** (structural assertion, not compilation test)
  - ❌ _Original: "Verify that importing both namespaces fails to compile" — can't test compilation at runtime_
  - ✅ **Corrected:** Scan the solution for all `.cs` files defining a class named `MemoryPortfolioManager`. Assert count >= 2. This proves the ambiguity risk exists structurally.
  - **Implementation:** Use `Directory.GetFiles("*.cs", SearchOption.AllDirectories)` + regex `class\s+MemoryPortfolioManager`
  - **Expected:** Test PASSES (finds 3 files), confirming the bug condition exists

- **Test B3: DeadMansSwitch Not Resolvable from DI**
  - ✅ Original plan is correct — build a service provider from `AddThatsMyQuantApplication()` and assert `sp.GetService<DeadMansSwitch>()` returns null
  - **Additional assertion:** Also verify `sp.GetService<IAuditLogger>()` returns null (the hidden dependency)
  - **Expected:** Test PASSES (both return null), confirming the bug condition

- **Test B4: Hardcoded Path Detection**
  - ❌ _Original: Check only `Program.cs`_
  - ✅ **Corrected:** Scan ALL FOUR files for the hardcoded path string `C:\Users\parlak`
  - Files: `QuantEngine.Worker/Program.cs`, `Execution.Worker/Program.cs`, `Listener.Api/Program.cs`, `App.xaml.cs`
  - **Expected:** Test PASSES (finds 4 occurrences), confirming the bug condition

### Task 2: Preservation Property Tests

- **Property P1: DI registrations that already work must continue working**
  - Build `IServiceCollection`, call `AddThatsMyQuantApplication()`
  - Assert `CircuitBreakerService` is resolvable (it IS registered at line 31)
  - Assert `MemoryPortfolioManager` (Strategy) is resolvable (registered at line 22)
  - Assert `TickPipeline` is resolvable (registered at line 29)
  - Assert `FeatureEngineeringAgent` is resolvable (registered at line 21)
  - **Expected:** All PASS on unfixed code

- **Property P2: Existing test suite passes**
  - Run `dotnet test` and record actual test count (don't hardcode "227" — verify first)
  - **Expected:** All PASS on unfixed code

### Task 3: Delete Duplicate MemoryPortfolioManagers

- ❌ _Original: Delete only `Application/Risk/MemoryPortfolioManager.cs`_
- ✅ **Corrected:** Delete the 65-line skeleton at `02_Core/Application/Risk/MemoryPortfolioManager.cs`
- ✅ **Keep:** `02_Core/Application/Strategy/MemoryPortfolioManager.cs` (289 lines — used by DI + CircuitBreakerService)
- ✅ **Keep:** `03_Infrastructure/Data.Persistence/Services/MemoryPortfolioManager.cs` (268 lines — used for venue-level execution via Singleton)
- **Note:** The Infrastructure version has a different API surface (`SetBalance(VenueKind, string, decimal)`) and implements `ISharedPortfolioStateManager`. It doesn't conflict in DI because it's accessed via `MemoryPortfolioManager.Instance`, not DI. But **the class NAME still collides** — document this as tech debt for Sprint 4 (rename the Infrastructure one to `VenuePortfolioManager`).
- **After deletion:** Verify `RiskAgent.cs` line 6 (`using ThatsMyQuant.Application.Risk;`) still compiles — check if it imports anything else from that namespace. If `MemoryPortfolioManager` was the only thing it used from `Application.Risk`, the import becomes a dead using.

### Task 4: Fix Hardcoded SQLite Database Path

- ❌ _Original: Use `AppContext.BaseDirectory` — this resolves to `bin/Debug/net9.0/`, creating an empty DB_
- ✅ **Corrected:** Move to `appsettings.json` configuration across ALL FOUR files:
  
  **Step 4.1:** Add connection string to each service's `appsettings.json`:
  ```json
  {
    "ConnectionStrings": {
      "Quant": "Data Source=quant.db;Cache=Shared"
    }
  }
  ```
  This uses a relative path — SQLite resolves relative to `Directory.GetCurrentDirectory()`.

  **Step 4.2:** Update all four files to read from configuration:
  ```csharp
  // Replace:
  options.UseSqlite("Data Source=C:\\Users\\parlak\\Downloads\\ThatsMyQuant\\quant.db;Cache=Shared")
  // With:
  options.UseSqlite(builder.Configuration.GetConnectionString("Quant") 
      ?? "Data Source=quant.db;Cache=Shared")
  ```

  **Step 4.3:** For Dashboard (`App.xaml.cs`), which doesn't use `builder.Configuration`:
  ```csharp
  // Use relative path directly:
  options.UseSqlite("Data Source=quant.db;Cache=Shared");
  ```

  Files to modify:
  1. `01_Services/QuantEngine.Worker/Program.cs` (line 66)
  2. `01_Services/Execution.Worker/Program.cs` (line 66)
  3. `01_Services/Listener.Api/Program.cs` (line 43)
  4. `04_UI/ThatsMyQuant.Dashboard/App.xaml.cs` (line 145)

### Task 5: Register DeadMansSwitch and HeartbeatMonitor in DI

- **5.0 (NEW — missed in original plan):** Create `IAuditLogger` implementation first.
  - ❌ _Original plan didn't account for this dependency_
  - `DeadMansSwitch(IAuditLogger auditLogger)` requires `IAuditLogger`
  - `IAuditLogger` is defined at `06_Contracts/Interfaces/IHsmAndAudit.cs:19` but has NO implementation
  - **Create:** `03_Infrastructure/Data.Persistence/Services/ConsoleAuditLogger.cs`
  ```csharp
  namespace ThatsMyQuant.Infrastructure.Services;
  
  public sealed class ConsoleAuditLogger : IAuditLogger
  {
      private readonly ILogger<ConsoleAuditLogger> _logger;
      
      public ConsoleAuditLogger(ILogger<ConsoleAuditLogger> logger) => _logger = logger;
      
      public Task LogSecurityEventAsync(string user, string category, string action, string detail)
      {
          _logger.LogWarning("[AUDIT] User={User} Category={Category} Action={Action} Detail={Detail}",
              user, category, action, detail);
          return Task.CompletedTask;
      }
  }
  ```
  - Register in DI: `services.AddSingleton<IAuditLogger, ConsoleAuditLogger>();`

- **5.1:** Add DI registrations in `DependencyInjection.cs`:
  ```csharp
  services.AddSingleton<IAuditLogger, ConsoleAuditLogger>();  // Must come first
  services.AddSingleton<DeadMansSwitch>();
  services.AddSingleton<HeartbeatMonitor>();
  ```
  Add `using ThatsMyQuant.Application.HftInfrastructure;` to imports.

- **5.2:** Wire heartbeat call in `TickPipeline.cs`
  - Inject `HeartbeatMonitor` into constructor (add parameter after `ITickLogger tickLogger`)
  - In `ProcessTicksAsync`, after `_featureAgent.ProcessTickNatively(...)` (line 85), add:
  ```csharp
  _heartbeatMonitor.RecordHeartbeat();
  ```

### Task 6: Checkpoint Sprint 0
- Run `dotnet test` — all tests pass
- Run `dotnet build` — 0 errors, 0 warnings
- Verify B1 now finds only 2 `MemoryPortfolioManager` classes (Risk one deleted)
- Verify B3 now resolves `DeadMansSwitch` from DI
- Verify B4 finds 0 hardcoded paths

---

## Sprint 1 — Interface Abstraction (CORRECTED)

### Task 7: Bug Condition Exploration

- ✅ Original plan is correct — verify `CircuitBreakerService` constructor requires concrete `MemoryPortfolioManager`, blocking mock injection
- **Confirmed:** `CircuitBreakerService.cs:43` — `MemoryPortfolioManager portfolio` (concrete type)

### Task 8: Preservation Property Tests

- ✅ Original plan is correct — verify `CheckOrderAllowed()` semantics
- **Note:** `CheckOrderAllowed()` calls `_portfolio.GetDrawdownPercent()` (line 70). The `IPortfolioManager` interface MUST include this method.

### Task 9: Extract IPortfolioManager

- **9.1:** Create `06_Contracts/Interfaces/IPortfolioManager.cs`
  - ❌ _Original: Listed methods that may not exist on the Strategy version_
  - ✅ **Corrected:** Only include methods actually called by consumers. Check `CircuitBreakerService` uses: `GetDrawdownPercent()`. Check `RiskAgent` uses: verify by reading field usage.
  - Required interface surface (minimum):
    ```csharp
    public interface IPortfolioManager
    {
        decimal GetDrawdownPercent();
        // Add other methods as discovered from consumer analysis
    }
    ```

- **9.2-9.4:** ✅ Original plan is correct — implement on `MemoryPortfolioManager`, update `CircuitBreakerService` and `RiskAgent`

- **9.5:** Update DI registration
  - ✅ Original approach is correct: forward interface resolution to same singleton
  ```csharp
  services.AddSingleton<IPortfolioManager>(sp => sp.GetRequiredService<MemoryPortfolioManager>());
  ```

- **9.6:** Update CircuitBreakerService tests
  - ❌ _Original: "Locate existing `CircuitBreakerServiceTests.cs`" — verify it exists first_
  - ✅ **Corrected:** Check if tests exist. If not, create them. If they do, update to use `Substitute.For<IPortfolioManager>()`

### Task 10: Checkpoint Sprint 1
- ✅ Original plan is correct

---

## Sprint 2 — Test Coverage (CORRECTED)

### Task 11: Bug Condition Exploration

- ❌ _Original: "Verify `MemoryPortfolioManagerTests.cs` does not exist" — IT DOES EXIST_
- ✅ **Corrected findings:**
  - `05_Tests/ThatsMyQuant.Tests/Infrastructure/MemoryPortfolioManagerTests.cs` **already exists** with 5 tests. These test the **Infrastructure version**, not the Strategy version.
  - The bug condition should instead be: "Verify that NO tests exist for the **Strategy** `MemoryPortfolioManager.GetDrawdownPercent()`"
  - `FeatureEngineeringAgent` test check is still valid — verify no test file exists
  - `SignalPipelineIntegrationTests` check is still valid — verify no test file exists

### Task 13: MemoryPortfolioManager Unit Tests

- ❌ _Original: Tests described methods like `Lock()`, `Unlock()`, `OpenPosition()`, `ClosePosition()` — need to verify these exist on the Strategy version_
- ✅ **Corrected:** The test file should target the **Strategy** `MemoryPortfolioManager` and specifically test:
  - `GetDrawdownPercent()` — this is the money-critical method used by `CircuitBreakerService`
  - `UpdateFromExecutionReport()` — execution path
  - The methods listed in the original plan should be verified against the actual class API before writing tests

### Task 14: FeatureEngineeringAgent Tests
- ✅ Original plan is correct — no changes needed

### Task 15: Signal Pipeline Integration Test
- ✅ Original plan is correct — no changes needed
- **Note:** May need to register `IAuditLogger` in the test host as well (Sprint 0 dependency)

### Task 16: Checkpoint Sprint 2
- ✅ Original plan is correct

---

## Sprint 3 — Hot-Path Performance Fixes (CORRECTED)

### Task 17: Bug Condition Exploration

- **B9 (Lock contention):**
  - ❌ _Original: "Measure p99 latency — assert below lock-free threshold" — flaky in CI_
  - ✅ **Corrected:** Use a **structural assertion** instead:
    - Read `FeatureEngineeringAgent.cs` source via `File.ReadAllText()`
    - Assert the file does NOT contain the string `lock(` or `lock (`
    - Or: use reflection to verify the class does not have a field of type `object` named `_syncLock`
    - This is deterministic and CI-safe

- **B10 (O(N×W) SMA):**
  - ✅ Original approach works, but clarify the assertion:
  - Assert `CalculateBatchSMA` does NOT call `SumVectorized` in a loop — either via profiling or source inspection
  - Alternative structural assertion: verify the method body contains a running-sum subtraction pattern (`prices[i - 1]`)

- **B11 (DateTime.UtcNow):**
  - ✅ Original plan is correct — verify source uses `Environment.TickCount64`

### Task 19: Replace Lock in FeatureEngineeringAgent

- ❌ _Original: Double-buffer swap has a race condition (both variables point to same array after Exchange)_
- ✅ **Corrected:** Use **publish-then-forget** pattern:
  ```csharp
  // Instead of double-buffer swap:
  private volatile float[] _currentTensor = new float[5];
  
  public void ProcessTickNatively(double price)
  {
      // Fresh array each call — 20 bytes gen0, trivial
      var tensor = new float[5];
      double rsi = _rsi14.UpdateAndGetRSI(price);
      _emaFast.UpdateEma(price);
      _movingAverageBuffer.Add(price);
  
      tensor[0] = SafeFloat(rsi / 100.0);
      tensor[1] = _emaFast.Ema > 0 ? SafeFloat(price / _emaFast.Ema, 1.0f) : 1.0f;
      tensor[2] = _movingAverageBuffer.MovingAverage > 0
          ? SafeFloat(price / _movingAverageBuffer.MovingAverage, 1.0f) : 1.0f;
      // Preserve OFI and foundation data from current tensor
      var current = _currentTensor;
      tensor[3] = current[3];
      tensor[4] = current[4];
  
      // Atomic publish — readers see old or new, never partial
      Volatile.Write(ref _currentTensor, tensor);
  }
  
  public void CopyTo(Span<float> destination)
  {
      // No lock — read the stable reference
      Volatile.Read(ref _currentTensor).AsSpan().CopyTo(destination);
  }
  ```
  **Why this works:** The writer creates a brand-new array, fills it completely, then publishes the reference atomically via `Volatile.Write`. Readers always see a fully-formed tensor. The old array becomes gen0 garbage (20 bytes per tick × 10K ticks/sec = 200KB/sec — GC won't even notice).

### Task 20: O(N) Sliding-Window SMA
- ✅ Original plan is correct — no changes needed

### Task 21: DateTime.UtcNow → Environment.TickCount64
- ✅ Original plan is correct — no changes needed

### Tasks 22-23: Verification & Checkpoint
- ✅ Original plan is correct — re-run same tests, don't write new ones
- **Task 22.1 B9 correction:** Verify structural assertion (no `lock(` in source) instead of p99 latency

---

## Sprint 4 (Tech Debt — Added)

### Task 24: Rename Infrastructure MemoryPortfolioManager

After Sprints 0-3 are stable, rename `03_Infrastructure/Data.Persistence/Services/MemoryPortfolioManager.cs` to `VenuePortfolioManager.cs` to eliminate the class-name collision. Update:
- Class name: `VenuePortfolioManager`
- Lazy instance field
- Test file name and class
- Any consumers referencing the Infrastructure version

---

## Summary of All Corrections

| # | Original Plan | Correction | Root Cause |
|:--|:--|:--|:--|
| 1 | B1: compilation test at runtime | Structural assertion (grep for class definitions) | Can't test compilation from unit tests |
| 2 | Two `MemoryPortfolioManager` classes | **Three** classes exist (Risk, Strategy, Infrastructure) | Missed the Infrastructure version |
| 3 | Zero test coverage on portfolio | Infrastructure version already has 5 tests | Missed existing test file |
| 4 | `CircuitBreakerService` not in DI | It IS registered at `DependencyInjection.cs:31` | Build output was stale |
| 5 | Fix hardcoded path in 1 file | Hardcoded path exists in **4 files** | Didn't grep the full solution |
| 6 | `AppContext.BaseDirectory` fix | Use `appsettings.json` config (BaseDirectory = bin/Debug/) | AppContext resolves to output dir |
| 7 | Register DeadMansSwitch directly | Must create `IAuditLogger` impl first (hidden dependency) | Constructor requires IAuditLogger |
| 8 | Double-buffer swap pattern | Publish-then-forget (race condition in original) | Writer modifies reader's buffer after swap |
| 9 | p99 latency test for lock removal | Structural assertion (no `lock(` in source) | Latency tests are flaky in CI |
| 10 | Sprint 2 B11: "verify test file doesn't exist" | Test file DOES exist — change assertion target | Missed existing Infrastructure tests |
