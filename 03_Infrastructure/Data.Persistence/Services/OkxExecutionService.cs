using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Exceptions;
using ThatsMyQuant.Contracts.Helpers;
using System.Net.Http.Headers;
using ThatsMyQuant.Application.Security;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// OKX Exchange execution service implementing IExchangeService.
/// Uses OKX REST API v5 with HMAC-SHA256 + Base64 signature scheme.
/// Symbol normalization: BTCUSDT → BTC-USDT (OKX uses hyphenated format).
/// </summary>
public sealed class OkxExecutionService : IExchangeService, IExchangeOrderStateProvider, IDisposable
{
    private readonly ILogger<OkxExecutionService> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ApiVault? _apiVault;
    private string _currentSecret = string.Empty;
    private HMACSHA256? _hmac;
    private const string BaseUrl = "https://www.okx.com";
    private const VenueKind Venue = VenueKind.Okx;

    private string ApiKey => _configuration["OKX:ApiKey"] ?? string.Empty;
    private string Passphrase => _configuration["OKX:Passphrase"] ?? string.Empty;
    private bool IsDemoMode => _configuration.GetValue<bool>("OKX:IsDemoMode");

    private readonly ConcurrentDictionary<string, OrderBookSnapshot> _latestOrderBooks = new();
    private readonly ISharedPortfolioStateManager _portfolio;

    public OkxExecutionService(
        ILogger<OkxExecutionService> logger,
        HttpClient httpClient,
        IConfiguration configuration,
        ISharedPortfolioStateManager? portfolioStateManager = null,
        ApiVault? apiVault = null)
    {
        _logger = logger;
        _httpClient = httpClient;
        _configuration = configuration;
        _apiVault = apiVault;
        _portfolio = portfolioStateManager ?? VenuePortfolioManager.Instance;

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<decimal> GetQueuePositionAsync(string symbol, decimal price, TradeSide side)
    {
        var okxSymbol = NormalizeToOkx(symbol);
        if (!_latestOrderBooks.TryGetValue(okxSymbol, out var book))
        {
            return 0;
        }

        return await Task.FromResult(QueuePositionCalculator.CalculateVolumeAhead(book, price, side));
    }

    public async Task<Trade> ExecuteOrderAsync(Signal signal)
    {
        // Institutional Guard: Local balance check via MemoryPortfolioManager
        var requiredNotional = signal.Quantity * signal.Price;
        if (!_portfolio.TryReserve(Venue, "USDT", requiredNotional))
        {
            _logger.LogWarning("OKX: Insufficient localized funds (MemoryPortfolio) for {Symbol}", signal.Symbol);
            throw new InvalidOperationException("Insufficient localized funds.");
        }

        var okxSymbol = NormalizeToOkx(signal.Symbol);
        var clientOrderId = ExecutionOrderIdentity.EnsureClientOrderId(signal);
        _logger.LogInformation("Executing OKX order for {Symbol} (→{OkxSymbol}) @ {Price} [DEMO:{IsDemo}]",
            signal.Symbol, okxSymbol, signal.Price, IsDemoMode);

        // OKX v5 order request body
        var orderBody = new
        {
            instId = okxSymbol,
            tdMode = "cash",      // cash for spot
            side = signal.Side == TradeSide.Buy ? "buy" : "sell",
            ordType = "limit",
            clOrdId = clientOrderId,
            px = signal.Price.ToString("F8"),
            sz = signal.Quantity.ToString("F8")
        };

        var bodyJson = JsonSerializer.Serialize(orderBody);
        var timestamp = GetIsoTimestamp();
        var signature = ComputeSignature(timestamp, "POST", "/api/v5/trade/order", bodyJson);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v5/trade/order");
        request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        // OKX-specific authentication headers
        request.Headers.Add("OK-ACCESS-KEY", ApiKey);
        request.Headers.Add("OK-ACCESS-SIGN", signature);
        request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        request.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);

        if (IsDemoMode)
        {
            request.Headers.Add("x-simulated-trading", "1");
        }

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("OKX execution failed: {Error}", error);
            throw new InvalidOperationException($"OKX execution failed: {error}");
        }

        // Check OKX response body for error codes (OKX returns 200 even on logical errors)
        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        var code = doc.RootElement.GetProperty("code").GetString();
        if (code != "0")
        {
            var msg = doc.RootElement.GetProperty("msg").GetString();
            _logger.LogError("OKX order rejected: code={Code}, msg={Msg}", code, msg);
            throw new InvalidOperationException($"OKX order rejected: {code} - {msg}");
        }

        // Funds were atomically reserved locally before the REST call.

        // Parse ordId from placement response and poll for fill status
        var ordId = string.Empty;
        try
        {
            ordId = doc.RootElement.GetProperty("data")[0].GetProperty("ordId").GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OKX: Could not parse ordId from placement response.");
        }

