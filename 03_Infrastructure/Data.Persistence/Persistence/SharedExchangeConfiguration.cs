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
        bool isDemoMode = false;
        if (File.Exists(settingsPath))
        {
            builder.AddJsonFile(settingsPath, optional: true, reloadOnChange: true);
            builder.AddInMemoryCollection(LoadExchangeModeMapping(settingsPath));
            isDemoMode = TryReadIsDemoMode(settingsPath);
        }

        var credentialsPath = Path.Combine(root, "exchange_credentials.dpapi");
        var credentials = LoadCredentials(credentialsPath, isDemoMode);
        if (credentials.Count > 0)
        {
            builder.AddInMemoryCollection(credentials);
        }

        // 2026-04-24 FIX: env var must WIN over the JSON-loaded mode. Launcher passes
        // TMQ_EXCHANGE_MODE=Mock for paper trading to skip credential check; without
        // this re-add, exchange_settings.json (which may be OKX for live use) overrides
        // the launcher intent. Re-add env vars LAST so they take precedence.
        builder.AddEnvironmentVariables();
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

    private static bool TryReadIsDemoMode(string settingsPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty("OKX", out var okx) &&
                okx.TryGetProperty("IsDemoMode", out var flag))
            {
                return flag.ValueKind == JsonValueKind.True;
            }
        }
        catch { /* silent — defaults to false */ }
        return false;
    }

    private static Dictionary<string, string?> LoadCredentials(string credentialsPath, bool isDemoMode)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(credentialsPath))
        {
            Console.Error.WriteLine($"[ExchangeConfig] exchange_credentials.dpapi not found at: {credentialsPath}");
            return values;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("[ExchangeConfig] DPAPI decryption only supported on Windows — credentials skipped.");
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
            // Demo slots are stored alongside live. Active set is picked based on
            // IsDemoMode from exchange_settings.json — ensures worker always reads
            // the correct environment credentials without manual juggling.
            var okxDemoApiKey = TryUnprotect(root, "DemoApiKey");
            var okxDemoSecretKey = TryUnprotect(root, "DemoSecretKey");
            var okxDemoPassphrase = TryUnprotect(root, "DemoPassphrase");

            if (!string.IsNullOrWhiteSpace(binanceApiKey))
            {
                values["TMQ_BINANCE_API_KEY"] = binanceApiKey;
            }

            if (!string.IsNullOrWhiteSpace(binanceApiSecret))
            {
                values["TMQ_BINANCE_API_SECRET"] = binanceApiSecret;
            }

            // Select the active OKX credential set based on IsDemoMode. Fall back
            // to the opposite set if the active one is blank so operators still
            // see a clear "AUTH FAILED" rather than "NO CREDENTIALS" when they
            // flipped the toggle without filling the new slot.
            var activeKey = isDemoMode
                ? (string.IsNullOrWhiteSpace(okxDemoApiKey) ? okxApiKey : okxDemoApiKey)
                : (string.IsNullOrWhiteSpace(okxApiKey) ? okxDemoApiKey : okxApiKey);
            var activeSecret = isDemoMode
                ? (string.IsNullOrWhiteSpace(okxDemoSecretKey) ? okxSecretKey : okxDemoSecretKey)
                : (string.IsNullOrWhiteSpace(okxSecretKey) ? okxDemoSecretKey : okxSecretKey);
            var activePass = isDemoMode
                ? (string.IsNullOrWhiteSpace(okxDemoPassphrase) ? okxPassphrase : okxDemoPassphrase)
                : (string.IsNullOrWhiteSpace(okxPassphrase) ? okxDemoPassphrase : okxPassphrase);

            if (!string.IsNullOrWhiteSpace(activeKey)) values["OKX:ApiKey"] = activeKey;
            if (!string.IsNullOrWhiteSpace(activeSecret)) values["OKX:SecretKey"] = activeSecret;
            if (!string.IsNullOrWhiteSpace(activePass)) values["OKX:Passphrase"] = activePass;

            Console.Error.WriteLine(
                $"[ExchangeConfig] OKX credential slot selected: {(isDemoMode ? "DEMO" : "LIVE")} " +
                $"(live set configured={!string.IsNullOrWhiteSpace(okxApiKey)}, demo set configured={!string.IsNullOrWhiteSpace(okxDemoApiKey)})");

            int loaded = values.Count;
            if (loaded == 0)
            {
                Console.Error.WriteLine(
                    "[ExchangeConfig] Credentials file parsed but NO keys decrypted. " +
                    "Likely cause: DPAPI blob was encrypted by a DIFFERENT Windows user account. " +
                    "Re-encrypt credentials on this user, OR set TMQ_EXCHANGE_MODE=Mock for paper trading.");
            }
            else
            {
                Console.Error.WriteLine($"[ExchangeConfig] Loaded {loaded} credential keys from DPAPI.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ExchangeConfig] Credential decrypt threw: {ex.GetType().Name}: {ex.Message}");
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
        catch (Exception ex)
        {
            // Surface the underlying failure so DPAPI / encoding bugs aren't masked
            // as "credentials not configured". Common causes: DPAPI blob written by
            // a different Windows user, machine-key changes, malformed Base64.
            Console.Error.WriteLine(
                $"[ExchangeConfig] TryUnprotect('{propertyName}') threw {ex.GetType().Name}: {ex.Message}");
            return string.Empty;
        }
    }
}
