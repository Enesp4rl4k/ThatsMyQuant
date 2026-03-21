using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

public class BinanceWebSocketService : IStreamingMarketDataService, IAsyncDisposable
{
    private readonly ILogger<BinanceWebSocketService> _logger;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;

    public event Action<MarketTick>? OnTickReceived;
    public event Action<OrderBookSnapshot>? OnOrderBookUpdated;

    public BinanceWebSocketService(ILogger<BinanceWebSocketService> logger)
    {
        _logger = logger;
    }

    public async Task ConnectAsync(string[] symbols, CancellationToken cancellationToken)
    {
        if (symbols == null || symbols.Length == 0) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _webSocket = new ClientWebSocket();

        try
        {
            // Build combined streams string so we can recover the symbol for both trades and depth snapshots.
            var streamParams = string.Join("/", symbols.SelectMany(s => 
            {
                var formatted = s.Replace("-", "").ToLowerInvariant();
                return new[] { $"{formatted}@aggTrade", $"{formatted}@depth20@100ms" };
            }));
            
            var uri = new Uri($"wss://stream.binance.com:9443/stream?streams={streamParams}");

            _logger.LogInformation("Connecting to Binance WS: {Uri}", uri);
            await _webSocket.ConnectAsync(uri, _cts.Token);
            _logger.LogInformation("Connected to Binance WebSocket Streams successfully.");

            // Start receiving loop in background
            _ = ReceiveLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Binance WebSocket.");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[8192];
        var memoryBuffer = new Memory<byte>(buffer);

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(memoryBuffer, token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogWarning("WebSocket closed by server.");
                    break;
                }

                if (result.Count > 0)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    ParseMessage(json);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in WebSocket receive loop.");
        }
    }

    private void ParseMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var payload = root;
            string? streamName = null;

            if (root.TryGetProperty("stream", out var streamElement) &&
                root.TryGetProperty("data", out var dataElement))
            {
                streamName = streamElement.GetString();
                payload = dataElement;
            }

            if (payload.TryGetProperty("e", out var eventType) && eventType.GetString() == "aggTrade")
            {
                string symbol = payload.GetProperty("s").GetString() ?? ExtractSymbolFromStream(streamName);
                string priceStr = payload.GetProperty("p").GetString() ?? "0";
                string qtyStr = payload.GetProperty("q").GetString() ?? "0";
                long ts = payload.GetProperty("T").GetInt64();
                bool isBuyerMaker = payload.GetProperty("m").GetBoolean(); // true = seller initiated

                if (decimal.TryParse(priceStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal price) &&
                    decimal.TryParse(qtyStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal qty))
                {
                    // Create zero-alloc struct
                    var tick = new MarketTick(symbol, price, qty, ts, isBuyerMaker);
                    OnTickReceived?.Invoke(tick);
                }
            }
            // Handling depth20 stream which doesn't have an "e" field, but has "bids" and "asks"
            else if (payload.TryGetProperty("bids", out var bidsElement) && payload.TryGetProperty("asks", out var asksElement))
            {
                string symbol = ExtractSymbolFromStream(streamName);
                
                var bids = ParseLevels(bidsElement);
                var asks = ParseLevels(asksElement);
                var snapshot = new OrderBookSnapshot(symbol, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), bids, asks);
                
                OnOrderBookUpdated?.Invoke(snapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Binance WS message: {Json}", json);
        }
    }

    private static string ExtractSymbolFromStream(string? streamName)
    {
        if (string.IsNullOrWhiteSpace(streamName))
        {
            return "UNKNOWN";
        }

        var separatorIndex = streamName.IndexOf('@');
        if (separatorIndex <= 0)
        {
            return streamName.ToUpperInvariant();
        }

        return streamName[..separatorIndex].ToUpperInvariant();
    }

    private OrderBookLevel[] ParseLevels(JsonElement element)
    {
        var count = element.GetArrayLength();
        var levels = new OrderBookLevel[count];
        for (int i = 0; i < count; i++)
        {
            var levelArr = element[i];
            var priceStr = levelArr[0].GetString() ?? "0";
            var qtyStr = levelArr[1].GetString() ?? "0";

            decimal.TryParse(priceStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal price);
            decimal.TryParse(qtyStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal qty);
            
            levels[i] = new OrderBookLevel(price, qty);
        }
        return levels;
    }

    public async Task DisconnectAsync()
    {
        if (_webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            _cts?.Cancel();
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            _webSocket.Dispose();
            _webSocket = null;
            _logger.LogInformation("Disconnected from WebSocket successfully.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _cts?.Dispose();
    }
}
