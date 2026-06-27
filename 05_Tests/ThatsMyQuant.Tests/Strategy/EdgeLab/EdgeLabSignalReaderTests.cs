using FluentAssertions;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy.EdgeLab;

/// <summary>
/// Covers each row of signals/README.md §"Failure Modes" plus the happy path.
/// All tests pin <c>now</c> so freshness logic is deterministic.
/// </summary>
public class EdgeLabSignalReaderTests
{
    private static EdgeLabSignalOptions DefaultOpts() => new()
    {
        Enabled = true,
        InputDir = "C:/tmp/edgelab",
        Strategy = "realized_vol_4h_xs_reverse",
        Timeframe = "4h",
        PollIntervalSeconds = 60,
        MinDeltaUsd = 25m,
        OrderType = "Market",
    };

    private static EdgeLabSignalDocument ValidDoc(DateTimeOffset asOfBar) => new()
    {
        SchemaVersion = 1,
        Strategy = "realized_vol_4h_xs_reverse",
        GeneratedAt = asOfBar.AddHours(1),
        AsOfBar = asOfBar,
        TargetWeights = new()
        {
            ["BTCUSDT"] = 0.5, ["BNBUSDT"] = 0.5,
            ["DOGEUSDT"] = -0.5, ["UNIUSDT"] = -0.5,
            ["ETHUSDT"] = 0.0,
        },
        FactorValues = new()
        {
            ["BTCUSDT"] = 0.007, ["BNBUSDT"] = 0.006,
            ["DOGEUSDT"] = 0.020, ["UNIUSDT"] = 0.013,
            ["ETHUSDT"] = 0.011,
        },
        Ranks = new(),
        Metadata = new()
        {
            Factor = "realized_vol",
            Timeframe = "4h",
            K = 2,
            Reverse = true,
            FeeBpsRoundTrip = 5.5,
            FeeVenue = "okx",
            NSymbolsTotal = 14,
            NSymbolsLoaded = 14,
            NSymbolsEligibleAtBar = 14,
        }
    };

    [Fact]
    public void HappyPath_returns_Ok()
    {
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        var now = asOf.AddHours(5); // within 2 timeframes (8h)
        var result = EdgeLabSignalReader.Validate(ValidDoc(asOf), DefaultOpts(), now);
        result.IsOk.Should().BeTrue(result.FailureReason);
        result.Status.Should().Be(EdgeLabReadStatus.Ok);
    }

    [Fact]
    public void SchemaMismatch_when_version_not_1()
    {
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        var doc = ValidDoc(asOf); doc.SchemaVersion = 2;
        var result = EdgeLabSignalReader.Validate(doc, DefaultOpts(), asOf.AddHours(5));
        result.Status.Should().Be(EdgeLabReadStatus.SchemaMismatch);
    }

    [Fact]
    public void Stale_when_older_than_two_timeframes()
    {
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        var now = asOf.AddHours(9); // > 2 * 4h
        var result = EdgeLabSignalReader.Validate(ValidDoc(asOf), DefaultOpts(), now);
        result.Status.Should().Be(EdgeLabReadStatus.Stale);
    }

    [Fact]
    public void InsufficientBreadth_when_eligible_below_2k()
    {
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        var doc = ValidDoc(asOf); doc.Metadata!.NSymbolsEligibleAtBar = 3; // 2*k = 4
        var result = EdgeLabSignalReader.Validate(doc, DefaultOpts(), asOf.AddHours(2));
        result.Status.Should().Be(EdgeLabReadStatus.InsufficientBreadth);
    }

    [Fact]
    public void BadFactorValues_when_NaN_present()
    {
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        var doc = ValidDoc(asOf); doc.FactorValues!["BTCUSDT"] = double.NaN;
        var result = EdgeLabSignalReader.Validate(doc, DefaultOpts(), asOf.AddHours(2));
        result.Status.Should().Be(EdgeLabReadStatus.BadFactorValues);
    }

