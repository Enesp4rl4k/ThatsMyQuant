namespace ThatsMyQuant.Dashboard.Auth;

/// <summary>
/// Configuration record for the idle-timeout subsystem.
/// </summary>
public record IdleTimeoutConfig
{
    public int TimeoutSeconds     { get; init; }
    public int WarningLeadSeconds { get; init; }

    public IdleTimeoutConfig(int timeoutSeconds = 300, int warningLeadSeconds = 30)
    {
        if (timeoutSeconds > 0 && warningLeadSeconds >= timeoutSeconds)
            throw new ArgumentException(
                "WarningLeadSeconds must be less than TimeoutSeconds.",
                nameof(warningLeadSeconds));

        TimeoutSeconds     = timeoutSeconds;
        WarningLeadSeconds = warningLeadSeconds;
    }

    /// <summary>Default: 5-minute timeout with 30-second warning.</summary>
    public static IdleTimeoutConfig Default  => new(300, 30);

    /// <summary>Disabled: TimeoutSeconds = 0 bypasses validation and disables idle enforcement.</summary>
    public static IdleTimeoutConfig Disabled => new(0, 0);
}
