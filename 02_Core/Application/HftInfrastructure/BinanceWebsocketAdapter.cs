using System.Net.WebSockets;
using System.Text.Json;
using System.Buffers;
using System.Buffers.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.HftInfrastructure;

public sealed class BinanceWebsocketAdapter : BackgroundService
{
    private readonly HftRingBuffer<Domain.Entities.Signal> _ringBuffer;
    private readonly AllocationFreeSignalPool _signalPool;
    private readonly ILogger<BinanceWebsocketAdapter> _logger;
    private readonly string _webSocketUrl = "wss://stream.binance.com:9443/ws/btcusdt@trade";

    public BinanceWebsocketAdapter(
        HftRingBuffer<Domain.Entities.Signal> ringBuffer,
        AllocationFreeSignalPool signalPool,
        ILogger<BinanceWebsocketAdapter> logger)
    {
        _ringBuffer = ringBuffer;
        _signalPool = signalPool;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Initializing Binance Live Streaming Socket...");

        // 64KB Buffer for safety across high-volatility tick bursts
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var client = new ClientWebSocket();
                try
                {
                    await client.ConnectAsync(new Uri(_webSocketUrl), stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation("Connected to Binance BTC/USDT Live Trade Feed.");

                    while (client.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close) break;

                        // SonarQube Fix: Sadece tam mesajları işle (Partial message protection)
                        if (result.EndOfMessage)
                        {
                            ParseAndDispatch(buffer.AsSpan(0, result.Count));
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Binance WS Network Disconnect. Reconnecting...");
                    await Task.Delay(3000, stoppingToken).ConfigureAwait(false);
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

        while (reader.Read())
        {
            // SonarQube Fix: reader.ValueTextEquals kullanarak string allocation'ı engelle
            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("p"))
            {
                reader.Read();
                // HFT Optimization: reader.ValueSpan üzerinden doğrudan decimal'e çevir
                if (Utf8Parser.TryParse(reader.ValueSpan, out price, out _)) break;
            }
        }

        if (price > 0)
        {
            var signal = _signalPool.Rent("BTCUSDT", price);

            if (!_ringBuffer.TryEnqueue(signal))
            {
                // Telemetry: Hız yetişemezse sinyali havuza iade et ve logla
                _signalPool.Return(signal);
                _logger.LogWarning("Execution Overloaded. Tick Dropped.");
            }
        }
    }
}