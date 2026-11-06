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

    public List<(byte[], string)> GetActiveSignatures()
    {
        var list = new List<(byte[], string)>();
        if (PNG) list.Add((new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "png"));
        if (JPEG) list.Add((new byte[] { 0xFF, 0xD8, 0xFF }, "jpg"));
        if (ZIP) list.Add((new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "zip"));
        if (PDF) list.Add((new byte[] { 0x25, 0x50, 0x44, 0x46 }, "pdf"));
        if (EXE) list.Add((new byte[] { 0x4D, 0x5A }, "exe"));
        if (GIF) list.Add((new byte[] { 0x47, 0x49, 0x46, 0x38 }, "gif"));
        if (BMP) list.Add((new byte[] { 0x42, 0x4D }, "bmp"));
        if (TIFF) list.Add((new byte[] { 0x49, 0x49, 0x2A, 0x00 }, "tif"));
        if (MP4) list.Add((new byte[] { 0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70 }, "mp4"));
        if (RAR) list.Add((new byte[] { 0x52, 0x61, 0x72, 0x21 }, "rar"));
        if (SevenZ) list.Add((new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, "7z"));
        if (DOCX) list.Add((new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "docx"));
        if (XLSX) list.Add((new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "xlsx"));
        if (PPTX) list.Add((new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "pptx"));
        return list;
    }
}
