using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Polls the cross-process <c>ManualCloseRequests</c> table every 3s for Pending rows,
/// dispatches each to <see cref="PaperTradingService.CloseSinglePositionAsync"/>, and
/// updates the row's Status / Result / CompletedUtc.
///
/// Mirror of <c>OperatorAlertDbReaderWorker</c> in the Listener but for the inverse
/// direction (UI → Worker). Trusts the Worker process owns the runtime PaperPortfolio.
/// </summary>
public sealed class ManualCloseRequestProcessor : BackgroundService
{
    private readonly ILogger<ManualCloseRequestProcessor> _logger;
    private readonly IServiceProvider _sp;
    private readonly PaperTradingService _paperService;

    public ManualCloseRequestProcessor(
        ILogger<ManualCloseRequestProcessor> logger,
        IServiceProvider sp,
        PaperTradingService paperService)
    {
        _logger       = logger;
        _sp           = sp;
        _paperService = paperService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ManualCloseRequestProcessor starting (3s poll).");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            do
            {
                try
                {
                    await ProcessPendingAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ManualCloseRequestProcessor tick failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task ProcessPendingAsync(CancellationToken ct)
    {
        // Fetch pending rows in a separate connection so we don't hold an EF transaction during
        // long-running close logic.
        List<(long Id, string Symbol, string? Side, string Reason)> rows;

        await using (var scope = _sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT "Id", "Symbol", "Side", "Reason"
                FROM "ManualCloseRequests"
                WHERE "Status" = 'Pending'
                ORDER BY "Id" ASC
                LIMIT 25;
                """;

            rows = new();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
            }
        }

        if (rows.Count == 0) return;

        foreach (var row in rows)
        {
            string status = "Done";
            string? result = null;
            try
            {
                // Sentinel symbol → flatten ALL positions (operator emergency button)
                if (string.Equals(row.Symbol, "__FLATTEN_ALL__", StringComparison.OrdinalIgnoreCase))
                {
                    var flatResult = await _paperService.FlattenAllPositionsAsync(row.Reason ?? "OperatorFlatAll")
                                                        .ConfigureAwait(false);
                    result = flatResult.Length > 800 ? flatResult[..800] : flatResult;
                    _logger.LogWarning("ManualCloseRequest #{Id} FLATTEN_ALL → {Status}", row.Id, status);
                    await UpdateStatusAsync(row.Id, status, result, ct).ConfigureAwait(false);
                    continue;
                }

                TradeSide? side = row.Side?.ToLowerInvariant() switch
                {
                    "buy"  => TradeSide.Buy,
                    "sell" => TradeSide.Sell,
                    _      => null,
                };
                var summary = await _paperService.CloseSinglePositionAsync(row.Symbol, side, row.Reason ?? "OperatorManual")
                                                 .ConfigureAwait(false);
                if (summary is null)
                {
                    status = "NotFound";
                    result = $"No open position matched symbol={row.Symbol} side={row.Side ?? "any"}";
                }
                else
                {
                    result = summary.Length > 800 ? summary[..800] : summary;
                }
                _logger.LogInformation("ManualCloseRequest #{Id} {Symbol} → {Status}", row.Id, row.Symbol, status);
            }
            catch (Exception ex)
            {
                status = "Failed";
                result = ex.Message.Length > 400 ? ex.Message[..400] : ex.Message;
                _logger.LogError(ex, "ManualCloseRequest #{Id} processing threw", row.Id);
            }

            await UpdateStatusAsync(row.Id, status, result, ct).ConfigureAwait(false);
        }
    }

    private async Task UpdateStatusAsync(long id, string status, string? result, CancellationToken ct)
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "ManualCloseRequests"
               SET "Status" = $status, "Result" = $result, "CompletedUtc" = $now
             WHERE "Id" = $id;
            """;
        var pStatus = cmd.CreateParameter(); pStatus.ParameterName = "$status"; pStatus.Value = status; cmd.Parameters.Add(pStatus);
        var pResult = cmd.CreateParameter(); pResult.ParameterName = "$result"; pResult.Value = (object?)result ?? DBNull.Value; cmd.Parameters.Add(pResult);
        var pNow    = cmd.CreateParameter(); pNow.ParameterName = "$now"; pNow.Value = DateTime.UtcNow.ToString("O"); cmd.Parameters.Add(pNow);
        var pId     = cmd.CreateParameter(); pId.ParameterName = "$id"; pId.Value = id; cmd.Parameters.Add(pId);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
