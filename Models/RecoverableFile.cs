using DataRecoveryApp.Helpers;

namespace DataRecoveryApp.Models;

public enum FileCategory { Image, Document, Video, Audio, Other }

/// <summary>Dosyanın nereden bulunduğu; kurtarma/önizleme bu bilgiye göre veri okur.</summary>
public enum RecoverySource
{
    /// <summary>NTFS: MFT kaydındaki $DATA veri parçaları (StartSector = MFT kaydı sektörü).</summary>
    Ntfs,
    /// <summary>FAT32: silinmiş dizin girişi, veri DataOffset'ten itibaren bitişik varsayılır.</summary>
    Fat32,
    /// <summary>İmza tabanlı kazıma: veri DataOffset + Size aralığında.</summary>
    Carved
}

/// <summary>Silinen (kurtarılabilir) dosya modeli.</summary>
public class RecoverableFile : ObservableObject
{
    private bool _isSelected;

    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime? DeletedDate { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int RecoverabilityPercent { get; set; }
    /// <summary>Resim, Belge, Video, Müzik, Diğer</summary>
    public string FileCategory { get; set; } = "Diğer";
    public long StartSector { get; set; }
    public ulong FileReferenceNumber { get; set; }

    public RecoverySource Source { get; set; } = RecoverySource.Ntfs;
    /// <summary>Birim başından itibaren bayt ofseti (FAT32 ve kazınmış dosyalar için).</summary>
    public long DataOffset { get; set; }
    /// <summary>Boyut tahmini mi (kazımada sonlandırıcı bulunamadıysa)?</summary>
    public bool SizeIsEstimated { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string SizeText => Size > 0 ? FileSizeHelper.Format(Size) + (SizeIsEstimated ? " (tahmini)" : "") : "Bilinmiyor";
    public string DateText => DeletedDate?.ToString("dd.MM.yyyy HH:mm") ?? "-";
    public string SourceText => Source switch
    {
        RecoverySource.Ntfs => "NTFS",
        RecoverySource.Fat32 => "FAT32",
        _ => "Kazıma"
    };

    public static string CategoryName(Models.FileCategory c) => c switch
    {
        Models.FileCategory.Image => "Resim",
        Models.FileCategory.Document => "Belge",
        Models.FileCategory.Video => "Video",
        Models.FileCategory.Audio => "Müzik",
        _ => "Diğer"
    };

    public static string CategoryNameFor(string extension) => CategoryName(FileTypeHelper.GetCategory(extension));
}
