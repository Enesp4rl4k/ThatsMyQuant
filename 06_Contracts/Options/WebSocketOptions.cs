namespace ThatsMyQuant.Contracts.Options;

public sealed class WebSocketOptions
{
    public const string SectionName = "WebSocket";

    public string BinanceBaseUrl { get; set; } = "wss://stream.binance.com:9443";
    public string DefaultStream { get; set; } = "btcusdt@trade";
}
