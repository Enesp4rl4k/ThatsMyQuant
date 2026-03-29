using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// OKX WebSocket market data service. Connects to OKX's public WebSocket v5 API
/// for real-time trades and order book snapshots. Implements the same IStreamingMarketDataService
/// interface as BinanceWebSocketService for seamless exchange swapping.
///
/// OKX WS docs: wss://ws.okx.com:8443/ws/v5/public
/// Subscribe format: {"op":"subscribe","args":[{"channel":"trades","instId":"BTC-USDT"}]}
/// </summary>
public sealed class OkxWebSocketService : IStreamingMarketDataService, IAsyncDisposable
{
    private readonly ILogger<OkxWebSocketService> _logger;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private string[] _symbols = Array.Empty<string>();
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly Uri OkxWsUri = new("wss://ws.okx.com:8443/ws/v5/public");

    public event Action<MarketTick>? OnTickReceived;
    public event Action<OrderBookSnapshot>? OnOrderBookUpdated;

    public OkxWebSocketService(ILogger<OkxWebSocketService> logger) => _logger = logger;

    public async Task ConnectAsync(string[] symbols, CancellationToken cancellationToken)
    {
        if (symbols == null || symbols.Length == 0) return;

        // OKX uses "BTC-USDT" format. Convert from Binance-style "BTCUSDT" if needed.
        _symbols = symbols.Select(NormalizeToOkxInstId).ToArray();
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

                await _webSocket.ConnectAsync(OkxWsUri, token);
                _logger.LogInformation("Connected to OKX WebSocket.");

                await SubscribeAsync(token);
                await ReceiveLoopAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OKX WS disconnected. Reconnecting in {Delay}s...", ReconnectDelay.TotalSeconds);
            }

            if (!token.IsCancellationRequested)
                await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
        }
    }

    private async Task SubscribeAsync(CancellationToken token)
    {
        // Subscribe to trades and order book for each symbol
        var tradeArgs = _symbols.Select(s => new { channel = "trades", instId = s });
        var bookArgs = _symbols.Select(s => new { channel = "books5", instId = s }); // top 5 levels

        var tradeSubscribe = JsonSerializer.Serialize(new { op = "subscribe", args = tradeArgs.ToArray() });
        var bookSubscribe = JsonSerializer.Serialize(new { op = "subscribe", args = bookArgs.ToArray() });

        var tradeBytes = Encoding.UTF8.GetBytes(tradeSubscribe);
        var bookBytes = Encoding.UTF8.GetBytes(bookSubscribe);

        await _webSocket!.SendAsync(new ArraySegment<byte>(tradeBytes), WebSocketMessageType.Text, true, token);
        await _webSocket.SendAsync(new ArraySegment<byte>(bookBytes), WebSocketMessageType.Text, true, token);

        _logger.LogInformation("OKX subscribed to trades+books5 for: {Symbols}", string.Join(", ", _symbols));
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        using var accumulator = new MemoryStream(65536);

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                if (result.MessageType == WebSocketMessageType.Close) break;

                accumulator.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    ParseMessage(accumulator.GetBuffer().AsSpan(0, (int)accumulator.Length));
                    accumulator.SetLength(0);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "OKX WS receive loop error — will reconnect.");
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ParseMessage(ReadOnlySpan<byte> span)
    {
        // OKX sends: {"arg":{"channel":"trades","instId":"BTC-USDT"},"data":[...]}
        // Also sends pings: "ping" and subscription confirmations with "event" field
        // Skip non-data messages
        if (span.Length < 10) return;

        // Quick route: check for "channel" to identify data messages
        var channelTradesMarker = "\"trades\""u8;
        var channelBooksMarker = "\"books5\""u8;

        if (span.IndexOf(channelTradesMarker) != -1)
        {
            ParseTrades(span);
        }
        else if (span.IndexOf(channelBooksMarker) != -1)
        {
            ParseOrderBook(span);
        }
        // else: subscription confirmation or ping — ignore
    }

    private void ParseTrades(ReadOnlySpan<byte> span)
    {
        // OKX trade format:
        // {"arg":{"channel":"trades","instId":"BTC-USDT"},
        //  "data":[{"instId":"BTC-USDT","tradeId":"123","px":"60000.5","sz":"0.01","side":"buy","ts":"1700000000000"}]}
        try
        {
            using var doc = JsonDocument.Parse(span.ToArray());
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var dataArr)) return;

            foreach (var trade in dataArr.EnumerateArray())
            {
                var instId = trade.GetProperty("instId").GetString() ?? "";
                var symbol = NormalizeToBinanceSymbol(instId);
                var price = decimal.Parse(trade.GetProperty("px").GetString() ?? "0");
                var qty = decimal.Parse(trade.GetProperty("sz").GetString() ?? "0");
                var ts = long.Parse(trade.GetProperty("ts").GetString() ?? "0");
                var side = trade.GetProperty("side").GetString();
                var isMaker = string.Equals(side, "sell", StringComparison.OrdinalIgnoreCase);

                OnTickReceived?.Invoke(new MarketTick(symbol, price, qty, ts, isMaker));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse OKX trade message.");
        }
    }

    private void ParseOrderBook(ReadOnlySpan<byte> span)
    {
        // OKX books5 format:
        // {"arg":{"channel":"books5","instId":"BTC-USDT"},
        //  "data":[{"asks":[["60001","0.5","0","1"],...],"bids":[["59999","0.3","0","1"],...],"ts":"1700000000000"}]}
        try
        {
            using var doc = JsonDocument.Parse(span.ToArray());
            var root = doc.RootElement;

            string instId = "";
            if (root.TryGetProperty("arg", out var arg))
                instId = arg.GetProperty("instId").GetString() ?? "";

            var symbol = NormalizeToBinanceSymbol(instId);

            if (!root.TryGetProperty("data", out var dataArr)) return;

            foreach (var snapshot in dataArr.EnumerateArray())
            {
                var bids = ParseLevels(snapshot, "bids");
                var asks = ParseLevels(snapshot, "asks");
                var ts = long.Parse(snapshot.GetProperty("ts").GetString() ?? "0");

                OnOrderBookUpdated?.Invoke(new OrderBookSnapshot(symbol, ts, bids, asks));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse OKX order book message.");
        }
    }

    private static OrderBookLevel[] ParseLevels(JsonElement snapshot, string property)
    {
        if (!snapshot.TryGetProperty(property, out var arr)) return Array.Empty<OrderBookLevel>();

        var levels = new List<OrderBookLevel>(5);
        foreach (var level in arr.EnumerateArray())
        {
            // OKX format: [price, qty, deprecated, orders]
            var price = decimal.Parse(level[0].GetString() ?? "0");
            var qty = decimal.Parse(level[1].GetString() ?? "0");
            levels.Add(new OrderBookLevel(price, qty));
        }
        return levels.ToArray();
    }

    /// <summary>
    /// Convert "BTCUSDT" → "BTC-USDT", "ETHUSDT" → "ETH-USDT" for OKX.
    /// Also handles already-formatted "BTC-USDT" passthrough.
    /// </summary>
    internal static string NormalizeToOkxInstId(string symbol)
    {
        if (symbol.Contains('-')) return symbol.ToUpperInvariant();

        // Common quote currencies in order of specificity
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

    /// <summary>
    /// Convert "BTC-USDT" → "BTCUSDT" for internal symbol consistency.
    /// </summary>
    internal static string NormalizeToBinanceSymbol(string okxInstId)
    {
        return okxInstId.Replace("-", "").ToUpperInvariant();
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
                _logger.LogWarning(ex, "Error closing OKX WebSocket.");
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
