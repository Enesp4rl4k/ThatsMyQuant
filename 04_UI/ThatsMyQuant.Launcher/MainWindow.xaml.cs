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

    public MainWindow()
    {
        InitializeComponent();

        _workspaceRoot = FindWorkspaceRoot();
        _bundleRoot = FindBundleRoot();
        _processManager = new LauncherProcessManager(_workspaceRoot, _bundleRoot);
        _processManager.OutputReceived += AppendLog;

        var sharedApiUrls = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development"
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
            foreach (var service in _services.Where(status => status.Definition != _dashboardDefinition))
            {
                await StartServiceAsync(service);
            }
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

        environment["TMQ_EXCHANGE_MODE"] = ReadExchangeMode();

        foreach (var pair in ReadCredentialEnvironment())
        {
            environment[pair.Key] = pair.Value;
        }

        return definition with { Environment = environment };
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

        if (File.Exists(settingsPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (document.RootElement.TryGetProperty("OKX", out var okx) &&
                    okx.TryGetProperty("IsDemoMode", out var demoMode))
                {
                    environment["OKX__IsDemoMode"] = demoMode.GetBoolean() ? "true" : "false";
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
            var okxApiKey = TryUnprotect(root, "ApiKey");
            var okxSecretKey = TryUnprotect(root, "SecretKey");
            var okxPassphrase = TryUnprotect(root, "Passphrase");

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
        => Path.Combine(_bundleRoot ?? AppContext.BaseDirectory, "exchange_settings.json");

    private string ResolveSharedCredentialsPath()
        => Path.Combine(_bundleRoot ?? AppContext.BaseDirectory, "exchange_credentials.dpapi");

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
