using System.Buffers;
using System.Buffers.Text;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Resilience;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

public class BinanceWebSocketService : IStreamingMarketDataService, IAsyncDisposable
{
    private const int DepthLevelsCapacity = 20;
    private readonly ILogger<BinanceWebSocketService> _logger;
    private readonly IAlertingService? _alertingService;
    private readonly int _maxReconnectAttempts;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private readonly HashSet<string> _activeSymbols = new();
    private readonly OrderBookLevel[] _bidScratch = new OrderBookLevel[DepthLevelsCapacity];
    private readonly OrderBookLevel[] _askScratch = new OrderBookLevel[DepthLevelsCapacity];
    private int _requestId = 0;
    private int _reconnectAttempt = 0;

    // HFT Optimization: Markers as static readonly Spans
    private static readonly byte[] AggTradeMarker = Encoding.UTF8.GetBytes("aggTrade");

    public event Action<MarketTick>? OnTickReceived;
    public event Action<OrderBookSnapshot>? OnOrderBookUpdated;

    public BinanceWebSocketService(
        ILogger<BinanceWebSocketService> logger,
        IAlertingService? alertingService = null,
        int maxReconnectAttempts = 10)
    {
        _logger = logger;
        _alertingService = alertingService;
        _maxReconnectAttempts = maxReconnectAttempts;
    }

    public async Task ConnectAsync(string[] symbols, CancellationToken cancellationToken)
    {
        if (symbols == null || symbols.Length == 0) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        foreach (var s in symbols) _activeSymbols.Add(s);

        _ = RunWithReconnectAsync(_cts.Token);

        await Task.CompletedTask;
    }

    private async Task RunWithReconnectAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ConnectCoreAsync(token);
                _reconnectAttempt = 0; // reset on success
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var delay = BackoffCalculator.Compute(_reconnectAttempt);
                _logger.LogWarning(
                    "WS reconnect attempt {Attempt} failed. Retrying in {Delay:F1}s. Error: {Message}",
                    _reconnectAttempt, delay.TotalSeconds, ex.Message);

                if (_reconnectAttempt >= _maxReconnectAttempts && _alertingService != null)
                {
                    await _alertingService.SendTradeAlertAsync(
                        "BINANCE_WS",
                        new TradeDecision { ShouldTrade = false, Reasoning = $"BinanceWebSocket unrecoverable after {_reconnectAttempt} attempts." },
                        new IndicatorSnapshot(),
                        new AlertAuditContext { ExecutionMode = "WebSocketReconnect", Reasoning = $"Exhausted {_reconnectAttempt} reconnect attempts." });
                }

                _reconnectAttempt++;

                try
                {
                    await DelayAsync(delay, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    protected virtual Task DelayAsync(TimeSpan delay, CancellationToken token)
        => Task.Delay(delay, token);

    protected virtual async Task ConnectCoreAsync(CancellationToken token)
    {
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();

        var streamParams = string.Join("/", _activeSymbols.Select(s =>
        {
            var f = s.Replace("-", "").ToLowerInvariant();
            return $"{f}@aggTrade/{f}@depth20@100ms";
        }));

        var uri = new Uri($"wss://stream.binance.com:9443/stream?streams={streamParams}");
        await _webSocket.ConnectAsync(uri, token);
        _logger.LogInformation("Connected to Binance WebSocket with {Count} symbols.", _activeSymbols.Count);

        await ReceiveLoopAsync(token);
    }

    public async Task SubscribeAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || _activeSymbols.Contains(symbol)) return;

        _activeSymbols.Add(symbol);
        if (_webSocket?.State == WebSocketState.Open)
        {
            var f = symbol.Replace("-", "").ToLowerInvariant();
            await SendCommandAsync("SUBSCRIBE", new[] { $"{f}@aggTrade", $"{f}@depth20@100ms" });
            _logger.LogInformation("Subscribed to {Symbol} dynamically.", symbol);
        }
    }

    public async Task UnsubscribeAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || !_activeSymbols.Contains(symbol)) return;

        _activeSymbols.Remove(symbol);
        if (_webSocket?.State == WebSocketState.Open)
        {
            var f = symbol.Replace("-", "").ToLowerInvariant();
            await SendCommandAsync("UNSUBSCRIBE", new[] { $"{f}@aggTrade", $"{f}@depth20@100ms" });
            _logger.LogInformation("Unsubscribed from {Symbol} dynamically.", symbol);
        }
    }

    private async Task SendCommandAsync(string method, string[] streams)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;

        var command = new
        {
            method = method,
            @params = streams,
            id = Interlocked.Increment(ref _requestId)
        };

        var json = JsonSerializer.Serialize(command);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
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
            throw; // rethrow so RunWithReconnectAsync can handle reconnect
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
        var bidCount = 0;
        var askCount = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("stream")) { reader.Read(); symbol = ExtractSymbol(reader.ValueSpan); }
                else if (reader.ValueTextEquals("bids")) ParseLevelsFast(ref reader, _bidScratch, out bidCount);
                else if (reader.ValueTextEquals("asks")) ParseLevelsFast(ref reader, _askScratch, out askCount);
            }
        }

        var bids = bidCount == 0 ? Array.Empty<OrderBookLevel>() : new OrderBookLevel[bidCount];
        if (bidCount > 0)
        {
            Array.Copy(_bidScratch, bids, bidCount);
        }

        var asks = askCount == 0 ? Array.Empty<OrderBookLevel>() : new OrderBookLevel[askCount];
        if (askCount > 0)
        {
            Array.Copy(_askScratch, asks, askCount);
        }

        OnOrderBookUpdated?.Invoke(new OrderBookSnapshot(symbol, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), bids, asks));
    }

    private static void ParseLevelsFast(ref Utf8JsonReader reader, OrderBookLevel[] target, out int count)
    {
        count = 0;
        reader.Read(); // Start Array
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            reader.Read(); // Start inner array
            Utf8Parser.TryParse(reader.ValueSpan, out decimal p, out _);
            reader.Read();
            Utf8Parser.TryParse(reader.ValueSpan, out decimal q, out _);
            reader.Read(); // End inner array
            if (count < target.Length)
            {
                target[count++] = new OrderBookLevel(p, q);
            }
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
            if (_cts != null)
            {
                await _cts.CancelAsync();
                _cts.Dispose();
            }
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
