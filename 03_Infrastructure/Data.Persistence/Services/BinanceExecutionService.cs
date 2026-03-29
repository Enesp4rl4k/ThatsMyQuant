using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Infrastructure.Services;

public sealed class BinanceExecutionService : IExchangeService
{
    private readonly ILogger<BinanceExecutionService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly Application.Security.ApiVault _secretVault;
    private const string BaseUrl = "https://api.binance.com";

    public BinanceExecutionService(
        ILogger<BinanceExecutionService> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        Application.Security.ApiVault apiVault)
    {
        _logger = logger;
        _apiKey = configuration["Binance:ApiKey"] ?? throw new InvalidOperationException("Binance:ApiKey is not configured.");
        var apiSecret = configuration["Binance:ApiSecret"] ?? throw new InvalidOperationException("Binance:ApiSecret is not configured.");
        _secretVault = apiVault;
        _secretVault.SecureInboundKey(apiSecret);
        _httpClient = httpClientFactory.CreateClient("Binance");
        _httpClient.DefaultRequestHeaders.Add("X-MBX-APIKEY", _apiKey);
    }

    public async Task<Trade> ExecuteOrderAsync(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (string.IsNullOrWhiteSpace(signal.Symbol))
            throw new ArgumentException("Signal.Symbol is required", nameof(signal));
        if (signal.Price <= 0)
            throw new ArgumentException("Signal.Price must be positive", nameof(signal));
        if (signal.Quantity <= 0)
            throw new ArgumentException("Signal.Quantity must be positive", nameof(signal));

        _logger.LogInformation("Executing Binance order for {Symbol} @ {Price}", signal.Symbol, signal.Price);

        var queryParams = new Dictionary<string, string>
        {
            { "symbol", signal.Symbol },
            { "side", signal.Side == TradeSide.Buy ? "BUY" : "SELL" },
            { "type", "LIMIT" },
            { "timeInForce", "GTC" },
            { "quantity", signal.Quantity.ToString("F8") },
            { "price", signal.Price.ToString("F8") },
            { "timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() }
        };

        var queryString = BuildQueryString(queryParams);
        var signature = ComputeSignature(queryString);
        var response = await _httpClient.PostAsync($"{BaseUrl}/api/v3/order?{queryString}&signature={signature}", null);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Binance execution failed: {Error}", error);
            throw new InvalidOperationException($"Binance execution failed: {error}");
        }

        var json = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("Binance order executed successfully: {Response}", json);

        return new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = signal.Price,
            Quantity = signal.Quantity,
            EntryTime = DateTime.UtcNow,
            ExecutionMode = "REAL"
        };
    }

    public async Task<Account> GetAccountStatusAsync()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var queryString = $"timestamp={timestamp}";
        var signature = ComputeSignature(queryString);

        var response = await _httpClient.GetAsync($"{BaseUrl}/api/v3/account?{queryString}&signature={signature}");
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Failed to fetch Binance account status: {error}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var balances = doc.RootElement.GetProperty("balances");

        decimal totalBalance = 0;
        foreach (var balance in balances.EnumerateArray())
        {
            var free = decimal.Parse(balance.GetProperty("free").GetString() ?? "0");
            var locked = decimal.Parse(balance.GetProperty("locked").GetString() ?? "0");
            totalBalance += (free + locked);
        }

        return new Account
        {
            Id = "BINANCE-LIVE",
            Provider = "Binance",
            Balance = totalBalance,
            Equity = totalBalance
        };
    }

    private string BuildQueryString(Dictionary<string, string> queryParams)
    {
        return string.Join("&", queryParams.Select(p => $"{p.Key}={p.Value}"));
    }

    private string ComputeSignature(string message)
    {
        var keyBytes = _secretVault.GetTransientDecryptedKey();
        try
        {
            var messageBytes = Encoding.UTF8.GetBytes(message);
            using var hmac = new HMACSHA256(keyBytes);
            var hash = hmac.ComputeHash(messageBytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
        finally
        {
            Array.Clear(keyBytes, 0, keyBytes.Length);
        }
    }
}
