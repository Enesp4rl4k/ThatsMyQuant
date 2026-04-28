using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ThatsMyQuant.Infrastructure.Persistence;

/// <summary>
/// Loads Dashboard-saved Telegram credentials from <c>telegram_settings.json</c>
/// at the repo root (same location as <c>exchange_settings.json</c>) into the
/// worker's IConfiguration pipeline. Keeps the <c>Telegram:*</c> keys that
/// <see cref="ThatsMyQuant.Contracts.Options.TelegramOptions"/> binds to.
///
/// The dashboard's TelegramSettingsViewModel writes this file so users don't
/// have to edit appsettings.*.json manually.
/// </summary>
public static class SharedTelegramConfiguration
{
    public static IConfigurationBuilder ApplySharedTelegramConfiguration(
        this IConfigurationBuilder builder, string? baseDirectory = null)
    {
        var root = SharedExchangeConfiguration.ResolveSharedConfigRoot(baseDirectory ?? AppContext.BaseDirectory);
        var settingsPath = Path.Combine(root, "telegram_settings.json");
        if (!File.Exists(settingsPath))
        {
            return builder;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root2 = document.RootElement;
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            if (root2.TryGetProperty("BotToken", out var tok) && tok.ValueKind == JsonValueKind.String)
                values["Telegram:BotToken"] = tok.GetString();
            if (root2.TryGetProperty("ChatId", out var cid))
                values["Telegram:ChatId"] = cid.ValueKind == JsonValueKind.Number ? cid.GetInt64().ToString() : cid.GetString();
            if (root2.TryGetProperty("EnableReadBot", out var ena))
                values["Telegram:EnableReadBot"] = ena.ValueKind == JsonValueKind.True ? "true"
                    : ena.ValueKind == JsonValueKind.False ? "false"
                    : ena.ValueKind == JsonValueKind.String ? ena.GetString() : "false";
            if (root2.TryGetProperty("AllowedChatIds", out var allowed) && allowed.ValueKind == JsonValueKind.String)
                values["Telegram:AllowedChatIds"] = allowed.GetString();

            if (values.Count > 0)
            {
                builder.AddInMemoryCollection(values);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TelegramConfig] Failed to load telegram_settings.json: {ex.Message}");
        }

        // Re-add env vars last so they keep precedence for any explicit overrides
        // (e.g., CI or secret managers).
        builder.AddEnvironmentVariables();
        return builder;
    }
}
