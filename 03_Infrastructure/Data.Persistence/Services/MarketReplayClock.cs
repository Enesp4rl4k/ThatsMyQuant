using System;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Offline Simulator clock governed sequentially by the CSV Cursor natively bypassing OS Hardware limits.
/// Backtests running on MarketReplayClock execute at 10,000x speeds without await Thread/Task lag.
/// </summary>
public class MarketReplayClock : ISystemClock
{
    private DateTime _currentReplayTime;
    
    public DateTime UtcNow => _currentReplayTime;

    public long UtcNowUnixMs => new DateTimeOffset(_currentReplayTime).ToUnixTimeMilliseconds();

    public void AdvanceTime(DateTime simulatedTime)
    {
        if (simulatedTime > _currentReplayTime)
        {
            _currentReplayTime = simulatedTime;
        }
    }

    public void AdvanceTimeUnixMs(long simulatedUnixMs)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(simulatedUnixMs).UtcDateTime;
        if (dt > _currentReplayTime)
        {
            _currentReplayTime = dt;
        }
    }
}
