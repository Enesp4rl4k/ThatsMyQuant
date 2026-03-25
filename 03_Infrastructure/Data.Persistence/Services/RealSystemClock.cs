using System;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Native operating system physical hardware clock binding for LIVE environments.
/// </summary>
public class RealSystemClock : ISystemClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    
    public long UtcNowUnixMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
