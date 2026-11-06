using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DataRecoveryApp.Converters;

public class RecoverabilityColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int p = value is int i ? i : 0;
        return p >= 80 ? Brushes.LimeGreen : p >= 50 ? Brushes.Orange : Brushes.Tomato;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
