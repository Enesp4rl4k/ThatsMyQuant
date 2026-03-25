namespace ThatsMyQuant.Contracts.Interfaces;

using ThatsMyQuant.Contracts.Models;

/// <summary>
/// Service for logging market ticks for historical replay and analysis.
/// Inspired by MiroFish's JSONL action logger.
/// </summary>
public interface ITickLogger : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Logs a single market tick to the persistent store.
    /// </summary>
    ValueTask LogTickAsync(MarketTick tick);
    
    /// <summary>
    /// Flushes any buffered ticks to ensure persistence.
    /// </summary>
    ValueTask FlushAsync();
}
