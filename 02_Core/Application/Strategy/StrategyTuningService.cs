using System.Text.Json;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

public interface IStrategyTuningStateStore
{
    Task<StrategyTuningState> LoadAsync();
    Task SaveAsync(StrategyTuningState state);
}

public sealed class JsonStrategyTuningStateStore : IStrategyTuningStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public JsonStrategyTuningStateStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant");
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "strategy-tuning.json");
    }

    public async Task<StrategyTuningState> LoadAsync()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return StrategyTuningState.CreateDefault();
            }

            var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<StrategyTuningState>(json, JsonOptions)
                   ?? StrategyTuningState.CreateDefault();
        }
        catch
        {
            return StrategyTuningState.CreateDefault();
        }
    }

    public async Task SaveAsync(StrategyTuningState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort persistence only.
        }
    }
}

public sealed class StrategyTuningService
{
    public const string MomentumKey = "Momentum";
    public const string BreakoutKey = "Breakout";
    public const string ScalpingKey = "Scalping";
    public const string StatArbKey = "StatArb";

    private readonly object _sync = new();
    private readonly IStrategyTuningStateStore _store;
    private StrategyTuningState _state = StrategyTuningState.CreateDefault();
    private bool _initialized;

    public StrategyTuningService(IStrategyTuningStateStore? store = null)
    {
        _store = store ?? new JsonStrategyTuningStateStore();
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        var loaded = await _store.LoadAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _state = loaded ?? StrategyTuningState.CreateDefault();
            NormalizeState(_state);
            _initialized = true;
        }
    }

    public IReadOnlyList<string> GetStrategyKeys()
    {
        lock (_sync)
        {
            return _state.Strategies
                .Select(strategy => strategy.StrategyKey)
                .ToArray();
        }
    }

    public StrategyTuningEnvelope GetEnvelope(string strategyKey)
    {
        lock (_sync)
        {
            return GetOrCreateEnvelope(strategyKey).Clone();
        }
    }

    public async Task<StrategyTuningEnvelope> SaveCandidateAsync(string strategyKey, StrategyParameterProfile candidate, string? note = null)
    {
        StrategyTuningState snapshot;
        StrategyTuningEnvelope envelope;

        lock (_sync)
        {
            envelope = GetOrCreateEnvelope(strategyKey);
            envelope.Candidate = candidate.Clone();
            if (!string.IsNullOrWhiteSpace(note))
            {
                envelope.Candidate.Note = note.Trim();
            }

            envelope.LastCandidateSummary = $"Candidate staged at {DateTime.Now:dd MMM HH:mm} for {strategyKey}.";
            envelope.UpdatedAtUtc = DateTime.UtcNow;
            snapshot = _state.Clone();
            envelope = envelope.Clone();
        }

        await _store.SaveAsync(snapshot).ConfigureAwait(false);
        return envelope;
    }

    public async Task<StrategyTuningEnvelope> PromoteCandidateAsync(string strategyKey, string? note = null)
    {
        StrategyTuningState snapshot;
        StrategyTuningEnvelope envelope;

        lock (_sync)
        {
            envelope = GetOrCreateEnvelope(strategyKey);
            var promoted = (envelope.Candidate ?? envelope.Active).Clone();
            if (!string.IsNullOrWhiteSpace(note))
            {
                promoted.Note = note.Trim();
            }

            envelope.Previous = envelope.Active.Clone();
            envelope.Active = promoted;
            envelope.Candidate = promoted.Clone();
            envelope.LastPromotionSummary = $"{strategyKey} promoted at {DateTime.Now:dd MMM HH:mm}.";
            envelope.LastPromotedAtUtc = DateTime.UtcNow;
            envelope.UpdatedAtUtc = DateTime.UtcNow;
            snapshot = _state.Clone();
            envelope = envelope.Clone();
        }

        await _store.SaveAsync(snapshot).ConfigureAwait(false);
        return envelope;
    }

    public async Task<StrategyTuningEnvelope> RollbackAsync(string strategyKey, string? note = null)
    {
        StrategyTuningState snapshot;
        StrategyTuningEnvelope envelope;

        lock (_sync)
        {
            envelope = GetOrCreateEnvelope(strategyKey);
            if (envelope.Previous == null)
            {
                return envelope.Clone();
            }

            var current = envelope.Active.Clone();
            var previous = envelope.Previous.Clone();
            if (!string.IsNullOrWhiteSpace(note))
            {
                previous.Note = note.Trim();
            }

            envelope.Active = previous;
            envelope.Previous = current;
            envelope.Candidate = envelope.Active.Clone();
            envelope.LastRollbackSummary = $"{strategyKey} rolled back at {DateTime.Now:dd MMM HH:mm}.";
            envelope.LastRolledBackAtUtc = DateTime.UtcNow;
            envelope.UpdatedAtUtc = DateTime.UtcNow;
            snapshot = _state.Clone();
            envelope = envelope.Clone();
        }

        await _store.SaveAsync(snapshot).ConfigureAwait(false);
        return envelope;
    }

    public MomentumOptions GetMomentumOptions()
    {
        var active = GetEnvelope(MomentumKey).Active;
        return new MomentumOptions
        {
            RsiBullThreshold = (double)active.PrimaryValue,
            AdxTrendThreshold = (double)active.SecondaryValue,
            VolumeExpansionMultiplier = (double)active.TertiaryValue,
            SwarmWeight = active.SwarmWeight
        };
    }

    public BreakoutOptions GetBreakoutOptions()
    {
        var active = GetEnvelope(BreakoutKey).Active;
        return new BreakoutOptions
        {
            VolumeSurgeMultiple = (double)active.PrimaryValue,
            RangeThresholdPercent = (double)active.SecondaryValue,
            RetestProximityPercent = (double)active.TertiaryValue,
            SwarmWeight = active.SwarmWeight
        };
    }

    public ScalpingOptions GetScalpingOptions()
    {
        var active = GetEnvelope(ScalpingKey).Active;
        return new ScalpingOptions
        {
            StrongOfiThreshold = (double)active.PrimaryValue,
            MaxSpreadBps = (double)active.SecondaryValue,
            MinFillProbability = (double)active.TertiaryValue,
            SwarmWeight = active.SwarmWeight
        };
    }

    public StatArbOptions GetStatArbOptions()
    {
        var active = GetEnvelope(StatArbKey).Active;
        return new StatArbOptions
        {
            EntryZScore = (double)active.PrimaryValue,
            MaxHalfLife = (double)active.SecondaryValue,
            VwapAgreementThreshold = (double)active.TertiaryValue,
            SwarmWeight = active.SwarmWeight
        };
    }

    private static void NormalizeState(StrategyTuningState state)
    {
        var defaults = StrategyTuningState.CreateDefault();
        foreach (var defaultEnvelope in defaults.Strategies)
        {
            var existing = state.Strategies.FirstOrDefault(strategy =>
                string.Equals(strategy.StrategyKey, defaultEnvelope.StrategyKey, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                state.Strategies.Add(defaultEnvelope);
                continue;
            }

            existing.Active ??= defaultEnvelope.Active.Clone();
            existing.Candidate ??= existing.Active.Clone();
        }
    }

    private StrategyTuningEnvelope GetOrCreateEnvelope(string strategyKey)
    {
        var envelope = _state.Strategies.FirstOrDefault(strategy =>
            string.Equals(strategy.StrategyKey, strategyKey, StringComparison.OrdinalIgnoreCase));
        if (envelope != null)
        {
            return envelope;
        }

        var created = StrategyTuningState.CreateDefault().Strategies.First(strategy =>
            string.Equals(strategy.StrategyKey, strategyKey, StringComparison.OrdinalIgnoreCase)).Clone();
        _state.Strategies.Add(created);
        return created;
    }
}