    [Fact]
    public void BadFactorValues_when_all_zero()
    {
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        var doc = ValidDoc(asOf);
        foreach (var k in doc.FactorValues!.Keys.ToList()) doc.FactorValues[k] = 0.0;
        var result = EdgeLabSignalReader.Validate(doc, DefaultOpts(), asOf.AddHours(2));
        result.Status.Should().Be(EdgeLabReadStatus.BadFactorValues);
    }

    [Fact]
    public void StrategyMismatch_when_doc_strategy_differs()
    {
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        var doc = ValidDoc(asOf); doc.Strategy = "some_other_strategy";
        var result = EdgeLabSignalReader.Validate(doc, DefaultOpts(), asOf.AddHours(2));
        result.Status.Should().Be(EdgeLabReadStatus.StrategyMismatch);
    }

    [Fact]
    public void WeightAsymmetry_when_sum_weights_nonzero()
    {
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        var doc = ValidDoc(asOf);
        // Make long leg heavier than short → directional exposure.
        doc.TargetWeights!["BTCUSDT"] = 0.6;  // was 0.5
        var result = EdgeLabSignalReader.Validate(doc, DefaultOpts(), asOf.AddHours(2));
        result.Status.Should().Be(EdgeLabReadStatus.WeightAsymmetry);
        result.FailureReason.Should().Contain("sum");
    }

    [Fact]
    public void WeightAsymmetry_when_gross_off_contract()
    {
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        var doc = ValidDoc(asOf);
        // Symmetric (sum=0) but only 100% gross instead of 200%.
        doc.TargetWeights!["BTCUSDT"] = 0.25; doc.TargetWeights["BNBUSDT"] = 0.25;
        doc.TargetWeights["DOGEUSDT"] = -0.25; doc.TargetWeights["UNIUSDT"] = -0.25;
        var result = EdgeLabSignalReader.Validate(doc, DefaultOpts(), asOf.AddHours(2));
        result.Status.Should().Be(EdgeLabReadStatus.WeightAsymmetry);
        result.FailureReason.Should().Contain("|weights|");
    }

    [Fact]
    public void FiftyPctGross_panel_Ok_when_ExpectedGross_configured() // xs_momentum_neutral_panel shape
    {
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        var doc = ValidDoc(asOf);
        // Dollar-neutral but deliberately 50% gross (the $2K-cycle panel: ±0.125 legs).
        doc.TargetWeights!["BTCUSDT"] = 0.125; doc.TargetWeights["BNBUSDT"] = 0.125;
        doc.TargetWeights["DOGEUSDT"] = -0.125; doc.TargetWeights["UNIUSDT"] = -0.125;

        var opts = DefaultOpts();
        opts.ExpectedGross = 0.5;

        var result = EdgeLabSignalReader.Validate(doc, opts, asOf.AddHours(2));
        result.IsOk.Should().BeTrue(result.FailureReason);
    }

    [Fact]
    public void FiftyPctGross_panel_rejected_under_default_200pct_expectation()
    {
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        var doc = ValidDoc(asOf);
        doc.TargetWeights!["BTCUSDT"] = 0.125; doc.TargetWeights["BNBUSDT"] = 0.125;
        doc.TargetWeights["DOGEUSDT"] = -0.125; doc.TargetWeights["UNIUSDT"] = -0.125;

        // DefaultOpts() leaves ExpectedGross at the 2.0 default → must reject (back-compat).
        var result = EdgeLabSignalReader.Validate(doc, DefaultOpts(), asOf.AddHours(2));
        result.Status.Should().Be(EdgeLabReadStatus.WeightAsymmetry);
        result.FailureReason.Should().Contain("|weights|");
    }

    [Fact]
    public void WeightSanity_tolerates_floating_point_rounding()
    {
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        var doc = ValidDoc(asOf);
        // Tiny float drift well within tolerance (1e-3 net, 1e-2 gross).
        doc.TargetWeights!["BTCUSDT"] = 0.5000001;
        doc.TargetWeights["DOGEUSDT"] = -0.4999999;
        var result = EdgeLabSignalReader.Validate(doc, DefaultOpts(), asOf.AddHours(2));
        result.IsOk.Should().BeTrue(result.FailureReason);
    }

