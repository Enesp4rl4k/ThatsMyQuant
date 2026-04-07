# Requirements Document

## Introduction

The session-logout feature adds two complementary session-termination mechanisms to the
ThatsMyQuant.Dashboard WPF application. First, an explicit Logout command in the Dashboard header
lets any authenticated operator voluntarily end their session and return to the LoginWindow without
shutting down the process. Second, an idle-timeout subsystem monitors mouse and keyboard activity
and automatically locks the session after a configurable period of inactivity, showing a countdown
warning before the lock fires. Both paths call `CurrentSession.Clear()` and re-present the
LoginWindow, reusing the existing `App.xaml.cs` startup flow. The feature also defines correctness
properties suitable for property-based testing.

---

## Glossary

- **Dashboard**: The ThatsMyQuant.Dashboard WPF application.
- **CurrentSession**: The application-lifetime static singleton that holds the authenticated
  `OperatorUser?` and exposes `Clear()`.
- **LoginWindow**: The WPF window that collects credentials and initiates authentication.
- **MainWindow**: The primary WPF window shown after a successful login.
- **LogoutCommand**: The `IRelayCommand` on `MainViewModel` that initiates an explicit logout.
- **IdleMonitor**: The component responsible for tracking user-input activity and raising idle
  and warning events.
- **IdleTimeoutService**: The application service that owns the `IdleMonitor`, reads configuration,
  and orchestrates the warning and lock sequence.
- **IdleTimeoutConfig**: The configuration record that holds `TimeoutSeconds` and
  `WarningLeadSeconds` values.
- **SessionLockDialog**: The modal WPF dialog shown during the idle-warning countdown that allows
  the user to dismiss the warning and reset the idle timer.
- **OperatorUser**: The entity representing the authenticated human operator (defined in
  multi-user-auth).
- **Role**: The enum with values `Admin`, `Operator`, and `ReadOnly` (defined in multi-user-auth).

---

## Requirements

### Requirement 1: Explicit Logout Command

**User Story:** As an operator, I want a Logout button in the Dashboard header, so that I can end
my session and hand the terminal to another user without restarting the application.

#### Acceptance Criteria

1. THE `MainViewModel` SHALL expose a `LogoutCommand` of type `IRelayCommand`.
2. WHEN `LogoutCommand` is executed, THE `MainViewModel` SHALL call `CurrentSession.Clear()`.
3. WHEN `LogoutCommand` is executed, THE `MainViewModel` SHALL stop all active background timers
   and trading loops before clearing the session.
4. WHEN `LogoutCommand` is executed, THE Dashboard SHALL present a new `LoginWindow` as a modal
   dialog using `ShowDialog()`.
5. WHEN the `LoginWindow` returns `DialogResult = true` after a logout-triggered re-login, THE
   Dashboard SHALL restore the `MainWindow` to a fully operational state reflecting the newly
   authenticated `OperatorUser`.
6. WHEN the `LoginWindow` is cancelled or closed during a logout-triggered re-login, THE Dashboard
   SHALL call `Application.Current.Shutdown()`.
7. THE Dashboard header XAML SHALL contain a Logout button bound to `LogoutCommand` that is visible
   to all authenticated roles (Admin, Operator, ReadOnly).

---

### Requirement 2: Idle Timeout Configuration

**User Story:** As a system administrator, I want the idle-timeout duration and warning lead time
to be configurable, so that I can tune the security policy without recompiling the application.

#### Acceptance Criteria

1. THE `IdleTimeoutConfig` SHALL expose an integer property `TimeoutSeconds` representing the total
   inactivity period in seconds before the session is locked.
2. THE `IdleTimeoutConfig` SHALL expose an integer property `WarningLeadSeconds` representing how
   many seconds before the lock the `SessionLockDialog` is shown.
3. THE `IdleTimeoutConfig` SHALL enforce the invariant `WarningLeadSeconds < TimeoutSeconds`.
4. THE `IdleTimeoutConfig` SHALL provide a default value of `TimeoutSeconds = 300` (5 minutes) and
   `WarningLeadSeconds = 30`.
5. WHERE the application configuration file contains an `IdleTimeout` section, THE
   `IdleTimeoutService` SHALL read `TimeoutSeconds` and `WarningLeadSeconds` from that section.
6. IF the configuration file is absent or the `IdleTimeout` section is missing, THEN THE
   `IdleTimeoutService` SHALL use the default `IdleTimeoutConfig` values.
7. IF `TimeoutSeconds` is set to `0`, THEN THE `IdleTimeoutService` SHALL disable idle-timeout
   enforcement entirely for the current session.

---

### Requirement 3: Idle Activity Monitoring

**User Story:** As a security engineer, I want the system to track mouse and keyboard activity
across the entire application window, so that the idle timer resets on any genuine user interaction.

#### Acceptance Criteria

1. THE `IdleMonitor` SHALL track the elapsed time since the last observed mouse-move, mouse-click,
   or keyboard-key-down event within the `MainWindow`.
2. WHEN any tracked input event is received, THE `IdleMonitor` SHALL reset its elapsed-idle counter
   to zero.
3. THE `IdleMonitor` SHALL raise an `IdleWarning` event when the elapsed-idle time reaches
   `TimeoutSeconds − WarningLeadSeconds`.
4. THE `IdleMonitor` SHALL raise an `IdleElapsed` event when the elapsed-idle time reaches
   `TimeoutSeconds`.
5. WHILE the `SessionLockDialog` is open, THE `IdleMonitor` SHALL continue counting elapsed time
   and SHALL raise `IdleElapsed` if the user does not interact before `TimeoutSeconds` is reached.
