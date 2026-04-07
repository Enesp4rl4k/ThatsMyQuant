using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Dashboard.Auth;

/// <summary>
/// Holds the currently authenticated operator for the lifetime of the session.
/// Thread-safe via volatile field. Requirements: 6.1, 6.3
/// </summary>
public static class CurrentSession
{
    private static volatile OperatorUser? _current;

    public static OperatorUser? Current
    {
        get => _current;
        internal set => _current = value;
    }

    public static void Clear() => _current = null;
}
