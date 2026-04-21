using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ThatsMyQuant.Application.Strategy;

namespace ThatsMyQuant.Dashboard.Converters;

/// <summary>ConverterParameter: Standard | Grid | Hft</summary>
public sealed class StrategyEvidenceVisualModeEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not StrategyEvidenceVisualMode mode || parameter is not string target)
        {
            return Visibility.Collapsed;
        }

        if (!Enum.TryParse<StrategyEvidenceVisualMode>(target, ignoreCase: true, out var expected))
        {
            return Visibility.Collapsed;
        }

        return mode == expected ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
