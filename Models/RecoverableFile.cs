using DataRecoveryApp.Helpers;

namespace DataRecoveryApp.Models;

public enum FileCategory { Image, Document, Video, Audio, Other }

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

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string SizeText => Size > 0 ? FileSizeHelper.Format(Size) : "Bilinmiyor";
    public string DateText => DeletedDate?.ToString("dd.MM.yyyy HH:mm") ?? "-";

    public static string CategoryName(Models.FileCategory c) => c switch
    {
        Models.FileCategory.Image => "Resim",
        Models.FileCategory.Document => "Belge",
        Models.FileCategory.Video => "Video",
        Models.FileCategory.Audio => "Müzik",
        _ => "Diğer"
    };
}
