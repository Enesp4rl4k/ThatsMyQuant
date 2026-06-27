using FluentAssertions;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy.EdgeLab;

/// <summary>
/// CONTRACT tests: run realistic Quantlab producer output (verbatim shapes captured from
/// real signals/*/latest.json on 2026-06-27) through the FULL reader pipeline — JSON
/// deserialisation included — and assert the consumer accepts them.
///
/// Unit tests elsewhere pin individual gates; these lock the END-TO-END producer→consumer
/// contract so the next format drift fails a build instead of failing closed in silence at
/// runtime. All three production bugs found 2026-06-27 (space-separated as_of_bar; 50%-gross
/// panel vs hardcoded 200%; …) would have been caught here.
///
/// When Quantlab legitimately changes a shape, update the fixture here deliberately — the
/// diff is the contract change, and the test tells you whether the consumer still accepts it.
/// </summary>
public class EdgeLabSignalContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tmq_contract_" + Guid.NewGuid().ToString("N"));

    private string WriteSignal(string strategy, string json)
    {
        var dir = Path.Combine(_root, strategy);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "latest.json"), json);
        return _root;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── XS panel: the xs_momentum_neutral_panel shape — SPACE-separated as_of_bar (pandas
    //    str()), dollar-neutral, deliberately 50% gross (k=8 at ±0.0625). ──
    private const string XsPanelJson = """
    {
      "schema_version": 1,
      "strategy": "xs_momentum_neutral_panel",
      "generated_at": "2026-06-26T10:40:07.869890+00:00",
      "as_of_bar": "2026-06-26 04:00:00+00:00",
      "target_weights": {
        "ADAUSDT": -0.0625, "BCHUSDT": -0.0625, "DOGEUSDT": -0.0625, "INJUSDT": -0.0625,
        "HYPEUSDT": 0.0625, "JTOUSDT": 0.0625, "SOLUSDT": 0.0625, "ZECUSDT": 0.0625
      },
      "factor_values": {
        "ADAUSDT": -0.04, "BCHUSDT": -0.03, "DOGEUSDT": -0.02, "INJUSDT": -0.01,
        "HYPEUSDT": 0.05, "JTOUSDT": 0.04, "SOLUSDT": 0.03, "ZECUSDT": 0.02
      },
      "ranks": {},
      "metadata": {
        "factor": "xs_momentum", "timeframe": "4h", "k": 8, "reverse": false,
        "n_symbols_total": 32, "n_symbols_loaded": 32, "n_symbols_eligible_at_bar": 24
      }
    }
    """;

    [Fact]
    public void XsPanel_realProducerShape_isActionable_underConfiguredGross()
    {
        var opts = new EdgeLabSignalOptions
        {
            Enabled = true,
            InputDir = WriteSignal("xs_momentum_neutral_panel", XsPanelJson),
            Strategy = "xs_momentum_neutral_panel",
            Timeframe = "4h",
            ExpectedGross = 0.5,   // matches appsettings.Paper.json for this strategy
        };
        // as_of_bar 2026-06-26 04:00Z, well within 2×4h of this "now".
        var now = DateTimeOffset.Parse("2026-06-26T05:30:00+00:00");

        var result = EdgeLabSignalReader.Read(opts, now);

        result.IsOk.Should().BeTrue(result.FailureReason);   // would fail if date OR gross drifts
    }

    // ── Single-asset breakout: the eth_breakout_1h_long shape — ISO-T date, one symbol,
    //    metadata.strategy_kind = single_asset. ──
    private const string SingleAssetJson = """
    {
      "schema_version": 1,
      "strategy": "eth_breakout_1h_long",
      "generated_at": "2026-06-26T12:03:34.672496+00:00",
      "as_of_bar": "2026-06-26T12:00:00+00:00",
      "target_weights": { "ETHUSDT": 0.35 },
      "factor_values": { "ETHUSDT": 1.0 },
      "ranks": {},
      "metadata": {
        "factor": "breakout", "timeframe": "1h", "strategy_kind": "single_asset",
        "leverage": 1.5, "kelly_haircut": 0.75
      }
    }
    """;

    [Fact]
    public void SingleAsset_realProducerShape_isActionable()
    {
        var root = WriteSignal("eth_breakout_1h_long", SingleAssetJson);
        var now = DateTimeOffset.Parse("2026-06-26T12:30:00+00:00"); // within 2×1h
        var result = EdgeLabSingleAssetReader.Read(root, "eth_breakout_1h_long", now, TimeSpan.FromHours(1));

        result.IsOk.Should().BeTrue(result.FailureReason);
        result.Symbol.Should().Be("ETHUSDT");
        result.TargetWeight.Should().BeApproximately(0.35, 1e-9);
    }

    [Fact]
    public void SingleAsset_flatZeroWeight_isStillActionable() // breakout not firing = valid flat signal
    {
        var json = SingleAssetJson.Replace("\"ETHUSDT\": 0.35", "\"ETHUSDT\": 0.0");
        var root = WriteSignal("eth_breakout_1h_long", json);
        var now = DateTimeOffset.Parse("2026-06-26T12:30:00+00:00");
        var result = EdgeLabSingleAssetReader.Read(root, "eth_breakout_1h_long", now, TimeSpan.FromHours(1));

        result.IsOk.Should().BeTrue(result.FailureReason);
        result.TargetWeight.Should().Be(0.0);
    }
}