6. THE `IdleMonitor` SHALL use a resolution of no coarser than one second for its internal tick.

---

### Requirement 4: Idle Warning Dialog

**User Story:** As an operator, I want a warning dialog to appear before my session is
automatically locked, so that I can dismiss it and continue working if I am still present.

#### Acceptance Criteria

1. WHEN the `IdleMonitor` raises `IdleWarning`, THE `IdleTimeoutService` SHALL open the
   `SessionLockDialog` on the UI thread showing the remaining seconds until lock.
2. WHILE the `SessionLockDialog` is open, THE `SessionLockDialog` SHALL update the displayed
   countdown every second.
3. WHEN the operator clicks "Stay Logged In" in the `SessionLockDialog`, THE `IdleTimeoutService`
   SHALL close the dialog and reset the `IdleMonitor` elapsed counter to zero.
4. WHEN the `IdleMonitor` raises `IdleElapsed` while the `SessionLockDialog` is open, THE
   `IdleTimeoutService` SHALL close the dialog and proceed with the idle-logout sequence defined
   in Requirement 5.
5. IF the `SessionLockDialog` is closed by any means other than "Stay Logged In" (e.g., Alt+F4),
   THEN THE `IdleTimeoutService` SHALL treat the closure as an idle-logout trigger.

---

### Requirement 5: Idle Logout Sequence

**User Story:** As a security engineer, I want the session to be automatically cleared and the
LoginWindow to be shown when the idle timeout elapses, so that an unattended terminal cannot be
accessed by an unauthorised person.

#### Acceptance Criteria

1. WHEN the `IdleMonitor` raises `IdleElapsed` (and no "Stay Logged In" dismissal has occurred),
   THE `IdleTimeoutService` SHALL call `CurrentSession.Clear()`.
2. WHEN `CurrentSession.Clear()` is called by the idle path, THE `IdleTimeoutService` SHALL stop
   all active background timers and trading loops in `MainViewModel`.
3. WHEN `CurrentSession.Clear()` is called by the idle path, THE `IdleTimeoutService` SHALL
   present a new `LoginWindow` using `ShowDialog()` on the UI thread.
4. WHEN the `LoginWindow` returns `DialogResult = true` after an idle-triggered re-login, THE
   Dashboard SHALL restore the `MainWindow` to a fully operational state reflecting the newly
   authenticated `OperatorUser`.
5. WHEN the `LoginWindow` is cancelled or closed after an idle-triggered re-login prompt, THE
   Dashboard SHALL call `Application.Current.Shutdown()`.
6. THE `IdleTimeoutService` SHALL write an entry to the system audit log recording the username,
   UTC timestamp, and the reason `"IdleTimeout"` whenever an idle logout occurs.

---

### Requirement 6: Logout Audit Logging

**User Story:** As a compliance officer, I want every session termination to be recorded in the
audit log, so that I can reconstruct who was logged in and when sessions ended.

#### Acceptance Criteria

1. WHEN `LogoutCommand` is executed, THE `MainViewModel` SHALL write an audit log entry containing
   the `OperatorUser.Username`, the UTC timestamp, and the reason `"ExplicitLogout"` before
   calling `CurrentSession.Clear()`.
2. WHEN an idle timeout logout occurs, THE `IdleTimeoutService` SHALL write an audit log entry
   containing the `OperatorUser.Username`, the UTC timestamp, and the reason `"IdleTimeout"` as
   specified in Requirement 5.6.
3. IF `CurrentSession.Current` is `null` at the time a logout audit entry would be written, THEN
   THE logging component SHALL record `"Unknown"` as the username and SHALL NOT throw an unhandled
   exception.

---

### Requirement 7: Session State Invariants (Correctness Properties)

**User Story:** As a developer, I want well-defined correctness properties for the session-logout
subsystem, so that property-based tests can verify the invariants hold across arbitrary sequences
of login, logout, and idle events.

#### Acceptance Criteria

1. FOR ALL sequences of `LogoutCommand` executions, `CurrentSession.Current` SHALL be `null`
   immediately after each execution completes (idempotence of logout).
2. FOR ALL sequences of idle-elapsed events, `CurrentSession.Current` SHALL be `null` immediately
   after each idle-logout sequence completes.
3. FOR ALL valid `IdleTimeoutConfig` values where `TimeoutSeconds > 0`, the elapsed time between
   the last input event and the `IdleElapsed` event SHALL be within the range
   `[TimeoutSeconds, TimeoutSeconds + 2]` seconds (timing tolerance of two ticks).
4. FOR ALL valid `IdleTimeoutConfig` values where `TimeoutSeconds > 0` and
   `WarningLeadSeconds > 0`, the `IdleWarning` event SHALL always be raised before the
   `IdleElapsed` event within the same idle cycle.
5. FOR ALL `IdleTimeoutConfig` instances, the invariant `WarningLeadSeconds < TimeoutSeconds`
   SHALL hold; constructing a config where `WarningLeadSeconds >= TimeoutSeconds` SHALL produce
   an `ArgumentException`.
6. WHEN `LogoutCommand` is executed and then a new login completes, `CurrentSession.Current`
   SHALL be non-null and SHALL reference the newly authenticated `OperatorUser` (round-trip
   property: clear → login → non-null).
7. FOR ALL states where `CurrentSession.Current` is `null`, THE `LogoutCommand` SHALL be a no-op
   and SHALL NOT throw an unhandled exception (error-condition property).
8. WHEN the idle timer is reset by a "Stay Logged In" dismissal, the elapsed time until the next
   `IdleElapsed` event SHALL equal `TimeoutSeconds` ± 2 seconds (reset restores full timeout).
