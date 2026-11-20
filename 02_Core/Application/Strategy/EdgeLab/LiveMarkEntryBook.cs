using System.Text.Json;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// Paper venues only: the average entry of each net position measured at the REAL market's mark price.
/// The OKX demo engine prices drift from the real market (NEAR −30%, ARB demo wicks of 13% that never happened on
/// 2026-11-20), so a stop placed on the demo venue fires on prices that did not exist. The bot-side catastrophe stop
/// uses this book instead. Changes in the venue's net position are priced at the live mark when they are observed
/// (within a minute of the fill): adds average in, reductions keep the entry, a flip or a new position restarts it.
/// Persisted so a restart keeps the entries.
/// </summary>
public sealed class LiveMarkEntryBook
{
    private readonly string? _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public sealed record Entry(decimal SignedQty, decimal Price);

    public LiveMarkEntryBook(string? persistPath = null)
    {
        _path = persistPath;
        Load();
    }

    /// <summary>
    /// Records the venue's current signed net quantity for <paramref name="symbol"/> at the live mark and returns the
    /// live-market average entry (null when flat).
    /// </summary>
    public decimal? Observe(string symbol, decimal signedQty, decimal liveMark)
    {
        lock (_gate)
        {
            if (signedQty == 0m)
            {
                if (_entries.Remove(symbol)) Save();
                return null;
            }
            _entries.TryGetValue(symbol, out var prev);
            decimal price;
            if (prev is null || prev.SignedQty == 0m || Math.Sign(prev.SignedQty) != Math.Sign(signedQty))
                price = liveMark;                                               // new position or flip
            else if (Math.Abs(signedQty) > Math.Abs(prev.SignedQty))
                price = (Math.Abs(prev.SignedQty) * prev.Price + (Math.Abs(signedQty) - Math.Abs(prev.SignedQty)) * liveMark)
                        / Math.Abs(signedQty);                                  // added: average in
            else
                price = prev.Price;                                             // reduced or unchanged
            if (prev is null || prev.SignedQty != signedQty || prev.Price != price)
            {
                _entries[symbol] = new Entry(signedQty, price);
                Save();
            }
            return price;
        }
    }

    /// <summary>Drops symbols that are no longer held.</summary>
    public void Retain(IEnumerable<string> heldSymbols)
    {
        var keep = new HashSet<string>(heldSymbols, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            var gone = _entries.Keys.Where(k => !keep.Contains(k)).ToList();
            foreach (var k in gone) _entries.Remove(k);
            if (gone.Count > 0) Save();
        }
    }

    public void Forget(string symbol)
    {
        lock (_gate) { if (_entries.Remove(symbol)) Save(); }
    }

    public Entry? Get(string symbol)
    {
        lock (_gate) return _entries.TryGetValue(symbol, out var e) ? e : null;
    }

    /// <summary>True when the live mark range reached the stop: low ≤ trigger for a long, high ≥ trigger for a short.</summary>
    public static bool Triggered(bool isLong, decimal entry, decimal stopPct, decimal low, decimal high)
        => isLong ? low <= entry * (1m - stopPct) : high >= entry * (1m + stopPct);

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var s = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path));
            if (s is not null) _entries = new Dictionary<string, Entry>(s, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* start empty */ }
    }

    private void Save()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
