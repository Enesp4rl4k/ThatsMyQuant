using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Listener.Api.Hubs;
using Listener.Api.Endpoints;

namespace Listener.Api.Services;

/// <summary>
/// Connects to the OKX public WebSocket and subscribes to the <c>books5</c> channel
/// (5-level snapshot, pushed every ~100 ms) for every symbol that has at least one
/// active SignalR viewer.  Fans snapshots out to the SignalR group <c>ob:{symbol}</c>
/// so that only the interested connections receive each message.
///
/// Reconnects automatically on any error.  Does not require OKX credentials
/// (books5 is a public channel).
/// </summary>
public sealed class OkxOrderBookSubscriber : BackgroundService
{
    private const string WsUrl = "wss://ws.okx.com:8443/ws/v5/public";
    private const int PingIntervalSeconds = 20; // OKX requires heartbeat within 25 s

    private readonly IHubContext<MarketDataHub> _hubContext;
    private readonly ILogger<OkxOrderBookSubscriber> _logger;

    // symbol (e.g. BTCUSDT) → active subscriber count
    private readonly ConcurrentDictionary<string, int> _subscriptions =
        new(StringComparer.OrdinalIgnoreCase);

    private ClientWebSocket? _ws;
    private CancellationToken _runCt;

    public OkxOrderBookSubscriber(
        IHubContext<MarketDataHub> hubContext,
        ILogger<OkxOrderBookSubscriber> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    // ─────────────────────────── public API (called by hub) ──────────────────

    /// <summary>Increment subscriber count; subscribe to WS channel when first viewer joins.</summary>
    public async Task AddSubscriberAsync(string symbol)
    {
        var count = _subscriptions.AddOrUpdate(symbol, 1, (_, v) => v + 1);
        _logger.LogDebug("[OKX OB] +subscriber {Symbol} → {Count}", symbol, count);
        if (count == 1 && _ws?.State == WebSocketState.Open)
            await SendSubscribeAsync(symbol, _runCt).ConfigureAwait(false);
    }

    /// <summary>Decrement subscriber count; unsubscribe from WS channel when last viewer leaves.</summary>
    public async Task RemoveSubscriberAsync(string symbol)
    {
        var updated = _subscriptions.AddOrUpdate(symbol, 0, (_, v) => Math.Max(0, v - 1));
        _logger.LogDebug("[OKX OB] -subscriber {Symbol} → {Count}", symbol, updated);
        if (updated == 0)
        {
            _subscriptions.TryRemove(symbol, out _);
            if (_ws?.State == WebSocketState.Open)
                await SendUnsubscribeAsync(symbol, _runCt).ConfigureAwait(false);
        }
    }

    // ─────────────────────────── BackgroundService ───────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _runCt = stoppingToken;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[OKX OB WS] Connection failed — retrying in 10 s");
            }

