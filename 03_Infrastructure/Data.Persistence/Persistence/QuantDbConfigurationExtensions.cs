using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ThatsMyQuant.Infrastructure.Persistence;

public static class QuantDbConfigurationExtensions
{
    public static DbContextOptionsBuilder UseConfiguredQuantDatabase(
        this DbContextOptionsBuilder optionsBuilder,
        IConfiguration configuration,
        int? poolSize = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(configuration);

        var provider = ResolveProvider(configuration);
        return provider switch
        {
            "postgres" or "postgresql" or "npgsql" => ConfigurePostgres(optionsBuilder, configuration),
            _ => ConfigureSqlite(optionsBuilder, configuration)
        };
    }

    public static string ResolveProvider(IConfiguration configuration)
    {
        var configuredProvider = configuration["Persistence:Provider"];
        if (!string.IsNullOrWhiteSpace(configuredProvider))
        {
            return configuredProvider.Trim().ToLowerInvariant();
        }

        var postgresConnection = configuration.GetConnectionString("QuantPostgres")
                                 ?? configuration.GetConnectionString("QuantPostgreSql");
        if (!string.IsNullOrWhiteSpace(postgresConnection))
        {
            return "postgres";
        }

        return "sqlite";
    }

    private static DbContextOptionsBuilder ConfigureSqlite(
        DbContextOptionsBuilder optionsBuilder,
        IConfiguration configuration)
    {
        var sqliteConnection = configuration.GetConnectionString("Quant")
                               ?? "Data Source=quant.db;Cache=Shared";
        return optionsBuilder.UseSqlite(sqliteConnection);
    }

    private static DbContextOptionsBuilder ConfigurePostgres(
        DbContextOptionsBuilder optionsBuilder,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QuantPostgres")
                               ?? configuration.GetConnectionString("QuantPostgreSql");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Persistence:Provider is set to PostgreSQL, but no QuantPostgres connection string is configured.");
        }

        var extensionType = Type.GetType(
            "Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilderExtensions, Npgsql.EntityFrameworkCore.PostgreSQL");

        if (extensionType is null)
        {
            throw new InvalidOperationException(
                "PostgreSQL support requires the Npgsql.EntityFrameworkCore.PostgreSQL package. Add the package before switching Persistence:Provider to PostgreSQL.");
        }

        var method = extensionType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m =>
            {
                if (!string.Equals(m.Name, "UseNpgsql", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = m.GetParameters();
                return parameters.Length >= 2 &&
                       parameters[0].ParameterType == typeof(DbContextOptionsBuilder) &&
                       parameters[1].ParameterType == typeof(string);
            });

        if (method is null)
        {
            throw new InvalidOperationException("Found Npgsql provider assembly, but could not locate UseNpgsql.");
        }

        var optionalArgs = method.GetParameters().Length switch
        {
            2 => new object?[] { optionsBuilder, connectionString },
            3 => new object?[] { optionsBuilder, connectionString, null },
            _ => throw new InvalidOperationException("Unsupported UseNpgsql signature encountered.")
        };

        return (DbContextOptionsBuilder)(method.Invoke(null, optionalArgs) ?? optionsBuilder);
    }
}
