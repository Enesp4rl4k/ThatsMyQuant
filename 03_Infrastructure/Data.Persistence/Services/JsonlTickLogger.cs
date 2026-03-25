using System.Text.Json;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

public sealed class JsonlTickLogger : ITickLogger
{
    private readonly FileStream _fileStream;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public JsonlTickLogger(string filePath = "logs/market_ticks.jsonl")
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, true);
        _writer = new StreamWriter(_fileStream, System.Text.Encoding.UTF8) { AutoFlush = false };
    }

    public async ValueTask LogTickAsync(MarketTick tick)
    {
        var json = JsonSerializer.Serialize(tick);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask FlushAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.FlushAsync().ConfigureAwait(false);
            await _fileStream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _writer.FlushAsync().ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        await _fileStream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
        _fileStream.Dispose();
        _writeLock.Dispose();
    }
}
