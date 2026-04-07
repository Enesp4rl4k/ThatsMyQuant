using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThatsMyQuant.Contracts.Exceptions;
using ThatsMyQuant.Contracts.Helpers;
using ThatsMyQuant.Infrastructure.Services;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel
{
    private static readonly string _credentialsPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "exchange_credentials.dpapi");

    private static readonly ILogger<MainViewModel> _syncLogger =
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger<MainViewModel>();

    // CanExecute guard — Requirements: 3.5
    private bool CanSyncWatchlist() => !IsSyncingWatchlist;

    /// <summary>
    /// Syncs the watchlist with OKX and Binance favorites.
    /// Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7, 4.1–4.3, 5.1–5.4, 6.1–6.4, 7.1–7.6, 8.1–8.4
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSyncWatchlist))]
    private async Task SyncWatchlist()
    {
        IsSyncingWatchlist = true;
        SyncStatusMessage = "Syncing with exchanges...";

        var okxSymbols = new List<string>();
        var binanceSymbols = new List<string>();
        string? okxError = null;
        string? binanceError = null;
        var exchangeMode = ExchangeSettings.SelectedExchangeMode;

        if (exchangeMode.Equals("OKX", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var (okxKey, okxSecret, okxPassphrase) = LoadOkxCredentials();
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
                var (binanceKey, binanceSecret) = LoadBinanceCredentials();
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
                    binanceLogger, binanceHttp, keyVault, secretVault,
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
            IsSyncingWatchlist = false;
            return;
        }

        // --- Merge & deduplicate (Requirements: 4.1–4.5) ---
        var existingSymbols = WatchlistItems
            .Select(w => w.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newSymbols = okxSymbols
            .Concat(binanceSymbols)
            .Where(SymbolNormalizer.IsValid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(s => !existingSymbols.Contains(s))
            .ToList();

        // --- Append new items and enrich with live prices (Requirements: 5.1–5.4) ---
        var newItems = new List<WatchlistItemViewModel>(newSymbols.Count);
        foreach (var symbol in newSymbols)
        {
            var item = new WatchlistItemViewModel { Symbol = symbol };
            WatchlistItems.Add(item);
            newItems.Add(item);

            try
            {
                var price = await _marketDataService.GetLivePriceAsync(symbol, "CRYPTO");
                if (price > 0) item.Price = price;
            }
            catch
            {
                // Leave Price at 0, continue (Requirement 5.3)
            }
        }

        // --- Trigger AI analysis for each new symbol (Requirements: 6.1–6.4) ---
        foreach (var symbol in newSymbols)
        {
            try
            {
                await AnalyzeSymbol(symbol, "CRYPTO", MarketOverview.SelectedTimeframe);
            }
            catch (Exception ex)
            {
                AddSystemLog($"SYNC: AI analysis failed for {symbol}: {ex.Message}");
            }
        }

        // --- Status message (Requirements: 7.3–7.5) ---
        SyncStatusMessage = BuildSyncStatusMessage(okxError, binanceError, newSymbols.Count);
        IsSyncingWatchlist = false;
    }

    /// <summary>
    /// Builds the sync status message based on exchange outcomes and symbol count.
    /// Requirements: 7.3, 7.4, 7.5
    /// </summary>
    internal static string BuildSyncStatusMessage(string? okxError, string? binanceError, int n)
    {
        if (okxError != null && binanceError != null)
            return "Sync failed: unable to reach OKX and Binance.";

        if (okxError != null || binanceError != null)
        {
            var failedName = okxError ?? binanceError;
            return $"Partial sync: {failedName} unavailable. {n} symbols imported.";
        }

        return $"Sync complete. {n} new symbols added.";
    }

    /// <summary>
    /// Loads OKX credentials from exchange_credentials.dpapi.
    /// Returns (ApiKey, SecretKey, Passphrase). Throws if file missing or decryption fails.
    /// Requirements: 8.1, 8.3
    /// </summary>
    private static (string ApiKey, string SecretKey, string Passphrase) LoadOkxCredentials()
    {
        if (!File.Exists(_credentialsPath))
            throw new InvalidOperationException("Credentials file not found.");

        string json = File.ReadAllText(_credentialsPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return (
            Unprotect(root.GetProperty("ApiKey").GetString() ?? ""),
            Unprotect(root.GetProperty("SecretKey").GetString() ?? ""),
            Unprotect(root.GetProperty("Passphrase").GetString() ?? "")
        );
    }

    /// <summary>
    /// Loads Binance credentials from exchange_credentials.dpapi.
    /// Returns (ApiKey, SecretKey). Throws if file missing or decryption fails.
    /// Requirements: 8.2, 8.3
    /// </summary>
    private static (string ApiKey, string SecretKey) LoadBinanceCredentials()
    {
        if (!File.Exists(_credentialsPath))
            throw new InvalidOperationException("Credentials file not found.");

        string json = File.ReadAllText(_credentialsPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return (
            Unprotect(root.GetProperty("BinanceApiKey").GetString() ?? ""),
            Unprotect(root.GetProperty("BinanceApiSecret").GetString() ?? "")
        );
    }

    /// <summary>
    /// Decrypts a Base64-encoded DPAPI-protected value using CurrentUser scope.
    /// Requirements: 8.1, 8.2, 8.4
    /// </summary>
    private static string Unprotect(string base64)
    {
        byte[] encrypted = Convert.FromBase64String(base64);
        byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        string value = Encoding.UTF8.GetString(plain);
        Array.Clear(plain, 0, plain.Length);  // Req 8.4
        return value;
    }
}
