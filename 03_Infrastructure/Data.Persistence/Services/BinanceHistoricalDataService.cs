using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Fetches historical OHLCV candle data from Binance public API (no auth required).
/// Supports up to 1000 candles per request with automatic pagination for larger datasets.
/// Used for backtesting, model training, and walk-forward validation.
///
/// Binance API: GET /api/v3/klines
/// Rate limit: 1200 req/min (weight 1-5 per call).
/// </summary>
public sealed class BinanceHistoricalDataService
{
    private readonly HttpClient _http;
    private readonly ILogger<BinanceHistoricalDataService> _logger;
    private const string BaseUrl = "https://api.binance.com";
    private const int MaxCandlesPerRequest = 1000;

    public BinanceHistoricalDataService(HttpClient http, ILogger<BinanceHistoricalDataService> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Fetches historical candles from Binance. Automatically paginates for large requests.
    /// </summary>
    /// <param name="symbol">Binance symbol (e.g., "BTCUSDT", "ETHUSDT")</param>
    /// <param name="interval">Kline interval: 1m, 5m, 15m, 1h, 4h, 1d, 1w</param>
    /// <param name="totalCandles">Total number of candles to fetch</param>
    /// <param name="endTime">End time (UTC). Defaults to now.</param>
    public async Task<List<Candle>> FetchCandlesAsync(
        string symbol,
        string interval = "1h",
        int totalCandles = 500,
        DateTime? endTime = null,
        CancellationToken ct = default)
    {
        var allCandles = new List<Candle>(totalCandles);
        long? endMs = endTime.HasValue
            ? new DateTimeOffset(endTime.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()
            : null;

        int remaining = totalCandles;

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();

            int batchSize = Math.Min(remaining, MaxCandlesPerRequest);
            var url = $"{BaseUrl}/api/v3/klines?symbol={symbol}&interval={interval}&limit={batchSize}";
            if (endMs.HasValue) url += $"&endTime={endMs.Value}";

            _logger.LogDebug("[BinanceHistory] Fetching {Count} {Interval} candles for {Symbol} (endTime={End})",
                batchSize, interval, symbol, endMs);

            var batch = await FetchBatchAsync(url, ct).ConfigureAwait(false);
            if (batch.Count == 0) break;

            allCandles.InsertRange(0, batch); // Prepend — oldest first
            remaining -= batch.Count;

            // Set endTime to just before the oldest candle in this batch for next page
            endMs = new DateTimeOffset(batch[0].Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds() - 1;

            if (batch.Count < batchSize) break; // No more data available

            // Respect rate limits
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("[BinanceHistory] Fetched {Total} {Interval} candles for {Symbol} ({Start:d} → {End:d})",
            allCandles.Count, interval, symbol,
            allCandles.Count > 0 ? allCandles[0].Timestamp : DateTime.MinValue,
            allCandles.Count > 0 ? allCandles[^1].Timestamp : DateTime.MinValue);

        return allCandles;
    }

    /// <summary>
    /// Fetches candles between two specific dates.
    /// </summary>
    public async Task<List<Candle>> FetchCandlesByDateRangeAsync(
        string symbol,
        string interval,
        DateTime startDate,
        DateTime endDate,
        CancellationToken ct = default)
    {
        var allCandles = new List<Candle>();
        long startMs = new DateTimeOffset(startDate, TimeSpan.Zero).ToUnixTimeMilliseconds();
        long endMs = new DateTimeOffset(endDate, TimeSpan.Zero).ToUnixTimeMilliseconds();

        while (startMs < endMs)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{BaseUrl}/api/v3/klines?symbol={symbol}&interval={interval}&limit={MaxCandlesPerRequest}&startTime={startMs}&endTime={endMs}";

            var batch = await FetchBatchAsync(url, ct).ConfigureAwait(false);
            if (batch.Count == 0) break;

            allCandles.AddRange(batch);
            startMs = new DateTimeOffset(batch[^1].Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds() + 1;

            if (batch.Count < MaxCandlesPerRequest) break;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("[BinanceHistory] Date range fetch: {Count} {Interval} candles for {Symbol}",
            allCandles.Count, interval, symbol);

        return allCandles;
    }

    /// <summary>
    /// Saves candles to CSV for offline analysis and reproducible backtesting.
    /// </summary>
    public static async Task SaveToCsvAsync(List<Candle> candles, string filePath)
    {
        using var writer = new StreamWriter(filePath);
        await writer.WriteLineAsync("Timestamp,Open,High,Low,Close,Volume").ConfigureAwait(false);
        foreach (var c in candles)
        {
            await writer.WriteLineAsync(string.Format(CultureInfo.InvariantCulture,
                "{0:O},{1},{2},{3},{4},{5}",
                c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads candles from a CSV file (same format as SaveToCsvAsync output).
    /// </summary>
    public static List<Candle> LoadFromCsv(string filePath)
    {
        var candles = new List<Candle>();
        bool header = true;
        foreach (var line in File.ReadLines(filePath))
        {
            if (header) { header = false; continue; }
            var parts = line.Split(',');
            if (parts.Length < 6) continue;

            if (DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts) &&
                decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var o) &&
                decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var h) &&
                decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var l) &&
                decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var c) &&
                decimal.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            {
                candles.Add(new Candle { Timestamp = ts.ToUniversalTime(), Open = o, High = h, Low = l, Close = c, Volume = v });
            }
        }
        return candles;
    }

    private async Task<List<Candle>> FetchBatchAsync(string url, CancellationToken ct)
    {
        var candles = new List<Candle>();
        try
        {
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement[]>(ct).ConfigureAwait(false);
            if (json == null) return candles;

            // Binance kline format: [openTime, open, high, low, close, volume, closeTime, ...]
            foreach (var kline in json)
            {
                if (kline.GetArrayLength() < 6) continue;

                long openTimeMs = kline[0].GetInt64();
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(openTimeMs).UtcDateTime;

                candles.Add(new Candle
                {
                    Timestamp = timestamp,
                    Open = decimal.Parse(kline[1].GetString()!, CultureInfo.InvariantCulture),
                    High = decimal.Parse(kline[2].GetString()!, CultureInfo.InvariantCulture),
                    Low = decimal.Parse(kline[3].GetString()!, CultureInfo.InvariantCulture),
                    Close = decimal.Parse(kline[4].GetString()!, CultureInfo.InvariantCulture),
                    Volume = decimal.Parse(kline[5].GetString()!, CultureInfo.InvariantCulture)
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[BinanceHistory] Failed to fetch batch from {Url}", url);
        }

        return candles;
    }
}
