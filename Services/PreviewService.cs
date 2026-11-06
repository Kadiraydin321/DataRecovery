using System.Text;
using System.Windows.Media.Imaging;
using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;

namespace DataRecoveryApp.Services;

/// <summary>
/// Seçilen silinmiş dosyanın gerçek baytlarını diskten okuyarak önizleme üretir:
/// resimler için küçük resim (BitmapImage, dondurulmuş), metinler için ilk ~4 KB,
/// diğerleri için onaltılık (hex) döküm.
/// </summary>
public class PreviewService
{
    private const int TextBytes = 4096;
    private const int HexBytes = 512;
    private const int MaxImageBytes = 16 * 1024 * 1024;

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "png", "gif", "bmp" };
    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
        { "txt", "csv", "json", "xml", "html", "htm", "md", "log", "ini", "cfg", "yml", "yaml", "cs", "js", "css", "py", "sql", "bat", "ps1" };

    public Task<PreviewData> GetPreviewAsync(RecoverableFile file, string driveLetter, CancellationToken ct)
        => Task.Run(() => GetPreview(file, driveLetter, ct), ct);

    private PreviewData GetPreview(RecoverableFile file, string driveLetter, CancellationToken ct)
    {
        string ext = file.Extension.ToLowerInvariant();
        var meta = BuildMeta(file);
        try
        {
            using var reader = new FileDataReader(driveLetter);
            if (ImageExts.Contains(ext))
            {
                if (file.Size > MaxImageBytes)
                    return Hex(reader, file, meta, ct, "Resim önizleme için çok büyük (16 MB sınırı); onaltılık döküm gösteriliyor.");
                var bytes = reader.ReadHead(file, MaxImageBytes, ct);
                var img = TryDecodeImage(bytes);
                if (img != null)
                {
                    meta.Insert(0, $"Çözünürlük:       {img.PixelWidth} × {img.PixelHeight} (küçük resim)");
                    return new PreviewData { Type = PreviewType.Image, Title = $"🖼️ {file.FileName}", Content = img, Metadata = meta };
                }
                return Hex(reader, file, meta, ct, "Resim çözülemedi — veri bozuk veya üzerine yazılmış olabilir.");
            }
            if (TextExts.Contains(ext))
            {
                var bytes = reader.ReadHead(file, TextBytes, ct);
                if (bytes.Length == 0) return Error(file, meta, "Dosya verisi okunamadı (boş veya üzerine yazılmış).");
                return new PreviewData
                {
                    Type = PreviewType.Text,
                    Title = $"📄 {file.FileName} (ilk {FileSizeHelper.Format(bytes.Length)})",
                    Content = DecodeText(bytes),
                    Metadata = meta
                };
            }
            return Hex(reader, file, meta, ct, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { return Error(file, meta, "Disk okunamadı: yönetici izni gerekiyor. Uygulamayı 'Yönetici olarak çalıştır' ile açın."); }
        catch (InvalidDataException ex) { return Error(file, meta, $"Veri okunamadı: {ex.Message}"); }
        catch (Exception ex) { return Error(file, meta, $"Önizleme oluşturulamadı: {ex.Message}"); }
    }

    private static PreviewData Hex(FileDataReader reader, RecoverableFile file, List<string> meta, CancellationToken ct, string? note)
    {
        var bytes = reader.ReadHead(file, HexBytes, ct);
        if (bytes.Length == 0) return Error(file, meta, "Dosya verisi okunamadı (boş veya üzerine yazılmış).");
        if (note != null) meta.Insert(0, note);
        return new PreviewData
        {
            Type = PreviewType.Text,
            Title = $"🔢 {file.FileName} — onaltılık döküm (ilk {bytes.Length} bayt)",
            Content = HexDump(bytes),
            Metadata = meta
        };
    }

    private static BitmapImage? TryDecodeImage(byte[] bytes)
    {
        if (bytes.Length < 16) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.DecodePixelWidth = 240; // küçük resim
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze(); // arka plan iş parçacığından UI'ya güvenle aktarılır
            return bi;
        }
        catch { return null; }
    }

    private static string DecodeText(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1254).GetString(b); // Türkçe Windows kod sayfası
        }
    }

    public static string HexDump(byte[] b)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < b.Length; i += 16)
        {
            sb.Append(i.ToString("X8")).Append("  ");
            for (int j = 0; j < 16; j++)
                sb.Append(i + j < b.Length ? b[i + j].ToString("X2") + " " : "   ");
            sb.Append(' ');
            for (int j = 0; j < 16 && i + j < b.Length; j++)
            {
                byte c = b[i + j];
                sb.Append(c is >= 0x20 and < 0x7F ? (char)c : '.');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static List<string> BuildMeta(RecoverableFile f) => new()
    {
        $"Boyut:            {f.SizeText}",
        $"Tarih:            {f.DateText}",
        $"Kaynak:           {f.SourceText}",
        $"Kurtarılabilirlik: %{f.RecoverabilityPercent}",
        $"Konum:            {f.FilePath}"
    };

    private static PreviewData Error(RecoverableFile f, List<string> meta, string message)
    {
        meta.Insert(0, "⚠ " + message);
        return new PreviewData { Type = PreviewType.Metadata, Title = $"{f.FileName} — önizleme yok", Metadata = meta };
    }
}
