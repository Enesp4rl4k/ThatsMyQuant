using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Resilience;
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
    private readonly IAlertingService? _alertingService;
    private readonly int _maxReconnectAttempts;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private readonly HashSet<string> _activeSymbols = new();
    private int _reconnectAttempt = 0;
    private static readonly Uri OkxWsUri = new("wss://ws.okx.com:8443/ws/v5/public");

    public event Action<MarketTick>? OnTickReceived;
    public event Action<OrderBookSnapshot>? OnOrderBookUpdated;

    public OkxWebSocketService(
        ILogger<OkxWebSocketService> logger,
        IAlertingService? alertingService = null,
        int maxReconnectAttempts = 10)
    {
        _logger = logger;
        _alertingService = alertingService;
        _maxReconnectAttempts = maxReconnectAttempts;
    }

    /// <summary>Unit tests: simulates a books5 snapshot without a live socket.</summary>
    internal void RaiseOrderBookUpdatedForTests(OrderBookSnapshot snapshot) => OnOrderBookUpdated?.Invoke(snapshot);

    public async Task ConnectAsync(string[] symbols, CancellationToken cancellationToken)
    {
        if (symbols == null || symbols.Length == 0) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        foreach (var s in symbols) _activeSymbols.Add(NormalizeToOkxInstId(s));

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
                _reconnectAttempt = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var delay = BackoffCalculator.Compute(_reconnectAttempt);
                _logger.LogWarning(
                    "OKX WS reconnect attempt {Attempt} failed. Retrying in {Delay:F1}s. Error: {Message}",
                    _reconnectAttempt, delay.TotalSeconds, ex.Message);

                if (_reconnectAttempt >= _maxReconnectAttempts && _alertingService != null)
                {
                    await _alertingService.SendTradeAlertAsync(
                        "OKX_WS",
                        new TradeDecision { ShouldTrade = false, Reasoning = $"OkxWebSocket unrecoverable after {_reconnectAttempt} attempts." },
                        new IndicatorSnapshot(),
                        new AlertAuditContext { ExecutionMode = "WebSocketReconnect", Reasoning = $"Exhausted {_reconnectAttempt} reconnect attempts." });
                }

                _reconnectAttempt++;

                try
                {
                    await Task.Delay(delay, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ConnectCoreAsync(CancellationToken token)
    {
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();

        await _webSocket.ConnectAsync(OkxWsUri, token);
        _logger.LogInformation("Connected to OKX WebSocket with {Count} symbols.", _activeSymbols.Count);

        // Subscribe to trades and order book for each active symbol
        await SendBulkSubscribeAsync(token);
        await ReceiveLoopAsync(token);
    }

    private async Task SendBulkSubscribeAsync(CancellationToken token)
    {
        if (_activeSymbols.Count == 0) return;

        var tradeArgs = _activeSymbols.Select(s => new { channel = "trades", instId = s }).ToArray();
        var bookArgs = _activeSymbols.Select(s => new { channel = "books5", instId = s }).ToArray();

        var tradeSubscribe = JsonSerializer.Serialize(new { op = "subscribe", args = tradeArgs });
        var bookSubscribe = JsonSerializer.Serialize(new { op = "subscribe", args = bookArgs });

        await SendJsonAsync(tradeSubscribe, token);
        await SendJsonAsync(bookSubscribe, token);

        _logger.LogInformation("OKX subscribed to trades+books5 for: {Symbols}", string.Join(", ", _activeSymbols));
    }

    public async Task SubscribeAsync(string symbol)
    {
        var okxSymbol = NormalizeToOkxInstId(symbol);
        if (string.IsNullOrWhiteSpace(okxSymbol) || _activeSymbols.Contains(okxSymbol)) return;

        _activeSymbols.Add(okxSymbol);

        if (_webSocket?.State == WebSocketState.Open)
        {
            var tradeSubscribe = JsonSerializer.Serialize(new
            {
                op = "subscribe",
                args = new[] { new { channel = "trades", instId = okxSymbol } }
            });
            var bookSubscribe = JsonSerializer.Serialize(new
            {
                op = "subscribe",
                args = new[] { new { channel = "books5", instId = okxSymbol } }
            });

            await SendJsonAsync(tradeSubscribe, CancellationToken.None);
            await SendJsonAsync(bookSubscribe, CancellationToken.None);
            _logger.LogInformation("OKX: Dynamically subscribed to {Symbol}.", okxSymbol);
        }
    }

    public async Task UnsubscribeAsync(string symbol)
    {
        var okxSymbol = NormalizeToOkxInstId(symbol);
        if (string.IsNullOrWhiteSpace(okxSymbol) || !_activeSymbols.Contains(okxSymbol)) return;

        _activeSymbols.Remove(okxSymbol);

        if (_webSocket?.State == WebSocketState.Open)
        {
            var tradeUnsub = JsonSerializer.Serialize(new
            {
                op = "unsubscribe",
                args = new[] { new { channel = "trades", instId = okxSymbol } }
            });
            var bookUnsub = JsonSerializer.Serialize(new
            {
                op = "unsubscribe",
                args = new[] { new { channel = "books5", instId = okxSymbol } }
            });

            await SendJsonAsync(tradeUnsub, CancellationToken.None);
            await SendJsonAsync(bookUnsub, CancellationToken.None);
            _logger.LogInformation("OKX: Dynamically unsubscribed from {Symbol}.", okxSymbol);
        }
    }

    private async Task SendJsonAsync(string json, CancellationToken token)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
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
        if (span.Length < 10) return;

        // Quick route: check for channel type to identify data messages
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
        // else: subscription confirmation, pong, or event — ignore
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

                var price = ParseDecimal(trade.GetProperty("px"));
                var qty = ParseDecimal(trade.GetProperty("sz"));
                var ts = long.Parse(trade.GetProperty("ts").GetString() ?? "0", CultureInfo.InvariantCulture);
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
        //  "data":[{"asks":[["60001","0.5","0","1"],...],
        //           "bids":[["59999","0.3","0","1"],...],
        //           "ts":"1700000000000"}]}
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
                var ts = long.Parse(snapshot.GetProperty("ts").GetString() ?? "0", CultureInfo.InvariantCulture);

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
            var price = ParseDecimal(level[0]);
            var qty = ParseDecimal(level[1]);
            levels.Add(new OrderBookLevel(price, qty));
        }
        return levels.ToArray();
    }

    /// <summary>
    /// Convert "BTCUSDT" → "BTC-USDT", "ETH-USDT" passthrough for OKX.
    /// Also handles "BTC-USDT" format input directly.
    /// </summary>
    internal static string NormalizeToOkxInstId(string symbol)
    {
        if (symbol.Contains('-')) return symbol.ToUpperInvariant();

        // Common quote currencies in order of specificity
        string[] quotes = ["USDT", "USDC", "USD", "BTC", "ETH", "TRY"];
        foreach (var q in quotes)
        {
            if (symbol.EndsWith(q, StringComparison.OrdinalIgnoreCase) && symbol.Length > q.Length)
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
        => okxInstId.Replace("-", "").ToUpperInvariant();

    private static decimal ParseDecimal(JsonElement element)
    {
        var raw = element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDecimal().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => element.GetString(),
            _ => null
        };

        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
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
