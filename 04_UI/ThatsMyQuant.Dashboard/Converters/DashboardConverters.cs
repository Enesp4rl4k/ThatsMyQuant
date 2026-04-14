using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.Converters;

public class WinRateToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double winRate)
        {
            if (winRate >= 0.6) return new SolidColorBrush(Color.FromRgb(0, 230, 118)); // Green
            if (winRate >= 0.45) return Brushes.White;
            return new SolidColorBrush(Color.FromRgb(255, 23, 68)); // Red
        }
        return Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class PnlToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is decimal pnl)
        {
            return pnl >= 0 
                ? new SolidColorBrush(Color.FromRgb(0, 230, 118)) 
                : new SolidColorBrush(Color.FromRgb(255, 23, 68));
        }
        return Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class PassToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool pass)
        {
            return pass
                ? new SolidColorBrush(Color.FromArgb(100, 0, 230, 118))
                : new SolidColorBrush(Color.FromArgb(100, 255, 23, 68));
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class SharpeToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double sharpe)
        {
            if (sharpe >= 2.0) return new SolidColorBrush(Color.FromRgb(0, 230, 118));
            if (sharpe >= 1.0) return new SolidColorBrush(Color.FromRgb(100, 181, 246));
            if (sharpe >= 0.5) return Brushes.White;
            return new SolidColorBrush(Color.FromRgb(255, 183, 77));
        }
        return Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class DrawdownToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double dd)
        {
            if (dd <= 10) return new SolidColorBrush(Color.FromRgb(0, 230, 118));
            if (dd <= 20) return new SolidColorBrush(Color.FromRgb(255, 183, 77));
            return new SolidColorBrush(Color.FromRgb(255, 23, 68));
        }
        return Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class PboToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double pbo)
        {
            if (pbo < 0) return new SolidColorBrush(Color.FromRgb(148, 163, 184)); // Not computed
            if (pbo <= 0.3) return new SolidColorBrush(Color.FromRgb(0, 230, 118));
            if (pbo <= 0.5) return new SolidColorBrush(Color.FromRgb(255, 183, 77));
            return new SolidColorBrush(Color.FromRgb(255, 23, 68));
        }
        return Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