            if (!stoppingToken.IsCancellationRequested)
                await Task.Delay(10_000, stoppingToken).ConfigureAwait(false);
        }
    }

    // ─────────────────────────── private helpers ─────────────────────────────

    private async Task RunConnectionAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        _ws = ws;

        _logger.LogInformation("[OKX OB WS] Connecting …");
        await ws.ConnectAsync(new Uri(WsUrl), ct).ConfigureAwait(false);
        _logger.LogInformation("[OKX OB WS] Connected");

        // Re-subscribe any symbols that had viewers before reconnect
        foreach (var sym in _subscriptions.Keys)
            await SendSubscribeAsync(sym, ct).ConfigureAwait(false);

        // Ping loop keeps the connection alive
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingTask = PingLoopAsync(ws, pingCts.Token);

        try
        {
            await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
        }
        finally
        {
            _ws = null;
            pingCts.Cancel();
            try { await pingTask.ConfigureAwait(false); } catch { /* ignored */ }
        }
    }

    private static async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(PingIntervalSeconds), ct).ConfigureAwait(false);
                if (ws.State == WebSocketState.Open)
                {
                    var ping = "ping"u8.ToArray();
                    await ws.SendAsync(new ArraySegment<byte>(ping), WebSocketMessageType.Text, true, ct)
                             .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var sb  = new StringBuilder(1024);

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            sb.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
            } while (!result.EndOfMessage);

            var msg = sb.ToString();
            if (msg is "pong") continue;

            ProcessMessage(msg);
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Ignore subscribe/unsubscribe ACK frames which have no "data"
            if (!root.TryGetProperty("data", out var dataArr)) return;
            if (!root.TryGetProperty("arg",  out var argEl))   return;
            if (!argEl.TryGetProperty("instId", out var instIdEl)) return;

            var instId = instIdEl.GetString() ?? "";
            // Convert OKX instId (BTC-USDT) → app symbol (BTCUSDT)
            var symbol = instId.Replace("-", "", StringComparison.Ordinal);

            var data0 = dataArr[0];
            var bids  = ParseLevels(data0.GetProperty("bids"));
            var asks  = ParseLevels(data0.GetProperty("asks"));

            var mid = bids.Length > 0 && asks.Length > 0
                ? (bids[0].Price + asks[0].Price) / 2m
                : 0m;
            var spreadBps = mid > 0 && bids.Length > 0 && asks.Length > 0
                ? (asks[0].Price - bids[0].Price) / mid * 10_000m
                : 0m;

            var snapshot = new OrderBookDto(symbol, mid, Math.Round(spreadBps, 2), bids, asks, IsSynthetic: false);

            // Fire-and-forget: fan out to all connections watching this symbol.
            // Awaiting here would stall the tight WS receive loop; log failures via continuation.
            _ = _hubContext.Clients
                           .Group($"ob:{symbol}")
                           .SendAsync("OrderBookSnapshot", snapshot)
                           .ContinueWith(t => _logger.LogDebug(t.Exception!.InnerException ?? t.Exception,
                               "[OKX OB] SignalR broadcast failed for {Symbol}.", symbol),
                               TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OKX OB WS] Failed to parse message");
        }
    }

    private static OrderBookLevelDto[] ParseLevels(JsonElement arr)
    {
        var len    = arr.GetArrayLength();
        var levels = new OrderBookLevelDto[len];
        decimal cum = 0m;
        for (int i = 0; i < len; i++)
        {
            var row   = arr[i];
            var price = decimal.Parse(row[0].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            var size  = decimal.Parse(row[1].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            cum += size;
            levels[i] = new OrderBookLevelDto(price, size, Math.Round(cum, 4));
        }
        return levels;
    }

    private async Task SendSubscribeAsync(string symbol, CancellationToken ct)
    {
        var instId = ToOkxInstId(symbol);
        _logger.LogInformation("[OKX OB WS] Subscribing books5:{InstId}", instId);
        await SendJsonAsync(new { op = "subscribe", args = new[] { new { channel = "books5", instId } } }, ct)
            .ConfigureAwait(false);
    }

    private async Task SendUnsubscribeAsync(string symbol, CancellationToken ct)
    {
        var instId = ToOkxInstId(symbol);
        _logger.LogInformation("[OKX OB WS] Unsubscribing books5:{InstId}", instId);
        await SendJsonAsync(new { op = "unsubscribe", args = new[] { new { channel = "books5", instId } } }, ct)
            .ConfigureAwait(false);
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
                 .ConfigureAwait(false);
    }

    /// <summary>Converts app symbol (BTCUSDT) to OKX instId (BTC-USDT).</summary>
    private static string ToOkxInstId(string symbol)
    {
        var s = symbol.ToUpperInvariant();
        if (s.EndsWith("USDT")) return s[..^4] + "-USDT";
        if (s.EndsWith("USDC")) return s[..^4] + "-USDC";
        if (s.EndsWith("BTC"))  return s[..^3] + "-BTC";
        if (s.EndsWith("ETH"))  return s[..^3] + "-ETH";
        return s; // already formatted or unknown
    }
}
