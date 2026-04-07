using FluentAssertions;
using Xunit;

namespace ThatsMyQuant.Tests.Structural;

/// <summary>
/// Sprint 2 Bug Condition Exploration Tests.
/// These tests PASS on unfixed code — they assert the bug conditions ARE present
/// (i.e., the test files do not yet exist, confirming zero coverage).
/// Validates: Requirements 1.6, 1.7, 1.8
/// </summary>
public class Sprint2BugConditionTests
{
    /// <summary>
    /// B6: Strategy/MemoryPortfolioManagerTests.cs now exists — coverage added.
    /// Validates: Requirements 1.6
    /// </summary>
    [Fact]
    public void B6_StrategyMemoryPortfolioManagerTests_DoesNotExist()
    {
        var solutionRoot = FindSolutionRoot();
        solutionRoot.Should().NotBeNull();

        var targetFile = Path.Combine(solutionRoot!, "05_Tests", "ThatsMyQuant.Tests",
            "Strategy", "MemoryPortfolioManagerTests.cs");

        File.Exists(targetFile).Should().BeTrue(
            because: "Bug B6 is fixed: Strategy/MemoryPortfolioManagerTests.cs must exist");
    }

    /// <summary>
    /// B7: MachineLearning/FeatureEngineeringAgentTests.cs now exists — coverage added.
    /// Validates: Requirements 1.7
    /// </summary>
    [Fact]
    public void B7_FeatureEngineeringAgentTests_DoesNotExist()
    {
        var solutionRoot = FindSolutionRoot();
        solutionRoot.Should().NotBeNull();

        var targetFile = Path.Combine(solutionRoot!, "05_Tests", "ThatsMyQuant.Tests",
            "MachineLearning", "FeatureEngineeringAgentTests.cs");

        File.Exists(targetFile).Should().BeTrue(
            because: "Bug B7 is fixed: MachineLearning/FeatureEngineeringAgentTests.cs must exist");
    }

    /// <summary>
    /// B8: Integration/SignalPipelineIntegrationTests.cs now exists — coverage added.
    /// Validates: Requirements 1.8
    /// </summary>
    [Fact]
    public void B8_SignalPipelineIntegrationTests_DoesNotExist()
    {
        var solutionRoot = FindSolutionRoot();
        solutionRoot.Should().NotBeNull();

        var targetFile = Path.Combine(solutionRoot!, "05_Tests", "ThatsMyQuant.Tests",
            "Integration", "SignalPipelineIntegrationTests.cs");

        File.Exists(targetFile).Should().BeTrue(
            because: "Bug B8 is fixed: Integration/SignalPipelineIntegrationTests.cs must exist");
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
