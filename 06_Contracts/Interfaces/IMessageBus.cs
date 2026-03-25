namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// A high-throughput, low-latency publish-subscribe channel (e.g., Apache Kafka or Redis Pub/Sub).
/// Crucial for distributed multi-agent architectures where the data ingest layer is physically separated from execution nodes.
/// </summary>
public interface IMessageBus
{
    Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken);
    Task SubscribeAsync<T>(string topic, Action<T> onMessageReceived, CancellationToken cancellationToken);
}

/// <summary>
/// Specialized storage mechanism optimized for append-only, high-frequency chronological data (e.g., kdb+, TimescaleDB).
/// </summary>
public interface ITimeSeriesRepository
{
    Task InsertTickAsync(string symbol, decimal price, long volume, DateTime timestamp);
    Task<IEnumerable<(DateTime Time, decimal Price)>> GetTimeSlicesAsync(string symbol, DateTime startTime, DateTime endTime);
}
