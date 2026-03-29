using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Enums;
using System.Net.Http.Headers;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// OKX Exchange execution service implementing IExchangeService.
/// Uses OKX REST API v5 with HMAC-SHA256 + Base64 signature scheme.
/// Symbol normalization: BTCUSDT → BTC-USDT (OKX uses hyphenated format).
/// </summary>
public sealed class OkxExecutionService : IExchangeService, IDisposable
{
    private readonly ILogger<OkxExecutionService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _passphrase;
    private readonly byte[] _apiSecretBytes;
    private readonly HMACSHA256 _hmac;
    private const string BaseUrl = "https://www.okx.com";
    private const VenueKind Venue = VenueKind.Okx;

    private readonly ConcurrentDictionary<string, OrderBookSnapshot> _latestOrderBooks = new();
    private readonly ISharedPortfolioStateManager _portfolio;

    public OkxExecutionService(
        ILogger<OkxExecutionService> logger,
        HttpClient httpClient,
        string apiKey,
        string apiSecret,
        string passphrase,
        ISharedPortfolioStateManager? portfolioStateManager = null)
    {
        _logger = logger;
        _httpClient = httpClient;
        _apiKey = apiKey;
        _apiSecretBytes = Encoding.UTF8.GetBytes(apiSecret);
        _passphrase = passphrase;
        _hmac = new HMACSHA256(_apiSecretBytes);
        _portfolio = portfolioStateManager ?? MemoryPortfolioManager.Instance;

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<decimal> GetQueuePositionAsync(string symbol, decimal price, TradeSide side)
    {
        var okxSymbol = NormalizeToOkx(symbol);
        if (!_latestOrderBooks.TryGetValue(okxSymbol, out var book))
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
            else
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
            _logger.LogWarning("OKX: Insufficient localized funds (MemoryPortfolio) for {Symbol}", signal.Symbol);
            throw new InvalidOperationException("Insufficient localized funds.");
        }

        var okxSymbol = NormalizeToOkx(signal.Symbol);
        _logger.LogInformation("Executing OKX order for {Symbol} (→{OkxSymbol}) @ {Price}",
            signal.Symbol, okxSymbol, signal.Price);

        // OKX v5 order request body
        var orderBody = new
        {
            instId = okxSymbol,
            tdMode = "cash",      // cash for spot
            side = signal.Side == TradeSide.Buy ? "buy" : "sell",
            ordType = "limit",
            px = signal.Price.ToString("F8"),
            sz = signal.Quantity.ToString("F8")
        };

        var bodyJson = JsonSerializer.Serialize(orderBody);
        var timestamp = GetIsoTimestamp();
        var signature = ComputeSignature(timestamp, "POST", "/api/v5/trade/order", bodyJson);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v5/trade/order");
        request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        // OKX-specific authentication headers
        request.Headers.Add("OK-ACCESS-KEY", _apiKey);
        request.Headers.Add("OK-ACCESS-SIGN", signature);
        request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        request.Headers.Add("OK-ACCESS-PASSPHRASE", _passphrase);

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

        return new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = signal.Price,
            Quantity = signal.Quantity,
            EntryTime = DateTime.UtcNow,
            ExecutionMode = "REAL-OKX"
        };
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
        request.Headers.Add("OK-ACCESS-KEY", _apiKey);
        request.Headers.Add("OK-ACCESS-SIGN", signature);
        request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        request.Headers.Add("OK-ACCESS-PASSPHRASE", _passphrase);

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

    /// <summary>
    /// OKX signature: Base64(HMAC-SHA256(timestamp + method + requestPath + body))
    /// </summary>
    private string ComputeSignature(string timestamp, string method, string requestPath, string body)
    {
        var prehash = $"{timestamp}{method}{requestPath}{body}";
        var prehashBytes = Encoding.UTF8.GetBytes(prehash);
        var hash = _hmac.ComputeHash(prehashBytes);
        return Convert.ToBase64String(hash);
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

    public void Dispose()
    {
        _hmac?.Dispose();
    }
}
