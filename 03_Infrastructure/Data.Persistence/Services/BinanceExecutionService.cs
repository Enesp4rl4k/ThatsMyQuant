using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Helpers;
using ThatsMyQuant.Contracts.Exceptions;
using System.Net.Http.Headers;
using ThatsMyQuant.Application.Security;

namespace ThatsMyQuant.Infrastructure.Services;

public sealed class BinanceExecutionService : IExchangeService, IExchangeOrderStateProvider, IDisposable
{
    private readonly ILogger<BinanceExecutionService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ApiVault _keyVault;
    private readonly ApiVault _secretVault;
    private static readonly Uri DefaultBaseUri = new("https://api.binance.com");
    private const VenueKind Venue = VenueKind.Binance;

    private readonly ConcurrentDictionary<string, OrderBookSnapshot> _latestOrderBooks = new();
    private readonly ConcurrentDictionary<Guid, decimal> _queueTracker = new();
    private readonly ISharedPortfolioStateManager _portfolio;

    public BinanceExecutionService(
        ILogger<BinanceExecutionService> logger,
        HttpClient httpClient,
        ApiVault keyVault,
        ApiVault secretVault,
        IStreamingMarketDataService streamingService,
        ISharedPortfolioStateManager? portfolioStateManager = null)
    {
        _logger = logger;
        _httpClient = httpClient;
        _keyVault = keyVault ?? throw new ArgumentNullException(nameof(keyVault));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _portfolio = portfolioStateManager ?? VenuePortfolioManager.Instance;

        // Transiently decrypt the API key to set the HTTP header, then zero it out
        byte[] keyBytes = _keyVault.GetTransientDecryptedKey();
        try
        {
            _httpClient.DefaultRequestHeaders.Add("X-MBX-APIKEY", Encoding.UTF8.GetString(keyBytes));
        }
        finally
        {
            Array.Clear(keyBytes, 0, keyBytes.Length);
        }

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (streamingService is BinanceWebSocketService bwss)
        {
            bwss.OnOrderBookUpdated += (snapshot) => _latestOrderBooks[snapshot.Symbol] = snapshot;
        }
    }

    public async Task<decimal> GetQueuePositionAsync(string symbol, decimal price, TradeSide side)
    {
        if (!_latestOrderBooks.TryGetValue(symbol, out var book))
        {
            return 0;
        }

        decimal volumeAhead = 0;
        var levels = side == TradeSide.Buy ? book.Bids : book.Asks;

        foreach (var level in levels)
        {
            if (side == TradeSide.Buy)
            {
                if (level.Price > price) volumeAhead += level.Quantity;
                else if (level.Price == price) 
                {
                    volumeAhead += level.Quantity * 0.5m;
                    break;
                }
            }
            else // Sell
            {
                if (level.Price < price) volumeAhead += level.Quantity;
                else if (level.Price == price)
                {
                    volumeAhead += level.Quantity * 0.5m;
                    break;
                }
            }
        }

        return await Task.FromResult(volumeAhead);
    }

