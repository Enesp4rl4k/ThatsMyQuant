# Requirements Document

## Introduction

The Watchlist Exchange Sync feature adds a "Sync with Exchanges" capability to the TMQ Dashboard Watchlist. When triggered, the system connects to OKX and Binance using the user's existing DPAPI-encrypted API keys, fetches active/favorited instruments from each exchange, merges and de-duplicates them into the `WatchlistItems` collection, enriches each new symbol with a live price, and immediately triggers AI analysis for each imported symbol.

Because neither OKX nor Binance exposes a native "favorites" API, the feature uses proxy strategies: OKX uses the authenticated instruments endpoint filtered to live SPOT instruments the user has interacted with; Binance uses non-zero balance holdings from `/api/v3/account` as a proxy for active symbols.

## Glossary

- **SyncOrchestrator**: The component (part of `MainViewModel`) responsible for coordinating the full sync workflow — credential loading, exchange calls, merge, price enrichment, and AI trigger.
- **ExchangeAdapter**: Either `OkxExecutionService` or `BinanceExecutionService`, extended to implement `GetFavoritesAsync`.
- **WatchlistItems**: The `ObservableCollection<WatchlistItemViewModel>` in `MainViewModel` that backs the watchlist UI.
- **CanonicalSymbol**: The normalized, uppercase, hyphen-free symbol format used internally (e.g., `BTCUSDT`). All symbols stored in `WatchlistItems` use this format.
- **OkxSymbol**: OKX exchange symbol format using hyphens (e.g., `BTC-USDT`).
- **BinanceSymbol**: Binance exchange symbol format with no separator (e.g., `BTCUSDT`).
- **DPAPI**: Windows Data Protection API used to encrypt/decrypt exchange credentials at rest under `CurrentUser` scope.
- **SyncStatusMessage**: A string property on `MainViewModel` surfaced in the UI to communicate sync progress and outcome.
- **IsSyncingWatchlist**: A boolean property on `MainViewModel` that is `true` while a sync operation is in progress.
- **PartialSync**: A sync outcome where at least one exchange succeeded and at least one failed.

---

## Requirements

### Requirement 1: Extend IExchangeService with GetFavoritesAsync

**User Story:** As a developer, I want a standard contract for fetching active symbols from any exchange, so that the SyncOrchestrator can work with OKX and Binance uniformly.

#### Acceptance Criteria

1. THE `IExchangeService` SHALL declare a method `GetFavoritesAsync()` returning `Task<List<string>>` where each string is a `CanonicalSymbol`.
2. WHEN `OkxExecutionService.GetFavoritesAsync()` is called, THE `ExchangeAdapter` SHALL authenticate using the DPAPI-decrypted OKX API key, secret, and passphrase via HMAC-SHA256 + Base64 signing.
3. WHEN `BinanceExecutionService.GetFavoritesAsync()` is called, THE `ExchangeAdapter` SHALL authenticate using the DPAPI-decrypted Binance API key and secret via HMAC-SHA256 hex signing.
4. THE `OkxExecutionService` SHALL fetch instruments from `/api/v5/account/instruments?instType=SPOT` and return the `instId` values converted to `CanonicalSymbol` format.
5. THE `BinanceExecutionService` SHALL fetch account data from `/api/v3/account`, filter assets with `free + locked > 0`, and return each asset name appended with `USDT` as a `CanonicalSymbol` (e.g., asset `BTC` → `BTCUSDT`).
6. IF the OKX API returns an HTTP error or a non-zero error code in the response body, THEN THE `OkxExecutionService` SHALL throw an `ExchangeSyncException` with the exchange name and error detail.
7. IF the Binance API returns an HTTP error, THEN THE `BinanceExecutionService` SHALL throw an `ExchangeSyncException` with the exchange name and error detail.
8. THE `OkxExecutionService.GetFavoritesAsync` SHALL return only symbols where `instType` is `SPOT` and `state` is `live`.

---

### Requirement 2: Symbol Normalization

**User Story:** As a developer, I want all exchange-specific symbol formats converted to a single canonical form, so that de-duplication and storage are consistent regardless of source exchange.

#### Acceptance Criteria

