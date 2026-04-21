using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Institutional-grade execution service for the Binance Cryptocurrency Exchange.
/// Uses the HardwareSecurityModule (HSM) for cold-signing orders directly in RAM.
/// </summary>
public class BinanceExchangeService : IExchangeService, IVenueTradeFeeProvider
{
    private readonly HttpClient _httpClient;
    private readonly IHardwareSecurityModule _hsm;
    private readonly ILogger<BinanceExchangeService> _logger;
    private const string BaseUrl = "https://api.binance.com";
    private readonly string _apiKey;
    private readonly ConcurrentDictionary<string, OrderBookSnapshot> _latestOrderBooks = new();

    public BinanceExchangeService(
        HttpClient httpClient, 
        IHardwareSecurityModule hsm, 
        ILogger<BinanceExchangeService> logger,
        string apiKey,
        IStreamingMarketDataService? streamingService = null)
    {
        _httpClient = httpClient;
        _hsm = hsm;
        _logger = logger;
        _apiKey = apiKey;

        if (streamingService is BinanceWebSocketService bwss)
        {
            bwss.OnOrderBookUpdated += snapshot => _latestOrderBooks[snapshot.Symbol] = snapshot;
        }
    }

    public async Task<Trade> ExecuteOrderAsync(Signal signal)
    {
        _logger.LogInformation("🚀 Initiating LIVE Execution for {Symbol} ({Side}) @ {Price}", 
            signal.Symbol, signal.Side, signal.Price);

        try
        {
            // 1. Prepare Order Parameters
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var symbol = signal.Symbol.Replace("-", "").ToUpperInvariant();
            var side = signal.Side == TradeSide.Buy ? "BUY" : "SELL";
            
            // For this HFT-lite implementation, we use MARKET orders for immediate liquidity.
            // In a more advanced version, we would use LIMIT (GTC) orders.
            var queryParams = $"symbol={symbol}&side={side}&type=MARKET&quantity=1.0&timestamp={timestamp}";

            // 2. Cold-Sign the payload via HSM (Zero-Plaintext leakage)
            var signature = await _hsm.SignPayloadAsync("BINANCE", queryParams);

            // 3. Dispatch REST request
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v3/order?{queryParams}&signature={signature}");
            request.Headers.Add("X-MBX-APIKEY", _apiKey);

            var response = await _httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("❌ Binance Execution REJECTED: {Content}", content);
                throw new HttpRequestException($"Binance API Error: {response.StatusCode} - {content}", null, response.StatusCode);
            }

            // 4. Parse Execution Result
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var now = DateTime.UtcNow;
            decimal execPrice = root.TryGetProperty("fills", out var fills) && fills.GetArrayLength() > 0
                ? decimal.Parse(fills[0].GetProperty("price").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                : signal.Price;
            decimal execQty = decimal.Parse(root.GetProperty("executedQty").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            decimal origQty = root.TryGetProperty("origQty", out var origQtyElement)
                ? decimal.Parse(origQtyElement.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                : execQty;
            var orderStatus = root.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;

            _logger.LogInformation("✅ Trade EXECUTED on Binance. OrderId: {OrderId}. Price: {Price}", 
                root.GetProperty("orderId").GetInt64(), execPrice);

            return new Trade
            {
                Id = Guid.NewGuid(),
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                Side = signal.Side,
                EntryPrice = execPrice,
                Quantity = execQty,
                FilledQuantity = execQty,
                RequestedQuantity = origQty,
                RemainingQuantity = Math.Max(0m, origQty - execQty),
                Status = execQty > 0m ? TradeStatus.Executed : TradeStatus.Pending,
                LifecycleStatus = MapBinanceLifecycle(orderStatus, execQty, origQty),
                EntryTime = now,
                SubmittedTime = now,
                LastUpdatedTime = now,
                MarkPrice = execPrice,
                ExecutionMode = "REAL",
                Leverage = ResolveLeverage(signal),
                MarginMode = ResolveMarginMode(signal),
                NetFunding = 0m
            };
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "FATAL: Binance Execution Failure. Manual intervention/audit required.");
            throw;
        }
    }

    public bool TryGetLatestOrderBook(string symbol, out OrderBookSnapshot book)
    {
        if (_latestOrderBooks.TryGetValue(symbol, out var snapshot))
        {
            book = snapshot;
            return true;
        }

        var normalized = symbol.Replace("-", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        if (_latestOrderBooks.TryGetValue(normalized, out snapshot))
        {
            book = snapshot;
            return true;
        }

        book = default!;
        return false;
    }

    public async Task<decimal> GetQueuePositionAsync(string symbol, decimal price, TradeSide side)
    {
        // For the cold-signed HSM service, queue tracking is handled via a secondary telemetry stream.
        return await Task.FromResult(0m);
    }

    public Task<List<string>> GetFavoritesAsync() => Task.FromResult(new List<string>());

    /// <inheritdoc />
    public async Task<decimal?> GetTakerCommissionPercentPerSideAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var sym = symbol.Replace("-", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var queryParams = $"symbol={sym}&timestamp={timestamp}";
        try
        {
            var signature = await _hsm.SignPayloadAsync("BINANCE", queryParams);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl}/sapi/v1/asset/tradeFee?{queryParams}&signature={signature}");
            request.Headers.Add("X-MBX-APIKEY", _apiKey);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
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
            _logger.LogDebug(ex, "BinanceExchangeService trade fee lookup failed for {Symbol}", symbol);
            return null;
        }
    }

    public async Task<int> CancelAllOrdersAsync(CancellationToken cancellationToken = default)
    {
        int cancelledCount = 0;

        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var queryParams = $"timestamp={timestamp}";
            var signature = await _hsm.SignPayloadAsync("BINANCE", queryParams);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/v3/openOrders?{queryParams}&signature={signature}");
            request.Headers.Add("X-MBX-APIKEY", _apiKey);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to fetch Binance open orders for emergency cancel: {Error}", error);
                return 0;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            foreach (var order in doc.RootElement.EnumerateArray())
            {
                var symbol = order.GetProperty("symbol").GetString() ?? string.Empty;
                var orderId = order.GetProperty("orderId").GetInt64();
                var cancelTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var cancelQuery = $"symbol={symbol}&orderId={orderId}&timestamp={cancelTimestamp}";
                var cancelSignature = await _hsm.SignPayloadAsync("BINANCE", cancelQuery);

                using var cancelRequest = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/api/v3/order?{cancelQuery}&signature={cancelSignature}");
                cancelRequest.Headers.Add("X-MBX-APIKEY", _apiKey);

                var cancelResponse = await _httpClient.SendAsync(cancelRequest, cancellationToken);
                if (cancelResponse.IsSuccessStatusCode)
                {
                    cancelledCount++;
                }
                else
                {
                    var cancelError = await cancelResponse.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("Failed to cancel Binance order {OrderId}: {Error}", orderId, cancelError);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Emergency CancelAllOrdersAsync failed on BinanceExchangeService.");
        }

        return cancelledCount;
    }

    public async Task<Account> GetAccountStatusAsync()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var queryParams = $"timestamp={timestamp}";
        var signature = await _hsm.SignPayloadAsync("BINANCE", queryParams);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/v3/account?{queryParams}&signature={signature}");
        request.Headers.Add("X-MBX-APIKEY", _apiKey);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to fetch account status: {Err}", err);
            return new Account { Id = "ERROR", Provider = "Binance", Balance = 0 };
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var balances = json.GetProperty("balances").EnumerateArray();
        
        // Sum up total USDT/equivalent for a simplified balance view
        decimal totalUsdt = 0;
        foreach (var balance in balances)
        {
            if (balance.GetProperty("asset").GetString() == "USDT")
            {
                totalUsdt = decimal.Parse(balance.GetProperty("free").GetString()!);
                break;
            }
        }

        return new Account
        {
            Id = "BINANCE-LIVE",
            Provider = "Binance",
            Balance = totalUsdt,
            Equity = totalUsdt // Simplified for mock replacement
        };
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
}
