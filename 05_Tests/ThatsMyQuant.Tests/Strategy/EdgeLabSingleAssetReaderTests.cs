using System.Text.Json;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

/// <summary>
/// Integration tests for the single-asset signal reader added 2026-05-08.
/// Verifies that signals emitted by Quantlab's `generate_single_asset_signal`
/// (edgelab/signals.py) parse correctly through the v1 contract on the bot
/// side. Each test is the C# half of a contract round-trip — same schema
/// the Python producer emits.
/// </summary>
public class EdgeLabSingleAssetReaderTests
{
    private const string Strategy = "ada_funding_zscore_meanrev_singlename";

    private static EdgeLabSignalDocument BuildValidSingleAssetDoc(
        DateTimeOffset asOfBar, double weight = -1.0)
    {
        return new EdgeLabSignalDocument
        {
            SchemaVersion = 1,
            Strategy = Strategy,
            GeneratedAt = asOfBar,
            AsOfBar = asOfBar,
            TargetWeights = new() { ["ADAUSDT"] = weight },
            FactorValues = new() { ["ADAUSDT"] = 1.0 },
            Ranks = new() { ["ADAUSDT"] = 0 },
            Metadata = new EdgeLabSignalMetadata
            {
                StrategyKind = "single_asset",
                Factor = "funding_zscore_meanrev",
                Timeframe = "4h",
                Leverage = 1.0,
                FeeVenue = "bybit",
                FeeTier = "retail",
                FeeStyle = "maker_taker",
                FeeBpsRoundTrip = 7.5,
                Notes = "audit-test",
            }
        };
    }

    [Fact]
    public void ValidSingleAssetSignal_IsAccepted()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = BuildValidSingleAssetDoc(now.AddHours(-1));
        var r = EdgeLabSingleAssetReader.Validate(doc, Strategy, now, TimeSpan.FromHours(4));

        Assert.True(r.IsOk, $"expected OK, got {r.Status}: {r.FailureReason}");
        Assert.Equal("ADAUSDT", r.Symbol);
        Assert.Equal(-1.0, r.TargetWeight);
    }

    [Fact]
    public void MissingStrategyKind_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = BuildValidSingleAssetDoc(now);
        doc.Metadata!.StrategyKind = null;
        var r = EdgeLabSingleAssetReader.Validate(doc, Strategy, now, TimeSpan.FromHours(4));

        Assert.Equal(EdgeLabSingleAssetReadStatus.KindMismatch, r.Status);
    }

    [Fact]
    public void XsKindMistakenlyRouted_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = BuildValidSingleAssetDoc(now);
        doc.Metadata!.StrategyKind = null;  // simulating XS signal mis-routed here
        doc.TargetWeights = new() { ["BTC"] = 0.5, ["ETH"] = 0.5, ["XRP"] = -0.5, ["SOL"] = -0.5 };
        var r = EdgeLabSingleAssetReader.Validate(doc, Strategy, now, TimeSpan.FromHours(4));

        Assert.Equal(EdgeLabSingleAssetReadStatus.KindMismatch, r.Status);
    }

    [Fact]
    public void TwoSymbols_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = BuildValidSingleAssetDoc(now);
        doc.TargetWeights = new() { ["ADAUSDT"] = 1.0, ["BTCUSDT"] = -1.0 };
        var r = EdgeLabSingleAssetReader.Validate(doc, Strategy, now, TimeSpan.FromHours(4));

        Assert.Equal(EdgeLabSingleAssetReadStatus.WeightShapeInvalid, r.Status);
    }

    [Fact]
    public void WeightExceedsLeverage_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = BuildValidSingleAssetDoc(now);
        doc.Metadata!.Leverage = 1.0;
        doc.TargetWeights = new() { ["ADAUSDT"] = 2.5 };  // leverage 1.0 violated
        var r = EdgeLabSingleAssetReader.Validate(doc, Strategy, now, TimeSpan.FromHours(4));

        Assert.Equal(EdgeLabSingleAssetReadStatus.WeightShapeInvalid, r.Status);
    }

    [Fact]
    public void StaleSignal_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = BuildValidSingleAssetDoc(now.AddHours(-12));  // 12h stale
        var r = EdgeLabSingleAssetReader.Validate(doc, Strategy, now, TimeSpan.FromHours(4));

        // 2x4h = 8h max age; 12h > 8h → stale
        Assert.Equal(EdgeLabSingleAssetReadStatus.Stale, r.Status);
    }

    [Fact]
    public void StrategyMismatch_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = BuildValidSingleAssetDoc(now);
        var r = EdgeLabSingleAssetReader.Validate(doc, "different_strategy_name", now, TimeSpan.FromHours(4));

        Assert.Equal(EdgeLabSingleAssetReadStatus.StrategyMismatch, r.Status);
    }

    [Fact]
    public void RoundTripJson_FromQuantlabEmittedShape()
    {
        // Mirror exactly what Quantlab edgelab/signals.py:generate_single_asset_signal emits
        const string json = """
        {
          "schema_version": 1,
          "strategy": "ada_funding_zscore_meanrev_singlename",
          "generated_at": "2026-05-08T08:00:00+00:00",
          "as_of_bar": "2026-05-08T08:00:00+00:00",
          "target_weights": { "ADAUSDT": -1.0 },
          "factor_values": { "ADAUSDT": 1.0 },
          "ranks": { "ADAUSDT": 0 },
          "metadata": {
            "strategy_kind": "single_asset",
            "factor": "funding_zscore_meanrev",
            "timeframe": "4h",
            "leverage": 1.0,
            "fee_venue": "bybit",
            "fee_tier": "retail",
            "fee_style": "maker_taker",
            "fee_bps_round_trip": 7.5,
            "n_bars_loaded": 10950,
            "raw_signal_at_bar": -1,
            "notes": "audit smoke"
          }
        }
        """;

        var doc = JsonSerializer.Deserialize<EdgeLabSignalDocument>(json);
        Assert.NotNull(doc);
        Assert.Equal(1, doc!.SchemaVersion);
        Assert.Equal("single_asset", doc.Metadata!.StrategyKind);
        Assert.Equal(1.0, doc.Metadata.Leverage);
        Assert.Single(doc.TargetWeights!);
        Assert.Equal(-1.0, doc.TargetWeights!["ADAUSDT"]);

        // Validate it through the reader using a now in the past so age is small
        var now = new DateTimeOffset(2026, 5, 8, 8, 30, 0, TimeSpan.Zero);
        var r = EdgeLabSingleAssetReader.Validate(
            doc, "ada_funding_zscore_meanrev_singlename", now, TimeSpan.FromHours(4));
        Assert.True(r.IsOk, $"validate failed: {r.Status}: {r.FailureReason}");
    }
}
