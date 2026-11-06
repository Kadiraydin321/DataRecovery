using System.Drawing;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace DataRecoveryApp.Converters;

public class BitmapToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is System.Windows.Media.ImageSource source) return source; // PreviewService zaten dondurulmuş BitmapImage üretir
        if (value is Bitmap bmp)
        {
            var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = ms;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        return null!;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
