using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;

namespace ThatsMyQuant.Infrastructure.Persistence;

public static class SharedExchangeConfiguration
{
    public static IConfigurationBuilder ApplySharedExchangeConfiguration(this IConfigurationBuilder builder, string? baseDirectory = null)
    {
        Apply(builder, baseDirectory);
        return builder;
    }

    public static void Apply(IConfigurationBuilder builder, string? baseDirectory = null)
    {
        var root = ResolveSharedConfigRoot(baseDirectory ?? AppContext.BaseDirectory);
        var settingsPath = Path.Combine(root, "exchange_settings.json");
        if (File.Exists(settingsPath))
        {
            builder.AddJsonFile(settingsPath, optional: true, reloadOnChange: true);
            builder.AddInMemoryCollection(LoadExchangeModeMapping(settingsPath));
        }

        var credentialsPath = Path.Combine(root, "exchange_credentials.dpapi");
        var credentials = LoadCredentials(credentialsPath);
        if (credentials.Count > 0)
        {
            builder.AddInMemoryCollection(credentials);
        }
    }

    public static string GetSelectedExchangeMode(IConfiguration configuration)
    {
        var mode = configuration["TMQ_EXCHANGE_MODE"] ?? configuration["ExchangeMode"];
        return string.IsNullOrWhiteSpace(mode) ? "Mock" : mode.Trim();
    }

    public static string ResolveSharedConfigRoot(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            var hasBundleMarkers =
                Directory.Exists(Path.Combine(directory.FullName, "Dashboard")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Execution.Worker"));
            if (hasBundleMarkers || File.Exists(Path.Combine(directory.FullName, "ThatsMyQuant.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return baseDirectory;
    }

    private static Dictionary<string, string?> LoadExchangeModeMapping(string settingsPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty("ExchangeMode", out var exchangeMode))
            {
                var mode = exchangeMode.GetString();
                if (!string.IsNullOrWhiteSpace(mode))
                {
                    return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["TMQ_EXCHANGE_MODE"] = mode
                    };
                }
            }
        }
        catch
        {
        }

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string?> LoadCredentials(string credentialsPath)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(credentialsPath))
        {
            return values;
        }

        if (!OperatingSystem.IsWindows())
        {
            return values;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(credentialsPath));
            var root = document.RootElement;

            var binanceApiKey = TryUnprotect(root, "BinanceApiKey");
            var binanceApiSecret = TryUnprotect(root, "BinanceApiSecret");
            var okxApiKey = TryUnprotect(root, "ApiKey");
            var okxSecretKey = TryUnprotect(root, "SecretKey");
            var okxPassphrase = TryUnprotect(root, "Passphrase");

            if (!string.IsNullOrWhiteSpace(binanceApiKey))
            {
                values["TMQ_BINANCE_API_KEY"] = binanceApiKey;
            }

            if (!string.IsNullOrWhiteSpace(binanceApiSecret))
            {
                values["TMQ_BINANCE_API_SECRET"] = binanceApiSecret;
            }

            if (!string.IsNullOrWhiteSpace(okxApiKey))
            {
                values["OKX:ApiKey"] = okxApiKey;
            }

            if (!string.IsNullOrWhiteSpace(okxSecretKey))
            {
                values["OKX:SecretKey"] = okxSecretKey;
            }

            if (!string.IsNullOrWhiteSpace(okxPassphrase))
            {
                values["OKX:Passphrase"] = okxPassphrase;
            }
        }
        catch
        {
        }

        return values;
    }

    [SupportedOSPlatform("windows")]
    private static string TryUnprotect(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        var encoded = property.GetString();
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return string.Empty;
        }

        try
        {
            var encrypted = Convert.FromBase64String(encoded);
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }
        catch
        {
            return string.Empty;
        }
    }
}
