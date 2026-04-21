using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Infrastructure.Services;

namespace ThatsMyQuant.Infrastructure.Repositories;

public class SignalRepository : ISignalRepository
{
    private static readonly Func<QuantDbContext, int, IAsyncEnumerable<Signal>> s_getRecentQuery =
        EF.CompileAsyncQuery((QuantDbContext db, int count) =>
            db.Signals
                .AsNoTracking()
                .OrderByDescending(s => s.Timestamp)
                .Take(count));

    private static readonly Func<QuantDbContext, TradeStatus, int, IAsyncEnumerable<Signal>> s_getByStatusQuery =
        EF.CompileAsyncQuery((QuantDbContext db, TradeStatus status, int count) =>
            db.Signals
                .AsNoTracking()
                .Where(s => s.Status == status)
                .OrderBy(s => s.Timestamp)
                .Take(count));

    private static readonly Func<QuantDbContext, DateTime, int, IAsyncEnumerable<Signal>> s_getExecutionClaimCandidatesQuery =
        EF.CompileAsyncQuery((QuantDbContext db, DateTime staleClaimBeforeUtc, int count) =>
            db.Signals
                .AsNoTracking()
                .Where(s => s.Status == TradeStatus.Queued ||
                            (s.Status == TradeStatus.Processing &&
                             s.ClaimedAtUtc != null &&
                             s.ClaimedAtUtc <= staleClaimBeforeUtc))
                .OrderBy(s => s.Timestamp)
                .Take(count));

    private static readonly Func<QuantDbContext, IAsyncEnumerable<SignalStatusCount>> s_getStatusCountsQuery =
        EF.CompileAsyncQuery((QuantDbContext db) =>
            db.Signals
                .AsNoTracking()
                .GroupBy(s => s.Status)
                .Select(g => new SignalStatusCount(g.Key, g.Count())));

    private readonly QuantDbContext _db;
    private readonly SignalWriterChannel? _signalWriterChannel;
    private readonly ExecutionSignalQueue? _executionSignalQueue;

    public SignalRepository(
        QuantDbContext db,
        SignalWriterChannel? signalWriterChannel = null,
        ExecutionSignalQueue? executionSignalQueue = null)
    {
        _db = db;
        _signalWriterChannel = signalWriterChannel;
        _executionSignalQueue = executionSignalQueue;
    }

    public async Task<Signal?> GetByIdAsync(Guid id)
        => await _db.Signals.FindAsync(id);

    public async Task<IReadOnlyList<Signal>> GetRecentAsync(int count = 50)
        => await MaterializeAsync(s_getRecentQuery(_db, count), count);

    public async Task<IReadOnlyList<Signal>> GetByStatusAsync(TradeStatus status, int count = 50)
        => await MaterializeAsync(s_getByStatusQuery(_db, status, count), count);

    public async Task<IReadOnlyList<Signal>> GetExecutionClaimCandidatesAsync(int count, DateTime staleClaimBeforeUtc)
        => await MaterializeAsync(s_getExecutionClaimCandidatesQuery(_db, staleClaimBeforeUtc, count), count);

    public async Task<bool> TryClaimQueuedAsync(Guid signalId, string workerId, DateTime claimedAtUtc, DateTime staleClaimBeforeUtc)
    {
        await _db.Database.OpenConnectionAsync();
        await using var command = (SqliteCommand)_db.Database.GetDbConnection().CreateCommand();
        if (_db.Database.CurrentTransaction is not null)
            command.Transaction = (SqliteTransaction?)_db.Database.CurrentTransaction.GetDbTransaction();

        command.CommandText = """
            UPDATE Signals
            SET Status = $processingStatus,
                ClaimedAtUtc = $claimedAtUtc,
                ClaimedBy = $claimedBy
            WHERE Id = $id
              AND (
                    Status = $queuedStatus
                    OR (
                        Status = $processingStatus
                        AND ClaimedAtUtc IS NOT NULL
                        AND ClaimedAtUtc <= $staleClaimBeforeUtc
                    )
                  );
            """;

        AddParameter(command, "$processingStatus", TradeStatus.Processing.ToString());
        AddParameter(command, "$claimedAtUtc", claimedAtUtc);
        AddParameter(command, "$claimedBy", workerId);
        AddParameter(command, "$id", signalId);
        AddParameter(command, "$queuedStatus", TradeStatus.Queued.ToString());
        AddParameter(command, "$staleClaimBeforeUtc", staleClaimBeforeUtc);

        return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task AddAsync(Signal signal)
    {
        TryEnqueueForExecution(signal);

        if (_signalWriterChannel?.TryWrite(SignalPersistenceBatchWriter.Clone(signal)) == true)
        {
            return;
        }

        await SignalPersistenceBatchWriter.UpsertAsync(_db, signal);
    }

    public async Task UpdateAsync(Signal signal)
    {
        TryEnqueueForExecution(signal);

        if (_signalWriterChannel?.TryWrite(SignalPersistenceBatchWriter.Clone(signal)) == true)
        {
            return;
        }

        await SignalPersistenceBatchWriter.UpsertAsync(_db, signal);
    }

    public async Task<Dictionary<TradeStatus, int>> GetStatusCountsAsync()
    {
        var counts = new Dictionary<TradeStatus, int>();
        await foreach (var row in s_getStatusCountsQuery(_db))
        {
            counts[row.Status] = row.Count;
        }

        return counts;
    }

    private static async Task<IReadOnlyList<Signal>> MaterializeAsync(IAsyncEnumerable<Signal> query, int capacity)
    {
        var signals = new List<Signal>(Math.Max(capacity, 0));
        await foreach (var signal in query)
        {
            signals.Add(signal);
        }

        return signals;
    }

    private void TryEnqueueForExecution(Signal signal)
    {
        if (signal.Status != TradeStatus.Queued)
        {
            return;
        }

        _executionSignalQueue?.TryEnqueue(SignalPersistenceBatchWriter.Clone(signal));
    }

    private readonly record struct SignalStatusCount(TradeStatus Status, int Count);

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
