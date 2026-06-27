using System.Text.Json;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy.EdgeLab;

/// <summary>
/// Regression cover for the 2026-06 production incident: xs_momentum_neutral_panel
/// emitted <c>as_of_bar</c> with a SPACE separator ("2026-06-26 04:00:00+00:00")
/// instead of 'T'. The strict built-in System.Text.Json converter rejected it →
/// every cycle came back Malformed and the edge never traded despite live weights.
/// These tests lock in that both separators deserialise to the same instant.
/// </summary>
public class LenientDateTimeOffsetConverterTests
{
    private static EdgeLabSignalDocument Deserialize(string asOfBarLiteral)
    {
        var json = $$"""
        {
          "schema_version": 1,
          "strategy": "xs_momentum_neutral_panel",
          "generated_at": "2026-06-26T10:40:07.869890+00:00",
          "as_of_bar": {{asOfBarLiteral}},
          "target_weights": { "ADAUSDT": -0.0625 },
          "factor_values": { "ADAUSDT": 0.01 }
        }
        """;
        return JsonSerializer.Deserialize<EdgeLabSignalDocument>(json)!;
    }

    [Fact]
    public void Space_separated_as_of_bar_parses() // the production-failing case
    {
        var doc = Deserialize("\"2026-06-26 04:00:00+00:00\"");
        doc.AsOfBar.Should().Be(DateTimeOffset.Parse("2026-06-26T04:00:00+00:00"));
    }

    [Fact]
    public void Iso_T_separated_as_of_bar_parses_identically()
    {
        var doc = Deserialize("\"2026-06-26T04:00:00+00:00\"");
        doc.AsOfBar.Should().Be(DateTimeOffset.Parse("2026-06-26T04:00:00+00:00"));
    }

    [Fact]
    public void Space_and_T_forms_yield_the_same_instant()
    {
        var space = Deserialize("\"2026-06-26 04:00:00+00:00\"").AsOfBar;
        var tee = Deserialize("\"2026-06-26T04:00:00+00:00\"").AsOfBar;
        space.Should().Be(tee);
    }

    [Fact]
    public void Fractional_seconds_with_T_still_parse()
    {
        var doc = Deserialize("\"2026-06-26T12:04:15.599611+00:00\"");
        doc.AsOfBar.Should().Be(DateTimeOffset.Parse("2026-06-26T12:04:15.599611+00:00"));
    }

    [Fact]
    public void Null_as_of_bar_stays_null()
    {
        var doc = Deserialize("null");
        doc.AsOfBar.Should().BeNull();
    }
}
