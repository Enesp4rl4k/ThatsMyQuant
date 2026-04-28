using Microsoft.AspNetCore.SignalR;
using Listener.Api.Endpoints;
using Listener.Api.Hubs;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace Listener.Api.Services;

/// <summary>
/// Polls trade repository every 2s, computes portfolio summary, broadcasts
/// "PortfolioUpdate" to all SignalR clients. Replaces per-client 5s polling
/// with a single server-side computation + push fan-out.
/// </summary>
public sealed class PortfolioBroadcastWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHubContext<MarketDataHub> _hub;
    private readonly ILogger<PortfolioBroadcastWorker> _logger;

    public PortfolioBroadcastWorker(
        IServiceScopeFactory scopes,
        IHubContext<MarketDataHub> hub,
        ILogger<PortfolioBroadcastWorker> logger)
    {
        _scopes = scopes;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so the rest of the app has time to come up.
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var trades = scope.ServiceProvider.GetRequiredService<ITradeRepository>();

                var open = await trades.GetOpenAsync(500);
                var allRecent = await trades.GetAllAsync(1000);
                var closed = allRecent.Where(t => t.IsClosed).ToList();

                var realized = closed.Sum(t => t.RealisedPnL);
                var unrealized = open.Sum(ComputeUnrealized);
                var fees = closed.Sum(t => t.TotalFeesPaid) + open.Sum(t => t.TotalFeesPaid);
                var funding = closed.Sum(t => t.NetFunding) + open.Sum(t => t.NetFunding);

                var winners = closed.Where(t => t.RealisedPnL > 0m).ToList();
                var losers = closed.Where(t => t.RealisedPnL < 0m).ToList();
                var winRate = closed.Count == 0 ? 0d : (double)winners.Count / closed.Count;

                var grossProfit = winners.Sum(t => t.RealisedPnL);
                var grossLoss = Math.Abs(losers.Sum(t => t.RealisedPnL));
                var profitFactor = grossLoss <= 0m ? 0d : (double)(grossProfit / grossLoss);

                var avgWin = winners.Count > 0 ? winners.Average(t => Math.Abs(t.RealisedPnL)) : 0m;
                var avgLoss = losers.Count > 0 ? losers.Average(t => Math.Abs(t.RealisedPnL)) : 0m;
                var avgRr = avgLoss <= 0m ? 0d : (double)(avgWin / avgLoss);
                var avgPnL = closed.Count == 0 ? 0m : closed.Sum(t => t.RealisedPnL) / closed.Count;

                var balance = realized;
                var totalEquity = balance + unrealized;

                var dto = new PortfolioSummaryDto(
                    Balance: balance,
                    RealizedPnL: realized,
                    UnrealizedPnL: unrealized,
                    TotalEquity: totalEquity,
                    TotalFeesPaid: fees,
                    NetFunding: funding,
                    OpenPositionCount: open.Count,
                    ClosedTradeCount: closed.Count,
                    WinningTradeCount: winners.Count,
                    LosingTradeCount: losers.Count,
                    WinRate: winRate,
                    ProfitFactor: profitFactor,
                    AverageRiskReward: avgRr,
                    AverageTradePnL: avgPnL);

                await _hub.Clients.All.SendAsync("PortfolioUpdate", dto, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "PortfolioBroadcastWorker tick failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private static decimal ComputeUnrealized(Trade t)
    {
        var mark = t.MarkPrice > 0m ? t.MarkPrice : t.EntryPrice;
        var qty = t.RemainingQuantity > 0m ? t.RemainingQuantity : t.Quantity;
        if (qty <= 0m) return 0m;
        var direction = t.Side == TradeSide.Sell ? -1m : 1m;
        return (mark - t.EntryPrice) * qty * direction;
    }
}
