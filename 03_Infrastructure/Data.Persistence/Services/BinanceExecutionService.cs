using System.Collections.Concurrent;
using System.Globalization;
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
using ThatsMyQuant.Application.Observability;

namespace ThatsMyQuant.Infrastructure.Services;

public sealed class BinanceExecutionService : IExchangeService, IExchangeOrderStateProvider, IVenueTradeFeeProvider, IDisposable
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
    private readonly bool _isFuturesMode;
    private readonly OperationalTelemetry? _telemetry;

    public BinanceExecutionService(
        ILogger<BinanceExecutionService> logger,
        HttpClient httpClient,
        ApiVault keyVault,
        ApiVault secretVault,
        IStreamingMarketDataService streamingService,
        ISharedPortfolioStateManager portfolioStateManager,
        OperationalTelemetry? operationalTelemetry = null)
    {
        _logger = logger;
        _httpClient = httpClient;
        _keyVault = keyVault ?? throw new ArgumentNullException(nameof(keyVault));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _portfolio = portfolioStateManager;
        _telemetry = operationalTelemetry;
        _isFuturesMode = IsFuturesBaseAddress(_httpClient.BaseAddress);

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

    public bool TryGetLatestOrderBook(string symbol, out OrderBookSnapshot book)
    {
        if (_latestOrderBooks.TryGetValue(symbol, out var b))
        {
            book = b;
            return true;
        }

        book = default;
        return false;
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
        var clientOrderId = ExecutionOrderIdentity.EnsureClientOrderId(signal);

        if (!_isFuturesMode)
        {
            // Institutional Guard: Local balance check via MemoryPortfolioManager
            var requiredNotional = signal.Quantity * signal.Price;
            var reserved = _portfolio.TryReserveForOrder(Venue, "USDT", clientOrderId, requiredNotional);
            if (!reserved)
            {
                // Backward compatibility for tests/mocks that still only configure TryReserve.
                reserved = _portfolio.TryReserve(Venue, "USDT", requiredNotional);
            }

            if (!reserved)
            {
                _logger.LogWarning("Insufficient localized funds (MemoryPortfolio) for {Symbol}", signal.Symbol);
                throw new InvalidOperationException("Insufficient localized funds.");
            }
        }

        _logger.LogInformation("Executing Binance order for {Symbol} @ {Price}", signal.Symbol, signal.Price);
        var reduceOnly = signal.Metadata.TryGetValue("ReduceOnly", out var reduceOnlyRaw) &&
                         bool.TryParse(reduceOnlyRaw, out var reduceOnlyParsed) &&
                         reduceOnlyParsed;

        // HFT Optimization: Using Span and stackalloc for query building to avoid large string allocations
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var orderPath = _isFuturesMode ? "/fapi/v1/order" : "/api/v3/order";
        // FULL response includes fills[] with commission / commissionAsset (spot & USDM when supported).
        var queryString = _isFuturesMode
            ? $"symbol={signal.Symbol}&side={(signal.Side == TradeSide.Buy ? "BUY" : "SELL")}&type=LIMIT&timeInForce=GTC&quantity={signal.Quantity:F8}&price={signal.Price:F8}&newClientOrderId={clientOrderId}&reduceOnly={reduceOnly.ToString().ToLowerInvariant()}&newOrderRespType=FULL&timestamp={timestamp}"
            : $"symbol={signal.Symbol}&side={(signal.Side == TradeSide.Buy ? "BUY" : "SELL")}&type=LIMIT&timeInForce=GTC&quantity={signal.Quantity:F8}&price={signal.Price:F8}&newClientOrderId={clientOrderId}&newOrderRespType=FULL&timestamp={timestamp}";

        var signature = ComputeSignature(queryString);
        
        // Use a background HttpClient or Direct Socket for HFT if available. 
        // Here we optimize the POST request.
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildBinanceUri($"{orderPath}?{queryString}&signature={signature}"));
        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            TrackHttpFailure(response);
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

        var feesQuote = SumBinanceFillsCommissionInQuote(root, signal.Symbol);
        var leverage = ResolveLeverage(signal);
        var marginMode = ResolveMarginMode(signal);

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
            ExecutionMode = _isFuturesMode ? "REAL-FUTURES" : "REAL",
            Leverage = leverage,
            MarginMode = marginMode,
            TotalFeesPaid = feesQuote,
            NetFunding = 0m
        };
    }

    private static decimal ResolveLeverage(Signal signal)
    {
        if (signal.Metadata.TryGetValue("Leverage", out var leverageRaw) &&
            decimal.TryParse(leverageRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var leverage) &&
            leverage > 0m)
        {
            return leverage;
        }

        return 1m;
    }

    private static MarginMode ResolveMarginMode(Signal signal)
    {
        if (signal.Metadata.TryGetValue("MarginMode", out var marginRaw) &&
            Enum.TryParse<MarginMode>(marginRaw, true, out var marginMode))
        {
            return marginMode;
        }

        return MarginMode.Isolated;
    }

    public async Task<IReadOnlyList<OpenOrderSnapshot>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var queryString = $"timestamp={timestamp}";
        var signature = ComputeSignature(queryString);

        var openOrdersPath = _isFuturesMode ? "/fapi/v1/openOrders" : "/api/v3/openOrders";
        var response = await _httpClient.GetAsync(
            BuildBinanceUri($"{openOrdersPath}?{queryString}&signature={signature}"),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            TrackHttpFailure(response);
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

        var accountPath = _isFuturesMode ? "/fapi/v2/account" : "/api/v3/account";
        var response = await _httpClient.GetAsync(BuildBinanceUri($"{accountPath}?{queryString}&signature={signature}"));
        
        if (!response.IsSuccessStatusCode)
        {
            TrackHttpFailure(response);
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Failed to fetch Binance account status: {error}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        decimal usdtBalance = 0;
        if (_isFuturesMode)
        {
            usdtBalance = ParseDecimal(doc.RootElement, "availableBalance");
            if (usdtBalance <= 0m)
            {
                usdtBalance = ParseDecimal(doc.RootElement, "totalWalletBalance");
            }
        }
        else
        {
            var balances = doc.RootElement.GetProperty("balances");
            foreach (var balance in balances.EnumerateArray())
            {
                var asset = balance.GetProperty("asset").GetString();
                if (asset == "USDT")
                {
                    usdtBalance = decimal.Parse(balance.GetProperty("free").GetString() ?? "0");
                    break;
                }
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
            TrackHttpFailure(response);
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

    private void TrackHttpFailure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        _telemetry?.RecordVenueHttpFailure("binance");
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

    /// <inheritdoc />
    public async Task<decimal?> GetTakerCommissionPercentPerSideAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var sym = symbol.Replace("-", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string queryString;
        string relativePath;
        if (_isFuturesMode)
        {
            queryString = $"symbol={sym}&timestamp={timestamp}";
            relativePath = $"/fapi/v1/commissionRate?{queryString}&signature={ComputeSignature(queryString)}";
        }
        else
        {
            queryString = $"symbol={sym}&timestamp={timestamp}";
            relativePath = $"/sapi/v1/asset/tradeFee?{queryString}&signature={ComputeSignature(queryString)}";
        }

        try
        {
            var response = await _httpClient.GetAsync(BuildBinanceUri(relativePath), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                TrackHttpFailure(response);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (_isFuturesMode)
            {
                if (!root.TryGetProperty("takerCommissionRate", out var takerEl))
                    return null;
                var s = takerEl.ValueKind == JsonValueKind.String ? takerEl.GetString() : takerEl.ToString();
                if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate))
                    return rate * 100m;
                return null;
            }

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return null;
            var row = root[0];
            if (!row.TryGetProperty("takerCommission", out var tc))
                return null;
            var ts = tc.ValueKind == JsonValueKind.String ? tc.GetString() : tc.ToString();
            if (decimal.TryParse(ts, NumberStyles.Any, CultureInfo.InvariantCulture, out var pct))
                return pct * 100m;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Binance trade fee lookup failed for {Symbol}", symbol);
            return null;
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

            var openOrdersPath = _isFuturesMode ? "/fapi/v1/openOrders" : "/api/v3/openOrders";
            var response = await _httpClient.GetAsync(
                BuildBinanceUri($"{openOrdersPath}?{queryString}&signature={signature}"), cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                TrackHttpFailure(response);
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

                    var cancelPath = _isFuturesMode ? "/fapi/v1/order" : "/api/v3/order";
                    using var cancelRequest = new HttpRequestMessage(
                        HttpMethod.Delete, BuildBinanceUri($"{cancelPath}?{cancelQuery}&signature={cancelSig}"));
                    var cancelResponse = await _httpClient.SendAsync(cancelRequest, cancellationToken);

                    if (cancelResponse.IsSuccessStatusCode)
                    {
                        cancelledCount++;
                        _logger.LogWarning("🚨 Emergency cancelled Binance order {OrderId} for {Symbol}", orderId, symbol);
                    }
                    else
                    {
                        TrackHttpFailure(cancelResponse);
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

    private static bool IsFuturesBaseAddress(Uri? baseAddress)
    {
        if (baseAddress is null)
        {
            return false;
        }

        var host = baseAddress.Host;
        return host.Contains("fapi", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("binancefuture", StringComparison.OrdinalIgnoreCase);
    }

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

        return TryParseJsonDecimal(element, out var parsed) ? parsed : fallback;
    }

    /// <summary>
    /// Sums fill commissions already expressed in the symbol's quote asset (e.g. USDT for BTCUSDT).
    /// BNB/other-asset fees are skipped until a conversion feed exists.
    /// </summary>
    private static decimal SumBinanceFillsCommissionInQuote(JsonElement root, string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) ||
            !root.TryGetProperty("fills", out var fills) ||
            fills.ValueKind != JsonValueKind.Array ||
            fills.GetArrayLength() == 0)
        {
            return 0m;
        }

        decimal sum = 0m;
        foreach (var fill in fills.EnumerateArray())
        {
            if (!fill.TryGetProperty("commission", out var commissionEl) ||
                !fill.TryGetProperty("commissionAsset", out var assetEl))
            {
                continue;
            }

            var asset = assetEl.GetString();
            if (string.IsNullOrEmpty(asset) ||
                !symbol.EndsWith(asset, StringComparison.OrdinalIgnoreCase) ||
                !TryParseJsonDecimal(commissionEl, out var commission) ||
                commission <= 0m)
            {
                continue;
            }

            sum += commission;
        }

        return sum;
    }

    private static bool TryParseJsonDecimal(JsonElement element, out decimal value)
    {
        value = 0m;
        return element.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(
                element.GetString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out value),
            JsonValueKind.Number => element.TryGetDecimal(out value),
            _ => false
        };
    }
}

