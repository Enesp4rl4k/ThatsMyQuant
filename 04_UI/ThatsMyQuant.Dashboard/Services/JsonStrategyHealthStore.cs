using System.Text.Json;
using System.IO;

namespace ThatsMyQuant.Dashboard.Services;

public sealed class JsonStrategyHealthStore : IStrategyHealthStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JsonStrategyHealthStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant");
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "strategy-health.json");
    }

    public async Task<IReadOnlyList<StrategyHealthSnapshot>> LoadAsync()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<StrategyHealthSnapshot>();
            }

            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<List<StrategyHealthSnapshot>>(json, JsonOptions)
                   ?? new List<StrategyHealthSnapshot>();
        }
        catch
        {
            return Array.Empty<StrategyHealthSnapshot>();
        }
    }

    public async Task SaveAsync(IEnumerable<StrategyHealthSnapshot> snapshots)
    {
        try
        {
            var json = JsonSerializer.Serialize(snapshots.ToList(), JsonOptions);
            await File.WriteAllTextAsync(_filePath, json);
        }
        catch
        {
            // Best-effort persistence only; UI should keep running even if local storage fails.
        }
    }
}
