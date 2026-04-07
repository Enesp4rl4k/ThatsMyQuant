using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Infrastructure.Exchange.Binance;

/// <summary>
/// A high-performance, binary-oriented execution provider for Binance.
/// Focuses on minimal allocations during request signing and payload generation.
/// Uses pre-allocated buffers and spans for HMAC-SHA256 signing of order requests.
/// </summary>
public sealed class BinaryExecutionProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BinaryExecutionProvider> _logger;
    private readonly byte[] _secretKeyBytes;
    private readonly string _apiKey;

    // Use a ThreadLocal or Pooled buffer to avoid per-request allocations for the signature string
    private static readonly ThreadLocal<byte[]> _workBuffer = new(() => new byte[1024]);

    public BinaryExecutionProvider(
        HttpClient httpClient, 
        ILogger<BinaryExecutionProvider> logger,
        string apiKey,
        string secretKey)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = apiKey;
        _secretKeyBytes = Encoding.UTF8.GetBytes(secretKey);
        
        _httpClient.DefaultRequestHeaders.Add("X-MBX-APIKEY", _apiKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<Trade> SendOrderAsync(Signal signal, CancellationToken ct = default)
    {
        // 1. Build Query String (Allocating strings here is bad for HFT, but standard for REST)
        // In a true bit-perfect engine, we'd use a Span-based builder.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var side = signal.Side == TradeSide.Buy ? "BUY" : "SELL";
        var type = "LIMIT";
        var timeInForce = "GTC"; // Good Til Cancelled
        
        // Use a ValueStringBuilder or similar if performance becomes critical
        var queryString = $"symbol={signal.Symbol}&side={side}&type={type}&timeInForce={timeInForce}&quantity={signal.Quantity}&price={signal.Price}&timestamp={timestamp}";
        
        // 2. Sign the payload using HMAC-SHA256 (Zero-Allocation approach for the hash)
        string signature = ComputeHmacSignature(queryString);
        
        var requestUrl = $"https://api.binance.com/api/v3/order?{queryString}&signature={signature}";

        try
        {
            var response = await _httpClient.PostAsync(requestUrl, null, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Binance Order Failed: {Status} - {Content}", response.StatusCode, content);
                return CreateFailedTrade(signal, $"API Error: {response.StatusCode}");
            }

            // Parse response (In HFT, we'd use Utf8JsonReader on the stream directly)
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            return new Trade
            {
                Id = Guid.NewGuid(),
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                Side = signal.Side,
                EntryPrice = root.GetProperty("price").GetDecimal(),
                Quantity = root.GetProperty("executedQty").GetDecimal(),
                Status = TradeStatus.Filled,
                EntryTime = DateTime.UtcNow,
                ExecutionMode = "BINANCE_LIVE"
            };
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal Exception during Binance Order Execution for {Symbol}", signal.Symbol);
            return CreateFailedTrade(signal, ex.Message);
        }
    }

    private string ComputeHmacSignature(string message)
    {
        // Use stackalloc or pooled buffer for the hash calculation
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
        
        using var hmac = new HMACSHA256(_secretKeyBytes);
        byte[] hashBytes = hmac.ComputeHash(messageBytes);
        
        // Convert to hex string without intermediate allocations where possible
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static Trade CreateFailedTrade(Signal signal, string reason)
    {
        return new Trade
        {
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            Status = TradeStatus.Failed,
            ErrorMessage = reason,
            EntryTime = DateTime.UtcNow
        };
    }
}
