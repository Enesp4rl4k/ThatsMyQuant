using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThatsMyQuant.Contracts.Exceptions;
using ThatsMyQuant.Contracts.Helpers;
using ThatsMyQuant.Dashboard.ViewModels.SubViewModels;
using ThatsMyQuant.Infrastructure.Services;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel
{
    private static readonly string _credentialsPath =
        ExchangeSettingsViewModel.ResolveSharedConfigPath("exchange_credentials.dpapi");

    private static readonly ILogger<MainViewModel> _syncLogger =
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger<MainViewModel>();

    private bool CanSyncWatchlist() => !IsSyncingWatchlist;

    [RelayCommand(CanExecute = nameof(CanSyncWatchlist))]
    private async Task SyncWatchlist()
    {
        IsSyncingWatchlist = true;
        SyncStatusMessage = "Syncing with exchanges...";
        SyncWatchlistCommand.NotifyCanExecuteChanged();

        try
        {
            var okxSymbols = new List<string>();
            var binanceSymbols = new List<string>();
            string? okxError = null;
            string? binanceError = null;
            var exchangeMode = ExchangeSettings.SelectedExchangeMode;

            if (exchangeMode.Equals("OKX", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var (okxKey, okxSecret, okxPassphrase) = ResolveOkxCredentials();
                    var okxConfig = new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["OKX:ApiKey"] = okxKey,
                            ["OKX:SecretKey"] = okxSecret,
                            ["OKX:Passphrase"] = okxPassphrase,
                            ["OKX:IsDemoMode"] = ExchangeSettings.IsDemoMode.ToString().ToLowerInvariant()
                        })
                        .Build();

                    using var okxHttp = new HttpClient();
                    var okxLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<OkxExecutionService>();
                    using var okxService = new OkxExecutionService(okxLogger, okxHttp, okxConfig);
                    okxSymbols = await okxService.GetFavoritesAsync();
                }
                catch (ExchangeSyncException ex)
                {
                    okxError = ex.ExchangeName;
                    _syncLogger.LogWarning(ex, "Exchange sync failed: {Exchange}", ex.ExchangeName);
                }
                catch (Exception ex)
                {
                    okxError = "OKX";
                    _syncLogger.LogWarning(ex, "Exchange sync failed: OKX credentials unavailable");
                }
            }
            else if (exchangeMode.Equals("Binance", StringComparison.OrdinalIgnoreCase) ||
                     exchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var (binanceKey, binanceSecret) = ResolveBinanceCredentials();
                    using var binanceHttp = new HttpClient
                    {
                        BaseAddress = exchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase)
                            ? new Uri("https://testnet.binance.vision")
                            : new Uri("https://api.binance.com")
                    };
                    var binanceLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<BinanceExecutionService>();

                    using var keyVault = new ThatsMyQuant.Application.Security.ApiVault();
                    keyVault.SecureInboundKey(binanceKey);
                    using var secretVault = new ThatsMyQuant.Application.Security.ApiVault();
                    secretVault.SecureInboundKey(binanceSecret);

                    using var binanceService = new BinanceExecutionService(
                        binanceLogger,
                        binanceHttp,
                        keyVault,
                        secretVault,
                        streamingService: null!);
                    binanceSymbols = await binanceService.GetFavoritesAsync();
                }
                catch (ExchangeSyncException ex)
                {
                    binanceError = ex.ExchangeName;
                    _syncLogger.LogWarning(ex, "Exchange sync failed: {Exchange}", ex.ExchangeName);
                }
                catch (Exception ex)
                {
                    binanceError = "Binance";
                    _syncLogger.LogWarning(ex, "Exchange sync failed: Binance credentials unavailable");
                }
            }
            else
            {
                SyncStatusMessage = "Watchlist sync requires Binance or OKX mode.";
                return;
            }

            var existingSymbols = WatchlistItems
                .Select(w => w.Symbol)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var newSymbols = okxSymbols
                .Concat(binanceSymbols)
                .Where(SymbolNormalizer.IsValid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(s => !existingSymbols.Contains(s))
                .Take(24)
                .ToList();

            foreach (var symbol in newSymbols)
            {
                var item = new WatchlistItemViewModel
                {
                    Symbol = symbol,
                    CandidateSummary = "Imported from exchange"
                };

                WatchlistItems.Add(item);

                try
                {
                    var price = await _marketDataService.GetLivePriceAsync(symbol, "Crypto");
                    if (price > 0m)
                    {
                        item.Price = price;
                    }
                }
                catch
                {
                }
            }

            foreach (var symbol in newSymbols.Take(8))
            {
                try
                {
                    await AnalyzeSymbol(symbol, "Crypto", MarketOverview.SelectedTimeframe);
                }
                catch (Exception ex)
                {
                    AddSystemLog($"SYNC: AI analysis failed for {symbol}: {ex.Message}");
                }
            }

            SyncStatusMessage = BuildSyncStatusMessage(okxError, binanceError, newSymbols.Count);
        }
        finally
        {
            IsSyncingWatchlist = false;
            SyncWatchlistCommand.NotifyCanExecuteChanged();
        }
    }

    internal static string BuildSyncStatusMessage(string? okxError, string? binanceError, int n)
    {
        if (okxError != null && binanceError != null)
        {
            return "Sync failed: unable to reach OKX and Binance.";
        }

        if (okxError != null || binanceError != null)
        {
            var failedName = okxError ?? binanceError;
            return $"Partial sync: {failedName} unavailable. {n} symbols imported.";
        }

        return $"Sync complete. {n} new symbols added.";
    }

    private (string ApiKey, string SecretKey, string Passphrase) ResolveOkxCredentials()
    {
        if (!string.IsNullOrWhiteSpace(ExchangeSettings.OkxApiKey) &&
            !string.IsNullOrWhiteSpace(ExchangeSettings.OkxSecretKey) &&
            !string.IsNullOrWhiteSpace(ExchangeSettings.OkxPassphrase))
        {
            return (ExchangeSettings.OkxApiKey, ExchangeSettings.OkxSecretKey, ExchangeSettings.OkxPassphrase);
        }

        return LoadOkxCredentials();
    }

    private (string ApiKey, string SecretKey) ResolveBinanceCredentials()
    {
        if (!string.IsNullOrWhiteSpace(ExchangeSettings.BinanceApiKey) &&
            !string.IsNullOrWhiteSpace(ExchangeSettings.BinanceApiSecret))
        {
            return (ExchangeSettings.BinanceApiKey, ExchangeSettings.BinanceApiSecret);
        }

        return LoadBinanceCredentials();
    }

    private static (string ApiKey, string SecretKey, string Passphrase) LoadOkxCredentials()
    {
        if (!File.Exists(_credentialsPath))
        {
            throw new InvalidOperationException("Credentials file not found.");
        }

        string json = File.ReadAllText(_credentialsPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return (
            Unprotect(root.GetProperty("ApiKey").GetString() ?? string.Empty),
            Unprotect(root.GetProperty("SecretKey").GetString() ?? string.Empty),
            Unprotect(root.GetProperty("Passphrase").GetString() ?? string.Empty));
    }

    private static (string ApiKey, string SecretKey) LoadBinanceCredentials()
    {
        if (!File.Exists(_credentialsPath))
        {
            throw new InvalidOperationException("Credentials file not found.");
        }

        string json = File.ReadAllText(_credentialsPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return (
            Unprotect(root.GetProperty("BinanceApiKey").GetString() ?? string.Empty),
            Unprotect(root.GetProperty("BinanceApiSecret").GetString() ?? string.Empty));
    }

    private static string Unprotect(string base64)
    {
        byte[] encrypted = Convert.FromBase64String(base64);
        byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        string value = Encoding.UTF8.GetString(plain);
        Array.Clear(plain, 0, plain.Length);
        return value;
    }
}
