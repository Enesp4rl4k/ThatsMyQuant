using System.Text.Json;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// After a catastrophe stop closes a position, no strategy may re-open the same side of that symbol until
/// its signal changes (goes flat or flips). This matches the stop backtest, where a stopped leg stays flat
/// until its original trade would have ended; without it the next cycle would simply buy the position back.
/// Persisted so a restart does not forget a stop-out.
/// </summary>
public sealed class EdgeLabStopBlock
{
    private readonly string? _path;
    private readonly object _gate = new();
    private Dictionary<string, int> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastCheckUtc = DateTime.UtcNow;

    public EdgeLabStopBlock(string? persistPath = null)
    {
        _path = persistPath;
        Load();
    }

    public DateTime LastCheckUtc { get { lock (_gate) return _lastCheckUtc; } }

    /// <summary>Record that a stop closed a position of sign <paramref name="side"/> (+1 long, −1 short).</summary>
    public void Block(string symbol, int side)
    {
        lock (_gate) { _blocked[symbol] = Math.Sign(side); Save(); }
    }

    public void MarkChecked(DateTime utc)
    {
        lock (_gate) { _lastCheckUtc = utc; Save(); }
    }

    /// <summary>
    /// Applies the block to a target notional: returns 0 while the target keeps the stopped side, and
    /// clears the block as soon as the target is flat or on the other side.
    /// </summary>
    public decimal Apply(string symbol, decimal targetNotional, out bool blocked)
    {
        blocked = false;
        lock (_gate)
        {
            if (!_blocked.TryGetValue(symbol, out var side)) return targetNotional;
            if (Math.Sign(targetNotional) == side)
            {
                blocked = true;
                return 0m;
            }
            _blocked.Remove(symbol);
            Save();
            return targetNotional;
        }
    }

    public bool IsBlocked(string symbol) { lock (_gate) return _blocked.ContainsKey(symbol); }

    private sealed record Snapshot(Dictionary<string, int> Blocked, DateTime LastCheckUtc);

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var s = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path));
            if (s is null) return;
            _blocked = new Dictionary<string, int>(s.Blocked, StringComparer.OrdinalIgnoreCase);
            _lastCheckUtc = s.LastCheckUtc;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* start empty */ }
    }

    private void Save()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Snapshot(_blocked, _lastCheckUtc), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
