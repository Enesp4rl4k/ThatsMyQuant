using System.Text.Json;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// Per-strategy ownership of live positions on a NET-mode venue.
/// <para>
/// OKX (net mode) keeps one position per instrument. Sizing every EdgeLab strategy against the
/// TOTAL symbol position made two strategies on the same symbol fight: each traded the whole
/// position toward its own target. The book records what each strategy owns (signed base qty),
/// so a strategy trades only the delta between its target and its own share.
/// </para>
/// Self-healing rules, applied by <see cref="Resolve"/> every cycle against the exchange position:
/// <list type="bullet">
/// <item>Symbol flat on the exchange → every owner's share is reset to 0 (manual close,
/// liquidation, TP/SL trigger).</item>
/// <item>Single registered owner → its share is set to the exchange position (fully
/// self-correcting; also seeds positions opened before the book existed).</item>
/// <item>Several owners → shares come from recorded fills; a strategy with no record is seeded with
/// <c>exchange − Σ others</c>; if Σ shares diverges from the exchange a warning is returned.</item>
/// </list>
/// Quantities are signed base units (same unit as <c>ExchangePositionSnapshot.Quantity</c>).
/// </summary>
public sealed class EdgeLabLivePositionBook
{
    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, decimal>> _shares = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _owners = new(StringComparer.OrdinalIgnoreCase);

    public EdgeLabLivePositionBook(string? persistPath = null)
    {
        _path = persistPath;
        Load();
    }

    /// <summary>Declare that <paramref name="strategy"/> trades <paramref name="symbol"/>.</summary>
    public void Register(string strategy, string symbol)
    {
        lock (_gate)
        {
            if (!_owners.TryGetValue(symbol, out var set))
                _owners[symbol] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (set.Add(strategy)) Save();
        }
    }

    public IReadOnlyCollection<string> OwnersOf(string symbol)
    {
        lock (_gate)
            return _owners.TryGetValue(symbol, out var set) ? set.ToArray() : Array.Empty<string>();
    }

    public decimal Get(string strategy, string symbol)
    {
        lock (_gate) return GetUnlocked(strategy, symbol) ?? 0m;
    }

    /// <summary>
    /// Returns this strategy's share of <paramref name="symbol"/> after reconciling the book with the
    /// exchange's signed position. <paramref name="warning"/> is set when shares and exchange disagree.
    /// </summary>
    public decimal Resolve(string strategy, string symbol, decimal exchangeSignedQty, out string? warning)
    {
        warning = null;
        lock (_gate)
        {
            var owners = _owners.TryGetValue(symbol, out var set) ? set : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { strategy };
            if (exchangeSignedQty == 0m)
            {
                var changed = false;
                foreach (var o in owners)
                    changed |= SetUnlocked(o, symbol, 0m);
                if (changed) Save();
                return 0m;
            }
            if (owners.Count <= 1)
            {
                if (SetUnlocked(strategy, symbol, exchangeSignedQty)) Save();
                return exchangeSignedQty;
            }
            if (GetUnlocked(strategy, symbol) is null)
            {
                var others = owners.Where(o => !string.Equals(o, strategy, StringComparison.OrdinalIgnoreCase))
                                   .Sum(o => GetUnlocked(o, symbol) ?? 0m);
                SetUnlocked(strategy, symbol, exchangeSignedQty - others);
                Save();
            }
            var sum = owners.Sum(o => GetUnlocked(o, symbol) ?? 0m);
            var tol = Math.Max(Math.Abs(exchangeSignedQty) * 0.01m, 1e-9m);
            if (Math.Abs(sum - exchangeSignedQty) > tol)
                warning = $"{symbol}: strategy shares sum to {sum} but exchange position is {exchangeSignedQty} " +
                          $"(owners: {string.Join(", ", owners)}). Using recorded shares.";
            return GetUnlocked(strategy, symbol) ?? 0m;
        }
    }

    /// <summary>Add a fill (signed base qty: buy +, sell −) to the strategy's share.</summary>
    public void ApplyFill(string strategy, string symbol, decimal signedQty)
    {
        if (signedQty == 0m) return;
        lock (_gate)
        {
            SetUnlocked(strategy, symbol, (GetUnlocked(strategy, symbol) ?? 0m) + signedQty);
            Save();
        }
    }

    private decimal? GetUnlocked(string strategy, string symbol) =>
        _shares.TryGetValue(strategy, out var m) && m.TryGetValue(symbol, out var q) ? q : null;

    private bool SetUnlocked(string strategy, string symbol, decimal qty)
    {
        if (!_shares.TryGetValue(strategy, out var m))
            _shares[strategy] = m = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (m.TryGetValue(symbol, out var old) && old == qty) return false;
        m[symbol] = qty;
        return true;
    }

    private sealed record Snapshot(Dictionary<string, Dictionary<string, decimal>> Shares, Dictionary<string, string[]> Owners);

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path));
            if (snap is null) return;
            foreach (var (s, m) in snap.Shares)
                _shares[s] = new Dictionary<string, decimal>(m, StringComparer.OrdinalIgnoreCase);
            foreach (var (sym, os) in snap.Owners)
                _owners[sym] = new HashSet<string>(os, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Corrupt/unreadable book: start empty. Single-owner symbols re-seed from the exchange
            // on the next cycle; multi-owner symbols re-seed via exchange − Σ others.
        }
    }

    private void Save()
    {
        if (_path is null) return;
        var snap = new Snapshot(_shares, _owners.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
        var tmp = _path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(tmp, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
