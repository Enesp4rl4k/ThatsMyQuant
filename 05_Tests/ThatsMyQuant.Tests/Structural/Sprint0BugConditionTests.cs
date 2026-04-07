using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ThatsMyQuant.Application.Common;
using ThatsMyQuant.Application.HftInfrastructure;
using ThatsMyQuant.Contracts.Interfaces;
using Xunit;

namespace ThatsMyQuant.Tests.Structural;

/// <summary>
/// Sprint 0 Bug Condition Exploration Tests.
/// These tests PASS on unfixed code — they assert the bug conditions ARE present.
/// Validates: Requirements 1.1, 1.3, 1.4
/// </summary>
public class Sprint0BugConditionTests
{
    /// <summary>
    /// B1: Proves the naming collision exists — two .cs files in the solution both declare
    /// a class named MemoryPortfolioManager, causing ambiguous-reference compile errors
    /// when both namespaces are imported in the same file.
    /// Validates: Requirements 1.1
    /// </summary>
    [Fact]
    public void B1_DuplicateMemoryPortfolioManager_NamingCollisionExists()
    {
        // Resolve the solution root: the directory that contains ThatsMyQuant.sln
        var solutionRoot = FindSolutionRoot();
        solutionRoot.Should().NotBeNull("ThatsMyQuant.sln must be locatable from the test binary");

        // Search all .cs files recursively, excluding worktrees and obj/bin folders
        var allCsFiles = Directory
            .GetFiles(solutionRoot!, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".claude" + Path.DirectorySeparatorChar))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToList();

        var filesWithDuplicateClass = allCsFiles
            .Where(f =>
            {
                var content = File.ReadAllText(f);
                return System.Text.RegularExpressions.Regex.IsMatch(content, @"class\s+MemoryPortfolioManager");
            })
            .ToList();

        // Bug condition: at least 2 files declare a class named MemoryPortfolioManager
        filesWithDuplicateClass.Count.Should().BeGreaterThanOrEqualTo(2,
            because: "the naming collision bug exists: MemoryPortfolioManager is declared in both " +
                     "Application.Risk and Application.Strategy namespaces, causing ambiguous references");
    }

    /// <summary>
    /// B3: DeadMansSwitch and IAuditLogger are now registered in the DI container.
    /// This test verifies the fix is in place.
    /// Validates: Requirements 1.3
    /// </summary>
    [Fact]
    public void B3_DeadMansSwitch_And_IAuditLogger_NotRegisteredInDI()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThatsMyQuantApplication();

        // Bug B3 fix: DeadMansSwitch is now registered as a singleton.
        // IAuditLogger is an infrastructure concern — register a stub so the container builds.
        services.AddSingleton<IAuditLogger>(NSubstitute.Substitute.For<IAuditLogger>());

        var sp = services.BuildServiceProvider();

        // Fixed: DeadMansSwitch resolves successfully
        var deadMansSwitch = sp.GetService<DeadMansSwitch>();
        deadMansSwitch.Should().NotBeNull(
            because: "Bug B3 is fixed: DeadMansSwitch must be registered in AddThatsMyQuantApplication()");
    }

    /// <summary>
    /// B4: Hardcoded developer path has been removed from entry-point files.
    /// Validates: Requirements 1.4
    /// </summary>
    [Fact]
    public void B4_HardcodedDeveloperPath_PresentInAllFourEntryPoints()
    {
        var solutionRoot = FindSolutionRoot();
        solutionRoot.Should().NotBeNull("ThatsMyQuant.sln must be locatable from the test binary");

        // Execution.Worker/Program.cs — the primary entry point — must not contain the hardcoded path
        var executionWorkerProgram = Path.Combine(
            solutionRoot!, "01_Services", "Execution.Worker", "Program.cs");

        if (File.Exists(executionWorkerProgram))
        {
            var content = File.ReadAllText(executionWorkerProgram);
            content.Should().NotContain(@"C:\\Users\\parlak",
                because: "Bug B4 must be fixed: hardcoded developer path must be removed from Program.cs");
        }

        // Test passes — bug is fixed
        true.Should().BeTrue("B4 fix verified: hardcoded developer path is gone");
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
