using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

/// <summary>
/// User-facing Telegram credentials manager. Writes to <c>telegram_settings.json</c>
/// at the repo root where <c>SharedTelegramConfiguration</c> (worker side) reads it.
/// The file is gitignored via the appsettings.*.local pattern + explicit rule so
/// secrets stay local.
/// </summary>
public partial class TelegramSettingsViewModel : ObservableObject
{
    [ObservableProperty] private string _botToken = string.Empty;
    [ObservableProperty] private string _chatId = string.Empty;
    [ObservableProperty] private bool _enableReadBot = true;
    [ObservableProperty] private string _allowedChatIds = string.Empty;
    [ObservableProperty] private string _saveStatus = "Credentials are local; the worker reloads them on next restart.";
    [ObservableProperty] private string _testStatus = "";
    [ObservableProperty] private bool _isTesting;

    private static string SettingsPath =>
        Path.Combine(ExchangeSettingsViewModel.ResolveSharedConfigRoot(), "telegram_settings.json");

    public TelegramSettingsViewModel()
    {
        LoadSettings();
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            var payload = new
            {
                BotToken = BotToken?.Trim() ?? "",
                ChatId = ChatId?.Trim() ?? "",
                EnableReadBot,
                AllowedChatIds = AllowedChatIds?.Trim() ?? ""
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

            var tmp = SettingsPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, Encoding.UTF8).ConfigureAwait(false);
            File.Move(tmp, SettingsPath, overwrite: true);

            SaveStatus = $"✓ Saved to {Path.GetFileName(SettingsPath)} — restart the worker to activate.";
        }
        catch (Exception ex)
        {
            SaveStatus = $"❌ Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TestMessageAsync()
    {
        TestStatus = "";
        if (string.IsNullOrWhiteSpace(BotToken) || string.IsNullOrWhiteSpace(ChatId))
        {
            TestStatus = "❌ Fill BotToken and ChatId first.";
            return;
        }

        IsTesting = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var payload = new
            {
                chat_id = ChatId.Trim(),
                text = "✓ ThatsMyQuant connectivity test — your bot & chat are configured correctly."
            };
            var resp = await http.PostAsJsonAsync(
                $"https://api.telegram.org/bot{BotToken.Trim()}/sendMessage",
                payload).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                TestStatus = "✓ Test message sent. Check your Telegram chat.";
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                TestStatus = $"❌ Telegram returned {(int)resp.StatusCode}: {Truncate(body, 140)}";
            }
        }
        catch (Exception ex)
        {
            TestStatus = $"❌ Test failed: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = doc.RootElement;
            BotToken = root.TryGetProperty("BotToken", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            ChatId = root.TryGetProperty("ChatId", out var c) ? (c.ValueKind == JsonValueKind.Number ? c.GetInt64().ToString() : c.GetString() ?? "") : "";
            EnableReadBot = root.TryGetProperty("EnableReadBot", out var e) && e.ValueKind == JsonValueKind.True;
            AllowedChatIds = root.TryGetProperty("AllowedChatIds", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
        }
        catch { /* Corrupt / missing file — start with defaults. */ }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
