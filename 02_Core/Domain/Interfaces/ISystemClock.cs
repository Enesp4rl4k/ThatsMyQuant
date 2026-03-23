using System;

namespace ThatsMyQuant.Domain.Interfaces;

/// <summary>
/// HFT Time Provider abstraction natively separating physical wall-time from simulated Replay-time.
/// </summary>
public interface ISystemClock
{
    DateTime UtcNow { get; }
    long UtcNowUnixMs { get; }
}
