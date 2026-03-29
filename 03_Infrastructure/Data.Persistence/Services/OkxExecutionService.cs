using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// OKX REST v5 execution service for live order placement and account status.
/// Implements the same IExchangeService interface as BinanceExecutionService for
/// seamless exchange routing.
///
/// OKX auth differs from Binance:
///   - Signs: ISO-8601 timestamp + HTTP method + requestPath + body (empty string if GET)
///   - Signature: HMAC-SHA256 → Base64 (not hex)
///   - Extra header: OK-ACCESS-PASSPHRASE (set in OKX account settings)
///
/// OKX REST docs: https://www.okx.com/docs-v5/en/
/// </summary>
public sealed class OkxExecutionService : IExchangeService
{
    private readonly ILogger<OkxExecutionService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _passphrase;
    private const string BaseUrl = "https://www.okx.com";

    // OKX uses simulated trading flag — set to "0" for live, "1" for paper trading
    private readonly string _simulatedTrading;

    public OkxExecutionService(
        ILogger<OkxExecutionService> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _apiKey = configuration["OKX:ApiKey"]
            ?? throw new InvalidOperationException("OKX:ApiKey is not configured.");
        _apiSecret = configuration["OKX:ApiSecret"]
            ?? throw new InvalidOperationException("OKX:ApiSecret is not configured.");
        _passphrase = configuration["OKX:Passphrase"]
            ?? throw new InvalidOperationException("OKX:Passphrase is not configured.");
        _simulatedTrading = configuration["OKX:SimulatedTrading"] ?? "0";

        _httpClient = httpClientFactory.CreateClient("OKX");
    }

    /// <summary>
    /// Places a LIMIT order on OKX.
    /// OKX instId format: "BTC-USDT" (dash-separated). Converts from Binance-style "BTCUSDT" if needed.
    /// </summary>
    public async Task<Trade> ExecuteOrderAsync(Signal signal)
    {
        var instId = NormalizeToOkxInstId(signal.Symbol);
        var side = signal.Side == TradeSide.Buy ? "buy" : "sell";

        _logger.LogInformation(
            "Executing OKX order: {InstId} {Side} qty={Qty} price={Price}",
            instId, side, signal.Quantity, signal.Price);

        const string path = "/api/v5/trade/order";

        var body = JsonSerializer.Serialize(new
        {
            instId,
            tdMode = "cash",        // spot trading; use "isolated" or "cross" for margin/futures
            side,
            ordType = "limit",
            sz = signal.Quantity.ToString("F8"),
            px = signal.Price.ToString("F8")
        });

        var response = await SendSignedPostAsync(path, body);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            _logger.LogError("OKX order failed [{Status}]: {Error}", (int)response.StatusCode, err);
            throw new InvalidOperationException($"OKX order failed: {err}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // OKX returns: { "code": "0", "msg": "", "data": [{ "ordId": "...", "clOrdId": "", "tag": "", "sCode": "0", "sMsg": "" }] }
        var code = root.GetProperty("code").GetString();
        if (code != "0")
        {
            var msg = root.GetProperty("msg").GetString() ?? "Unknown OKX error";
            _logger.LogError("OKX rejected order: code={Code} msg={Msg}", code, msg);
            throw new InvalidOperationException($"OKX rejected order (code={code}): {msg}");
        }

        var orderId = root.GetProperty("data")[0].GetProperty("ordId").GetString() ?? "";
        _logger.LogInformation("OKX order placed: ordId={OrderId} instId={InstId}", orderId, instId);

        return new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            EntryPrice = signal.Price,
            Quantity = signal.Quantity,
            EntryTime = DateTime.UtcNow,
            ExecutionMode = "REAL"
        };
    }

    /// <summary>
    /// Fetches account balance from OKX /api/v5/account/balance.
    /// Sums all USDT-equivalent assets across spot wallet.
    /// </summary>
    public async Task<Account> GetAccountStatusAsync()
    {
        const string path = "/api/v5/account/balance";

        var response = await SendSignedGetAsync(path);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"OKX account status failed: {err}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // OKX returns: { "code": "0", "data": [{ "totalEq": "...", "details": [...] }] }
        decimal totalEq = 0;
        if (root.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
        {
            var account = data[0];
            if (account.TryGetProperty("totalEq", out var eq))
                decimal.TryParse(eq.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out totalEq);
        }

        return new Account
        {
            Id = "OKX-LIVE",
            Provider = "OKX",
            Balance = totalEq,
            Equity = totalEq
        };
    }

    // ─── Signed request helpers ───────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendSignedPostAsync(string path, string body)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var sign = Sign(timestamp, "POST", path, body);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{path}");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        AddAuthHeaders(request, timestamp, sign);

        return await _httpClient.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendSignedGetAsync(string path, string query = "")
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var requestPath = string.IsNullOrEmpty(query) ? path : $"{path}?{query}";
        var sign = Sign(timestamp, "GET", requestPath, "");

        var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{requestPath}");
        AddAuthHeaders(request, timestamp, sign);

        return await _httpClient.SendAsync(request);
    }

    private void AddAuthHeaders(HttpRequestMessage request, string timestamp, string signature)
    {
        request.Headers.Add("OK-ACCESS-KEY", _apiKey);
        request.Headers.Add("OK-ACCESS-SIGN", signature);
        request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        request.Headers.Add("OK-ACCESS-PASSPHRASE", _passphrase);
        // Paper trading flag: "0" = live, "1" = simulated
        request.Headers.Add("x-simulated-trading", _simulatedTrading);
    }

    /// <summary>
    /// OKX signature: Base64( HMAC-SHA256( secret, timestamp + method + requestPath + body ) )
    /// Note: Binance uses hex; OKX uses Base64.
    /// Exposed as internal static so unit tests can verify the signing logic directly.
    /// </summary>
    internal static string ComputeSignature(string timestamp, string method, string requestPath, string body, string secret)
    {
        var preHash = $"{timestamp}{method}{requestPath}{body}";
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var msgBytes = Encoding.UTF8.GetBytes(preHash);
        using var hmac = new HMACSHA256(keyBytes);
        return Convert.ToBase64String(hmac.ComputeHash(msgBytes));
    }

    private string Sign(string timestamp, string method, string requestPath, string body)
        => ComputeSignature(timestamp, method, requestPath, body, _apiSecret);

    // ─── Symbol normalisation (shared logic with OkxWebSocketService) ─────────

    /// <summary>
    /// Convert "BTCUSDT" → "BTC-USDT" for OKX. Already-formatted "BTC-USDT" passes through.
    /// </summary>
    internal static string NormalizeToOkxInstId(string symbol)
    {
        if (symbol.Contains('-')) return symbol.ToUpperInvariant();

        string[] quotes = { "USDT", "USDC", "USD", "BTC", "ETH", "TRY" };
        foreach (var q in quotes)
        {
            if (symbol.EndsWith(q, StringComparison.OrdinalIgnoreCase))
            {
                var baseCur = symbol[..^q.Length];
                return $"{baseCur.ToUpperInvariant()}-{q.ToUpperInvariant()}";
            }
        }

        return symbol.ToUpperInvariant();
    }
}
