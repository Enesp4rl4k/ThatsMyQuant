using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel
{
    private DispatcherTimer? _exchangeHealthTimer;
    private HttpClient? _exchangeHealthHttp;

    private static readonly Brush ExchangeBadgeOkBrush = FreezeBadgeBrush(0x3F, 0xB9, 0x50);
    private static readonly Brush ExchangeBadgeWarnBrush = FreezeBadgeBrush(0xF5, 0x9E, 0x0B);
    private static readonly Brush ExchangeBadgeDownBrush = FreezeBadgeBrush(0xF7, 0x5F, 0x68);
    private static readonly Brush ExchangeBadgeMutedBrush = FreezeBadgeBrush(0x5C, 0x65, 0x70);

    [ObservableProperty] private string _exchangeStatusText = "EXCHANGE: CHECKING…";
    [ObservableProperty] private Brush _exchangeStatusBadgeBrush = ExchangeBadgeMutedBrush;
    [ObservableProperty] private string _exchangeStatusTooltip = "Waiting for first probe from QuantEngine worker.";
    [ObservableProperty] private bool _isExchangeStatusWarningVisible;
    [ObservableProperty] private string _exchangeStatusWarningMessage = "";

    public void StartExchangeHealthPolling()
    {
        if (_exchangeHealthTimer is not null) return;

        var managementUrl = "http://127.0.0.1:18082";
        _exchangeHealthHttp = new HttpClient { BaseAddress = new Uri(managementUrl), Timeout = TimeSpan.FromSeconds(5) };

        _exchangeHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _exchangeHealthTimer.Tick += async (_, _) => await PollExchangeHealthOnceAsync().ConfigureAwait(true);
        _exchangeHealthTimer.Start();

        _ = Task.Run(async () =>
        {
            await Task.Delay(2000).ConfigureAwait(false);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                await PollExchangeHealthOnceAsync().ConfigureAwait(true));
        });
    }

    public void StopExchangeHealthPolling()
    {
        _exchangeHealthTimer?.Stop();
        _exchangeHealthTimer = null;
        _exchangeHealthHttp?.Dispose();
        _exchangeHealthHttp = null;
    }

    private async Task PollExchangeHealthOnceAsync()
    {
        if (_exchangeHealthHttp is null) return;

        try
        {
            using var resp = await _exchangeHealthHttp.GetAsync("/exchange/health").ConfigureAwait(true);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string venue = root.TryGetProperty("venue", out var v) ? v.GetString() ?? "?" : "?";
            bool credsConfigured = root.TryGetProperty("credentialsConfigured", out var c) && c.GetBoolean();
            bool isConnected = root.TryGetProperty("isConnected", out var co) && co.GetBoolean();
            bool isDemo = root.TryGetProperty("isDemoMode", out var d) && d.GetBoolean();
            string? lastError = root.TryGetProperty("lastError", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            int consecFail = root.TryGetProperty("consecutiveFailures", out var cf) ? cf.GetInt32() : 0;
            decimal? balance = root.TryGetProperty("balance", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDecimal() : null;
            DateTime? lastSuccess = root.TryGetProperty("lastSuccessUtc", out var ls) && ls.ValueKind == JsonValueKind.String && DateTime.TryParse(ls.GetString(), out var parsedLs) ? parsedLs : null;

            ApplyExchangeHealth(venue, credsConfigured, isConnected, isDemo, lastError, consecFail, balance, lastSuccess);
        }
        catch (Exception ex)
        {
            // Worker offline or management port not reachable.
            ExchangeStatusText = "EXCHANGE: WORKER OFFLINE";
            ExchangeStatusBadgeBrush = ExchangeBadgeMutedBrush;
            ExchangeStatusTooltip = $"Cannot reach worker at 127.0.0.1:18082. {ex.Message}";
            IsExchangeStatusWarningVisible = true;
            ExchangeStatusWarningMessage = "Worker process is not running or Management:Url is unreachable. Start Launch-PaperTrade.bat.";
        }
    }

    private void ApplyExchangeHealth(
        string venue, bool credsConfigured, bool isConnected, bool isDemo,
        string? lastError, int consecFail, decimal? balance, DateTime? lastSuccess)
    {
        var demoLabel = isDemo ? " DEMO" : "";
        if (!credsConfigured)
        {
            ExchangeStatusText = $"{venue}: ❌ NO CREDENTIALS";
            ExchangeStatusBadgeBrush = ExchangeBadgeDownBrush;
            ExchangeStatusTooltip = "Exchange credentials not configured in appsettings.Paper.json. " +
                                    "Fill OKX.ApiKey/SecretKey/Passphrase and restart the worker.";
            IsExchangeStatusWarningVisible = true;
            ExchangeStatusWarningMessage =
                "⚠ EXCHANGE CREDENTIALS MISSING — paper/live trades cannot be placed. " +
                "Open appsettings.Paper.json, fill OKX keys, then restart Launch-PaperTrade.bat.";
            return;
        }

        if (!isConnected)
        {
            ExchangeStatusText = $"{venue}{demoLabel}: 🚨 AUTH FAILED";
            ExchangeStatusBadgeBrush = ExchangeBadgeDownBrush;
            ExchangeStatusTooltip = $"Last error: {lastError ?? "unknown"} · consec failures: {consecFail}";
            IsExchangeStatusWarningVisible = true;
            ExchangeStatusWarningMessage =
                $"🚨 {venue} API INACTIVE — credentials rejected or venue unreachable. " +
                $"Last error: {lastError ?? "unknown"}. Verify OKX demo keys are correct and IP not restricted.";
            return;
        }

        // Connected.
        var lastSuccessLabel = lastSuccess is null ? "—" : lastSuccess.Value.ToLocalTime().ToString("HH:mm:ss");
        var balanceLabel = balance is null ? "—" : $"{balance:N2} USDT";
        ExchangeStatusText = $"{venue}{demoLabel}: ✓ ACTIVE · {balanceLabel}";
        ExchangeStatusBadgeBrush = isDemo ? ExchangeBadgeWarnBrush : ExchangeBadgeOkBrush;
        ExchangeStatusTooltip = $"Last success: {lastSuccessLabel} local · balance: {balanceLabel}";
        IsExchangeStatusWarningVisible = false;
        ExchangeStatusWarningMessage = "";
    }

    private static Brush FreezeBadgeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }
}
