using DataRecoveryApp.Helpers;

namespace DataRecoveryApp.Models;

public class PartitionInfo
{
    public int Index { get; set; }
    /// <summary>MBR, GPT, EBR (genişletilmiş) veya "Kayıp".</summary>
    public string Scheme { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string FileSystem { get; set; } = "-";
    public string Name { get; set; } = string.Empty;
    public long StartLba { get; set; }
    public long SectorCount { get; set; }
    public int BytesPerSector { get; set; } = 512;
    public bool IsLost { get; set; }

    public long StartOffset => StartLba * BytesPerSector;
    public long Size => SectorCount * BytesPerSector;
    public string SizeText => FileSizeHelper.Format(Size);
    public string StatusText => IsLost ? "⚠ Kayıp bölüm (bölüm tablosunda yok)" : "Bölüm tablosunda kayıtlı";
}

public class PartitionAnalysisResult
{
    public int DiskNumber { get; set; }
    public string PartitionStyle { get; set; } = "Bilinmiyor";
    public long DiskSize { get; set; }
    public int BytesPerSector { get; set; } = 512;
    public List<PartitionInfo> Partitions { get; } = new();
    public List<string> Warnings { get; } = new();

    public string Summary =>
        $"Fiziksel Disk {DiskNumber} — {FileSizeHelper.Format(DiskSize)}, {PartitionStyle}, sektör {BytesPerSector} B, " +
        $"{Partitions.Count(p => !p.IsLost)} bölüm, {Partitions.Count(p => p.IsLost)} kayıp bölüm adayı";
}
