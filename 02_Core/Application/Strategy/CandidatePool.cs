using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Process-wide registry of EdgeLab-discovered candidate strategies running in parallel
/// paper validation. Tier B+ foundation from UNIFIED_MASTER_PLAN_v2 (2026-05-01).
///
/// Discipline: each candidate has a pre-registered hypothesis test BEFORE entering the
/// pool — the markdown lives in `docs/PRE_REGISTERED_<id>.md` and is committed to git
/// with the candidate's <see cref="CandidateRecord.PreRegHash"/>. Mid-test config tweaks
/// invalidate the test, so the pool stores the IMMUTABLE config snapshot at entry time.
///
/// Capacity: <see cref="MaxParallelCandidates"/> hard cap (default 5). When full, new
/// candidates are queued — operator must explicitly retire one to free a slot. This
/// prevents Edge Lab from hosing the system with 50 candidates that compete for $5K
/// basis (each ends up with $25/trade, statistical power destroyed).
///
/// Capital: each candidate gets <see cref="CapitalFractionPerCandidate"/> of basis
/// (default 0.5%) until promoted. Total at-risk = N × 0.5% capped at 10% basis. Funding
/// + pairs (PROVEN tiers) get separate 4% allocations on top.
///
/// Lifecycle: Discovered → Queued → Active (paper running) → either Promoted (canary
/// live) or Retired (markdown gets the FINAL section appended).
/// </summary>
public sealed class CandidatePool
{
    /// <summary>Max simultaneously-running candidates. Hard cap; new candidates queue.</summary>
    public const int MaxParallelCandidates = 5;

    /// <summary>Per-candidate basis fraction during paper validation.
    /// 0.5% × 5 candidates = 2.5% total at-risk on top of proven tiers.</summary>
    public const decimal CapitalFractionPerCandidate = 0.005m;

    private readonly ConcurrentDictionary<string, CandidateRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lifecycleLock = new();
    private readonly string _persistencePath;

    public CandidatePool(string? persistencePath = null)
    {
        _persistencePath = persistencePath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThatsMyQuant",
                "candidate_pool.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_persistencePath)!);
        LoadFromDisk();
    }

    /// <summary>Add a discovered candidate. Returns false if the pool is full or the
    /// candidate id already exists.</summary>
    public bool TryAdd(CandidateRecord record)
    {
        lock (_lifecycleLock)
        {
            var activeCount = _records.Values.Count(r => r.Status == CandidateStatus.Active);
            if (activeCount >= MaxParallelCandidates && record.Status == CandidateStatus.Active)
            {
                // Force-queue if pool is full
                record = record with { Status = CandidateStatus.Queued };
            }
            if (!_records.TryAdd(record.Id, record)) return false;
            PersistToDisk();
            return true;
        }
    }

    /// <summary>Update lifecycle state (e.g. Queued → Active when a slot frees up).</summary>
    public bool UpdateStatus(string id, CandidateStatus newStatus, string? note = null)
    {
        lock (_lifecycleLock)
        {
            if (!_records.TryGetValue(id, out var existing)) return false;
            var updated = existing with
            {
                Status = newStatus,
                LastUpdatedUtc = DateTime.UtcNow,
                Note = note ?? existing.Note
            };
            _records[id] = updated;
            // Promote a queued candidate if a slot freed up.
            if (newStatus is CandidateStatus.Promoted or CandidateStatus.Retired)
            {
                var queued = _records.Values.FirstOrDefault(r => r.Status == CandidateStatus.Queued);
                if (queued is not null)
                {
                    var activated = queued with { Status = CandidateStatus.Active, LastUpdatedUtc = DateTime.UtcNow };
                    _records[queued.Id] = activated;
                }
            }
            PersistToDisk();
            return true;
        }
    }

    public CandidateRecord? Get(string id) => _records.TryGetValue(id, out var r) ? r : null;

    public IReadOnlyList<CandidateRecord> ListActive()
        => _records.Values.Where(r => r.Status == CandidateStatus.Active).ToList();

    public IReadOnlyList<CandidateRecord> ListAll() => _records.Values.ToList();

    public PoolSnapshot Snapshot()
    {
        var all = _records.Values.ToList();
        return new PoolSnapshot(
            Active: all.Count(r => r.Status == CandidateStatus.Active),
            Queued: all.Count(r => r.Status == CandidateStatus.Queued),
            Promoted: all.Count(r => r.Status == CandidateStatus.Promoted),
            Retired: all.Count(r => r.Status == CandidateStatus.Retired),
            Total: all.Count,
            MaxParallel: MaxParallelCandidates,
            CapitalFraction: CapitalFractionPerCandidate);
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_persistencePath)) return;
            var json = File.ReadAllText(_persistencePath);
            var loaded = JsonSerializer.Deserialize<List<CandidateRecord>>(json);
            if (loaded is null) return;
            foreach (var r in loaded) _records[r.Id] = r;
        }
        catch { /* swallow corrupted state file — pool starts empty */ }
    }

    private void PersistToDisk()
    {
        try
        {
            var json = JsonSerializer.Serialize(_records.Values.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_persistencePath, json);
        }
        catch { /* persistence is best-effort */ }
    }
}

/// <summary>Immutable record of a candidate strategy at registration time. Anything mid-test
/// that changes any of these fields (other than Status / Note / LastUpdatedUtc) invalidates
/// the pre-registered test — that's the whole point of the immutability discipline.</summary>
public sealed record CandidateRecord(
    string Id,
    string StrategyTag,                 // PaperPosition.StrategyTag for attribution
    string TemplateName,                // Which strategy template (FundingThreshold, PairsZScore, ...)
    string ConfigJson,                  // Immutable JSON config snapshot (parameters at entry)
    string PreRegHash,                  // Git commit hash of docs/PRE_REGISTERED_<id>.md
    int FamilySize,                     // Bonferroni m (universe size at registration)
    int TargetN,                        // Stopping-rule sample size
    int NumTrials,                      // Total EdgeLab search-space size for selection-bias correction
    DateTime RegisteredUtc,
    DateTime LastUpdatedUtc,
    CandidateStatus Status,
    string Note);

public enum CandidateStatus
{
    Discovered,   // Just-found, not yet entered paper
    Queued,       // Pool full, awaiting slot
    Active,       // Running paper
    Promoted,     // Passed Bonferroni — canary live
    Retired       // Failed test or operationally demoted
}

public sealed record PoolSnapshot(
    int Active, int Queued, int Promoted, int Retired, int Total,
    int MaxParallel, decimal CapitalFraction);
