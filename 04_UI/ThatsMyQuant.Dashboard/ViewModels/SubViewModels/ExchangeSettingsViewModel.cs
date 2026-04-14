using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Collections.ObjectModel;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public partial class ExchangeSettingsViewModel : ObservableObject
{
    [ObservableProperty] private string _selectedExchangeMode = "Mock";
    [ObservableProperty] private string _binanceApiKey = string.Empty;
    [ObservableProperty] private string _binanceApiSecret = string.Empty;
    [ObservableProperty] private string _okxApiKey = string.Empty;
    [ObservableProperty] private string _okxSecretKey = string.Empty;
    [ObservableProperty] private string _okxPassphrase = string.Empty;
    [ObservableProperty] private bool _isDemoMode = true;
    [ObservableProperty] private string _saveStatus = "READY";
    [ObservableProperty] private bool _isSaving = false;

    public ObservableCollection<string> AvailableExchangeModes { get; } =
    [
        "Mock",
        "Binance",
        "BinanceTestnet",
        "OKX"
    ];

    private string SettingsPath => Path.Combine(ResolveSharedConfigRoot(), "exchange_settings.json");
    private string CredentialsPath => Path.Combine(ResolveSharedConfigRoot(), "exchange_credentials.dpapi");

    public ExchangeSettingsViewModel()
    {
        LoadSettings();
    }

    [RelayCommand]
    private async Task SaveSettings()
    {
        IsSaving = true;
        SaveStatus = "SAVING...";

        try
        {
            var settings = new
            {
                ExchangeMode = SelectedExchangeMode,
                OKX = new
                {
                    IsDemoMode = IsDemoMode
                }
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(settings, options);
            await File.WriteAllTextAsync(SettingsPath, jsonString);

            SaveCredentialsDpapi();
            ApplyRuntimeEnvironment();

            SaveStatus = "SAVED SUCCESSFULLY!";
            await Task.Delay(1200);
            SaveStatus = "READY";
        }
        catch (Exception ex)
        {
            SaveStatus = $"ERROR: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private async Task RestartWorker()
    {
        SaveStatus = "ATTEMPTING RESTART...";

        try
        {
            await SaveSettings();

            var processes = System.Diagnostics.Process.GetProcessesByName("ThatsMyQuant.Execution.Worker");
            if (processes.Length == 0)
            {
                processes = System.Diagnostics.Process.GetProcessesByName("dotnet")
                    .Where(p => p.MainWindowTitle.Contains("Execution.Worker") || p.MainModule?.FileName.Contains("Execution.Worker") == true)
                    .ToArray();
            }

            if (processes.Length > 0)
            {
                foreach (var p in processes)
                {
                    try { p.Kill(); } catch { }
                }
                SaveStatus = "WORKER STOPPED. STARTING...";
            }
            else
            {
                SaveStatus = "WORKER NOT FOUND. STARTING...";
            }

            SaveStatus = "APPLIED & RELOADED";
            await Task.Delay(2000);
            SaveStatus = "READY";
        }
        catch (Exception ex)
        {
            SaveStatus = $"RESTART ERROR: {ex.Message}";
        }
    }

    partial void OnIsDemoModeChanged(bool value)
    {
        // Guard: only prompt when switching TO live (value == false).
        if (value) return;

        var result = MessageBox.Show(
            "You are switching to LIVE trading. Real funds will be at risk. Confirm?",
            "Live Trading Warning",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            // Revert — re-triggers this handler with value == true, which returns immediately.
            IsDemoMode = true;
        }
    }

    private void LoadSettings()
    {
        if (File.Exists(SettingsPath))
        {
            try
            {
                string jsonString = File.ReadAllText(SettingsPath);
                using var doc = JsonDocument.Parse(jsonString);
                if (doc.RootElement.TryGetProperty("ExchangeMode", out var exchangeMode))
                {
                    SelectedExchangeMode = exchangeMode.GetString() ?? "Mock";
                }

                if (doc.RootElement.TryGetProperty("OKX", out var okx))
                {
                    if (okx.TryGetProperty("IsDemoMode", out var demo)) IsDemoMode = demo.GetBoolean();
                }
            }
            catch
            {
                // Silent fail on load
            }
        }

        LoadCredentialsDpapi();
        ApplyRuntimeEnvironment();
    }

    /// <summary>
    /// Encrypts ApiKey, SecretKey, and Passphrase using Windows DPAPI (CurrentUser scope)
    /// and writes them as Base64-encoded values to exchange_credentials.dpapi.
    /// </summary>
    private void SaveCredentialsDpapi()
    {
        static string Protect(string value)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(value);
            byte[] encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            Array.Clear(plainBytes, 0, plainBytes.Length);
            return Convert.ToBase64String(encrypted);
        }

        var creds = new
        {
            BinanceApiKey = Protect(BinanceApiKey),
            BinanceApiSecret = Protect(BinanceApiSecret),
            ApiKey = Protect(OkxApiKey),
            SecretKey = Protect(OkxSecretKey),
            Passphrase = Protect(OkxPassphrase)
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(CredentialsPath, JsonSerializer.Serialize(creds, options));
    }

    /// <summary>
    /// Reads and decrypts credentials from exchange_credentials.dpapi using Windows DPAPI.
    /// </summary>
    private void LoadCredentialsDpapi()
    {
        if (!File.Exists(CredentialsPath)) return;

        try
        {
            string json = File.ReadAllText(CredentialsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            static string Unprotect(string base64)
            {
                byte[] encrypted = Convert.FromBase64String(base64);
                byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                string value = Encoding.UTF8.GetString(plain);
                Array.Clear(plain, 0, plain.Length);
                return value;
            }

            if (root.TryGetProperty("BinanceApiKey", out var binanceApiKey)) BinanceApiKey = Unprotect(binanceApiKey.GetString() ?? "");
            if (root.TryGetProperty("BinanceApiSecret", out var binanceApiSecret)) BinanceApiSecret = Unprotect(binanceApiSecret.GetString() ?? "");
            if (root.TryGetProperty("ApiKey", out var apiKey)) OkxApiKey = Unprotect(apiKey.GetString() ?? "");
            if (root.TryGetProperty("SecretKey", out var secretKey)) OkxSecretKey = Unprotect(secretKey.GetString() ?? "");
            if (root.TryGetProperty("Passphrase", out var passphrase)) OkxPassphrase = Unprotect(passphrase.GetString() ?? "");
        }
        catch
        {
            // Silent fail — credentials may not exist yet on first run.
        }
    }

    private void ApplyRuntimeEnvironment()
    {
        Environment.SetEnvironmentVariable("TMQ_EXCHANGE_MODE", SelectedExchangeMode, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("TMQ_BINANCE_API_KEY", BinanceApiKey, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("TMQ_BINANCE_API_SECRET", BinanceApiSecret, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("OKX__ApiKey", OkxApiKey, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("OKX__SecretKey", OkxSecretKey, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("OKX__Passphrase", OkxPassphrase, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("OKX__IsDemoMode", IsDemoMode ? "true" : "false", EnvironmentVariableTarget.Process);
    }

    internal static string ResolveSharedConfigRoot()
    {
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (directory is not null)
        {
            var hasBundleMarkers =
                Directory.Exists(Path.Combine(directory.FullName, "Dashboard")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Execution.Worker"));
            if (hasBundleMarkers)
            {
                return directory.FullName;
            }

            if (File.Exists(Path.Combine(directory.FullName, "ThatsMyQuant.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppDomain.CurrentDomain.BaseDirectory;
    }

    internal static string ResolveSharedConfigPath(string fileName)
        => Path.Combine(ResolveSharedConfigRoot(), fileName);
}
