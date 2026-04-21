using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public sealed class StrategyEvidenceRouterTests
{
    [Theory]
    [InlineData("Grid Runner", StrategyEvidenceVisualMode.Grid)]
    [InlineData("HFT Micro Edge", StrategyEvidenceVisualMode.Hft)]
    [InlineData("Trend Confluence", StrategyEvidenceVisualMode.Standard)]
    public void GetVisualMode_ClassifiesFamilies(string label, StrategyEvidenceVisualMode expected)
    {
        StrategyEvidenceRouter.GetVisualMode(label).Should().Be(expected);
    }

    [Fact]
    public void ResolveVisualModeForDesk_NormalDeskDowngradesHft()
    {
        StrategyEvidenceRouter.ResolveVisualModeForDesk(StrategyEvidenceVisualMode.Hft, "Normal")
            .Should().Be(StrategyEvidenceVisualMode.Standard);
    }

    [Fact]
    public void ResolveVisualModeForDesk_HftDeskKeepsHft()
    {
        StrategyEvidenceRouter.ResolveVisualModeForDesk(StrategyEvidenceVisualMode.Hft, "HFT")
            .Should().Be(StrategyEvidenceVisualMode.Hft);
    }
}
