using Microsoft.Extensions.Configuration;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Dashboard.Auth;
using ThatsMyQuant.Dashboard.ViewModels;

#if WINDOWS
using System.Windows;
using ThatsMyQuant.Dashboard.Views;
#endif

namespace ThatsMyQuant.Dashboard.Services;

/// <summary>
/// Orchestrates the idle-timeout warning and lock sequence.
/// Requirements: 4.1, 4.2, 4.3, 4.4, 4.5, 5.1, 5.2, 5.3, 5.6, 6.2, 6.3, 2.5, 2.6, 2.7
/// </summary>
public class IdleTimeoutService
{
    private readonly IAuditLogger      _auditLogger;
    private readonly Func<Task<bool>>  _reLoginCallback;
    private MainViewModel?             _viewModel;

#if WINDOWS
    private SessionLockDialog? _lockDialog;
#endif

    /// <summary>The underlying idle monitor. Wire its Reset() to input events in MainWindow.</summary>
    public IdleMonitor IdleMonitor { get; }

    public IdleTimeoutService(
        IdleTimeoutConfig  config,
        IAuditLogger       auditLogger,
        Func<Task<bool>>   reLoginCallback)
    {
        _auditLogger     = auditLogger;
        _reLoginCallback = reLoginCallback;

        IdleMonitor = new IdleMonitor(config);
        IdleMonitor.IdleWarning += OnIdleWarning;
        IdleMonitor.IdleElapsed += OnIdleElapsed;
    }

    // -------------------------------------------------------------------------
    // Factory — reads IConfiguration section "IdleTimeout" (Req 2.5, 2.6, 2.7)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates an <see cref="IdleTimeoutService"/> from the <c>"IdleTimeout"</c>
    /// configuration section. Falls back to <see cref="IdleTimeoutConfig.Default"/>
    /// when the section is absent. Disables idle enforcement when
    /// <c>TimeoutSeconds == 0</c>.
    /// </summary>
    public static IdleTimeoutService FromConfiguration(
        IConfiguration   config,
        IAuditLogger     auditLogger,
        Func<Task<bool>> reLoginCallback)
    {
        IdleTimeoutConfig idleConfig;

        var section = config.GetSection("IdleTimeout");
        if (section.Exists())
        {
            int timeout = section.GetValue<int>("TimeoutSeconds",  IdleTimeoutConfig.Default.TimeoutSeconds);
            int warning = section.GetValue<int>("WarningLeadSeconds", IdleTimeoutConfig.Default.WarningLeadSeconds);

            // TimeoutSeconds == 0 → disabled (bypasses invariant validation)
            idleConfig = timeout == 0
                ? IdleTimeoutConfig.Disabled
                : new IdleTimeoutConfig(timeout, warning);
        }
        else
        {
            idleConfig = IdleTimeoutConfig.Default;
        }

        return new IdleTimeoutService(idleConfig, auditLogger, reLoginCallback);
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Stores a reference to the <see cref="MainViewModel"/> so that
    /// <see cref="MainViewModel.StopBackgroundTimers"/> can be called during
    /// the idle-logout sequence (Req 5.2).
    /// </summary>
    public void Attach(MainViewModel viewModel) => _viewModel = viewModel;

    /// <summary>Starts the idle monitor. No-op when TimeoutSeconds == 0.</summary>
    public void Start() => IdleMonitor.Start();

    /// <summary>Stops the idle monitor.</summary>
    public void Stop() => IdleMonitor.Stop();

    // -------------------------------------------------------------------------
    // Event handlers
    // -------------------------------------------------------------------------

    private void OnIdleWarning(object? sender, EventArgs e)
    {
#if WINDOWS
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            // If a dialog is already open (shouldn't happen, but guard anyway), skip.
            if (_lockDialog != null) return;

            _lockDialog = new SessionLockDialog();

            // Wire the Closed event so that any non-"Stay Logged In" close triggers logout (Req 4.5)
            _lockDialog.Closed += OnLockDialogClosed;

            bool? result = _lockDialog.ShowDialog();

            // ShowDialog returns after the window closes.
            // result == true  → "Stay Logged In" was clicked
            // result == false / null → any other close path
            _lockDialog = null;

            if (result == true)
            {
                // Operator chose to stay — reset the idle counter (Req 4.3)
                IdleMonitor.Reset();
            }
            else
            {
                // Closed without "Stay Logged In" → treat as idle-logout (Req 4.5)
                _ = ExecuteIdleLogoutAsync();
            }
        });
#endif
    }

    private void OnIdleElapsed(object? sender, EventArgs e)
    {
#if WINDOWS
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            // If the warning dialog is still open, close it first (Req 4.4)
            if (_lockDialog != null)
            {
                // Detach the Closed handler so it doesn't double-trigger logout
                _lockDialog.Closed -= OnLockDialogClosed;
                _lockDialog.Close();
                _lockDialog = null;
            }

            _ = ExecuteIdleLogoutAsync();
        });
#endif
    }

#if WINDOWS
    private void OnLockDialogClosed(object? sender, EventArgs e)
    {
        // This fires when the dialog is closed by any means OTHER than the
        // normal ShowDialog() return path (e.g. Alt+F4 while ShowDialog is
        // still blocking). In practice ShowDialog handles the result, so this
        // is a safety net — no action needed here; the ShowDialog result path
        // already handles the false case.
    }
#endif

    // -------------------------------------------------------------------------
    // Idle-logout sequence (Req 5.1, 5.2, 5.3, 5.6, 6.2, 6.3)
    // -------------------------------------------------------------------------

    private async Task ExecuteIdleLogoutAsync()
    {
        // Capture username/role BEFORE clearing the session (Req 6.2, 6.3)
        string username = Auth.CurrentSession.Current?.Username ?? "Unknown";
        string role     = Auth.CurrentSession.Current?.Role.ToString() ?? "Unknown";

        // Stop background timers and trading loops (Req 5.2)
        _viewModel?.StopBackgroundTimers();

        // Clear the session (Req 5.1)
        Auth.CurrentSession.Clear();

        // Write audit log entry (Req 5.6, 6.2, 6.3)
        await _auditLogger.LogSecurityEventAsync(
            principalId: username,
            role:        role,
            actionType:  "IdleTimeout",
            details:     $"Idle timeout at {DateTime.UtcNow:O}");

        // Invoke the re-login callback (Req 5.3)
        await _reLoginCallback();
    }
}
