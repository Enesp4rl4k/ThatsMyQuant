using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThatsMyQuant.Infrastructure.Persistence;
#pragma warning disable CA1416 // Suppress platform warnings — runtime guard via OperatingSystem.IsWindows() before any DPAPI call

namespace Listener.Api.Services;

/// <summary>
/// Reads + writes <c>exchange_credentials.dpapi</c>. Uses Windows DPAPI
/// CurrentUser scope so the same account that ran the API is the only one
/// that can decrypt them later (matches existing
/// <see cref="SharedExchangeConfiguration"/> read path).
///
/// File schema (JSON, base64-encoded DPAPI blobs):
/// <code>
/// {
///   "ApiKey":          "...",  // OKX live
///   "SecretKey":       "...",
///   "Passphrase":      "...",
///   "DemoApiKey":      "...",  // OKX demo
///   "DemoSecretKey":   "...",
///   "DemoPassphrase":  "...",
///   "BinanceApiKey":    "...",
///   "BinanceApiSecret": "..."
/// }
/// </code>
/// Missing fields are left untouched on save (partial updates allowed).
/// </summary>
public static class ExchangeCredentialsService
{
    private static string CredentialsPath
    {
        get
        {
            var root = SharedExchangeConfiguration.ResolveSharedConfigRoot(AppContext.BaseDirectory);
            return Path.Combine(root, "exchange_credentials.dpapi");
        }
    }

    public static (string path, bool okxLive, bool okxDemo, bool binance) GetStatus()
    {
        var path = CredentialsPath;
        if (!File.Exists(path)) return (path, false, false, false);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            bool has(string p) =>
                root.TryGetProperty(p, out var v) &&
                v.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(v.GetString());
            var okxLive = has("ApiKey") && has("SecretKey") && has("Passphrase");
            var okxDemo = has("DemoApiKey") && has("DemoSecretKey") && has("DemoPassphrase");
            var binance = has("BinanceApiKey") && has("BinanceApiSecret");
            return (path, okxLive, okxDemo, binance);
        }
        catch
        {
            return (path, false, false, false);
        }
    }

    [SupportedOSPlatform("windows")]
    public static async Task<object> SaveAsync(ExchangeCredentialsRequestDto dto, CancellationToken ct)
    {
        var path = CredentialsPath;

        // Load existing values (if any) so partial updates don't wipe other slots.
        var entries = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        entries[prop.Name] = prop.Value.GetString();
                }
            }
            catch { /* corrupt file → start fresh */ }
        }

        // Merge incoming plaintext → DPAPI-protected base64.  An empty/whitespace
        // value means "don't change this slot"; an explicit "" with the explicit
        // `clearOkxLive` etc. flags below removes a slot.
        if (!string.IsNullOrWhiteSpace(dto.OkxApiKey))      entries["ApiKey"]          = Protect(dto.OkxApiKey);
        if (!string.IsNullOrWhiteSpace(dto.OkxSecretKey))   entries["SecretKey"]       = Protect(dto.OkxSecretKey);
        if (!string.IsNullOrWhiteSpace(dto.OkxPassphrase))  entries["Passphrase"]      = Protect(dto.OkxPassphrase);
        if (!string.IsNullOrWhiteSpace(dto.OkxDemoApiKey))      entries["DemoApiKey"]      = Protect(dto.OkxDemoApiKey);
        if (!string.IsNullOrWhiteSpace(dto.OkxDemoSecretKey))   entries["DemoSecretKey"]   = Protect(dto.OkxDemoSecretKey);
        if (!string.IsNullOrWhiteSpace(dto.OkxDemoPassphrase))  entries["DemoPassphrase"]  = Protect(dto.OkxDemoPassphrase);
        if (!string.IsNullOrWhiteSpace(dto.BinanceApiKey))    entries["BinanceApiKey"]    = Protect(dto.BinanceApiKey);
        if (!string.IsNullOrWhiteSpace(dto.BinanceApiSecret)) entries["BinanceApiSecret"] = Protect(dto.BinanceApiSecret);

        if (dto.ClearOkxLive == true)
        {
            entries.Remove("ApiKey"); entries.Remove("SecretKey"); entries.Remove("Passphrase");
        }
        if (dto.ClearOkxDemo == true)
        {
            entries.Remove("DemoApiKey"); entries.Remove("DemoSecretKey"); entries.Remove("DemoPassphrase");
        }
        if (dto.ClearBinance == true)
        {
            entries.Remove("BinanceApiKey"); entries.Remove("BinanceApiSecret");
        }

        // Atomic write: tmp file → File.Move overwrite.
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        var tmp  = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);

        // Optionally toggle IsDemoMode in exchange_settings.json so the new active
        // slot is selected without manual editing.
        if (dto.SetDemoMode is bool wantDemo)
        {
            await UpdateDemoModeAsync(wantDemo, ct).ConfigureAwait(false);
        }

        var (p, okxL, okxD, bnc) = GetStatus();
        return new
        {
            path           = p,
            okxLiveConfigured  = okxL,
            okxDemoConfigured  = okxD,
            binanceConfigured  = bnc,
            note = "Saved. Restart Listener.Api + QuantEngine.Worker to apply — credentials are loaded once at startup.",
        };
    }

    [SupportedOSPlatform("windows")]
    private static string Protect(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    private static async Task UpdateDemoModeAsync(bool isDemo, CancellationToken ct)
    {
        var root = SharedExchangeConfiguration.ResolveSharedConfigRoot(AppContext.BaseDirectory);
        var settingsPath = Path.Combine(root, "exchange_settings.json");
        if (!File.Exists(settingsPath)) return;

        try
        {
            // Roundtrip through System.Text.Json — preserve other keys.
            var raw = await File.ReadAllTextAsync(settingsPath, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            var dict = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("OKX")) continue; // we'll rewrite this
                dict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
            }
            dict["OKX"] = new { IsDemoMode = isDemo };
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            var tmp  = settingsPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, settingsPath, overwrite: true);
        }
        catch { /* best-effort — caller still gets credentials saved */ }
    }
}

/// <summary>Switch the active exchange mode at runtime.</summary>
public sealed record ExchangeModeRequestDto(
    string Mode,        // "OKX" | "OKXSwap" | "Binance" | "BinanceFutures" | "Mock"
    bool? IsDemo);      // true = OKX demo slot; false = live slot

/// <summary>Plaintext credential payload — only valid for the duration of the request.</summary>
public sealed record ExchangeCredentialsRequestDto(
    string? OkxApiKey,
    string? OkxSecretKey,
    string? OkxPassphrase,
    string? OkxDemoApiKey,
    string? OkxDemoSecretKey,
    string? OkxDemoPassphrase,
    string? BinanceApiKey,
    string? BinanceApiSecret,
    bool? SetDemoMode,
    bool? ClearOkxLive,
    bool? ClearOkxDemo,
    bool? ClearBinance);