    [Fact]
    public void TimeframeMismatch_when_metadata_timeframe_differs()
    {
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        var doc = ValidDoc(asOf); doc.Metadata!.Timeframe = "1h";
        var result = EdgeLabSignalReader.Validate(doc, DefaultOpts(), asOf.AddHours(2));
        result.Status.Should().Be(EdgeLabReadStatus.TimeframeMismatch);
    }

    [Theory]
    [InlineData("4h", true, 4 * 60)]
    [InlineData("15m", true, 15)]
    [InlineData("1d", true, 24 * 60)]
    [InlineData("30s", true, 0)] // < 1 min, special-case below
    [InlineData("", false, 0)]
    [InlineData("4x", false, 0)]
    public void TryParseTimeframe_handles_known_units(string input, bool expectedOk, int expectedMinutes)
    {
        var ok = EdgeLabSignalReader.TryParseTimeframe(input, out var span);
        ok.Should().Be(expectedOk);
        if (expectedOk && input == "30s")
            span.Should().Be(TimeSpan.FromSeconds(30));
        else if (expectedOk)
            span.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void Read_returns_Missing_when_file_absent()
    {
        var opts = DefaultOpts();
        opts.InputDir = Path.Combine(Path.GetTempPath(), "edgelab-test-" + Guid.NewGuid().ToString("N"));
        var result = EdgeLabSignalReader.Read(opts, DateTimeOffset.UtcNow);
        result.Status.Should().Be(EdgeLabReadStatus.Missing);
    }

    [Fact]
    public void Read_parses_real_producer_output_shape()
    {
        // Verbatim trimmed copy of Quantlab/signals/realized_vol_4h_xs_reverse/latest.json
        // (2026-05-01 approved edge). Exercises the on-disk path, not just Validate.
        var json = """
        {
          "as_of_bar": "2026-04-30T20:00:00+00:00",
          "factor_values": {"BTCUSDT": 0.0078, "BNBUSDT": 0.0064, "DOGEUSDT": 0.0208, "UNIUSDT": 0.0136, "ETHUSDT": 0.0113},
          "generated_at": "2026-05-01T14:57:39+00:00",
          "metadata": {
            "factor": "realized_vol", "fee_bps_round_trip": 5.5, "fee_style": "maker_taker",
            "fee_tier": "vip1", "fee_venue": "okx", "k": 2, "n_symbols_eligible_at_bar": 14,
            "n_symbols_loaded": 14, "n_symbols_total": 14, "reverse": true, "timeframe": "4h"
          },
          "ranks": {"BTCUSDT": 1, "BNBUSDT": 0, "DOGEUSDT": 13, "UNIUSDT": 12, "ETHUSDT": 11},
          "schema_version": 1,
          "strategy": "realized_vol_4h_xs_reverse",
          "target_weights": {"BTCUSDT": 0.5, "BNBUSDT": 0.5, "DOGEUSDT": -0.5, "UNIUSDT": -0.5, "ETHUSDT": 0.0}
        }
        """;
        var dir = Path.Combine(Path.GetTempPath(), "edgelab-test-" + Guid.NewGuid().ToString("N"));
        var stratDir = Path.Combine(dir, "realized_vol_4h_xs_reverse");
        Directory.CreateDirectory(stratDir);
        File.WriteAllText(Path.Combine(stratDir, "latest.json"), json);
        try
        {
            var opts = DefaultOpts(); opts.InputDir = dir;
            var now = DateTimeOffset.Parse("2026-04-30T22:00:00+00:00"); // 2h after as_of_bar
            var result = EdgeLabSignalReader.Read(opts, now);
            result.IsOk.Should().BeTrue(result.FailureReason);
            result.Document!.Metadata!.K.Should().Be(2);
            result.Document.TargetWeights!["DOGEUSDT"].Should().Be(-0.5);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
