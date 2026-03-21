using System.Diagnostics;
using System.Runtime;

namespace ThatsMyQuant.Application.Common;

/// <summary>
/// A specialized utility to prep the .NET Runtime for HFT workloads.
/// Handles ThreadPool "warming", GC tuning, and Processor Affinity.
/// </summary>
public static class PerformanceBootstrapper
{
    public static void OptimizeForHft()
    {
        // 1. ThreadPool Warming: 
        // Force the pool to have instantly available threads. 
        // Prevents the 0.5s "one-thread-at-a-time" injection delay during bursts.
        int minWorker, minIOC;
        ThreadPool.GetMinThreads(out minWorker, out minIOC);
        
        // Ensure at least 64 workers (or 4x CPU count) are pre-warmed
        int targetMin = Math.Max(64, Environment.ProcessorCount * 4);
        ThreadPool.SetMinThreads(targetMin, targetMin);

        // 2. GC Tuning: 
        // SustainLowLatency is ideal for HFT to reduce "Stop the world" pauses during trading hours.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // 3. Process Priority:
        // Give the engine higher precedence over background OS tasks.
        using (var process = Process.GetCurrentProcess())
        {
            process.PriorityClass = ProcessPriorityClass.High;
        }

        Console.WriteLine($"[PERF] Runtime Optimized: MinThreads={targetMin}, LatencyMode={GCSettings.LatencyMode}, Priority=High");
    }
}
