using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.HftInfrastructure;

/// <summary>
/// A dedicated BackgroundService establishing a persistent streaming WebSocket connection directly 
/// to the Binance cryptocurrency matching engine (stream.binance.com).
/// </summary>
public class BinanceWebsocketAdapter : BackgroundService
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
        
        while (!stoppingToken.IsCancellationRequested)
        {
            using var client = new ClientWebSocket();
            try
            {
                await client.ConnectAsync(new Uri(_webSocketUrl), stoppingToken);
                _logger.LogInformation("Connected to Binance BTC/USDT Live Trade Feed.");

                var pool = System.Buffers.ArrayPool<byte>.Shared;
                var arrayBuffer = pool.Rent(4096);
                
                try 
                {
                    while (client.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        var result = await client.ReceiveAsync(new ArraySegment<byte>(arrayBuffer), stoppingToken);
                        if (result.MessageType == WebSocketMessageType.Close) break;

                        // Optimized Zero-Allocation JSON Parsing
                        var reader = new Utf8JsonReader(arrayBuffer.AsSpan(0, result.Count));
                        decimal price = 0;
                        
                        while (reader.Read())
                        {
                            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("p"))
                            {
                                reader.Read();
                                if (decimal.TryParse(reader.GetString(), out price)) break;
                            }
                        }

                        if (price > 0)
                        {
                            var signal = _signalPool.Rent("BTCUSDT", price);

                            // 2. Safely dump it onto the Lock-Free execution pipeline.
                            bool enqueued = _ringBuffer.TryEnqueue(signal);
                            
                            if (!enqueued)
                            {
                                _logger.LogWarning("Execution Swarm Overloaded. Tick Dropped (RingBuffer Full).");
                                _signalPool.Return(signal); 
                            }
                        }
                    }
                }
                finally
                {
                    pool.Return(arrayBuffer);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Binance WS Network Disconnect: {ex.Message}. Reconnecting in 3 seconds.");
                await Task.Delay(3000, stoppingToken);
            }
        }
    }
}
