using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

/// <summary>
/// One row in the Dashboard's "Live Bot Activity" feed — shows what the bot
/// just did so the operator can watch overnight without tailing log files.
/// </summary>
public sealed class LiveActivityEntry
{
    public LiveActivityEntry(DateTime timestampUtc, string kind, string symbol, string headline, string detail, Brush kindBrush)
    {
        TimestampUtc = timestampUtc;
        Kind = kind;
        Symbol = symbol;
        Headline = headline;
        Detail = detail;
        KindBrush = kindBrush;
    }

    public DateTime TimestampUtc { get; }
    public string TimestampLocal => TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
    public string Kind { get; }
    public string Symbol { get; }
    public string Headline { get; }
    public string Detail { get; }
    public Brush KindBrush { get; }
}
