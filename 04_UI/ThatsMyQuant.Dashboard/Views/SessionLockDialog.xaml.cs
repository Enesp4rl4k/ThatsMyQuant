using System.Windows;
using System.Windows.Threading;

namespace ThatsMyQuant.Dashboard.Views;

/// <summary>
/// Idle-timeout warning dialog. Shows a countdown and lets the operator dismiss
/// the warning to reset the idle timer.
/// Requirements: 4.1, 4.2, 4.3, 4.5
/// </summary>
public partial class SessionLockDialog : Window
{
    private readonly DispatcherTimer _countdownTimer;
    private int _remainingSeconds;

    /// <summary>
    /// Remaining seconds until the session is locked. Setting this property
    /// immediately updates the countdown label so <see cref="Services.IdleTimeoutService"/>
    /// can initialise the display with the correct value before the timer ticks.
    /// </summary>
    public int RemainingSeconds
    {
        get => _remainingSeconds;
        set
        {
            _remainingSeconds = value;
            CountdownLabel.Text = $"Locking in {_remainingSeconds} second{(_remainingSeconds == 1 ? "" : "s")}...";
        }
    }

    /// <summary>
    /// Creates the dialog with the default 30-second countdown.
    /// </summary>
    public SessionLockDialog() : this(30) { }

    /// <summary>
    /// Creates the dialog with a custom initial countdown value, typically
    /// sourced from <see cref="Auth.IdleTimeoutConfig.WarningLeadSeconds"/>.
    /// </summary>
    /// <param name="initialSeconds">Seconds to display when the dialog first opens.</param>
    public SessionLockDialog(int initialSeconds)
    {
        InitializeComponent();

        // Ensure any non-"Stay Logged In" close path yields DialogResult = false (Req 4.5)
        Closing += (_, _) =>
        {
            if (DialogResult == null)
                DialogResult = false;
        };

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) =>
        {
            RemainingSeconds = Math.Max(0, RemainingSeconds - 1);
        };

        // Initialise the label before the timer starts (Req 4.1)
        RemainingSeconds = initialSeconds;
        _countdownTimer.Start();
    }

    private void StayLoggedIn_Click(object sender, RoutedEventArgs e)
    {
        _countdownTimer.Stop();
        DialogResult = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _countdownTimer.Stop();
        base.OnClosed(e);
    }
}
