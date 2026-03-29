using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.MetaLearning;

public sealed class OnlineLearningAdaptorAgent : IMetaLearningAgent
{
    private readonly ILogger<OnlineLearningAdaptorAgent> _logger;
    private readonly IDbContextFactory<QuantDbContext> _dbFactory;
    private readonly ConcurrentDictionary<string, ExponentialWeightedStat> _strategyStats = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _stateLoaded;

    private const float DecayAlpha = 0.05f;
    private const float BaseWeight = 1.0f;
    private const int MinHistoryForAdaptation = 5;

    public string AgentId => "Meta.OnlineLearning";

    public OnlineLearningAdaptorAgent(
        ILogger<OnlineLearningAdaptorAgent> logger,
        IDbContextFactory<QuantDbContext> dbFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
    }

    public async Task<MetaAdaptation> AdaptAsync(
        IReadOnlyList<Trade> recentHistory,
        MarketRegime regime,
        CancellationToken ct = default)
    {
        await EnsureStateLoadedAsync(ct);

        foreach (var trade in recentHistory.TakeLast(20))
        {
            if (string.IsNullOrEmpty(trade.StrategyTag)) continue;

            var stat = _strategyStats.GetOrAdd(trade.StrategyTag, _ => new ExponentialWeightedStat());
            stat.Update(trade.PnlPercent > 0 ? 1f : 0f);
        }

        if (_strategyStats.Count < 1 || recentHistory.Count < MinHistoryForAdaptation)
            return MetaAdaptation.Default;

        var weights = _strategyStats
            .ToDictionary(
                kvp => kvp.Key,
                kvp => BaseWeight + kvp.Value.EwmaWinRate - 0.5f);

        await SaveStateAsync(ct);

        return new MetaAdaptation
        {
            StrategyWeights = weights,
            ConfidenceScalar = 1.0f
        };
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private async Task EnsureStateLoadedAsync(CancellationToken ct)
    {
        if (_stateLoaded) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_stateLoaded) return;
            await LoadStateAsync(ct);
            _stateLoaded = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task LoadStateAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var record = await db.MetaLearningStates.FindAsync(new object[] { AgentId }, ct);
            if (record == null) return;

            var saved = JsonSerializer.Deserialize<Dictionary<string, float>>(record.StateJson);
            if (saved == null) return;

            foreach (var (tag, ewma) in saved)
                _strategyStats[tag] = new ExponentialWeightedStat(ewma);

            _logger.LogInformation("[{AgentId}] Loaded {Count} strategy EWMA weights from DB (updated {At:u})",
                AgentId, saved.Count, record.UpdatedAt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{AgentId}] Could not load state from DB — starting fresh", AgentId);
        }
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = _strategyStats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.EwmaWinRate);
            var json = JsonSerializer.Serialize(snapshot);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var record = await db.MetaLearningStates.FindAsync(new object[] { AgentId }, ct);
            if (record == null)
            {
                db.MetaLearningStates.Add(new MetaLearningStateRecord
                {
                    AgentId = AgentId,
                    StateJson = json,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                record.StateJson = json;
                record.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{AgentId}] Could not save state to DB — in-memory state intact", AgentId);
        }
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    private sealed class ExponentialWeightedStat
    {
        private readonly object _lock = new();
        public float EwmaWinRate { get; private set; }

        public ExponentialWeightedStat(float initialEwma = 0.5f) => EwmaWinRate = initialEwma;

        public void Update(float outcome)
        {
            lock (_lock)
            {
                EwmaWinRate = (1 - DecayAlpha) * EwmaWinRate + DecayAlpha * outcome;
            }
        }
    }
}