        decimal filledQty = 0m;
        decimal remainingQty = signal.Quantity;
        var fillStatus = TradeStatus.Pending;

        if (!string.IsNullOrEmpty(ordId))
        {
            (filledQty, remainingQty, fillStatus) = await PollFillStatusAsync(ordId, okxSymbol, signal.Quantity);
        }

        return new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            EntryPrice = signal.Price,
            Quantity = filledQty,
            FilledQuantity = filledQty,
            RequestedQuantity = signal.Quantity,
            RemainingQuantity = remainingQty,
            EntryTime = DateTime.UtcNow,
            SubmittedTime = DateTime.UtcNow,
            LastUpdatedTime = DateTime.UtcNow,
            Status = fillStatus,
            LifecycleStatus = TradeLifecycleStatus.Accepted,
            ExecutionMode = IsDemoMode ? "DEMO-OKX" : "REAL-OKX"
        };
    }

    /// <summary>
    /// Polls OKX GET /api/v5/trade/order for fill status.
    /// Max 3 attempts with 500ms delay. Returns (0, requestedQty, Pending) on any failure.
    /// </summary>
    private async Task<(decimal FilledQty, decimal RemainingQty, TradeStatus Status)> PollFillStatusAsync(
        string ordId,
        string instId,
        decimal requestedQty,
        CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 3;
        const int delayMs = 500;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var path = $"/api/v5/trade/order?instId={instId}&ordId={ordId}";
                var timestamp = GetIsoTimestamp();
                var signature = ComputeSignature(timestamp, "GET", path, string.Empty);

                using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
                request.Headers.Add("OK-ACCESS-KEY", ApiKey);
                request.Headers.Add("OK-ACCESS-SIGN", signature);
                request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
                request.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
                if (IsDemoMode) request.Headers.Add("x-simulated-trading", "1");

                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("OKX fill poll HTTP {Status} for ordId={OrdId}", response.StatusCode, ordId);
                    if (attempt < maxAttempts) await Task.Delay(delayMs, cancellationToken);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0)
                {
                    if (attempt < maxAttempts) await Task.Delay(delayMs, cancellationToken);
                    continue;
                }

                var order = data[0];
                var fillSz = ParseDecimal(order, "fillSz");
                var state  = order.TryGetProperty("state", out var stateEl) ? stateEl.GetString() : "live";

                return state switch
                {
                    "filled"           => (fillSz, 0m, TradeStatus.Executed),
                    "partially_filled" => (fillSz, requestedQty - fillSz, TradeStatus.Pending),
                    _                  => (0m, requestedQty, TradeStatus.Pending)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OKX fill poll exception on attempt {Attempt} for ordId={OrdId}", attempt, ordId);
                if (attempt < maxAttempts) await Task.Delay(delayMs, cancellationToken);
            }
        }

        return (0m, requestedQty, TradeStatus.Pending);
    }

    public async Task<Account> GetAccountStatusAsync()
    {
        // Use localized portfolio state if already synced
        var availableUsdt = _portfolio.GetAvailableBalance(Venue, "USDT");
        if (availableUsdt > 0)
        {
            return new Account
            {
                Id = "OKX-LIVE-CACHED",
                Provider = "OKX",
                Balance = availableUsdt,
                Equity = availableUsdt
            };
        }

        var timestamp = GetIsoTimestamp();
        var signature = ComputeSignature(timestamp, "GET", "/api/v5/account/balance", string.Empty);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/v5/account/balance");
        request.Headers.Add("OK-ACCESS-KEY", ApiKey);
        request.Headers.Add("OK-ACCESS-SIGN", signature);
        request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        request.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Failed to fetch OKX account status: {error}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        decimal usdtBalance = 0;
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() > 0)
        {
            var details = data[0].GetProperty("details");
            foreach (var detail in details.EnumerateArray())
            {
                var ccy = detail.GetProperty("ccy").GetString();
                if (ccy == "USDT")
                {
                    usdtBalance = decimal.Parse(
                        detail.GetProperty("availBal").GetString() ?? "0",
                        System.Globalization.CultureInfo.InvariantCulture);
                    break;
                }
            }
        }

        _portfolio.SetBalance(Venue, "USDT", usdtBalance);

        return new Account
        {
            Id = "OKX-LIVE",
            Provider = "OKX",
            Balance = usdtBalance,
            Equity = usdtBalance
        };
    }

    public async Task<IReadOnlyList<OpenOrderSnapshot>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        var timestamp = GetIsoTimestamp();
        const string path = "/api/v5/trade/orders-pending?instType=SPOT";
        var signature = ComputeSignature(timestamp, "GET", path, string.Empty);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
        request.Headers.Add("OK-ACCESS-KEY", ApiKey);
        request.Headers.Add("OK-ACCESS-SIGN", signature);
        request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        request.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
        if (IsDemoMode)
        {
            request.Headers.Add("x-simulated-trading", "1");
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Failed to fetch OKX open orders: {error}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("code").GetString() != "0")
        {
            var msg = root.GetProperty("msg").GetString();
            throw new InvalidOperationException($"OKX open orders query rejected: {msg}");
        }

        var snapshots = new List<OpenOrderSnapshot>();
        foreach (var item in root.GetProperty("data").EnumerateArray())
        {
            var requestedQuantity = ParseDecimal(item, "sz");
            var filledQuantity = ParseDecimal(item, "accFillSz");
            var remainingQuantity = Math.Max(0m, requestedQuantity - filledQuantity);
            var price = ParseDecimal(item, "px");
            var averagePrice = ParseDecimal(item, "avgPx", price);
            var side = item.TryGetProperty("side", out var sideElement) &&
                       string.Equals(sideElement.GetString(), "sell", StringComparison.OrdinalIgnoreCase)
                ? TradeSide.Sell
                : TradeSide.Buy;

            snapshots.Add(new OpenOrderSnapshot(
                NormalizeFromOkx(item.TryGetProperty("instId", out var symbolElement) ? symbolElement.GetString() ?? string.Empty : string.Empty),
                side,
                item.TryGetProperty("clOrdId", out var clientOrderIdElement) ? clientOrderIdElement.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("ordId", out var orderIdElement) ? orderIdElement.GetString() ?? string.Empty : string.Empty,
                requestedQuantity,
                filledQuantity,
                remainingQuantity,
                price,
                averagePrice,
                item.TryGetProperty("state", out var stateElement) ? stateElement.GetString() ?? "live" : "live",
                ResolveOkxTimestamp(item)));
        }

        return snapshots;
    }

    private static readonly HashSet<string> SkippedFavoriteAssets =
        new(StringComparer.OrdinalIgnoreCase) { "USDT", "USDC", "USD", "BTC", "ETH", "TRY" };

    public async Task<List<string>> GetFavoritesAsync()
    {
        var timestamp = GetIsoTimestamp();
        const string path = "/api/v5/account/balance";
        var signature = ComputeSignature(timestamp, "GET", path, string.Empty);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
        request.Headers.Add("OK-ACCESS-KEY", ApiKey);
        request.Headers.Add("OK-ACCESS-SIGN", signature);
        request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        request.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
        if (IsDemoMode) request.Headers.Add("x-simulated-trading", "1");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new ExchangeSyncException("OKX", $"HTTP {(int)response.StatusCode}: {error}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var code = root.GetProperty("code").GetString();
        if (code != "0")
        {
            var msg = root.GetProperty("msg").GetString() ?? "Unknown error";
            throw new ExchangeSyncException("OKX", $"code={code}: {msg}");
        }

        var results = new List<string>();
        foreach (var account in root.GetProperty("data").EnumerateArray())
        {
            if (!account.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var detail in details.EnumerateArray())
            {
                var asset = detail.TryGetProperty("ccy", out var ccyElement)
                    ? ccyElement.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(asset) || SkippedFavoriteAssets.Contains(asset))
                {
                    continue;
                }

                var available = ParseDecimal(detail, "availBal");
                var cashBalance = ParseDecimal(detail, "cashBal");
                if (available + cashBalance <= 0m)
                {
                    continue;
                }

                var canonical = SymbolNormalizer.ToCanonical(asset + "USDT");
                if (SymbolNormalizer.IsValid(canonical))
                {
                    results.Add(canonical);
                }
            }
        }

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// OKX signature: Base64(HMAC-SHA256(timestamp + method + requestPath + body))
    /// When ApiVault is injected, the secret bytes are retrieved transiently and zeroed
    /// immediately after signing — no plaintext key lingers in memory.
    /// Falls back to IConfiguration["OKX:SecretKey"] when vault is null (e.g. in tests).
    /// </summary>
    private string ComputeSignature(string timestamp, string method, string requestPath, string body)
    {
        var prehash = $"{timestamp}{method}{requestPath}{body}";
        var prehashBytes = Encoding.UTF8.GetBytes(prehash);

        if (_apiVault != null)
        {
            // Vault path: get transient secret bytes, sign, then zero them out.
            byte[] secretBytes = _apiVault.GetTransientDecryptedKey();
            try
            {
                using var hmac = new HMACSHA256(secretBytes);
                return Convert.ToBase64String(hmac.ComputeHash(prehashBytes));
            }
            finally
            {
                Array.Clear(secretBytes, 0, secretBytes.Length);
            }
        }

        // Fallback path (backward compat / tests): use IConfiguration.
        var secret = _configuration["OKX:SecretKey"] ?? string.Empty;
        if (secret != _currentSecret || _hmac == null)
        {
            _hmac?.Dispose();
            _hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            _currentSecret = secret;
        }

        return Convert.ToBase64String(_hmac.ComputeHash(prehashBytes));
    }

    /// <summary>
    /// OKX requires ISO 8601 timestamp: e.g. "2024-01-01T00:00:00.000Z"
    /// </summary>
    private static string GetIsoTimestamp()
        => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>
    /// Converts internal symbol format (BTCUSDT) to OKX format (BTC-USDT).
    /// Also handles symbols that are already in OKX format.
    /// </summary>
    public static string NormalizeToOkx(string symbol)
    {
        // Already OKX format
        if (symbol.Contains('-'))
            return symbol;

        // Common quote assets — try to split
        string[] quoteAssets = ["USDT", "USDC", "BTC", "ETH", "EUR", "TRY"];
        foreach (var quote in quoteAssets)
        {
            if (symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase) && symbol.Length > quote.Length)
            {
                var baseAsset = symbol[..^quote.Length];
                return $"{baseAsset}-{quote}";
            }
        }

        // Fallback: return as-is (may fail on OKX, but won't crash here)
        return symbol;
    }

    /// <summary>
    /// Converts OKX symbol format (BTC-USDT) back to internal format (BTCUSDT).
    /// </summary>
    public static string NormalizeFromOkx(string okxSymbol)
        => okxSymbol.Replace("-", string.Empty);

    public async Task<int> CancelAllOrdersAsync(CancellationToken cancellationToken = default)
    {
        int cancelledCount = 0;
        try
        {
            // OKX v5: Get pending orders
            var timestamp = GetIsoTimestamp();
            const string pendingPath = "/api/v5/trade/orders-pending?instType=SPOT";
            var signature = ComputeSignature(timestamp, "GET", pendingPath, string.Empty);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{pendingPath}");
            request.Headers.Add("OK-ACCESS-KEY", ApiKey);
            request.Headers.Add("OK-ACCESS-SIGN", signature);
            request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
            request.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
            if (IsDemoMode) request.Headers.Add("x-simulated-trading", "1");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("OKX: Failed to fetch pending orders for emergency cancel: {Error}", error);
                return 0;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.GetProperty("code").GetString() != "0")
            {
                _logger.LogError("OKX: Pending orders query returned error: {Msg}",
                    doc.RootElement.GetProperty("msg").GetString());
                return 0;
            }

            var orders = doc.RootElement.GetProperty("data");
            foreach (var order in orders.EnumerateArray())
            {
                try
                {
                    var instId = order.GetProperty("instId").GetString() ?? "";
                    var ordId = order.GetProperty("ordId").GetString() ?? "";

                    var cancelBody = JsonSerializer.Serialize(new { instId, ordId });
                    var cancelTimestamp = GetIsoTimestamp();
                    var cancelSig = ComputeSignature(cancelTimestamp, "POST", "/api/v5/trade/cancel-order", cancelBody);

                    using var cancelReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v5/trade/cancel-order");
                    cancelReq.Content = new StringContent(cancelBody, Encoding.UTF8, "application/json");
                    cancelReq.Headers.Add("OK-ACCESS-KEY", ApiKey);
                    cancelReq.Headers.Add("OK-ACCESS-SIGN", cancelSig);
                    cancelReq.Headers.Add("OK-ACCESS-TIMESTAMP", cancelTimestamp);
                    cancelReq.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
                    if (IsDemoMode) cancelReq.Headers.Add("x-simulated-trading", "1");

                    var cancelResp = await _httpClient.SendAsync(cancelReq, cancellationToken);
                    if (cancelResp.IsSuccessStatusCode)
                    {
                        cancelledCount++;
                        _logger.LogWarning("🚨 Emergency cancelled OKX order {OrderId} for {InstId}", ordId, instId);
                    }
                    else
                    {
                        var cancelErr = await cancelResp.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogError("Failed to cancel OKX order {OrderId}: {Error}", ordId, cancelErr);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error cancelling individual OKX order during emergency halt.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Emergency CancelAllOrdersAsync failed on OKX.");
        }

        _logger.LogCritical("🚨 OKX emergency halt: {Count} orders cancelled.", cancelledCount);
        return cancelledCount;
    }

    private static decimal ParseDecimal(JsonElement element, string propertyName, decimal fallback = 0m)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        return decimal.TryParse(
            property.GetString(),
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;
    }

    private static DateTime ResolveOkxTimestamp(JsonElement element)
    {
        foreach (var propertyName in new[] { "uTime", "cTime" })
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (long.TryParse(property.GetString(), out var epochMs) && epochMs > 0)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;
            }
        }

        return DateTime.UtcNow;
    }

    public void Dispose()
    {
        _hmac?.Dispose();
    }
}
