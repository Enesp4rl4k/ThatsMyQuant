using System.Windows;

namespace ThatsMyQuant.Dashboard.Services;

/// <summary>
/// P3 accessibility: reduce decorative UI motion when requested (env) or when Windows
/// disables client-area animations (common hint for "animation effects" off).
/// </summary>
public static class UiMotionPreferences
{
    public static bool ReduceMotion { get; private set; }

    public static void Initialize()
    {
        if (IsTruthy(Environment.GetEnvironmentVariable("TMQ_REDUCE_MOTION")))
        {
            ReduceMotion = true;
            return;
        }

        ReduceMotion = !SystemParameters.ClientAreaAnimation;
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value is "1" or "true" or "TRUE" or "yes" or "YES" or "on" or "ON";
    }
}