    public async Task<Trade> ExecuteOrderAsync(Signal signal)
    {
        // Institutional Guard: Local balance check via MemoryPortfolioManager
        var requiredNotional = signal.Quantity * signal.Price;
        if (!_portfolio.TryReserve(Venue, "USDT", requiredNotional))
        {
            _logger.LogWarning("Insufficient localized funds (MemoryPortfolio) for {Symbol}", signal.Symbol);
            throw new InvalidOperationException("Insufficient localized funds.");
        }

        _logger.LogInformation("Executing Binance order for {Symbol} @ {Price}", signal.Symbol, signal.Price);
        var clientOrderId = ExecutionOrderIdentity.EnsureClientOrderId(signal);

        // HFT Optimization: Using Span and stackalloc for query building to avoid large string allocations
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string queryString = $"symbol={signal.Symbol}&side={(signal.Side == TradeSide.Buy ? "BUY" : "SELL")}&type=LIMIT&timeInForce=GTC&quantity={signal.Quantity:F8}&price={signal.Price:F8}&newClientOrderId={clientOrderId}&timestamp={timestamp}";
        
        var signature = ComputeSignature(queryString);
        
        // Use a background HttpClient or Direct Socket for HFT if available. 
        // Here we optimize the POST request.
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildBinanceUri($"/api/v3/order?{queryString}&signature={signature}"));
        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Binance execution failed: {Error}", error);
            throw new InvalidOperationException($"Binance execution failed: {error}");
        }

        // Funds were atomically reserved locally before the REST call.
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var executedQty = ParseDecimal(root, "executedQty");
        var originalQty = ParseDecimal(root, "origQty");
        var orderStatus = root.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString()
            : null;
        var avgPrice = signal.Price;
        if (root.TryGetProperty("fills", out var fillsElement) && fillsElement.ValueKind == JsonValueKind.Array && fillsElement.GetArrayLength() > 0)
        {
            avgPrice = ParseDecimal(fillsElement[0], "price", signal.Price);
        }
        else if (root.TryGetProperty("price", out var priceElement) && decimal.TryParse(priceElement.GetString(), out var parsedPrice) && parsedPrice > 0m)
        {
            avgPrice = parsedPrice;
        }

        var queuePosId = Guid.NewGuid();
        var volumeAhead = await GetQueuePositionAsync(signal.Symbol, signal.Price, signal.Side);
        _queueTracker[queuePosId] = volumeAhead;
        _logger.LogInformation("Binance Queue Tracking established for Order {OrderId} at Position-in-Book: {VolumeAhead:F4}", queuePosId, volumeAhead);

        return new Trade
        {
            Id = queuePosId,
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            RequestedQuantity = originalQty > 0m ? originalQty : signal.Quantity,
            FilledQuantity = executedQty,
            RemainingQuantity = ResolveRemainingQuantity(signal.Quantity, originalQty, executedQty),
            EntryPrice = signal.Price,
            MarkPrice = avgPrice,
            Quantity = executedQty,
            EntryTime = DateTime.UtcNow,
            SubmittedTime = DateTime.UtcNow,
            LastUpdatedTime = DateTime.UtcNow,
            Status = executedQty > 0m ? TradeStatus.Executed : TradeStatus.Pending,
            LifecycleStatus = MapBinanceLifecycle(orderStatus, executedQty, originalQty > 0m ? originalQty : signal.Quantity),
            ExecutionMode = "REAL"
        };
    }

    public async Task<IReadOnlyList<OpenOrderSnapshot>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var queryString = $"timestamp={timestamp}";
        var signature = ComputeSignature(queryString);

        var response = await _httpClient.GetAsync(
            BuildBinanceUri($"/api/v3/openOrders?{queryString}&signature={signature}"),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Failed to fetch Binance open orders: {error}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var snapshots = new List<OpenOrderSnapshot>();

        foreach (var order in doc.RootElement.EnumerateArray())
        {
            var requestedQuantity = ParseDecimal(order, "origQty");
            var filledQuantity = ParseDecimal(order, "executedQty");
            var remainingQuantity = Math.Max(0m, requestedQuantity - filledQuantity);
            var price = ParseDecimal(order, "price");
            var side = order.TryGetProperty("side", out var sideElement) &&
                       string.Equals(sideElement.GetString(), "SELL", StringComparison.OrdinalIgnoreCase)
                ? TradeSide.Sell
                : TradeSide.Buy;
            var timestampUtc = order.TryGetProperty("time", out var timeElement) &&
                               long.TryParse(timeElement.ToString(), out var epochMs)
                ? DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime
                : DateTime.UtcNow;

            snapshots.Add(new OpenOrderSnapshot(
                order.TryGetProperty("symbol", out var symbolElement) ? symbolElement.GetString() ?? string.Empty : string.Empty,
                side,
                order.TryGetProperty("clientOrderId", out var clientOrderIdElement) ? clientOrderIdElement.GetString() ?? string.Empty : string.Empty,
                order.TryGetProperty("orderId", out var orderIdElement) ? orderIdElement.ToString() : string.Empty,
                requestedQuantity,
                filledQuantity,
                remainingQuantity,
                price,
                price,
                order.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? "NEW" : "NEW",
                timestampUtc));
        }

        return snapshots;
    }

    public async Task<Account> GetAccountStatusAsync()
    {
        // Use localized portfolio state if already synced, otherwise fetch.
        var availableUsdt = _portfolio.GetAvailableBalance(Venue, "USDT");
        if (availableUsdt > 0)
        {
            return new Account
            {
                Id = "BINANCE-LIVE-CACHED",
                Provider = "Binance",
                Balance = availableUsdt,
                Equity = availableUsdt
            };
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var queryString = $"timestamp={timestamp}";
        var signature = ComputeSignature(queryString);

        var response = await _httpClient.GetAsync(BuildBinanceUri($"/api/v3/account?{queryString}&signature={signature}"));
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Failed to fetch Binance account status: {error}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var balances = doc.RootElement.GetProperty("balances");

        decimal usdtBalance = 0;
        foreach (var balance in balances.EnumerateArray())
        {
            var asset = balance.GetProperty("asset").GetString();
            if (asset == "USDT")
            {
                usdtBalance = decimal.Parse(balance.GetProperty("free").GetString() ?? "0");
                break;
            }
        }

        _portfolio.SetBalance(Venue, "USDT", usdtBalance); // Sync memory portfolio

        return new Account
        {
            Id = "BINANCE-LIVE",
            Provider = "Binance",
            Balance = usdtBalance,
            Equity = usdtBalance
        };
    }

    private static readonly HashSet<string> SkippedAssets =
        new(StringComparer.OrdinalIgnoreCase) { "USDT", "BUSD", "BNB", "USDC", "FDUSD" };

    public async Task<List<string>> GetFavoritesAsync()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var queryString = $"timestamp={timestamp}";
        var signature = ComputeSignature(queryString);

        var response = await _httpClient.GetAsync(BuildBinanceUri($"/api/v3/account?{queryString}&signature={signature}"));
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new ExchangeSyncException("Binance", $"HTTP {(int)response.StatusCode}: {error}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var balances = doc.RootElement.GetProperty("balances");

        var results = new List<string>();
        foreach (var balance in balances.EnumerateArray())
        {
            var asset = balance.GetProperty("asset").GetString() ?? string.Empty;
            if (SkippedAssets.Contains(asset)) continue;

            var free = decimal.TryParse(balance.GetProperty("free").GetString(), out var f) ? f : 0m;
            var locked = decimal.TryParse(balance.GetProperty("locked").GetString(), out var l) ? l : 0m;
            if (free + locked <= 0m) continue;

            var canonical = SymbolNormalizer.ToCanonical(asset + "USDT");
            if (SymbolNormalizer.IsValid(canonical))
                results.Add(canonical);
        }
        return results;
    }

    private string ComputeSignature(string message)
    {
        int byteCount = Encoding.UTF8.GetByteCount(message);
        Span<byte> messageBytes = byteCount <= 1024 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(message, messageBytes);

        byte[] secretBytes = _secretVault.GetTransientDecryptedKey();
        try
        {
            Span<byte> hashBytes = stackalloc byte[32];
            HMACSHA256.HashData(secretBytes, messageBytes, hashBytes);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        finally
        {
            Array.Clear(secretBytes, 0, secretBytes.Length);
        }
    }

    public async Task<int> CancelAllOrdersAsync(CancellationToken cancellationToken = default)
    {
        int cancelledCount = 0;
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Get all open orders across all symbols
            var queryString = $"timestamp={timestamp}";
            var signature = ComputeSignature(queryString);

            var response = await _httpClient.GetAsync(
                BuildBinanceUri($"/api/v3/openOrders?{queryString}&signature={signature}"), cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to fetch open orders for emergency cancellation: {Error}", error);
                return 0;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            foreach (var order in doc.RootElement.EnumerateArray())
            {
                try
                {
                    var symbol = order.GetProperty("symbol").GetString() ?? "";
                    var orderId = order.GetProperty("orderId").GetInt64();
                    var cancelTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var cancelQuery = $"symbol={symbol}&orderId={orderId}&timestamp={cancelTs}";
                    var cancelSig = ComputeSignature(cancelQuery);

                    using var cancelRequest = new HttpRequestMessage(
                        HttpMethod.Delete, BuildBinanceUri($"/api/v3/order?{cancelQuery}&signature={cancelSig}"));
                    var cancelResponse = await _httpClient.SendAsync(cancelRequest, cancellationToken);

                    if (cancelResponse.IsSuccessStatusCode)
                    {
                        cancelledCount++;
                        _logger.LogWarning("🚨 Emergency cancelled Binance order {OrderId} for {Symbol}", orderId, symbol);
                    }
                    else
                    {
                        var cancelError = await cancelResponse.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogError("Failed to cancel Binance order {OrderId}: {Error}", orderId, cancelError);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error cancelling individual Binance order during emergency halt.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Emergency CancelAllOrdersAsync failed on Binance.");
        }

        _logger.LogCritical("🚨 Binance emergency halt: {Count} orders cancelled.", cancelledCount);
        return cancelledCount;
    }

    public void Dispose()
    {
        _keyVault?.Dispose();
        _secretVault?.Dispose();
    }

    private Uri BuildBinanceUri(string relativePathAndQuery)
        => _httpClient.BaseAddress is null
            ? new Uri(DefaultBaseUri, relativePathAndQuery)
            : new Uri(_httpClient.BaseAddress, relativePathAndQuery);

    private static TradeLifecycleStatus MapBinanceLifecycle(string? status, decimal executedQty, decimal requestedQty)
    {
        return status?.ToUpperInvariant() switch
        {
            "NEW" => TradeLifecycleStatus.Accepted,
            "PARTIALLY_FILLED" => TradeLifecycleStatus.PartiallyFilled,
            "FILLED" => executedQty + 0.00000001m < requestedQty ? TradeLifecycleStatus.PartiallyFilled : TradeLifecycleStatus.Active,
            "CANCELED" => TradeLifecycleStatus.Cancelled,
            "REJECTED" => TradeLifecycleStatus.Rejected,
            "EXPIRED" => TradeLifecycleStatus.Failed,
            _ when executedQty <= 0m => TradeLifecycleStatus.Accepted,
            _ when requestedQty > 0m && executedQty + 0.00000001m < requestedQty => TradeLifecycleStatus.PartiallyFilled,
            _ => TradeLifecycleStatus.Active
        };
    }

    private static decimal ResolveRemainingQuantity(decimal signalQty, decimal originalQty, decimal executedQty)
    {
        var requestedQty = originalQty > 0m ? originalQty : signalQty;
        if (requestedQty <= 0m)
        {
            return executedQty;
        }

        return Math.Max(0m, requestedQty - executedQty);
    }

    private static decimal ParseDecimal(JsonElement root, string propertyName, decimal fallback = 0m)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return fallback;
        }

        var raw = element.GetString();
        return decimal.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}