1. WHEN an `OkxSymbol` is normalized, THE `SyncOrchestrator` SHALL remove hyphens and convert to uppercase (e.g., `BTC-USDT` → `BTCUSDT`).
2. WHEN a `BinanceSymbol` is normalized, THE `SyncOrchestrator` SHALL convert to uppercase (e.g., `btcusdt` → `BTCUSDT`).
3. THE `SyncOrchestrator` SHALL produce idempotent normalization: normalizing an already-normalized `CanonicalSymbol` SHALL return the same value unchanged.
4. FOR ALL valid `OkxSymbol` values, converting to `CanonicalSymbol` and back to `OkxSymbol` SHALL produce a symbol equivalent to the original (round-trip property).
5. THE `SyncOrchestrator` SHALL discard any symbol that, after normalization, is an empty string or contains characters outside `[A-Z0-9]`.

---

### Requirement 3: Sync Command and Loading State

**User Story:** As a trader, I want a "Sync with Exchanges" button in the Watchlist UI, so that I can import my active exchange instruments into the watchlist with a single click.

#### Acceptance Criteria

1. THE `MainViewModel` SHALL expose a `SyncWatchlistCommand` (IRelayCommand) bound to the "Sync with Exchanges" button.
2. THE `MainViewModel` SHALL expose an `IsSyncingWatchlist` boolean property that is `false` by default.
3. WHEN `SyncWatchlistCommand` is executed, THE `SyncOrchestrator` SHALL set `IsSyncingWatchlist` to `true` before any exchange call is made.
4. WHEN the sync operation completes (successfully or with errors), THE `SyncOrchestrator` SHALL set `IsSyncingWatchlist` to `false`.
5. WHILE `IsSyncingWatchlist` is `true`, THE `SyncWatchlistCommand` SHALL not execute a second concurrent sync (CanExecute returns `false`).
6. THE `MainViewModel` SHALL expose a `SyncStatusMessage` string property initialized to an empty string.
7. WHEN `SyncWatchlistCommand` begins execution, THE `SyncOrchestrator` SHALL set `SyncStatusMessage` to `"Syncing with exchanges..."`.

---

### Requirement 4: Non-Destructive Merge and De-duplication

**User Story:** As a trader, I want my existing watchlist preserved when syncing, so that manually added symbols are never lost.

#### Acceptance Criteria

1. WHEN the sync completes, THE `SyncOrchestrator` SHALL preserve all pre-existing `WatchlistItems` entries unchanged.
2. WHEN a symbol returned by an exchange already exists in `WatchlistItems` (case-insensitive `CanonicalSymbol` match), THE `SyncOrchestrator` SHALL skip that symbol and not add a duplicate entry.
3. WHEN a symbol is new (not present in `WatchlistItems`), THE `SyncOrchestrator` SHALL append a new `WatchlistItemViewModel` with the `Symbol` set to the `CanonicalSymbol`.
4. FOR ALL combinations of existing watchlist contents and exchange-returned symbol lists, the count of `WatchlistItems` after sync SHALL be greater than or equal to the count before sync (no removals invariant).
5. FOR ALL combinations of existing watchlist contents and exchange-returned symbol lists, each `CanonicalSymbol` SHALL appear at most once in `WatchlistItems` after sync (uniqueness invariant).

---

### Requirement 5: Live Price Enrichment

**User Story:** As a trader, I want newly imported symbols to show a live price immediately, so that the watchlist is useful right after sync.

#### Acceptance Criteria

1. WHEN a new symbol is appended to `WatchlistItems`, THE `SyncOrchestrator` SHALL call `IMarketDataService.GetLivePriceAsync(symbol, "CRYPTO")` for that symbol.
2. WHEN `GetLivePriceAsync` returns a value greater than zero, THE `SyncOrchestrator` SHALL set the `WatchlistItemViewModel.Price` to the returned value.
3. IF `GetLivePriceAsync` throws an exception or returns zero for a symbol, THEN THE `SyncOrchestrator` SHALL leave `WatchlistItemViewModel.Price` at `0` and continue processing remaining symbols.
4. THE `SyncOrchestrator` SHALL fetch live prices for all new symbols before triggering AI analysis.

---

### Requirement 6: AI Analysis Trigger

**User Story:** As a trader, I want AI analysis to run automatically for each newly imported symbol, so that I get immediate insights without manually selecting each one.

#### Acceptance Criteria

