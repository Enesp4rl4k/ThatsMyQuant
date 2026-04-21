using System.Globalization;
using System.Text;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Writes per-session CSV artifacts: trade log + equity curve. Deterministic column order so
/// downstream tooling (pandas, Excel) can diff across runs without schema churn.
/// </summary>
public static class BacktestReportExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string GetReportsRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "backtest-reports");
        Directory.CreateDirectory(root);
        return root;
    }

    public static string ExportTradeLog(BacktestResult result, string version)
    {
        var path = Path.Combine(GetReportsRoot(), $"{result.Symbol}_{version}_trades.csv");
        var sb = new StringBuilder();
        sb.AppendLine("EntryTime,ExitTime,Symbol,Side,Strategy,Regime,ExitPolicy,ExitReason," +
                      "EntryPrice,ExitPrice,PnL,PnLPercent,Fees,Funding,Confidence");
        foreach (var t in result.Trades)
        {
            sb.Append(t.EntryTime.ToString("o", Inv)).Append(',');
            sb.Append(t.ExitTime.ToString("o", Inv)).Append(',');
            sb.Append(result.Symbol).Append(',');
            sb.Append(t.Side).Append(',');
            sb.Append(Esc(t.StrategyTag)).Append(',');
            sb.Append(Esc(t.RegimeTag)).Append(',');
            sb.Append(Esc(t.ExitPolicyTag)).Append(',');
            sb.Append(Esc(t.ExitReason)).Append(',');
            sb.Append(t.EntryPrice.ToString("F8", Inv)).Append(',');
            sb.Append(t.ExitPrice.ToString("F8", Inv)).Append(',');
            sb.Append(t.PnL.ToString("F4", Inv)).Append(',');
            sb.Append(t.PnLPercent.ToString("F4", Inv)).Append(',');
            sb.Append(t.TotalFeesPaid.ToString("F4", Inv)).Append(',');
            sb.Append(t.NetFunding.ToString("F4", Inv)).Append(',');
            sb.Append(t.Confidence).AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    public static string ExportEquityCurve(BacktestResult result, string version)
    {
        var path = Path.Combine(GetReportsRoot(), $"{result.Symbol}_{version}_equity.csv");
        var sb = new StringBuilder();
        sb.AppendLine("TradeIndex,Equity");
        for (int i = 0; i < result.EquityCurve.Count; i++)
        {
            sb.Append(i).Append(',')
              .Append(result.EquityCurve[i].ToString("F4", Inv))
              .AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static string Esc(string s) => s.Contains(',') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
