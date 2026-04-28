namespace ThatsMyQuant.Contracts.Models;

public class PromotionLadderSnapshot
{
    public string Symbol { get; set; } = string.Empty;
    public string StrategyTag { get; set; } = string.Empty;
    public string RegimeTag { get; set; } = string.Empty;
    public string SessionTag { get; set; } = string.Empty;

    public bool BacktestPass { get; set; }
    public int BacktestTrades { get; set; }
    public double BacktestPF { get; set; }

    public bool PaperPass { get; set; }
    public int PaperTrades { get; set; }
    public double PaperPF { get; set; }
    public double PaperWinRate { get; set; }

    public bool LiveEligible { get; set; }
    public int LiveTrades { get; set; }
    public double LivePF { get; set; }
    public decimal LiveEdgeScore { get; set; }

    public string CurrentStage { get; set; } = "Backtest";
    public bool NextStageEligible { get; set; }
}
