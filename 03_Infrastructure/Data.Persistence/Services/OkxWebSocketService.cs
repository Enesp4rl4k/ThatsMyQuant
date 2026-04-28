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

    /// <summary>
    /// [P1.1] Fired when a liquidation order arrives on OKX's public
    /// <c>liquidation-orders</c> channel. The payload uses the
    /// <see cref="ThatsMyQuant.Application.Strategy.LiquidationCascadeFader.LiquidationEvent"/>
    /// shape: <c>Side</c> follows the fader's convention (Buy = a long was liquidated;
    /// Sell = a short was liquidated). NotionalUsd is approximate
    /// (<c>sz × bkPx</c>; ctVal is NOT applied here, so trigger thresholds in
    /// <c>LiquidationCascadeOptions</c> must be calibrated against this proxy).
    /// </summary>
    public event Action<ThatsMyQuant.Application.Strategy.LiquidationCascadeFader.LiquidationEvent>? OnLiquidationReceived;

    /// <summary>
    /// [P1.6] Fired on each OKX <c>open-interest</c> channel update for an active
    /// SWAP symbol. Payload: (symbol, oiContracts, oiCcy, lastPrice or 0, timestamp).
    /// Consumers (OiFlowStateService) maintain rolling history and classify regimes.
    /// </summary>
    public event Action<OpenInterestUpdate>? OnOpenInterestReceived;

    /// <summary>
    /// Open-interest snapshot from OKX. <paramref name="OiContracts"/> = number of
    /// open contracts (instrument-specific units); <paramref name="OiCcy"/> = same
    /// quantity in base currency. <paramref name="LastPrice"/> is 0 when not piggy-
    /// backed; consumers should pair with the latest tick price.
    /// </summary>
    public readonly record struct OpenInterestUpdate(
        string Symbol,
        decimal OiContracts,
        decimal OiCcy,
        DateTime Timestamp);

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
        // [P1.6] Subscribe to open-interest only on perpetual SWAP instIds (OKX rejects
        // non-perp instIds on this channel). Spot/options symbols are silently skipped.
        var oiArgs = _activeSymbols
            .Where(s => s.EndsWith("-SWAP", StringComparison.OrdinalIgnoreCase))
            .Select(s => new { channel = "open-interest", instId = s }).ToArray();

        var tradeSubscribe = JsonSerializer.Serialize(new { op = "subscribe", args = tradeArgs });
        var bookSubscribe = JsonSerializer.Serialize(new { op = "subscribe", args = bookArgs });

        await SendJsonAsync(tradeSubscribe, token);
        await SendJsonAsync(bookSubscribe, token);
        if (oiArgs.Length > 0)
        {
            var oiSubscribe = JsonSerializer.Serialize(new { op = "subscribe", args = oiArgs });
            await SendJsonAsync(oiSubscribe, token);
        }

        // [P1.1] Single global subscription to SWAP liquidation orders. OKX publishes
        // ALL perp liquidations on this channel (filtered by instType, not instId), so
        // we subscribe once and dispatch per-symbol downstream.
        var liqSubscribe = JsonSerializer.Serialize(new
        {
            op = "subscribe",
            args = new[] { new { channel = "liquidation-orders", instType = "SWAP" } }
        });
        await SendJsonAsync(liqSubscribe, token);

        _logger.LogInformation("OKX subscribed to trades+books5 for: {Symbols} (+ liquidation-orders SWAP)", string.Join(", ", _activeSymbols));
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
        var channelLiquidationsMarker = "\"liquidation-orders\""u8;
        var channelOpenInterestMarker = "\"open-interest\""u8;

        if (span.IndexOf(channelTradesMarker) != -1)
        {
            ParseTrades(span);
        }
        else if (span.IndexOf(channelBooksMarker) != -1)
        {
            ParseOrderBook(span);
        }
        else if (span.IndexOf(channelLiquidationsMarker) != -1)
        {
            ParseLiquidations(span);
        }
        else if (span.IndexOf(channelOpenInterestMarker) != -1)
        {
            ParseOpenInterest(span);
        }
        // else: subscription confirmation, pong, or event — ignore
    }

    /// <summary>
    /// [P1.6] Parse OKX <c>open-interest</c> channel messages.
    /// Format: {"arg":{"channel":"open-interest","instId":"BTC-USDT-SWAP"},
    ///          "data":[{"instId":"BTC-USDT-SWAP","instType":"SWAP","oi":"123","oiCcy":"5","ts":"..."}]}
    /// </summary>
    private void ParseOpenInterest(ReadOnlySpan<byte> span)
    {
        try
        {
            using var doc = JsonDocument.Parse(span.ToArray());
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var dataArr)) return;

            foreach (var entry in dataArr.EnumerateArray())
            {
                var instId = entry.TryGetProperty("instId", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(instId)) continue;
                var symbol = NormalizeToBinanceSymbol(StripSwapSuffix(instId));

                var oi = entry.TryGetProperty("oi", out var oiEl) ? ParseDecimal(oiEl) : 0m;
                var oiCcy = entry.TryGetProperty("oiCcy", out var oiCcyEl) ? ParseDecimal(oiCcyEl) : 0m;
                long ts = 0;
                if (entry.TryGetProperty("ts", out var tsEl) && tsEl.GetString() is string tsStr)
                    long.TryParse(tsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out ts);
                var timestamp = ts > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime
                    : DateTime.UtcNow;

                if (oi <= 0m && oiCcy <= 0m) continue;

                OnOpenInterestReceived?.Invoke(new OpenInterestUpdate(symbol, oi, oiCcy, timestamp));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse OKX open-interest message.");
        }
    }

    /// <summary>
    /// [P1.1] Parse OKX <c>liquidation-orders</c> channel messages.
    ///
    /// Wire format:
    /// <code>
    /// {"arg":{"channel":"liquidation-orders","instType":"SWAP"},
    ///  "data":[{
    ///    "details":[{"side":"sell","sz":"10","bkPx":"60000","posSide":"long","ts":"1700000000000"}],
    ///    "instId":"BTC-USDT-SWAP","instFamily":"BTC-USDT","instType":"SWAP","uly":"BTC-USDT"
    ///  }]}
    /// </code>
    /// Side mapping (OKX → fader):
    ///   posSide=long  → long was liquidated → fader Side = Buy
    ///   posSide=short → short was liquidated → fader Side = Sell
    /// Falls back to <c>side</c> ("sell" = sell-into-book to close long → Buy).
    /// Notional is approximated as <c>sz × bkPx</c> without ctVal — see event docstring.
    /// </summary>
    private void ParseLiquidations(ReadOnlySpan<byte> span)
    {
        try
        {
            using var doc = JsonDocument.Parse(span.ToArray());
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var dataArr)) return;

            foreach (var entry in dataArr.EnumerateArray())
            {
                var instId = entry.TryGetProperty("instId", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(instId)) continue;
                var symbol = NormalizeToBinanceSymbol(StripSwapSuffix(instId));

                if (!entry.TryGetProperty("details", out var detailsArr)) continue;
                foreach (var detail in detailsArr.EnumerateArray())
                {
                    var sz = ParseDecimal(detail.TryGetProperty("sz", out var szEl) ? szEl : default);
                    var bkPx = ParseDecimal(detail.TryGetProperty("bkPx", out var pxEl) ? pxEl : default);
                    if (sz <= 0m || bkPx <= 0m) continue;

                    Contracts.Enums.TradeSide side;
                    if (detail.TryGetProperty("posSide", out var posEl) &&
                        posEl.GetString() is string posSide &&
                        !string.IsNullOrWhiteSpace(posSide))
                    {
                        side = string.Equals(posSide, "long", StringComparison.OrdinalIgnoreCase)
                            ? Contracts.Enums.TradeSide.Buy
                            : Contracts.Enums.TradeSide.Sell;
                    }
                    else if (detail.TryGetProperty("side", out var sideEl) &&
                             sideEl.GetString() is string okxSide)
                    {
                        // Sell order placed → closing a long → fader Buy.
                        side = string.Equals(okxSide, "sell", StringComparison.OrdinalIgnoreCase)
                            ? Contracts.Enums.TradeSide.Buy
                            : Contracts.Enums.TradeSide.Sell;
                    }
                    else continue;

                    long ts = 0;
                    if (detail.TryGetProperty("ts", out var tsEl) && tsEl.GetString() is string tsStr)
                        long.TryParse(tsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out ts);
                    var timestamp = ts > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime
                        : DateTime.UtcNow;

                    var notional = sz * bkPx;
                    OnLiquidationReceived?.Invoke(
                        new ThatsMyQuant.Application.Strategy.LiquidationCascadeFader.LiquidationEvent(
                            timestamp, symbol, side, notional));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse OKX liquidation-orders message.");
        }
    }

    /// <summary>BTC-USDT-SWAP → BTC-USDT (the market-data symbol). Idempotent for non-SWAP ids.</summary>
    private static string StripSwapSuffix(string instId)
    {
        const string suffix = "-SWAP";
        return instId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? instId[..^suffix.Length]
            : instId;
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
