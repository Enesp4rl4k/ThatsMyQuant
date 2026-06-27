using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// Tolerant <see cref="DateTimeOffset"/> JSON reader for EdgeLab signal timestamps.
///
/// System.Text.Json's built-in converter only accepts strict ISO 8601 with a 'T'
/// date/time separator. Quantlab's emit script for the panel strategies (notably
/// <c>xs_momentum_neutral_panel</c>) serialises pandas Timestamps via <c>str()</c>,
/// which uses a SPACE separator: <c>"2026-06-26 04:00:00+00:00"</c>. The strict
/// converter throws <see cref="JsonException"/> → <see cref="EdgeLabSignalReader"/>
/// tags the whole signal <c>Malformed</c> and the edge never trades (observed
/// 2026-06: xs_momentum_neutral_panel rejected every cycle despite live non-zero
/// weights). RFC 3339 §5.6 explicitly permits a space in place of 'T' "by mutual
/// agreement", so accepting it is spec-compliant, not a workaround.
///
/// Behaviour contract: strict ISO 8601 (with 'T') is handled by the built-in fast
/// path and parses byte-identically to before — only the previously-rejected space
/// variant changes outcome. Applied to <see cref="EdgeLabSignalDocument.AsOfBar"/>
/// and <see cref="EdgeLabSignalDocument.GeneratedAt"/>; both readers deserialise the
/// same POCO with default options, so the annotation covers the XS and single-asset
/// paths alike.
/// </summary>
public sealed class LenientDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        // Fast path: built-in strict ISO 8601 (T separator). Keeps every existing
        // signal's parse result identical — only space-separated inputs fall through.
        if (reader.TryGetDateTimeOffset(out var dto))
            return dto;

        var raw = reader.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // Normalise "yyyy-MM-dd HH:mm:ss…" → "yyyy-MM-ddTHH:mm:ss…" (date is always
        // 10 chars; separator sits at index 10).
        var s = raw;
        if (s.Length > 10 && s[10] == ' ')
            s = string.Concat(s.AsSpan(0, 10), "T", s.AsSpan(11));

        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out dto))
            return dto;

        throw new JsonException($"Unparseable DateTimeOffset '{raw}'");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value);
    }
}
