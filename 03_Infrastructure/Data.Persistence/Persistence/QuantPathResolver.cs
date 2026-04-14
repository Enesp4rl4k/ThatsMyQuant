using Microsoft.Data.Sqlite;

namespace ThatsMyQuant.Infrastructure.Persistence;

public static class QuantPathResolver
{
    public static string ResolveSqliteConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return BuildDefaultConnectionString();
        }

        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            builder.DataSource = ResolveDefaultDatabasePath();
            return builder.ToString();
        }

        if (!Path.IsPathRooted(builder.DataSource) &&
            !string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            builder.DataSource = Path.Combine(ResolveDataDirectory(), builder.DataSource);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(builder.DataSource)!);
        return builder.ToString();
    }

    public static string BuildDefaultConnectionString()
        => new SqliteConnectionStringBuilder
        {
            DataSource = ResolveDefaultDatabasePath(),
            Cache = SqliteCacheMode.Shared
        }.ToString();

    public static string ResolveDefaultDatabasePath()
        => Path.Combine(ResolveDataDirectory(), "quant.db");

    public static string ResolveDataDirectory()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant");
        Directory.CreateDirectory(root);
        return root;
    }
}
