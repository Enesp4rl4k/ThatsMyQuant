# Implementation Plan: Watchlist Exchange Sync

## Overview

Implement the "Sync with Exchanges" feature by following the sequence below. Each step builds on the previous, ending with the UI wired to the fully-tested orchestration logic. All code is C#.

## Tasks

- [x] 1. Add `ExchangeSyncException` to `06_Contracts`
  - Create `06_Contracts/Exceptions/ExchangeSyncException.cs` as a `sealed` exception with `ExchangeName` (string) and `Detail` (string) properties; the `Message` is formatted as `"[{ExchangeName}] {Detail}"`
  - _Requirements: 1.6, 1.7_

- [x] 2. Add `SymbolNormalizer` to `06_Contracts`
  - Create `06_Contracts/Helpers/SymbolNormalizer.cs` as a pure static class with no dependencies
  - Implement `ToCanonical(string symbol)` — removes hyphens, uppercases (e.g. `BTC-USDT` → `BTCUSDT`)
  - Implement `ToOkx(string canonical)` — splits on known quote assets (`USDT`, `USDC`, `BTC`, `ETH`, `EUR`, `TRY`) and inserts hyphen (e.g. `BTCUSDT` → `BTC-USDT`); returns canonical unchanged if no match
  - Implement `IsValid(string canonical)` — returns `true` iff the string matches `^[A-Z][A-Z0-9]{1,19}$`
  - _Requirements: 2.1, 2.2, 2.3, 2.4, 2.5_

- [x] 3. Property tests for `SymbolNormalizer`
  - [x] 3.1 Write property tests in `05_Tests/ThatsMyQuant.Tests/Watchlist/SymbolNormalizerTests.cs` using FsCheck.Xunit 3.*
    - Create the `Watchlist/` subdirectory under the test project
    - Tag each test with `// Feature: watchlist-exchange-sync, Property N`
    - **Property 1: OKX round-trip** — generate valid OKX symbols `{BASE}-{QUOTE}` where QUOTE ∈ `["USDT","USDC","BTC","ETH","EUR","TRY"]`; assert `ToCanonical(ToOkx(ToCanonical(s))) == ToCanonical(s)`
    - **Property 2: Idempotence** — for arbitrary non-null strings; assert `ToCanonical(ToCanonical(x)) == ToCanonical(x)`
    - **Property 5: Format invariant** — for strings matching `^[A-Z][A-Z0-9]{1,19}$` assert `IsValid` returns `true`; for strings that do not match assert `IsValid` returns `false`
    - Minimum 100 iterations per property
    - **Validates: Requirements 2.3, 2.4, 2.5, 9.1, 9.2, 9.6**

- [x] 4. Extend `IExchangeService` with `GetFavoritesAsync`
  - Add `Task<List<string>> GetFavoritesAsync();` to `06_Contracts/Interfaces/IExchangeService.cs`
  - XML doc comment: returns canonical symbols (uppercase, no hyphens, e.g. `"BTCUSDT"`)
  - _Requirements: 1.1_

- [x] 5. Implement `OkxExecutionService.GetFavoritesAsync`
  - Add `GetFavoritesAsync()` to `03_Infrastructure/Data.Persistence/Services/OkxExecutionService.cs`
  - Call `GET /api/v5/account/instruments?instType=SPOT` with the existing HMAC-SHA256 + Base64 auth headers (`OK-ACCESS-KEY`, `OK-ACCESS-SIGN`, `OK-ACCESS-TIMESTAMP`, `OK-ACCESS-PASSPHRASE`); include `x-simulated-trading: 1` when `IsDemoMode` is true
  - On non-2xx HTTP response throw `ExchangeSyncException("OKX", $"HTTP {statusCode}: {body}")`
  - On response body `code != "0"` throw `ExchangeSyncException("OKX", $"code={code}: {msg}")`
  - Filter data array to items where `state == "live"`; extract `instId`, call `SymbolNormalizer.ToCanonical`, discard entries where `SymbolNormalizer.IsValid` returns false
  - Return the resulting `List<string>`
  - _Requirements: 1.2, 1.4, 1.6, 1.8_

- [x] 6. Implement `BinanceExecutionService.GetFavoritesAsync`
  - Add `GetFavoritesAsync()` to `03_Infrastructure/Data.Persistence/Services/BinanceExecutionService.cs`
  - Add private static `HashSet<string> SkippedAssets` = `{ "USDT", "BUSD", "BNB", "USDC", "FDUSD" }` (OrdinalIgnoreCase)
  - Call `GET /api/v3/account?timestamp={ts}&signature={sig}` using the existing HMAC-SHA256 hex `ComputeSignature` helper
  - On non-2xx HTTP response throw `ExchangeSyncException("Binance", $"HTTP {statusCode}: {body}")`
  - Filter `balances` array: skip assets in `SkippedAssets`; skip where `free + locked <= 0`; for each remaining asset call `SymbolNormalizer.ToCanonical(asset + "USDT")` and include only entries where `IsValid` returns true
  - Return the resulting `List<string>`
  - _Requirements: 1.3, 1.5, 1.7_

