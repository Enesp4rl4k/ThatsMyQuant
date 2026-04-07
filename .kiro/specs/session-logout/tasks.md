# Implementation Plan: Session Logout

## Overview

Implement explicit logout and idle-timeout session termination for ThatsMyQuant.Dashboard. The
work is split into eight incremental steps: config record → idle monitor → idle timeout service →
warning dialog → MainViewModel changes → MainWindow wiring → XAML button → App.xaml.cs re-login
flow. Property-based tests (FsCheck.Xunit) cover config invariants, monitor timing, and logout
idempotence.

## Tasks

- [x] 1. Add `IdleTimeoutConfig` record with validation
  - Create `04_UI/ThatsMyQuant.Dashboard/Auth/IdleTimeoutConfig.cs`
  - Expose `int TimeoutSeconds` (default 300) and `int WarningLeadSeconds` (default 30)
  - Throw `ArgumentException` in constructor when `WarningLeadSeconds >= TimeoutSeconds`
  - Provide a static `Default` singleton and a `Disabled` factory (`TimeoutSeconds = 0` bypasses validation)
  - _Requirements: 2.1, 2.2, 2.3, 2.4, 7.5_

  - [x] 1.1 Write property tests for `IdleTimeoutConfig` invariants
    - **Property 1: Invariant enforcement** — for all `(ts, wl)` where `wl >= ts && ts > 0`, constructor throws `ArgumentException`
    - **Property 2: Valid construction** — for all `(ts, wl)` where `0 < wl < ts`, construction succeeds and properties round-trip
    - **Validates: Requirements 2.3, 7.5**
    - Add `<Compile Include>` link in `ThatsMyQuant.Tests.csproj` for `IdleTimeoutConfig.cs`

- [x] 2. Implement `IdleMonitor` class
  - Create `04_UI/ThatsMyQuant.Dashboard/Auth/IdleMonitor.cs`
  - Own a `DispatcherTimer` with 1-second tick; expose `Reset()` method that zeroes elapsed counter
  - Raise `IdleWarning` event at `TimeoutSeconds − WarningLeadSeconds` elapsed seconds
  - Raise `IdleElapsed` event at `TimeoutSeconds` elapsed seconds
  - Accept `IdleTimeoutConfig` in constructor; if `TimeoutSeconds == 0` never start the timer
  - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6_

  - [x] 2.1 Write property test for `IdleMonitor` warning-before-elapsed ordering
    - **Property 3: Warning precedes elapsed** — for all valid configs, `IdleWarning` is always raised before `IdleElapsed` in the same idle cycle
    - **Validates: Requirements 3.3, 3.4, 7.4**
    - Add `<Compile Include>` link in `ThatsMyQuant.Tests.csproj` for `IdleMonitor.cs`

  - [x] 2.2 Write property test for `IdleMonitor` reset behaviour
    - **Property 4: Reset restores full timeout** — after `Reset()`, elapsed time until next `IdleElapsed` equals `TimeoutSeconds ± 2 s`
    - **Validates: Requirements 3.2, 7.8**

- [x] 3. Implement `IdleTimeoutService`
  - Create `04_UI/ThatsMyQuant.Dashboard/Services/IdleTimeoutService.cs`
  - Constructor accepts `IdleTimeoutConfig`, `IAuditLogger`, and a `Func<MainViewModel>` (or direct reference injected later)
  - On `IdleWarning`: open `SessionLockDialog` on UI thread; on "Stay Logged In" call `IdleMonitor.Reset()`; on `IdleElapsed` while dialog open, close dialog and proceed to lock
  - On `IdleElapsed` (no dialog dismissal): call `CurrentSession.Clear()`, call `MainViewModel.StopBackgroundTimers()`, call `_tradingLoopService.Stop()`, write audit log entry (`"IdleTimeout"`), then invoke the re-login callback
  - If `SessionLockDialog` is closed by any means other than "Stay Logged In", treat as idle-logout trigger (Req 4.5)
  - Read config from `IConfiguration` section `"IdleTimeout"`; fall back to `IdleTimeoutConfig.Default` if absent; disable if `TimeoutSeconds == 0`
  - _Requirements: 4.1, 4.2, 4.3, 4.4, 4.5, 5.1, 5.2, 5.3, 5.6, 6.2, 6.3, 2.5, 2.6, 2.7_

