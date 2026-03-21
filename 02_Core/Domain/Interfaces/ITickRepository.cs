using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Domain.Interfaces;

/// <summary>
/// Contract for interfacing with high-density, compressed time-series columnar databases (e.g., kdb+, Apache Parquet).
/// </summary>
public interface ITickRepository
{
    /// <summary>
    /// Opens a data-stream from a columnar file. Emits chunks continuously instead of loading billions of rows into RAM.
    /// </summary>
    IAsyncEnumerable<TickDataChunk> StreamHistoricalTicksAsync(string symbol, DateTime startUtc, DateTime endUtc);
}
