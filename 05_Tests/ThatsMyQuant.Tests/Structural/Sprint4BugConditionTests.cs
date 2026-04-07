using FluentAssertions;
using Xunit;

namespace ThatsMyQuant.Tests.Structural;

/// <summary>
/// Sprint 4 Bug Condition Tests.
/// These tests PASS on FIXED code — they assert the bugs are GONE.
/// Validates: Requirements 1.1, 1.2, 7.1, 8.1
/// </summary>
public class Sprint4BugConditionTests
{
    // ── B12: Fire-and-forget gone ─────────────────────────────────────────────
    // RiskSupervisorService must NOT contain `_ = StartMonitoringLoop()`.
    // Validates: Requirements 1.1 (IHostedService lifecycle)
    [Fact]
    public void B12_RiskSupervisorService_FireAndForget_IsGone()
    {
        var solutionRoot = FindSolutionRoot();
        solutionRoot.Should().NotBeNull("ThatsMyQuant.sln must be locatable from the test binary");

        var filePath = Path.Combine(
            solutionRoot!,
            "02_Core", "Application", "Risk", "RiskSupervisorService.cs");

        File.Exists(filePath).Should().BeTrue(because: $"source file must exist: {filePath}");

        var content = File.ReadAllText(filePath);

        content.Should().NotContain("_ = StartMonitoringLoop()",
            because: "Bug B12 must be fixed: fire-and-forget `_ = StartMonitoringLoop()` " +
                     "must be replaced with the BackgroundService ExecuteAsync pattern");
    }

    // ── B13: Commented-out CancelAllOrdersAsync gone ──────────────────────────
    // RiskSupervisorService must NOT contain `// await _execution.CancelAllOrdersAsync()`.
    // Validates: Requirements 1.2 (emergency halt cancels orders)
    [Fact]
    public void B13_RiskSupervisorService_CommentedOutCancelAllOrders_IsGone()
    {
        var solutionRoot = FindSolutionRoot();
        solutionRoot.Should().NotBeNull("ThatsMyQuant.sln must be locatable from the test binary");

        var filePath = Path.Combine(
            solutionRoot!,
            "02_Core", "Application", "Risk", "RiskSupervisorService.cs");

        File.Exists(filePath).Should().BeTrue(because: $"source file must exist: {filePath}");

        var content = File.ReadAllText(filePath);

        content.Should().NotContain("// await _execution.CancelAllOrdersAsync()",
            because: "Bug B13 must be fixed: the commented-out CancelAllOrdersAsync call " +
                     "must be replaced with an actual awaited call in TriggerEmergencyHaltAsync");
    }

    // ── B14: Hardcoded Telegram BotToken constant gone ────────────────────────
    // TelegramAlertService must NOT contain `private const string BotToken`.
    // Validates: Requirements 7.1 (secrets from IConfiguration)
    [Fact]
    public void B14_TelegramAlertService_HardcodedBotToken_IsGone()
    {
        var solutionRoot = FindSolutionRoot();
        solutionRoot.Should().NotBeNull("ThatsMyQuant.sln must be locatable from the test binary");

        var filePath = Path.Combine(
            solutionRoot!,
            "03_Infrastructure", "Data.Persistence", "Services", "TelegramAlertService.cs");

        File.Exists(filePath).Should().BeTrue(because: $"source file must exist: {filePath}");

        var content = File.ReadAllText(filePath);

        content.Should().NotContain("private const string BotToken",
            because: "Bug B14 must be fixed: hardcoded `private const string BotToken` " +
                     "must be replaced with IOptions<TelegramOptions> injection");
    }

    // ── B15: Raw string _apiKey field gone ────────────────────────────────────
    // BinanceExecutionService must NOT contain `private readonly string _apiKey`.
    // Validates: Requirements 8.1 (API keys encrypted via ApiVault)
    [Fact]
    public void B15_BinanceExecutionService_RawApiKeyField_IsGone()
    {
        var solutionRoot = FindSolutionRoot();
        solutionRoot.Should().NotBeNull("ThatsMyQuant.sln must be locatable from the test binary");

        var filePath = Path.Combine(
            solutionRoot!,
            "03_Infrastructure", "Data.Persistence", "Services", "BinanceExecutionService.cs");

        File.Exists(filePath).Should().BeTrue(because: $"source file must exist: {filePath}");

        var content = File.ReadAllText(filePath);

        content.Should().NotContain("private readonly string _apiKey",
            because: "Bug B15 must be fixed: raw `private readonly string _apiKey` " +
                     "must be replaced with ApiVault instances for in-memory AES-256-GCM encryption");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

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
