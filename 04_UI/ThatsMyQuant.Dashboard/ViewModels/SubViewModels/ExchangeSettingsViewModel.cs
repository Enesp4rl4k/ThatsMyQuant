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
    // Compound mode string persisted to exchange_settings.json and env vars.
    // Derived from (Venue, MarketType, IsDemoMode); kept for backward compat
    // with Program.cs dispatch logic in every worker.
    [ObservableProperty] private string _selectedExchangeMode = "Mock";

    // Axes the user actually picks from in the UI.
    [ObservableProperty] private string _selectedVenue = "Mock";
    [ObservableProperty] private string _selectedMarketType = "Spot";

    [ObservableProperty] private string _binanceApiKey = string.Empty;
    [ObservableProperty] private string _binanceApiSecret = string.Empty;

    // Live OKX credentials (real account). Active when IsDemoMode = false.
    [ObservableProperty] private string _okxApiKey = string.Empty;
    [ObservableProperty] private string _okxSecretKey = string.Empty;
    [ObservableProperty] private string _okxPassphrase = string.Empty;

    // Demo/Simulated OKX credentials. Active when IsDemoMode = true. Stored
    // alongside live credentials so flipping the toggle swaps the active set
    // without re-entering keys. OKX requires BOTH sets to be issued from their
    // respective environment (demo keys only work with x-simulated-trading header).
    [ObservableProperty] private string _okxDemoApiKey = string.Empty;
    [ObservableProperty] private string _okxDemoSecretKey = string.Empty;
    [ObservableProperty] private string _okxDemoPassphrase = string.Empty;

    [ObservableProperty] private bool _isDemoMode = true;
    [ObservableProperty] private string _saveStatus = "READY";
    [ObservableProperty] private bool _isSaving = false;

    public ObservableCollection<string> AvailableVenues { get; } =
    [
        "Mock",
        "Binance",
        "OKX"
    ];

    public ObservableCollection<string> AvailableMarketTypes { get; } =
    [
        "Spot",
        "Futures"
    ];

    // Retained for backward compatibility — settings files and any persisted
    // bindings that still reference the combined list should still read.
    public ObservableCollection<string> AvailableExchangeModes { get; } =
    [
        "Mock",
        "Binance",
        "BinanceTestnet",
        "BinanceFutures",
        "BinanceFuturesTestnet",
        "OKX",
        "OKXSwap"
    ];

    private string SettingsPath => Path.Combine(ResolveSharedConfigRoot(), "exchange_settings.json");
    private string CredentialsPath => Path.Combine(ResolveSharedConfigRoot(), "exchange_credentials.dpapi");

    // Prevents re-entrancy while the change handlers mutate each other.
    private bool _isComposingMode;

    public ExchangeSettingsViewModel()
    {
        LoadSettings();
    }

    partial void OnSelectedVenueChanged(string value) => ComposeExchangeMode();
    partial void OnSelectedMarketTypeChanged(string value) => ComposeExchangeMode();

    // IsDemoMode drives Binance spot→testnet and Binance futures→futuresTestnet; it is
    // already used for OKX. Recompose whenever the toggle flips so the mode string
    // tracks the UI state without requiring a Save round-trip.
    partial void OnIsDemoModeChanged(bool value)
    {
        // Guard: only prompt when switching TO live (value == false).
        if (!value)
        {
            var result = MessageBox.Show(
                "You are switching to LIVE trading. Real funds will be at risk. Confirm?",
                "Live Trading Warning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                // Revert — re-triggers this handler with value == true.
                IsDemoMode = true;
                return;
            }
        }

        ComposeExchangeMode();
    }

    /// <summary>
    /// Derive the compound ExchangeMode string (consumed by every worker's Program.cs
    /// dispatch logic and the TMQ_EXCHANGE_MODE env var) from the three UI axes
    /// the user actually picks: Venue × MarketType × Demo/Testnet flag.
    /// </summary>
    private void ComposeExchangeMode()
    {
        if (_isComposingMode) return;
        _isComposingMode = true;
        try
        {
            SelectedExchangeMode = (SelectedVenue, SelectedMarketType, IsDemoMode) switch
            {
                ("Mock", _, _) => "Mock",
                ("Binance", "Spot", true) => "BinanceTestnet",
                ("Binance", "Spot", false) => "Binance",
                ("Binance", "Futures", true) => "BinanceFuturesTestnet",
                ("Binance", "Futures", false) => "BinanceFutures",
                ("OKX", "Spot", _) => "OKX",
                ("OKX", "Futures", _) => "OKXSwap",
                _ => SelectedExchangeMode
            };
        }
        finally
        {
            _isComposingMode = false;
        }
    }

    /// <summary>
    /// Reverse of ComposeExchangeMode: parse the persisted compound string back
    /// into the three UI axes so the user's last selection round-trips correctly.
    /// OKX demo-mode information lives in the JSON's OKX.IsDemoMode field, not
    /// the compound string, so only Binance modes drive IsDemoMode here.
    /// </summary>
    private void DecomposeExchangeMode(string mode)
    {
        _isComposingMode = true;
        try
        {
            switch (mode)
            {
                case "Mock":
                    SelectedVenue = "Mock";
                    SelectedMarketType = "Spot";
                    break;
                case "Binance":
                    SelectedVenue = "Binance";
                    SelectedMarketType = "Spot";
                    IsDemoMode = false;
                    break;
                case "BinanceTestnet":
                    SelectedVenue = "Binance";
                    SelectedMarketType = "Spot";
                    IsDemoMode = true;
                    break;
                case "BinanceFutures":
                    SelectedVenue = "Binance";
                    SelectedMarketType = "Futures";
                    IsDemoMode = false;
                    break;
                case "BinanceFuturesTestnet":
                    SelectedVenue = "Binance";
                    SelectedMarketType = "Futures";
                    IsDemoMode = true;
                    break;
                case "OKX":
                    SelectedVenue = "OKX";
                    SelectedMarketType = "Spot";
                    break;
                case "OKXSwap":
                    SelectedVenue = "OKX";
                    SelectedMarketType = "Futures";
                    break;
            }
        }
        finally
        {
            _isComposingMode = false;
        }
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

                // Project the compound mode back into the UI's (Venue, MarketType)
                // axes so the user sees their previous selection correctly on load.
                DecomposeExchangeMode(SelectedExchangeMode);
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

        // Preserve existing encrypted values when the UI field is empty — prevents
        // a "Save" click with an un-populated PasswordBox from wiping previously
        // stored credentials. Any field with non-empty new value overwrites.
        string? PreservingProtect(string newValue, string jsonField)
        {
            if (!string.IsNullOrWhiteSpace(newValue)) return Protect(newValue);
            var existing = TryReadExistingField(jsonField);
            return existing;
        }

        var creds = new
        {
            BinanceApiKey = PreservingProtect(BinanceApiKey, "BinanceApiKey"),
            BinanceApiSecret = PreservingProtect(BinanceApiSecret, "BinanceApiSecret"),
            // Live OKX
            ApiKey = PreservingProtect(OkxApiKey, "ApiKey"),
            SecretKey = PreservingProtect(OkxSecretKey, "SecretKey"),
            Passphrase = PreservingProtect(OkxPassphrase, "Passphrase"),
            // Demo OKX (issued separately from OKX Demo Trading environment)
            DemoApiKey = PreservingProtect(OkxDemoApiKey, "DemoApiKey"),
            DemoSecretKey = PreservingProtect(OkxDemoSecretKey, "DemoSecretKey"),
            DemoPassphrase = PreservingProtect(OkxDemoPassphrase, "DemoPassphrase")
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(CredentialsPath, JsonSerializer.Serialize(creds, options));
    }

    /// <summary>
    /// Reads the previously-persisted Base64-encrypted value for a single field
    /// from the credentials file, used as a fallback when the current UI field is
    /// empty so a partial save can't destroy existing secrets.
    /// </summary>
    private string? TryReadExistingField(string fieldName)
    {
        try
        {
            if (!File.Exists(CredentialsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            return doc.RootElement.TryGetProperty(fieldName, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null;
        }
        catch
        {
            return null;
        }
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
            // Demo set — may be absent on legacy credential files.
            if (root.TryGetProperty("DemoApiKey", out var demoApiKey)) OkxDemoApiKey = Unprotect(demoApiKey.GetString() ?? "");
            if (root.TryGetProperty("DemoSecretKey", out var demoSecretKey)) OkxDemoSecretKey = Unprotect(demoSecretKey.GetString() ?? "");
            if (root.TryGetProperty("DemoPassphrase", out var demoPassphrase)) OkxDemoPassphrase = Unprotect(demoPassphrase.GetString() ?? "");
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
