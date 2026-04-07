using System.Net.WebSockets;
using System.Text.Json;
using System.Buffers;
using System.Buffers.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.HftInfrastructure;

public sealed class BinanceWebsocketAdapter : BackgroundService
{
    private readonly HftRingBuffer<Contracts.Models.Signal> _ringBuffer;
    private readonly AllocationFreeSignalPool _signalPool;
    private readonly ILogger<BinanceWebsocketAdapter> _logger;
    private readonly WebSocketOptions _wsOptions;

    public BinanceWebsocketAdapter(
        HftRingBuffer<Contracts.Models.Signal> ringBuffer,
        AllocationFreeSignalPool signalPool,
        ILogger<BinanceWebsocketAdapter> logger,
        IOptions<WebSocketOptions>? wsOptions = null)
    {
        _ringBuffer = ringBuffer;
        _signalPool = signalPool;
        _logger = logger;
        _wsOptions = wsOptions?.Value ?? new WebSocketOptions();
    }

    private static readonly Random JitterRandom = new();
    private const int MaxBackoffMs = 60_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Build combined stream URL: wss://stream.binance.com:9443/ws/btcusdt@trade/ethusdt@trade/...
        var streams = string.Join("/", _wsOptions.Symbols.Select(s => $"{s.ToLowerInvariant()}@trade"));
        var webSocketUrl = $"{_wsOptions.BaseUrl}/{streams}";

        _logger.LogInformation("Initializing Binance Live Streaming Socket for {Symbols}...",
            string.Join(", ", _wsOptions.Symbols));

        // 64KB Buffer for safety across high-volatility tick bursts
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        int consecutiveFailures = 0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var client = new ClientWebSocket();
                try
                {
                    await client.ConnectAsync(new Uri(webSocketUrl), stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation("Connected to Binance Live Trade Feed ({Count} symbols).", _wsOptions.Symbols.Length);
                    consecutiveFailures = 0; // Reset backoff on successful connection

                    while (client.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close) break;

                        // SonarQube Fix: Sadece tam mesajlari isle (Partial message protection)
                        if (result.EndOfMessage)
                        {
                            ParseAndDispatch(buffer.AsSpan(0, result.Count));
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    consecutiveFailures++;

                    if (consecutiveFailures >= _wsOptions.MaxReconnectAttempts)
                    {
                        _logger.LogCritical(ex,
                            "Binance WS reconnect attempts exhausted after {Attempts} consecutive failures. Stopping adapter.",
                            consecutiveFailures);
                        return;
                    }

                    int baseDelay = (int)Math.Min(
                        (long)_wsOptions.ReconnectDelayMs * (1L << Math.Min(consecutiveFailures - 1, 20)),
                        MaxBackoffMs);

                    // Add +/-25% jitter to prevent thundering herd
                    double jitterFactor;
                    lock (JitterRandom) { jitterFactor = 0.75 + (JitterRandom.NextDouble() * 0.50); }
                    int delayWithJitter = (int)(baseDelay * jitterFactor);

                    _logger.LogError(ex,
                        "Binance WS Network Disconnect (attempt {Attempt}/{Max}). Reconnecting in {Delay}ms...",
                        consecutiveFailures, _wsOptions.MaxReconnectAttempts, delayWithJitter);

                    await Task.Delay(delayWithJitter, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ParseAndDispatch(ReadOnlySpan<byte> span)
    {
        var reader = new Utf8JsonReader(span);
        decimal price = 0;
        string symbol = string.Empty;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("s"))
                {
                    reader.Read();
                    symbol = reader.GetString() ?? string.Empty;
                }
                else if (reader.ValueTextEquals("p"))
                {
                    reader.Read();
                    if (Utf8Parser.TryParse(reader.ValueSpan, out price, out _) && !string.IsNullOrEmpty(symbol))
                        break;
                }
            }
        }

        if (price > 0 && !string.IsNullOrEmpty(symbol))
        {
            var signal = _signalPool.Rent(symbol.ToUpperInvariant(), price);

            if (!_ringBuffer.TryEnqueue(signal))
            {
                _signalPool.Return(signal);
                _logger.LogWarning("Execution Overloaded. Tick Dropped for {Symbol}.", symbol);
            }
        }
    }
}
