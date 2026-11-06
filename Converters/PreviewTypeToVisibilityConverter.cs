using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DataRecoveryApp.Models;

namespace DataRecoveryApp.Converters;

public class PreviewTypeToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not PreviewData preview) return Visibility.Collapsed;
        return preview.Type == PreviewType.NotAvailable ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
