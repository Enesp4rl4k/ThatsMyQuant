using System.Buffers;
using System.Buffers.Text;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Infrastructure.Services;

public sealed class BinanceWebSocketService : IStreamingMarketDataService, IAsyncDisposable
{
    private readonly ILogger<BinanceWebSocketService> _logger;
    private readonly string _binanceBaseUrl;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private string[] _symbols = Array.Empty<string>();
    private long _lastFrameReceivedTicks;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(30);

    // HFT Optimization: Markers as static readonly Spans
    private static readonly byte[] AggTradeMarker = Encoding.UTF8.GetBytes("aggTrade");
    private static readonly byte[] PongPayload = Encoding.UTF8.GetBytes("pong");

    public event Action<MarketTick>? OnTickReceived;
    public event Action<OrderBookSnapshot>? OnOrderBookUpdated;

    public BinanceWebSocketService(ILogger<BinanceWebSocketService> logger, IOptions<WebSocketOptions>? wsOptions = null)
    {
        _logger = logger;
        _binanceBaseUrl = wsOptions?.Value?.BinanceBaseUrl ?? "wss://stream.binance.com:9443";
    }

    public async Task ConnectAsync(string[] symbols, CancellationToken cancellationToken)
    {
        if (symbols == null || symbols.Length == 0) return;

        _symbols = symbols;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _ = ReconnectLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    private async Task ReconnectLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _webSocket?.Dispose();
                _webSocket = new ClientWebSocket();

                var streamParams = string.Join("/", _symbols.Select(s =>
                {
                    var f = s.Replace("-", "").ToLowerInvariant();
                    return $"{f}@aggTrade/{f}@depth20@100ms";
                }));

                var uri = new Uri($"{_binanceBaseUrl}/stream?streams={streamParams}");
                await _webSocket.ConnectAsync(uri, token);
                _logger.LogInformation("Connected to Binance WebSocket for streams: {Streams}", streamParams);
                Volatile.Write(ref _lastFrameReceivedTicks, DateTime.UtcNow.Ticks);

                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var receiveTask = ReceiveLoopAsync(sessionCts.Token);
                var heartbeatTask = HeartbeatWatchdogAsync(sessionCts);

                // If either task completes, cancel the other and tear down the session
                await Task.WhenAny(receiveTask, heartbeatTask).ConfigureAwait(false);
                await sessionCts.CancelAsync();

                // Await both to observe any exceptions (prevent UnobservedTaskException)
                try { await receiveTask; } catch (OperationCanceledException) { }
                try { await heartbeatTask; } catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Binance WS disconnected. Reconnecting in {Delay}s...", ReconnectDelay.TotalSeconds);
            }

            if (!token.IsCancellationRequested)
                await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        using var accumulator = new System.IO.MemoryStream(65536);

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                // Per-receive timeout breaks TCP half-open deadlocks:
                // if no frame arrives within ReceiveTimeout, the CTS fires and
                // ReceiveAsync throws OperationCanceledException → reconnect.
                using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                recvCts.CancelAfter(ReceiveTimeout);

                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), recvCts.Token);
                Volatile.Write(ref _lastFrameReceivedTicks, DateTime.UtcNow.Ticks);

                if (result.MessageType == WebSocketMessageType.Close) break;

                accumulator.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    ParseMessageCore(new ReadOnlyMemory<byte>(accumulator.GetBuffer(), 0, (int)accumulator.Length));
                    accumulator.SetLength(0);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Per-receive timeout fired (not app shutdown) — treat as stale connection
            _logger.LogWarning("Binance WS receive timed out after {Timeout}s — forcing reconnect.", ReceiveTimeout.TotalSeconds);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "WS receive loop error — will reconnect.");
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Sends a WebSocket ping every HeartbeatInterval seconds and verifies frames are
    /// still arriving. Kills the session if the connection is silently dead.
    /// </summary>
    private async Task HeartbeatWatchdogAsync(CancellationTokenSource sessionCts)
    {
        var token = sessionCts.Token;
        try
        {
            while (!token.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                await Task.Delay(HeartbeatInterval, token).ConfigureAwait(false);

                // Check: did we receive any frame since last check?
                var lastTicks = Volatile.Read(ref _lastFrameReceivedTicks);
                var silenceSec = (DateTime.UtcNow.Ticks - lastTicks) / TimeSpan.TicksPerSecond;

                if (silenceSec >= ReceiveTimeout.TotalSeconds)
                {
                    _logger.LogWarning(
                        "Binance WS heartbeat: no frame for {Silence}s (threshold={Threshold}s) — killing session.",
                        silenceSec, (int)ReceiveTimeout.TotalSeconds);
                    await sessionCts.CancelAsync();
                    return;
                }

                // Send a WebSocket-level ping to provoke a TCP error on half-open sockets.
                // If the TCP connection is dead, this write will throw → session dies → reconnect.
                try
                {
                    if (_webSocket?.State == WebSocketState.Open)
                    {
                        await _webSocket.SendAsync(
                            new ArraySegment<byte>(PongPayload),
                            WebSocketMessageType.Text,
                            true,
                            token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Binance WS ping send failed — connection is dead.");
                    await sessionCts.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
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
        // Parse depth data with Utf8JsonReader for zero-allocation processing
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
