using ThatsMyQuant.Contracts.Interfaces;

namespace Listener.Api.Endpoints;

public static class FundingEndpoints
{
    private static readonly string[] DefaultSymbols =
    {
        "DOGE-USDT-SWAP", "LTC-USDT-SWAP", "AAVE-USDT-SWAP",
        "TRX-USDT-SWAP", "XLM-USDT-SWAP", "LINK-USDT-SWAP",
        "ADA-USDT-SWAP", "XRP-USDT-SWAP", "SHIB-USDT-SWAP",
    };

    public static IEndpointRouteBuilder MapFundingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/funding").WithTags("Funding");

        group.MapGet("/rates", GetCurrentRates);
        group.MapGet("/history/{symbol}", GetHistory);

        return endpoints;
    }

    private static IResult GetCurrentRates(IFundingRateProvider provider, string? symbols)
    {
        var list = ParseSymbols(symbols);
        var nowUtc = DateTime.UtcNow;
        var fromUtc = nowUtc.AddDays(-2);

        var rows = list.Select(sym =>
        {
            decimal rate;
            try
            {
                rate = provider.GetRate(sym, nowUtc);
            }
            catch
            {
                rate = 0m;
            }

            var rateBps = rate * 10_000m;
            string signal = rateBps switch
            {
                <= -3m => "STRONG_LONG_FUNDING",
                <= -1m => "MILD_LONG_FUNDING",
                >= 3m => "STRONG_SHORT_FUNDING",
                >= 1m => "MILD_SHORT_FUNDING",
                _ => "NEUTRAL",
            };

            int sparkPoints = 0;
            decimal[] sparkValues = Array.Empty<decimal>();
            try
            {
                var history = provider.GetHistory(sym, fromUtc, nowUtc);
                if (history.Count > 0)
                {
                    sparkPoints = history.Count;
                    sparkValues = history.Select(h => h.Rate * 10_000m).ToArray();
                }
            }
            catch
            {
                // GetHistory is optional; ignore.
            }

            return new FundingRateRowDto(
                Symbol: sym,
                CurrentRate: rate,
                RateBps: rateBps,
                Signal: signal,
                SparklineBps: sparkValues,
                SparklinePointCount: sparkPoints,
                LastUpdatedUtc: nowUtc);
        }).ToList();

        return Results.Ok(rows);
    }

    private static IResult GetHistory(string symbol, IFundingRateProvider provider, DateTime? from, DateTime? to)
    {
        var toUtc = to ?? DateTime.UtcNow;
        var fromUtc = from ?? toUtc.AddDays(-7);
        try
        {
            var history = provider.GetHistory(symbol, fromUtc, toUtc);
            var dto = history.Select(h => new FundingHistoryPointDto(h.Timestamp, h.Rate, h.Rate * 10_000m)).ToList();
            return Results.Ok(dto);
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
    }

    private static List<string> ParseSymbols(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultSymbols.ToList();
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}

public sealed record FundingRateRowDto(
    string Symbol,
    decimal CurrentRate,
    decimal RateBps,
    string Signal,
    decimal[] SparklineBps,
    int SparklinePointCount,
    DateTime LastUpdatedUtc);

public sealed record FundingHistoryPointDto(
    DateTime Timestamp,
    decimal Rate,
    decimal RateBps);
