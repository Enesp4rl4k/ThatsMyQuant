using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThatsMyQuant.Launcher;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<LauncherServiceStatus> _services;
    private readonly LauncherProcessManager _processManager;
    private readonly DispatcherTimer _healthTimer;
    private readonly string _workspaceRoot;
    private readonly string? _bundleRoot;
    private readonly LauncherServiceDefinition _dashboardDefinition;
    private Dictionary<string, string>? _cachedCredentialEnvironment;
    private readonly object _credentialCacheLock = new();

    public MainWindow()
    {
        InitializeComponent();

        _workspaceRoot = FindWorkspaceRoot();
        _bundleRoot = FindBundleRoot();
        _processManager = new LauncherProcessManager(_workspaceRoot, _bundleRoot);
        _processManager.OutputReceived += AppendLog;

        var sharedApiUrls = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            // 2026-04-24: Launcher defaults to Mock exchange mode so services don't refuse
            // to start when no OKX/Binance credentials are present (paper-safe default).
            // For live trading, operator must set TMQ_EXCHANGE_MODE=OKX + supply credentials
            // via exchange_credentials.dpapi or env vars (see StartupSafetyPolicy).
            ["TMQ_EXCHANGE_MODE"] = Environment.GetEnvironmentVariable("TMQ_EXCHANGE_MODE") ?? "Mock"
        };

        var gatewayDefinition = new LauncherServiceDefinition(
            "Gateway.Api",
            "Gateway.Api",
            "ThatsMyQuant.Gateway.Api.exe",
            Path.Combine("01_Services", "Gateway.Api", "ThatsMyQuant.Gateway.Api.csproj"),
            "http://localhost:5181/health",
            new Dictionary<string, string>(sharedApiUrls)
            {
                ["ASPNETCORE_URLS"] = "https://localhost:7138;http://localhost:5181"
            });

        var listenerDefinition = new LauncherServiceDefinition(
            "Listener.Api",
            "Listener.Api",
            "Listener.Api.exe",
            Path.Combine("01_Services", "Listener.Api", "Listener.Api.csproj"),
            "http://localhost:5266/health",
            new Dictionary<string, string>(sharedApiUrls)
            {
                ["ASPNETCORE_URLS"] = "https://localhost:7054;http://localhost:5266"
            });

        var executionDefinition = new LauncherServiceDefinition(
            "Execution.Worker",
            "Execution.Worker",
            "Execution.Worker.exe",
            Path.Combine("01_Services", "Execution.Worker", "Execution.Worker.csproj"),
            "http://127.0.0.1:18081/health");

        var quantDefinition = new LauncherServiceDefinition(
            "QuantEngine.Worker",
            "QuantEngine.Worker",
            "ThatsMyQuant.Bot.exe",
            Path.Combine("01_Services", "QuantEngine.Worker", "ThatsMyQuant.Bot.csproj"),
            "http://127.0.0.1:18082/health");

        _dashboardDefinition = new LauncherServiceDefinition(
            "Dashboard",
            "Dashboard",
            "ThatsMyQuant.Dashboard.exe",
            Path.Combine("04_UI", "ThatsMyQuant.Dashboard", "ThatsMyQuant.Dashboard.csproj"),
            LaunchWindow: true);

        _services =
        [
            new LauncherServiceStatus(gatewayDefinition),
            new LauncherServiceStatus(listenerDefinition),
            new LauncherServiceStatus(executionDefinition),
            new LauncherServiceStatus(quantDefinition),
            new LauncherServiceStatus(_dashboardDefinition)
        ];

        ServicesGrid.ItemsSource = _services;
        EnvironmentTextBlock.Text = BuildEnvironmentSummary();

        _healthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _healthTimer.Tick += async (_, _) => await RefreshStatusesAsync();
        Loaded += async (_, _) =>
        {
            AppendLog("Launcher initialized.");
            await RefreshStatusesAsync();
            _healthTimer.Start();
        };
        Closing += async (_, _) =>
        {
            _healthTimer.Stop();
            await _processManager.StopAllAsync();
            _processManager.Dispose();
        };
    }

    private async void StartAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        await WithButtonsDisabled(async () =>
        {
            var workers = _services.Where(status => status.Definition != _dashboardDefinition).ToList();
            var tasks = workers.Select(service => StartServiceAsync(service));
            await Task.WhenAll(tasks);
        });
    }

    private async void StopAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        await WithButtonsDisabled(async () =>
        {
            await _processManager.StopAllAsync();
            await RefreshStatusesAsync();
        });
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshStatusesAsync();
    }

    private async void OpenDashboardButton_OnClick(object sender, RoutedEventArgs e)
    {
        await WithButtonsDisabled(async () =>
        {
            await StartServiceAsync(_services.First(status => status.Definition == _dashboardDefinition));
        });
    }

    private void OpenBundleButton_OnClick(object sender, RoutedEventArgs e)
    {
        var path = _bundleRoot ?? _workspaceRoot;
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }

    private async Task StartServiceAsync(LauncherServiceStatus service)
    {
        var definition = ApplySharedRuntimeConfiguration(service.Definition);

        try
        {
            service.State = "Starting";
            service.Details = "Launching process...";
            service.StateBrush = Brushes.Goldenrod;
            await _processManager.StartAsync(definition);
            await RefreshStatusAsync(service);
        }
        catch (Exception ex)
        {
            service.State = "Failed";
            service.Details = ex.Message;
            service.StateBrush = Brushes.IndianRed;
            AppendLog($"[{service.DisplayName}] failed to start: {ex.Message}");
        }
    }

    private async Task RefreshStatusesAsync()
    {
        foreach (var service in _services)
        {
            await RefreshStatusAsync(service);
        }
    }

    private async Task RefreshStatusAsync(LauncherServiceStatus service)
    {
        if (service.Definition == _dashboardDefinition)
        {
            var running = _processManager.IsRunning(service.DisplayName);
            service.State = running ? "Running" : "Stopped";
            service.Details = running ? "Desktop window is open." : "Dashboard is not running.";
            service.StateBrush = running ? Brushes.ForestGreen : Brushes.Gray;
            return;
        }

        var processRunning = _processManager.IsRunning(service.DisplayName);
        if (!processRunning)
        {
            service.State = "Stopped";
            service.Details = "No active process.";
            service.StateBrush = Brushes.Gray;
            return;
        }

        if (!string.IsNullOrWhiteSpace(service.Definition.HealthUrl))
        {
            var healthy = await _processManager.CheckHealthAsync(service.Definition.HealthUrl!);
            service.State = healthy ? "Healthy" : "Starting";
            service.Details = healthy ? service.Definition.HealthUrl! : "Process is up, health probe is not ready yet.";
            service.StateBrush = healthy ? Brushes.ForestGreen : Brushes.Goldenrod;
        }
        else
        {
            service.State = "Running";
            service.Details = "Process is running.";
            service.StateBrush = Brushes.ForestGreen;
        }
    }

    private async Task WithButtonsDisabled(Func<Task> action)
    {
        ToggleButtons(false);
        try
        {
            await action();
        }
        finally
        {
            ToggleButtons(true);
        }
    }

    private void ToggleButtons(bool isEnabled)
    {
        StartAllButton.IsEnabled = isEnabled;
        StopAllButton.IsEnabled = isEnabled;
        RefreshButton.IsEnabled = isEnabled;
        OpenDashboardButton.IsEnabled = isEnabled;
        OpenBundleButton.IsEnabled = isEnabled;
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogTextBox.ScrollToEnd();
        });
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ThatsMyQuant.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private string? FindBundleRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Gateway.Api", "ThatsMyQuant.Gateway.Api.exe")) ||
                File.Exists(Path.Combine(directory.FullName, "Gateway.Api", "Listener.Api.exe")) ||
                File.Exists(Path.Combine(directory.FullName, "Execution.Worker", "Execution.Worker.exe")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private LauncherServiceDefinition ApplySharedRuntimeConfiguration(LauncherServiceDefinition definition)
    {
        var environment = definition.Environment is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(definition.Environment, StringComparer.OrdinalIgnoreCase);

        // Ensure all worker services load appsettings.Development.json and use
        // Development-mode defaults (e.g., detailed errors, SQLite dev DB path).
        if (!environment.ContainsKey("DOTNET_ENVIRONMENT") && !environment.ContainsKey("ASPNETCORE_ENVIRONMENT"))
        {
            environment["DOTNET_ENVIRONMENT"] = "Development";
        }

        // Default child services to paper mode unless the user has explicitly opted
        // into live trading. Paper mode relaxes the credential requirement (the worker
        // uses PaperTradingService) so DPAPI decrypt failures on launcher-spawned
        // children don't block the paper loop from starting.
        if (!environment.ContainsKey("TMQ_MODE"))
        {
            environment["TMQ_MODE"] = "paper";
        }

        // Credentials are decrypted ONCE per launcher session and cached so that
        // every spawned service gets the identical env payload (prevents DPAPI
        // race conditions under parallel startup).
        var credentials = GetCachedCredentialEnvironment();
        foreach (var pair in credentials)
        {
            environment[pair.Key] = pair.Value;
        }

        // Safety: if the operator selected a live venue (OKXSwap/Binance/etc.) but
        // DPAPI decryption failed (no OKX__ApiKey or TMQ_BINANCE_API_KEY in the
        // cache), force Mock mode so StartupSafetyPolicy doesn't abort the worker.
        // This keeps the paper loop runnable even without valid credentials.
        var exchangeMode = ReadExchangeMode();
        var hasOkxCreds = credentials.ContainsKey("OKX__ApiKey");
        var hasBinanceCreds = credentials.ContainsKey("TMQ_BINANCE_API_KEY");
        var isPaperMode = string.Equals(environment["TMQ_MODE"], "paper", StringComparison.OrdinalIgnoreCase);
        var isMockAlready = exchangeMode.Equals("Mock", StringComparison.OrdinalIgnoreCase);

        if (!isMockAlready && !hasOkxCreds && !hasBinanceCreds && isPaperMode)
        {
            exchangeMode = "Mock";
            AppendLog($"[{definition.DisplayName}] No decrypted credentials found; forcing TMQ_EXCHANGE_MODE=Mock for paper startup.");
        }

        environment["TMQ_EXCHANGE_MODE"] = exchangeMode;

        return definition with { Environment = environment };
    }

    private Dictionary<string, string> GetCachedCredentialEnvironment()
    {
        lock (_credentialCacheLock)
        {
            return _cachedCredentialEnvironment ??= ReadCredentialEnvironment();
        }
    }

    private string BuildEnvironmentSummary()
    {
        var root = _bundleRoot ?? _workspaceRoot;
        return $"Bundle mode  |  Root: {root}  |  Exchange: {ReadExchangeMode()}";
    }

    private string ReadExchangeMode()
    {
        var settingsPath = ResolveSharedSettingsPath();
        if (!File.Exists(settingsPath))
        {
            return "Mock";
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty("ExchangeMode", out var exchangeMode))
            {
                var value = exchangeMode.GetString();
                return string.IsNullOrWhiteSpace(value) ? "Mock" : value;
            }
        }
        catch
        {
        }

        return "Mock";
    }

    private Dictionary<string, string> ReadCredentialEnvironment()
    {
        var settingsPath = ResolveSharedSettingsPath();
        var credentialsPath = ResolveSharedCredentialsPath();
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        bool isDemoMode = false;
        if (File.Exists(settingsPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (document.RootElement.TryGetProperty("OKX", out var okx) &&
                    okx.TryGetProperty("IsDemoMode", out var demoMode))
                {
                    isDemoMode = demoMode.GetBoolean();
                    environment["OKX__IsDemoMode"] = isDemoMode ? "true" : "false";
                }
            }
            catch
            {
            }
        }

        if (!File.Exists(credentialsPath))
        {
            return environment;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(credentialsPath));
            var root = document.RootElement;

            var binanceApiKey = TryUnprotect(root, "BinanceApiKey");
            var binanceApiSecret = TryUnprotect(root, "BinanceApiSecret");
            // Live OKX slot (legacy names kept for backward compatibility).
            var okxLiveApiKey = TryUnprotect(root, "ApiKey");
            var okxLiveSecretKey = TryUnprotect(root, "SecretKey");
            var okxLivePassphrase = TryUnprotect(root, "Passphrase");
            // Demo OKX slot (added 2026-04-25). Active when OKX:IsDemoMode=true.
            var okxDemoApiKey = TryUnprotect(root, "DemoApiKey");
            var okxDemoSecretKey = TryUnprotect(root, "DemoSecretKey");
            var okxDemoPassphrase = TryUnprotect(root, "DemoPassphrase");

            // Select the active OKX set based on IsDemoMode, with graceful fallback
            // to the opposite set so the services fail with a clear AUTH error rather
            // than silently booting with the wrong environment's keys.
            var okxApiKey = isDemoMode
                ? (string.IsNullOrWhiteSpace(okxDemoApiKey) ? okxLiveApiKey : okxDemoApiKey)
                : (string.IsNullOrWhiteSpace(okxLiveApiKey) ? okxDemoApiKey : okxLiveApiKey);
            var okxSecretKey = isDemoMode
                ? (string.IsNullOrWhiteSpace(okxDemoSecretKey) ? okxLiveSecretKey : okxDemoSecretKey)
                : (string.IsNullOrWhiteSpace(okxLiveSecretKey) ? okxDemoSecretKey : okxLiveSecretKey);
            var okxPassphrase = isDemoMode
                ? (string.IsNullOrWhiteSpace(okxDemoPassphrase) ? okxLivePassphrase : okxDemoPassphrase)
                : (string.IsNullOrWhiteSpace(okxLivePassphrase) ? okxDemoPassphrase : okxLivePassphrase);

            if (!string.IsNullOrWhiteSpace(binanceApiKey))
            {
                environment["TMQ_BINANCE_API_KEY"] = binanceApiKey;
            }

            if (!string.IsNullOrWhiteSpace(binanceApiSecret))
            {
                environment["TMQ_BINANCE_API_SECRET"] = binanceApiSecret;
            }

            if (!string.IsNullOrWhiteSpace(okxApiKey))
            {
                environment["OKX__ApiKey"] = okxApiKey;
            }

            if (!string.IsNullOrWhiteSpace(okxSecretKey))
            {
                environment["OKX__SecretKey"] = okxSecretKey;
            }

            if (!string.IsNullOrWhiteSpace(okxPassphrase))
            {
                environment["OKX__Passphrase"] = okxPassphrase;
            }
        }
        catch
        {
        }

        return environment;
    }

    private string ResolveSharedSettingsPath()
        => Path.Combine(_bundleRoot ?? _workspaceRoot, "exchange_settings.json");

    private string ResolveSharedCredentialsPath()
        => Path.Combine(_bundleRoot ?? _workspaceRoot, "exchange_credentials.dpapi");

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
                return System.Text.Encoding.UTF8.GetString(plain);
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
