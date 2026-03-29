using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Institutional-grade execution service for the Binance Cryptocurrency Exchange.
/// Uses the HardwareSecurityModule (HSM) for cold-signing orders directly in RAM.
/// </summary>
public class BinanceExchangeService : IExchangeService
{
    private readonly HttpClient _httpClient;
    private readonly IHardwareSecurityModule _hsm;
    private readonly ILogger<BinanceExchangeService> _logger;
    private const string BaseUrl = "https://api.binance.com";
    private readonly string _apiKey;

    public BinanceExchangeService(
        HttpClient httpClient, 
        IHardwareSecurityModule hsm, 
        ILogger<BinanceExchangeService> logger,
        string apiKey)
    {
        _httpClient = httpClient;
        _hsm = hsm;
        _logger = logger;
        _apiKey = apiKey;
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
            decimal execPrice = decimal.Parse(root.GetProperty("fills")[0].GetProperty("price").GetString()!);
            decimal execQty = decimal.Parse(root.GetProperty("executedQty").GetString()!);

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
                Status = TradeStatus.Executed,
                EntryTime = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "FATAL: Binance Execution Failure. Manual intervention/audit required.");
            throw;
        }
    }

    public async Task<decimal> GetQueuePositionAsync(string symbol, decimal price, TradeSide side)
    {
        // For the cold-signed HSM service, queue tracking is handled via a secondary telemetry stream.
        return await Task.FromResult(0m);
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
}
