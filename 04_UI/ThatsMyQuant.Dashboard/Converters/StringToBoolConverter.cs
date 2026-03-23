using System;
using System.Globalization;
using System.Windows.Data;

namespace ThatsMyQuant.Dashboard.Converters;

public class StringToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true)
        {
            return parameter?.ToString() ?? string.Empty;
        }

        return Binding.DoNothing;
    }
}
