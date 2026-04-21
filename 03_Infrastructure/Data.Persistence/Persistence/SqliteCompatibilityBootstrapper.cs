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
        ["ProfitLeakReducedSizeNetExpectancy"] = "REAL NOT NULL DEFAULT 0"
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
        ["ClaimedBy"] = "TEXT NULL"
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
        if (compatibility.IsCompatible)
        {
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
            return;
        }

        logger.LogWarning(
            "Detected SQLite schema drift at {DatabasePath}. Applying additive compatibility upgrades. Missing trade columns: {MissingTradeColumns}. Missing signal columns: {MissingSignalColumns}.",
            databasePath,
            compatibility.MissingTradeColumns.Count == 0 ? "<none>" : string.Join(", ", compatibility.MissingTradeColumns),
            compatibility.MissingSignalColumns.Count == 0 ? "<none>" : string.Join(", ", compatibility.MissingSignalColumns));

        await ApplyMissingColumnsAsync(connectionString, compatibility, logger, cancellationToken);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
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