1. WHEN price enrichment for a new symbol completes, THE `SyncOrchestrator` SHALL invoke `AnalyzeSymbol(symbol, "CRYPTO", defaultTimeframe)` for that symbol.
2. THE `SyncOrchestrator` SHALL trigger AI analysis only for symbols that were newly added during the current sync (not for pre-existing `WatchlistItems`).
3. IF `AnalyzeSymbol` throws an exception for a given symbol, THEN THE `SyncOrchestrator` SHALL log the error and continue triggering analysis for remaining new symbols.
4. THE `SyncOrchestrator` SHALL trigger AI analysis sequentially to avoid overwhelming the AI service with concurrent requests.

---

### Requirement 7: Partial Sync Resilience

**User Story:** As a trader, I want the sync to succeed for available exchanges even if one exchange is unreachable, so that a single failure doesn't block all imports.

#### Acceptance Criteria

1. IF `OkxExecutionService.GetFavoritesAsync` throws an `ExchangeSyncException`, THEN THE `SyncOrchestrator` SHALL continue to call `BinanceExecutionService.GetFavoritesAsync` and process its results.
2. IF `BinanceExecutionService.GetFavoritesAsync` throws an `ExchangeSyncException`, THEN THE `SyncOrchestrator` SHALL continue to process results from `OkxExecutionService.GetFavoritesAsync`.
3. WHEN both exchanges fail, THE `SyncOrchestrator` SHALL set `SyncStatusMessage` to `"Sync failed: unable to reach OKX and Binance."` and set `IsSyncingWatchlist` to `false`.
4. WHEN exactly one exchange fails, THE `SyncOrchestrator` SHALL set `SyncStatusMessage` to `"Partial sync: [ExchangeName] unavailable. [N] symbols imported."` where `[ExchangeName]` is the failing exchange name and `[N]` is the count of newly added symbols.
5. WHEN both exchanges succeed, THE `SyncOrchestrator` SHALL set `SyncStatusMessage` to `"Sync complete. [N] new symbols added."` where `[N]` is the count of newly added symbols.
6. THE `SyncOrchestrator` SHALL log each `ExchangeSyncException` at Warning level including the exchange name and exception message.

---

### Requirement 8: Credential Access

**User Story:** As a developer, I want the sync to reuse the existing DPAPI-encrypted credentials, so that no additional credential storage or UI is needed.

#### Acceptance Criteria

1. WHEN `SyncWatchlistCommand` is executed, THE `SyncOrchestrator` SHALL read OKX credentials (ApiKey, SecretKey, Passphrase) from `exchange_credentials.dpapi` using `ProtectedData.Unprotect` with `DataProtectionScope.CurrentUser`.
2. WHEN `SyncWatchlistCommand` is executed, THE `SyncOrchestrator` SHALL read Binance credentials (ApiKey, SecretKey) from `exchange_credentials.dpapi` using `ProtectedData.Unprotect` with `DataProtectionScope.CurrentUser`.
3. IF the `exchange_credentials.dpapi` file does not exist or decryption fails for an exchange, THEN THE `SyncOrchestrator` SHALL treat that exchange as unavailable and apply the partial sync rules from Requirement 7.
4. THE `SyncOrchestrator` SHALL clear decrypted credential byte arrays from memory immediately after constructing the authentication headers.

---

### Requirement 9: Correctness Properties for Testing

**User Story:** As a developer, I want well-defined correctness properties for the sync logic, so that property-based tests can verify invariants across arbitrary inputs.

#### Acceptance Criteria

1. FOR ALL non-empty lists of `OkxSymbol` strings, normalizing each to `CanonicalSymbol` and then converting back to `OkxSymbol` SHALL produce a list equivalent to the original (round-trip property).
2. FOR ALL non-empty lists of `CanonicalSymbol` strings, applying normalization twice SHALL produce the same result as applying it once (idempotence property).
3. FOR ALL combinations of an existing `WatchlistItems` list and an incoming symbol list from exchanges, the merged result SHALL contain every symbol from both inputs exactly once (union-with-dedup invariant).
4. FOR ALL combinations of an existing `WatchlistItems` list and an incoming symbol list, no symbol present before sync SHALL be absent after sync (no-removal invariant).
5. FOR ALL incoming symbol lists containing duplicates across OKX and Binance responses, the merged `WatchlistItems` SHALL contain each `CanonicalSymbol` at most once (cross-exchange dedup invariant).
6. FOR ALL valid `CanonicalSymbol` strings, the string SHALL match the pattern `^[A-Z][A-Z0-9]{1,19}$` (format invariant).
