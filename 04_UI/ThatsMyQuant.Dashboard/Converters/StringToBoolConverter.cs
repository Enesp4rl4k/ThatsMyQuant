using System;
using System.Globalization;
using System.Windows.Data;

namespace ThatsMyQuant.Dashboard.Converters;

public class StringToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() == parameter?.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? parameter?.ToString() : Binding.DoNothing;
    }
}
