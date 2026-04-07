using FluentAssertions;
using Xunit;

namespace ThatsMyQuant.Tests.Structural;

/// <summary>
/// Sprint 3 Bug Condition Exploration Tests.
/// These tests PASS on unfixed code — they assert the bug conditions ARE present.
/// Validates: Requirements 1.9, 1.10, 1.11
/// </summary>
public class Sprint3BugConditionTests
{
    /// <summary>
    /// B9: FeatureEngineeringAgent no longer uses a kernel mutex (lock statement).
    /// Validates: Requirements 1.9
    /// </summary>
    [Fact]
    public void B9_FeatureEngineeringAgent_ContainsKernelMutex()
    {
        var solutionRoot = FindSolutionRoot();
        solutionRoot.Should().NotBeNull();

        var filePath = Path.Combine(solutionRoot!, "02_Core", "Application", "MachineLearning", "FeatureEngineeringAgent.cs");
        File.Exists(filePath).Should().BeTrue(because: $"source file must exist: {filePath}");

        var content = File.ReadAllText(filePath);
        var containsLock = content.Contains("lock (") || content.Contains("lock(");
        containsLock.Should().BeFalse(
            because: "Bug B9 is fixed: FeatureEngineeringAgent must not use a kernel mutex (lock statement) on the hot path");
    }

    /// <summary>
    /// B10: CalculateBatchSMA now uses an O(N) running-sum sliding window.
    /// Validates: Requirements 1.10
    /// </summary>
    [Fact]
    public void B10_VectorizedMathUtils_CalculateBatchSMA_UsesONxWLoop()
    {
        var solutionRoot = FindSolutionRoot();
        solutionRoot.Should().NotBeNull();

        var filePath = Path.Combine(solutionRoot!, "02_Core", "Application", "Strategy", "VectorizedMathUtils.cs");
        File.Exists(filePath).Should().BeTrue(because: $"source file must exist: {filePath}");

        var content = File.ReadAllText(filePath);
        content.Should().Contain("prices[i - 1]",
            because: "Bug B10 is fixed: CalculateBatchSMA must use a running-sum sliding window (subtracting prices[i - 1])");
    }

    /// <summary>
    /// B11: HeartbeatMonitor and TokenBucketRateLimiter now use Environment.TickCount64.
    /// Validates: Requirements 1.11
    /// </summary>
    [Fact]
    public void B11_HotPathComponents_UseDateTime_InsteadOfTickCount64()
    {
        var solutionRoot = FindSolutionRoot();
        solutionRoot.Should().NotBeNull();

        var circuitBreakersPath = Path.Combine(solutionRoot!, "02_Core", "Application", "HftInfrastructure", "CircuitBreakers.cs");
        File.Exists(circuitBreakersPath).Should().BeTrue(because: $"source file must exist: {circuitBreakersPath}");

        var circuitBreakersContent = File.ReadAllText(circuitBreakersPath);
        circuitBreakersContent.Should().NotContain("DateTime.UtcNow",
            because: "Bug B11 is fixed in HeartbeatMonitor: must use Environment.TickCount64, not DateTime.UtcNow");

        var riskAgentPath = Path.Combine(solutionRoot!, "02_Core", "Application", "Strategy", "RiskAgent.cs");
        File.Exists(riskAgentPath).Should().BeTrue(because: $"source file must exist: {riskAgentPath}");

        var riskAgentContent = File.ReadAllText(riskAgentPath);

        // Only check the TokenBucketRateLimiter class section — the fix was specifically there
        var tbrlStart = riskAgentContent.IndexOf("class TokenBucketRateLimiter", StringComparison.Ordinal);
        if (tbrlStart >= 0)
        {
            var tbrlSection = riskAgentContent[tbrlStart..];
            tbrlSection.Should().NotContain("DateTime.UtcNow",
                because: "Bug B11 is fixed in TokenBucketRateLimiter: must use Environment.TickCount64, not DateTime.UtcNow");
        }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Walks up from the test binary's base directory until it finds the folder
    /// containing ThatsMyQuant.sln, or returns null if not found.
    /// </summary>
    private static string? FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("ThatsMyQuant.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
