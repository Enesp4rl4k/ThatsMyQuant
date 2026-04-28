using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Data.Common;

namespace ThatsMyQuant.Infrastructure.Persistence;

public static class SqliteCompatibilityBootstrapper
{
    private static readonly IReadOnlyDictionary<string, string> RequiredTradeColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // --- Pre-existing (earlier schema drifts) ---
        ["AverageExitPrice"] = "REAL NOT NULL DEFAULT 0",
        ["RequestedQuantity"] = "REAL NOT NULL DEFAULT 0",
        ["FilledQuantity"] = "REAL NOT NULL DEFAULT 0",
        ["RemainingQuantity"] = "REAL NOT NULL DEFAULT 0",
        ["LifecycleStatus"] = "TEXT NOT NULL DEFAULT 'Pending'",
        ["MlModelVersion"] = "TEXT NOT NULL DEFAULT ''",
        ["RegimeTag"] = "TEXT NOT NULL DEFAULT ''",
        ["StrategyTag"] = "TEXT NOT NULL DEFAULT ''",
        ["ExitPolicyTag"] = "TEXT NOT NULL DEFAULT ''",
        ["EdgeScore"] = "REAL NOT NULL DEFAULT 0",
        ["ExecutionLatencyMs"] = "INTEGER NOT NULL DEFAULT 0",
        ["EntryTime"] = "TEXT NOT NULL DEFAULT '0001-01-01 00:00:00'",
        ["ProfitLeakAuditSummary"] = "TEXT NOT NULL DEFAULT ''",
        ["ProfitLeakWorstBucketBeforeLiveChange"] = "TEXT NOT NULL DEFAULT ''",
        ["ProfitLeakWorstBucketAfterLiveChange"] = "TEXT NOT NULL DEFAULT ''",
        ["ProfitLeakExactNetExpectancy"] = "REAL NOT NULL DEFAULT 0",
        ["ProfitLeakExitPolicyNetExpectancy"] = "REAL NOT NULL DEFAULT 0",
        ["ProfitLeakRegimeNetExpectancy"] = "REAL NOT NULL DEFAULT 0",
        ["ProfitLeakVenueNetExpectancy"] = "REAL NOT NULL DEFAULT 0",
        ["ProfitLeakReducedSizeNetExpectancy"] = "REAL NOT NULL DEFAULT 0",
        // --- 2026-04-24 additions (full Trade model catch-up) ---
        // Bucket / edge metadata
        ["EdgeBand"] = "TEXT NOT NULL DEFAULT ''",
        ["ExactBucketImpactLabel"] = "TEXT NOT NULL DEFAULT ''",
        ["ExactBucketSummary"] = "TEXT NOT NULL DEFAULT ''",
        ["ProfitLeakWorstBucketLabel"] = "TEXT NOT NULL DEFAULT ''",
        ["ProfitLeakAuditSizeMultiplier"] = "REAL NOT NULL DEFAULT 0",
        ["ProfitLeakReducedSizeNoise"] = "INTEGER NOT NULL DEFAULT 0",
        ["ProfitLeakLoserClusterRate"] = "REAL NOT NULL DEFAULT 0",
        ["ProfitLeakDominantLosingExitPolicy"] = "TEXT NOT NULL DEFAULT ''",
        // Allocator / venue
        ["FinalAllocatorBias"] = "REAL NOT NULL DEFAULT 0",
        ["FinalAllocatorScope"] = "TEXT NOT NULL DEFAULT ''",
        ["VenueExecutionBias"] = "REAL NOT NULL DEFAULT 0",
        ["VenueExecutionScope"] = "TEXT NOT NULL DEFAULT ''",
        ["VenueImpactLabel"] = "TEXT NOT NULL DEFAULT ''",
        ["ConcordanceImpactLabel"] = "TEXT NOT NULL DEFAULT ''",
        ["ConcordanceSummary"] = "TEXT NOT NULL DEFAULT ''",
        // Feature drift
        ["FeatureDriftDetected"] = "INTEGER NOT NULL DEFAULT 0",
        ["FeatureDriftCritical"] = "INTEGER NOT NULL DEFAULT 0",
        ["DriftedFeatureCount"] = "INTEGER NOT NULL DEFAULT 0",
        ["FeatureDriftMaxZScore"] = "REAL NOT NULL DEFAULT 0",
        ["FeatureDriftImpactLabel"] = "TEXT NOT NULL DEFAULT ''",
        ["FeatureDriftSummary"] = "TEXT NOT NULL DEFAULT ''",
        // Execution / leverage / lifecycle
        ["ExecutionMode"] = "TEXT NOT NULL DEFAULT 'REAL'",
        ["PromotionStage"] = "TEXT NOT NULL DEFAULT 'Backtest'",
        ["Leverage"] = "REAL NOT NULL DEFAULT 1",
        ["MarginMode"] = "INTEGER NOT NULL DEFAULT 0",
        ["ClosedQuantity"] = "REAL NOT NULL DEFAULT 0",
        // Risk / exit levels
        ["StopLoss"] = "REAL NOT NULL DEFAULT 0",
        ["TakeProfit"] = "REAL NOT NULL DEFAULT 0",
        ["PartialTakeProfit"] = "REAL NOT NULL DEFAULT 0",
        ["PartialExitFraction"] = "REAL NOT NULL DEFAULT 0",
        ["BreakevenTriggerFraction"] = "REAL NOT NULL DEFAULT 0.5",
        ["IsBreakevenArmed"] = "INTEGER NOT NULL DEFAULT 0",
        ["HasTakenPartialProfit"] = "INTEGER NOT NULL DEFAULT 0",
        ["TrailingStopPrice"] = "REAL NOT NULL DEFAULT 0",
        ["OptimalPriceSinceEntry"] = "REAL NOT NULL DEFAULT 0",
        ["WorstPriceSinceEntry"] = "REAL NOT NULL DEFAULT 0",
        ["MaxFavorableExcursionPercent"] = "REAL NOT NULL DEFAULT 0",
        ["MaxAdverseExcursionPercent"] = "REAL NOT NULL DEFAULT 0",
        ["MarkPrice"] = "REAL NOT NULL DEFAULT 0",
        ["BarsHeld"] = "INTEGER NOT NULL DEFAULT 0",
        ["MaxHoldingBars"] = "INTEGER NOT NULL DEFAULT 0",
        ["ExitReason"] = "TEXT NOT NULL DEFAULT ''",
        ["EntrySlippagePercent"] = "REAL NOT NULL DEFAULT 0",
        ["PositionSizePercent"] = "REAL NOT NULL DEFAULT 0",
        ["TotalFeesPaid"] = "REAL NOT NULL DEFAULT 0",
        ["NetFunding"] = "REAL NOT NULL DEFAULT 0",
        // Paper/live drift tracking
        ["SignalPrice"] = "REAL NOT NULL DEFAULT 0",
        ["BacktestExpectedPnL"] = "REAL NOT NULL DEFAULT 0",
        ["ExecutionSource"] = "TEXT NOT NULL DEFAULT 'BACKTEST'",
        ["SubmittedTime"] = "TEXT NULL",
        ["LastUpdatedTime"] = "TEXT NULL"
    };

    private static readonly IReadOnlyDictionary<string, string> RequiredSignalColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Exchange"] = "TEXT NOT NULL DEFAULT ''",
        ["MarketType"] = "TEXT NOT NULL DEFAULT 'Unknown'",
        ["Quantity"] = "REAL NOT NULL DEFAULT 0",
        ["Confidence"] = "INTEGER NOT NULL DEFAULT 0",
        ["Reasoning"] = "TEXT NOT NULL DEFAULT ''",
        ["ExecutionAction"] = "TEXT NOT NULL DEFAULT 'None'",
        ["PriceActionSignal"] = "TEXT NOT NULL DEFAULT 'None'",
        ["RegimeTag"] = "TEXT NOT NULL DEFAULT ''",
        ["StrategyTag"] = "TEXT NOT NULL DEFAULT ''",
        ["Volume24h"] = "REAL NOT NULL DEFAULT 0",
        ["ReasonFlags"] = "INTEGER NOT NULL DEFAULT 0",
        ["ClaimedAtUtc"] = "TEXT NULL",
        ["ClaimedBy"] = "TEXT NULL",
        ["OrderType"] = "TEXT NOT NULL DEFAULT 'Limit'"
    };

    public static async Task EnsureCompatibleSchemaAsync(
        QuantDbContext dbContext,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(QuantDbConfigurationExtensions.ResolveProvider(configuration), "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
            return;
        }

        var dbConnection = dbContext.Database.GetDbConnection();
        var connectionString = string.IsNullOrWhiteSpace(dbConnection.ConnectionString)
            ? configuration.GetConnectionString("Quant") ?? "Data Source=quant.db;Cache=Shared"
            : dbConnection.ConnectionString;
        var sqliteBuilder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = sqliteBuilder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
            return;
        }

        var databasePath = Path.IsPathRooted(dataSource)
            ? dataSource
            : Path.GetFullPath(dataSource);

        logger.LogInformation("Checking SQLite compatibility for database at {DatabasePath}.", databasePath);

        if (!File.Exists(databasePath))
        {
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
            return;
        }

        var compatibility = await ReadCompatibilityAsync(connectionString, cancellationToken);
        if (!compatibility.IsCompatible)
        {
            logger.LogWarning(
                "Detected SQLite schema drift at {DatabasePath}. Applying additive compatibility upgrades. Missing trade columns: {MissingTradeColumns}. Missing signal columns: {MissingSignalColumns}.",
                databasePath,
                compatibility.MissingTradeColumns.Count == 0 ? "<none>" : string.Join(", ", compatibility.MissingTradeColumns),
                compatibility.MissingSignalColumns.Count == 0 ? "<none>" : string.Join(", ", compatibility.MissingSignalColumns));

            await ApplyMissingColumnsAsync(connectionString, compatibility, logger, cancellationToken);
        }

        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        // Cross-process tables: EF EnsureCreatedAsync does NOT add missing tables to
        // existing databases — provision them explicitly here. Idempotent (CREATE IF NOT EXISTS).
        await EnsureOperatorAlertEventsTableAsync(connectionString, logger, cancellationToken);
        await EnsureServiceHeartbeatsTableAsync(connectionString, logger, cancellationToken);
        await EnsureManualCloseRequestsTableAsync(connectionString, logger, cancellationToken);
    }

    /// <summary>
    /// Cross-process queue for "close this open paper position" requests originating from the
    /// web Cockpit. Listener.Api INSERTs Pending rows; QuantEngine.Worker polls every few
    /// seconds, dispatches to PaperTradingService.CloseSinglePositionAsync, and updates Status.
    /// Identical pattern to OperatorAlertEvents but reversed direction (UI → Worker).
    /// </summary>
    private static async Task EnsureManualCloseRequestsTableAsync(
        string connectionString,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS "ManualCloseRequests" (
                "Id" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                "Symbol" TEXT NOT NULL,
                "Side" TEXT NULL,
                "Reason" TEXT NOT NULL DEFAULT '',
                "Status" TEXT NOT NULL DEFAULT 'Pending',
                "RequestedUtc" TEXT NOT NULL,
                "CompletedUtc" TEXT NULL,
                "Result" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_ManualCloseRequests_Status"
                ON "ManualCloseRequests" ("Status");
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogDebug("ManualCloseRequests table ensured.");
    }

    private static async Task EnsureServiceHeartbeatsTableAsync(
        string connectionString,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS "ServiceHeartbeats" (
                "Id" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                "ServiceName" TEXT NOT NULL,
                "LastSeenUtc" TEXT NOT NULL,
                "StartedUtc" TEXT NOT NULL,
                "Version" TEXT NOT NULL DEFAULT '',
                "MachineName" TEXT NOT NULL DEFAULT '',
                "ProcessId" INTEGER NOT NULL DEFAULT 0,
                "Status" TEXT NOT NULL DEFAULT 'running',
                "MemoryBytes" INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ServiceHeartbeats_ServiceName"
                ON "ServiceHeartbeats" ("ServiceName");
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogDebug("ServiceHeartbeats table ensured.");
    }

    private static async Task EnsureOperatorAlertEventsTableAsync(
        string connectionString,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS "OperatorAlertEvents" (
                "Id" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                "UtcTime" TEXT NOT NULL,
                "Severity" TEXT NOT NULL,
                "Source" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Body" TEXT NOT NULL,
                "ProcessName" TEXT NOT NULL DEFAULT '',
                "CorrelationId" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_OperatorAlertEvents_UtcTime"
                ON "OperatorAlertEvents" ("UtcTime");
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogDebug("OperatorAlertEvents table ensured.");
    }

    private static async Task<SchemaCompatibility> ReadCompatibilityAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tradeColumns = await ReadColumnsAsync(connection, "Trades", cancellationToken);
        var signalColumns = await ReadColumnsAsync(connection, "Signals", cancellationToken);

        if (tradeColumns.Count == 0 || signalColumns.Count == 0)
        {
            return SchemaCompatibility.Compatible();
        }

        var missingTradeColumns = RequiredTradeColumns.Keys.Where(column => !tradeColumns.Contains(column)).ToArray();
        var missingSignalColumns = RequiredSignalColumns.Keys.Where(column => !signalColumns.Contains(column)).ToArray();

        return missingTradeColumns.Length == 0 && missingSignalColumns.Length == 0
            ? SchemaCompatibility.Compatible()
            : new SchemaCompatibility(false, missingTradeColumns, missingSignalColumns);
    }

    private static async Task ApplyMissingColumnsAsync(
        string connectionString,
        SchemaCompatibility compatibility,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var column in compatibility.MissingTradeColumns)
        {
            await AddColumnAsync(connection, transaction, "Trades", column, RequiredTradeColumns[column], logger, cancellationToken);
        }

        foreach (var column in compatibility.MissingSignalColumns)
        {
            await AddColumnAsync(connection, transaction, "Signals", column, RequiredSignalColumns[column], logger, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task AddColumnAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string tableName,
        string columnName,
        string definition,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {definition};";
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Added missing SQLite column {Table}.{Column}.", tableName, columnName);
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{tableName}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private sealed record SchemaCompatibility(
        bool IsCompatible,
        IReadOnlyList<string> MissingTradeColumns,
        IReadOnlyList<string> MissingSignalColumns)
    {
        public static SchemaCompatibility Compatible() => new(true, Array.Empty<string>(), Array.Empty<string>());
    }
}
