using FluentAssertions;
using ThatsMyQuant.Dashboard.ViewModels;
using Xunit;

namespace ThatsMyQuant.Dashboard.Tests;

/// <summary>
/// Smoke tests for the 5 new chart ViewModels added in 2026-04-25:
/// SignalConfidence (radar), MultiIndicatorChart, PortfolioSunburst,
/// OrderBookHeatmap, plus AlphaPortfolio's underwater + MC fan additions.
///
/// Goal: verify default-state geometry is non-empty and value polygons / arrays
/// have the right cardinality. WPF-render side-effects are NOT exercised — these
/// tests run headless and only validate the math + observable wiring.
/// </summary>
public class NewChartViewModelTests
{
    [Fact]
    public void SignalConfidence_default_state_renders_six_axes()
    {
        var vm = new SignalConfidenceViewModel();
        // 4 background rings (25/50/75/100%).
        vm.RadarRings.Should().HaveCount(4);
        // 6 spokes & 6 axis labels for the 6 dimensions.
        vm.RadarSpokes.Should().HaveCount(6);
        vm.RadarLabels.Should().HaveCount(6);
        // Value polygon closes itself: 6 vertices + 1 closing point = 7.
        vm.ValuePolygon.Count.Should().Be(7);
        vm.ValueDots.Should().HaveCount(6);
    }

    [Fact]
    public void SignalConfidence_setScores_clamps_and_classifies_verdict()
    {
        var vm = new SignalConfidenceViewModel();
        vm.SetScores(0.95, 0.92, 0.91, 0.88, 0.93, 0.94);
        vm.VerdictLabel.Should().Contain("ALL-IN");

        vm.SetScores(2.0, -1.0, 0.5, 0.5, 0.5, 0.5);
        vm.ScoreMomentum.Should().Be(1.0);            // clamp to 1
        vm.ScoreTrendStrength.Should().Be(0.0);       // clamp to 0
    }

    [Fact]
    public void MultiIndicatorChart_default_state_is_empty_until_refresh()
    {
        var vm = new MultiIndicatorChartViewModel();
        vm.Symbol.Should().Be("BTC-USDT");
        vm.Bars.Should().Be(200);
        vm.Candles.Should().BeEmpty();
        // Indicator toggles default ON.
        vm.ShowAlphaTrend.Should().BeTrue();
        vm.ShowPmax.Should().BeTrue();
    }

    [Fact]
    public async System.Threading.Tasks.Task MultiIndicatorChart_refresh_with_no_history_renders_synthetic()
    {
        var vm = new MultiIndicatorChartViewModel(); // no history service → synthetic path
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Candles.Count.Should().Be(vm.Bars);
        vm.PmaxLine.Count.Should().BeGreaterThan(0);
        vm.EmaLine.Count.Should().BeGreaterThan(0);
        vm.PriceAxisLabels.Count.Should().Be(6);
        vm.SrBands.Count.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public void PortfolioSunburst_seeds_six_validated_positions()
    {
        var vm = new PortfolioSunburstViewModel();
        vm.Tiles.Should().HaveCount(6);
        // Every tile has positive geometry.
        foreach (var tile in vm.Tiles)
        {
            tile.Width.Should().BeGreaterThan(0);
            tile.Height.Should().BeGreaterThan(0);
            tile.Title.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void PortfolioSunburst_update_replaces_dataset()
    {
        var vm = new PortfolioSunburstViewModel();
        vm.UpdateFromPositions(new[]
        {
            new TreemapInputPosition("Crypto", "Pairs", "ABC-USDT", 0.5m, 100m),
            new TreemapInputPosition("Crypto", "Funding", "XYZ-USDT", 0.5m, -50m)
        });
        vm.Tiles.Should().HaveCount(2);
    }

    [Fact]
    public void OrderBookHeatmap_initial_render_uses_synthetic_book()
    {
        var vm = new OrderBookHeatmapViewModel();
        vm.BidCells.Count.Should().BeGreaterThan(0);
        vm.AskCells.Count.Should().BeGreaterThan(0);
        vm.BidCells.Count.Should().Be(vm.AskCells.Count);
        vm.BestBidLabel.Should().NotBe("—");
        vm.SpreadLabel.Should().NotBe("—");
    }

    [Fact]
    public void AlphaPortfolio_underwater_curve_starts_empty()
    {
        var vm = new AlphaPortfolioViewModel();
        // Before backtest, polylines are empty PointCollections.
        vm.UnderwaterAreaPoints.Count.Should().Be(0);
        vm.UnderwaterZeroLinePoints.Count.Should().Be(0);
        vm.McFanHasResults.Should().BeFalse();
    }

    [Fact]
    public void AlphaPortfolio_monte_carlo_without_source_data_reports_empty()
    {
        var vm = new AlphaPortfolioViewModel();
        vm.RunMonteCarloFanCommand.Execute(null);
        vm.McFanHasResults.Should().BeFalse();
        vm.McFanStatus.Should().Contain("backtest");
    }
}
