using ThatsMyQuant.Contracts.Interfaces;

namespace Listener.Api.Endpoints;

public sealed record AiTradeReviewDto(
    string TradeId,
    string Analysis);

public static class AiEndpoints
{
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/ai").WithTags("AI");
        g.MapGet("/review/{tradeId:guid}", ReviewTrade);
        return endpoints;
    }

    private static async Task<IResult> ReviewTrade(
        Guid tradeId,
        ITradeRepository trades,
        IAiAnalystService analyst)
    {
        var trade = await trades.GetByIdAsync(tradeId);
        if (trade is null) return Results.NotFound();

        var analysis = await analyst.ReviewTradeAsync(trade);
        return Results.Ok(new AiTradeReviewDto(
            TradeId: tradeId.ToString(),
            Analysis: analysis));
    }
}
