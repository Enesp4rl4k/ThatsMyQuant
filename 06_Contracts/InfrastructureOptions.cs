namespace ThatsMyQuant.Contracts.Options;

public sealed class ResilienceOptions
{
    public int MaxRetryAttempts { get; set; } = 3;
    public int InitialRetryDelaySeconds { get; set; } = 1;
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;
    public int CircuitBreakerSamplingDurationSeconds { get; set; } = 30;
    public int CircuitBreakerMinimumThroughput { get; set; } = 5;
    public int CircuitBreakerBreakDurationSeconds { get; set; } = 30;
}

public sealed class ExchangeUrlOptions
{
    public string BinanceRestBaseUrl { get; set; } = "https://api.binance.com";
    public string BinanceExchangeInfoUrl { get; set; } = "https://api.binance.com/api/v3/exchangeInfo";
    public string OkxRestBaseUrl { get; set; } = "https://www.okx.com";
}
