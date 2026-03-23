using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public class OperatorAuditAlertRow
{
    public string Headline { get; set; } = "";
    public string Detail { get; set; } = "";
    public Brush AccentColor { get; set; } = Brushes.Gray;
}