public sealed class StrategyTuningState
{
    public List<StrategyTuningEnvelope> Strategies { get; set; } = new();
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public StrategyTuningState Clone()
    {
        return new StrategyTuningState
        {
            Strategies = Strategies.Select(strategy => strategy.Clone()).ToList(),
            UpdatedAtUtc = UpdatedAtUtc
        };
    }

    public static StrategyTuningState CreateDefault()
    {
        return new StrategyTuningState
        {
            Strategies =
            [
                new StrategyTuningEnvelope
                {
                    StrategyKey = StrategyTuningService.MomentumKey,
                    Active = new StrategyParameterProfile
                    {
                        StrategyKey = StrategyTuningService.MomentumKey,
                        PrimaryValue = 60m,
                        SecondaryValue = 20m,
                        TertiaryValue = 1.10m,
                        SwarmWeight = 20,
                        Note = "Baseline momentum profile."
                    }
                },
                new StrategyTuningEnvelope
                {
                    StrategyKey = StrategyTuningService.BreakoutKey,
                    Active = new StrategyParameterProfile
                    {
                        StrategyKey = StrategyTuningService.BreakoutKey,
                        PrimaryValue = 1.50m,
                        SecondaryValue = 0.03m,
                        TertiaryValue = 0.005m,
                        SwarmWeight = 25,
                        Note = "Baseline breakout profile."
                    }
                },
                new StrategyTuningEnvelope
                {
                    StrategyKey = StrategyTuningService.ScalpingKey,
                    Active = new StrategyParameterProfile
                    {
                        StrategyKey = StrategyTuningService.ScalpingKey,
                        PrimaryValue = 0.60m,
                        SecondaryValue = 10m,
                        TertiaryValue = 0.70m,
                        SwarmWeight = 15,
                        Note = "Baseline scalping profile."
                    }
                },
                new StrategyTuningEnvelope
                {
                    StrategyKey = StrategyTuningService.StatArbKey,
                    Active = new StrategyParameterProfile
                    {
                        StrategyKey = StrategyTuningService.StatArbKey,
                        PrimaryValue = 2.50m,
                        SecondaryValue = 15m,
                        TertiaryValue = 0.50m,
                        SwarmWeight = 30,
                        Note = "Baseline stat-arb profile."
                    }
                }
            ]
        };
    }
}

