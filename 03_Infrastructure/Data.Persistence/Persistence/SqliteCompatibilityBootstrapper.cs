using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Persistence;

public static class SqliteCompatibilityBootstrapper
{
    private static readonly string[] RequiredTradeColumns =
    [
        "AverageExitPrice",
        "RequestedQuantity",
        "FilledQuantity",
        "RemainingQuantity",
        "LifecycleStatus",
        "MlModelVersion",
        "RegimeTag",
        "StrategyTag",
        "ExitPolicyTag",
        "EdgeScore",
        "ExecutionLatencyMs",
        "EntryTime"
    ];

    private static readonly string[] RequiredSignalColumns =
    [
        "Exchange",
        "MarketType",
        "Quantity",
        "Confidence",
        "Reasoning",
        "ExecutionAction",
        "PriceActionSignal",
        "RegimeTag",
        "StrategyTag",
        "Volume24h",
        "ReasonFlags"
    ];

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

        var backupPath = $"{databasePath}.backup-{DateTime.UtcNow:yyyyMMddHHmmss}";
        logger.LogWarning(
            "Detected stale SQLite schema at {DatabasePath}. Missing trade columns: {MissingTradeColumns}. Missing signal columns: {MissingSignalColumns}. Backing it up to {BackupPath} and recreating the database.",
            databasePath,
            compatibility.MissingTradeColumns.Count == 0 ? "<none>" : string.Join(", ", compatibility.MissingTradeColumns),
            compatibility.MissingSignalColumns.Count == 0 ? "<none>" : string.Join(", ", compatibility.MissingSignalColumns),
            backupPath);

        dbContext.Database.CloseConnection();
        SqliteConnection.ClearAllPools();

        File.Copy(databasePath, backupPath, overwrite: false);
        DeleteSqliteFiles(databasePath, logger);
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

        var missingTradeColumns = RequiredTradeColumns.Where(column => !tradeColumns.Contains(column)).ToArray();
        var missingSignalColumns = RequiredSignalColumns.Where(column => !signalColumns.Contains(column)).ToArray();

        return missingTradeColumns.Length == 0 && missingSignalColumns.Length == 0
            ? SchemaCompatibility.Compatible()
            : new SchemaCompatibility(false, missingTradeColumns, missingSignalColumns);
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

    private static void DeleteSqliteFiles(string databasePath, ILogger logger)
    {
        foreach (var candidate in EnumerateSqliteFiles(databasePath))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            File.Delete(candidate);
            logger.LogInformation("Deleted stale SQLite file {Path}.", candidate);
        }
    }

    private static IEnumerable<string> EnumerateSqliteFiles(string databasePath)
    {
        yield return databasePath;
        yield return $"{databasePath}-wal";
        yield return $"{databasePath}-shm";
    }

    private sealed record SchemaCompatibility(
        bool IsCompatible,
        IReadOnlyList<string> MissingTradeColumns,
        IReadOnlyList<string> MissingSignalColumns)
    {
        public static SchemaCompatibility Compatible() => new(true, Array.Empty<string>(), Array.Empty<string>());
    }
}
