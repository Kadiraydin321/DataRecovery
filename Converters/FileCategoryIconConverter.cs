using System.Globalization;
using System.Windows.Data;

namespace DataRecoveryApp.Converters;

public class FileCategoryIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "Resim" => "🖼️",
        "Belge" => "📄",
        "Video" => "🎬",
        "Müzik" => "🎵",
        _ => "📁"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