public sealed class StrategyTuningEnvelope
{
    public string StrategyKey { get; set; } = string.Empty;
    public StrategyParameterProfile Active { get; set; } = new();
    public StrategyParameterProfile? Candidate { get; set; }
    public StrategyParameterProfile? Previous { get; set; }
    public string LastCandidateSummary { get; set; } = "No staged candidate.";
    public string LastPromotionSummary { get; set; } = "No promoted profile yet.";
    public string LastRollbackSummary { get; set; } = "No rollback has been performed.";
    public DateTime? LastPromotedAtUtc { get; set; }
    public DateTime? LastRolledBackAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public StrategyTuningEnvelope Clone()
    {
        return new StrategyTuningEnvelope
        {
            StrategyKey = StrategyKey,
            Active = Active.Clone(),
            Candidate = Candidate?.Clone(),
            Previous = Previous?.Clone(),
            LastCandidateSummary = LastCandidateSummary,
            LastPromotionSummary = LastPromotionSummary,
            LastRollbackSummary = LastRollbackSummary,
            LastPromotedAtUtc = LastPromotedAtUtc,
            LastRolledBackAtUtc = LastRolledBackAtUtc,
            UpdatedAtUtc = UpdatedAtUtc
        };
    }
}

public sealed class StrategyParameterProfile
{
    public string StrategyKey { get; set; } = string.Empty;
    public decimal PrimaryValue { get; set; }
    public decimal SecondaryValue { get; set; }
    public decimal TertiaryValue { get; set; }
    public int SwarmWeight { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public StrategyParameterProfile Clone()
    {
        return new StrategyParameterProfile
        {
            StrategyKey = StrategyKey,
            PrimaryValue = PrimaryValue,
            SecondaryValue = SecondaryValue,
            TertiaryValue = TertiaryValue,
            SwarmWeight = SwarmWeight,
            Note = Note,
            UpdatedAtUtc = UpdatedAtUtc
        };
    }
}