- [x] 4. Create `SessionLockDialog` XAML and code-behind
  - Create `04_UI/ThatsMyQuant.Dashboard/Views/SessionLockDialog.xaml` and `.xaml.cs`
  - Show remaining seconds as a countdown label, updated every second via a `DispatcherTimer`
  - "Stay Logged In" button sets `DialogResult = true` and closes the window
  - Any other close path (Alt+F4, X button) sets `DialogResult = false`
  - Expose `int RemainingSeconds` property so `IdleTimeoutService` can push updates
  - _Requirements: 4.1, 4.2, 4.3, 4.5_

- [x] 5. Update `MainViewModel`: promote timer fields, add `StopBackgroundTimers()`, add `LogoutCommand`, inject `IAuditLogger`
  - In `04_UI/ThatsMyQuant.Dashboard/ViewModels/MainViewModel.cs`:
    - Promote the two local `DispatcherTimer` variables in the constructor to private fields `_clockTimer` and `_queueTimer`
    - Add `public void StopBackgroundTimers()` that calls `.Stop()` on both fields and calls `_tradingLoopService.Stop()`
    - Add `IAuditLogger _auditLogger` constructor parameter and store it
    - Add `[RelayCommand] private async Task Logout()` that: writes audit log (`"ExplicitLogout"`), calls `StopBackgroundTimers()`, calls `CurrentSession.Clear()`, then raises a `LogoutRequested` event (or invokes an injected callback) so `App.xaml.cs` can show `LoginWindow`
    - Guard: if `CurrentSession.Current` is null, return immediately without throwing (Req 7.7)
  - _Requirements: 1.1, 1.2, 1.3, 1.7, 5.2, 6.1, 6.3, 7.1, 7.7_

  - [x] 5.1 Write property test for `LogoutCommand` idempotence
    - **Property 5: Idempotence** — for all sequences of `LogoutCommand` executions, `CurrentSession.Current` is null after each call
    - **Property 6: No-op when null** — calling `LogoutCommand` when `CurrentSession.Current` is null does not throw
    - **Validates: Requirements 7.1, 7.7**
    - Add `<Compile Include>` link in `ThatsMyQuant.Tests.csproj` for `IdleTimeoutConfig.cs` (already added in task 1.1)

- [x] 6. Wire mouse and keyboard activity to `IdleMonitor` in `MainWindow.xaml.cs`
  - In `04_UI/ThatsMyQuant.Dashboard/MainWindow.xaml.cs`:
    - Accept `IdleTimeoutService` as a constructor parameter (injected via DI)
    - In the constructor (after `InitializeComponent`), subscribe `MouseMove`, `MouseDown`, and `KeyDown` to call `IdleTimeoutService.IdleMonitor.Reset()`
    - Start the `IdleMonitor` after the window is loaded
  - _Requirements: 3.1, 3.2_

- [x] 7. Add Logout button to `MainWindow.xaml` header
  - In `04_UI/ThatsMyQuant.Dashboard/MainWindow.xaml`, add a `<Button>` in the header bar bound to `{Binding LogoutCommand}` with content "Logout"
  - Button must be visible regardless of role (no `Visibility` binding to role)
  - _Requirements: 1.7_

- [x] 8. Update `App.xaml.cs`: register services and wire re-login flow
  - In `ConfigureServices`: register `IdleTimeoutService` as singleton; register `IAuditLogger` → `ConsoleAuditLogger` as singleton (if not already registered)
  - Extract the login-then-show-main-window logic into a private `async Task<bool> RunLoginFlowAsync()` method
  - Subscribe to `MainViewModel.LogoutRequested` (or equivalent callback) after `MainWindow` is created; handler should: hide `MainWindow`, call `RunLoginFlowAsync()`, show `MainWindow` again on success or call `Shutdown()` on cancel
  - Wire the same re-login callback into `IdleTimeoutService` so both logout paths reuse the same flow
  - Pass `IConfiguration` (or `appsettings.json`-backed config) to `IdleTimeoutService` for reading the `IdleTimeout` section
  - _Requirements: 1.4, 1.5, 1.6, 5.3, 5.4, 5.5, 2.5, 2.6_

- [x] 9. Checkpoint — ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for a faster MVP
- Each task references specific requirements for traceability
- Property tests use FsCheck.Xunit 3.* already present in the test project
- New Auth/Service source files should be linked into the test project via `<Compile Include>` following the existing pattern in `ThatsMyQuant.Tests.csproj`
