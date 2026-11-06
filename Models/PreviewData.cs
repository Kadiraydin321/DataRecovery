namespace DataRecoveryApp.Models;

public enum PreviewType { Image, Text, Metadata, Video, NotAvailable }

public class PreviewData
{
    public PreviewType Type { get; set; }
    public object? Content { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<string> Metadata { get; set; } = new();
}

public class DiskImageProgress
{
    public long BytesRead { get; set; }
    public long TotalBytes { get; set; }
    /// <summary>Okunamayıp sıfırla doldurulan bayt sayısı (bozuk sektörler).</summary>
    public long BadBytes { get; set; }
    public int Percent => TotalBytes == 0 ? 0 : (int)(BytesRead * 100 / TotalBytes);
}

public class CarverSettings
{
    public bool PNG { get; set; } = true;
    public bool JPEG { get; set; } = true;
    public bool ZIP { get; set; } = true;
    public bool PDF { get; set; } = true;
    public bool EXE { get; set; } = true;
    public bool GIF { get; set; } = true;
    public bool BMP { get; set; } = true;
    public bool TIFF { get; set; } = true;
    public bool MP4 { get; set; } = true;
    public bool RAR { get; set; } = true;
    public bool SevenZ { get; set; } = true;
    public bool DOCX { get; set; } = true;
    public bool XLSX { get; set; } = true;
    public bool PPTX { get; set; } = true;
    public bool Other { get; set; } = true;

    /// <summary>ZIP/DOCX/XLSX/PPTX aynı imzayı (PK\x03\x04) paylaşır; tek tarama yapılır, içerik incelenerek ayrılır.</summary>
    public bool AnyZipFamily => ZIP || DOCX || XLSX || PPTX;

    public bool IsEnabled(string ext) => ext switch
    {
        "png" => PNG,
        "jpg" => JPEG,
        "gif" => GIF,
        "bmp" => BMP,
        "tif" => TIFF,
        "pdf" => PDF,
        "zip" => ZIP,
        "docx" => DOCX,
        "xlsx" => XLSX,
        "pptx" => PPTX,
        "rar" => RAR,
        "7z" => SevenZ,
        "mp4" => MP4,
        "exe" => EXE,
        _ => Other
    };
}
