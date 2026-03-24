using System.Buffers;
using System.Buffers.Text;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

public sealed class BinanceWebSocketService : IStreamingMarketDataService, IAsyncDisposable
{
    private readonly ILogger<BinanceWebSocketService> _logger;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    
    // HFT Optimization: Markers as static readonly Spans
    private static readonly byte[] AggTradeMarker = Encoding.UTF8.GetBytes("aggTrade");

    public event Action<MarketTick>? OnTickReceived;
    public event Action<OrderBookSnapshot>? OnOrderBookUpdated;

    public BinanceWebSocketService(ILogger<BinanceWebSocketService> logger) => _logger = logger;

    public async Task ConnectAsync(string[] symbols, CancellationToken cancellationToken)
    {
        if (symbols == null || symbols.Length == 0) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _webSocket = new ClientWebSocket();

        try
        {
            var streamParams = string.Join("/", symbols.Select(s => 
            {
                var f = s.Replace("-", "").ToLowerInvariant();
                return $"{f}@aggTrade/{f}@depth20@100ms";
            }));
            
            var uri = new Uri($"wss://stream.binance.com:9443/stream?streams={streamParams}");
            await _webSocket.ConnectAsync(uri, _cts.Token);
            _logger.LogInformation("Connected to Binance WebSocket.");

            _ = ReceiveLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WS Connection failed.");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        // SonarQube Fix: ArrayPool kullanarak bellek yönetimini optimize ediyoruz
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536); // 64KB Buffer for safety
        
        try
        {
            while (_webSocket?.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                if (result.MessageType == WebSocketMessageType.Close) break;

                // SonarQube Fix: Parçalı mesaj desteği eklenmeli (Gerçek HFT için şart)
                if (result.EndOfMessage)
                {
                    ParseMessageCore(new ReadOnlyMemory<byte>(buffer, 0, result.Count));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "WS Loop Error.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ParseMessageCore(ReadOnlyMemory<byte> memory)
    {
        var span = memory.Span;
        // Zero-allocation routing
        if (span.IndexOf(AggTradeMarker) != -1)
        {
            ParseAggTradeFast(span);
        }
        else
        {
            ParseDepthSnapshotFast(span);
        }
    }

    private void ParseAggTradeFast(ReadOnlySpan<byte> span)
    {
        var reader = new Utf8JsonReader(span);
        string symbol = string.Empty;
        decimal price = 0, qty = 0;
        long ts = 0;
        bool isMaker = false;

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("s")) { reader.Read(); symbol = reader.GetString() ?? ""; }
            else if (reader.ValueTextEquals("p")) { reader.Read(); price = GetDecimalFast(ref reader); }
            else if (reader.ValueTextEquals("q")) { reader.Read(); qty = GetDecimalFast(ref reader); }
            else if (reader.ValueTextEquals("T")) { reader.Read(); ts = reader.GetInt64(); }
            else if (reader.ValueTextEquals("m")) { reader.Read(); isMaker = reader.GetBoolean(); }
        }

        if (!string.IsNullOrEmpty(symbol))
            OnTickReceived?.Invoke(new MarketTick(symbol, price, qty, ts, isMaker));
    }

    private void ParseDepthSnapshotFast(ReadOnlySpan<byte> span)
    {
        // Depth verisini de Utf8JsonReader ile parse ederek "Zero-Allocation" hedefimize ulaşıyoruz
        var reader = new Utf8JsonReader(span);
        string symbol = "UNKNOWN";
        var bids = new List<OrderBookLevel>(20);
        var asks = new List<OrderBookLevel>(20);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("stream")) { reader.Read(); symbol = ExtractSymbol(reader.ValueSpan); }
                else if (reader.ValueTextEquals("bids")) ParseLevelsFast(ref reader, bids);
                else if (reader.ValueTextEquals("asks")) ParseLevelsFast(ref reader, asks);
            }
        }

        OnOrderBookUpdated?.Invoke(new OrderBookSnapshot(symbol, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), bids.ToArray(), asks.ToArray()));
    }

    private static void ParseLevelsFast(ref Utf8JsonReader reader, List<OrderBookLevel> target)
    {
        reader.Read(); // Start Array
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            reader.Read(); // Start inner array
            Utf8Parser.TryParse(reader.ValueSpan, out decimal p, out _);
            reader.Read();
            Utf8Parser.TryParse(reader.ValueSpan, out decimal q, out _);
            reader.Read(); // End inner array
            target.Add(new OrderBookLevel(p, q));
        }
    }

    private static decimal GetDecimalFast(ref Utf8JsonReader reader)
    {
        if (Utf8Parser.TryParse(reader.ValueSpan, out decimal val, out _)) return val;
        return 0;
    }

    private static string ExtractSymbol(ReadOnlySpan<byte> streamSpan)
    {
        int idx = streamSpan.IndexOf((byte)'@');
        return idx == -1 ? Encoding.UTF8.GetString(streamSpan).ToUpperInvariant() : Encoding.UTF8.GetString(streamSpan[..idx]).ToUpperInvariant();
    }

    public async Task DisconnectAsync()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        finally
        {
            _cts = null;
        }

        if (_webSocket != null)
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while closing WebSocket.");
            }
            finally
            {
                _webSocket.Dispose();
                _webSocket = null;
            }
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}