- [x] 7. Implement `MockBrokerService.GetFavoritesAsync`
  - Add `GetFavoritesAsync()` to `03_Infrastructure/Data.Persistence/Services/MockBrokerService.cs`
  - Return `Task.FromResult(new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT" })`
  - _Requirements: 1.1_

- [x] 8. Add sync state and command to `MainViewModel`
  - Add `[ObservableProperty] private bool _isSyncingWatchlist = false;` and `[ObservableProperty] private string _syncStatusMessage = string.Empty;` to `04_UI/ThatsMyQuant.Dashboard/ViewModels/MainViewModel.cs`
  - Add `private bool CanSyncWatchlist() => !IsSyncingWatchlist;`
  - Add `[RelayCommand(CanExecute = nameof(CanSyncWatchlist))] private async Task SyncWatchlist()` implementing the full `SyncOrchestrator` logic:
    - Set `IsSyncingWatchlist = true` and `SyncStatusMessage = "Syncing with exchanges..."`
    - Load DPAPI credentials from `exchange_credentials.dpapi` using `ProtectedData.Unprotect(DataProtectionScope.CurrentUser)` — same pattern as `ExchangeSettingsViewModel.LoadCredentialsDpapi`; if file missing or decryption throws, treat that exchange as unavailable
    - Construct temporary `HttpClient` instances for OKX and Binance; call `GetFavoritesAsync()` on each inside separate try/catch blocks catching `ExchangeSyncException`; log each failure at Warning level with `_logger.LogWarning`
    - Clear decrypted credential byte arrays with `Array.Clear` immediately after constructing auth headers
    - Merge and deduplicate: union both symbol lists, filter with `SymbolNormalizer.IsValid`, deduplicate case-insensitively, exclude symbols already in `WatchlistItems`
    - For each new symbol: append `new WatchlistItemViewModel { Symbol = symbol }` to `WatchlistItems`; call `_marketDataService.GetLivePriceAsync(symbol, "CRYPTO")` in a try/catch — set `Price` if > 0, leave at 0 on exception
    - After all prices: for each new symbol call `await AnalyzeSymbol(symbol, "CRYPTO", MarketOverview.SelectedTimeframe)` in a try/catch — log failures with `AddSystemLog`, continue to next symbol
    - Set `SyncStatusMessage` via `BuildStatusMessage(okxError, binanceError, newSymbols.Count)`:
      - Both failed → `"Sync failed: unable to reach OKX and Binance."`
      - One failed → `$"Partial sync: {failedName} unavailable. {n} symbols imported."`
      - Both succeeded → `$"Sync complete. {n} new symbols added."`
    - Set `IsSyncingWatchlist = false`
  - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7, 4.1, 4.2, 4.3, 5.1, 5.2, 5.3, 5.4, 6.1, 6.2, 6.3, 6.4, 7.1, 7.2, 7.3, 7.4, 7.5, 7.6, 8.1, 8.2, 8.3, 8.4_

- [x] 9. Property tests for merge/dedup logic
  - [x] 9.1 Create `05_Tests/ThatsMyQuant.Tests/Watchlist/WatchlistMergeTests.cs` using FsCheck.Xunit 3.*
    - Tag each test with `// Feature: watchlist-exchange-sync, Property N`
    - **Property 3: Union-with-dedup** — generate arbitrary lists of canonical symbols for `existing`, `okxSymbols`, and `binanceSymbols`; run the merge/dedup logic (inline or via a pure helper extracted from `MainViewModel`); assert every unique symbol from all three inputs appears exactly once in the result
    - **Property 4: No-removal** — same generators; assert `result.Count >= existing.Count` and every symbol in `existing` is still present in `result`
    - Minimum 100 iterations per property
    - **Validates: Requirements 4.1, 4.2, 4.3, 4.4, 4.5, 9.3, 9.4, 9.5**

- [x] 10. Add WATCHLIST section to `MainWindow.xaml`
  - In `04_UI/ThatsMyQuant.Dashboard/MainWindow.xaml`, add the WATCHLIST `Border` block to the right sidebar (`Grid.Column="1"`) below existing content
  - `InverseBooleanConverter` already exists at `04_UI/ThatsMyQuant.Dashboard/Converters/InverseBooleanConverter.cs` — verify it is registered in `App.xaml` resources before referencing it; add registration if missing
  - The block contains: `TextBlock` "WATCHLIST" label, `ItemsControl` bound to `WatchlistItems` with a `DataTemplate` showing `Symbol`, `Price` (F2), and `ChangeText` columns, a `Button` with `Content="SYNC EXCHANGES"` bound to `SyncWatchlistCommand` with `IsEnabled` bound via `InverseBooleanConverter`, and a `TextBlock` bound to `SyncStatusMessage`
  - _Requirements: 3.1, 3.2, 3.5, 3.6_

- [x] 11. Checkpoint — build and all tests pass
  - Ensure the solution builds without errors or warnings introduced by this feature
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for a faster MVP
- `InverseBooleanConverter` already exists — do not create a duplicate
- Property tests 3.1 and 9.1 test the merge logic in isolation; extract a pure static helper from `MainViewModel` if needed to make it unit-testable without a full WPF host
- Each task references specific requirements for traceability
