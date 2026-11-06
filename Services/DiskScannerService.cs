using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;

namespace DataRecoveryApp.Services;

/// <summary>Tarama orkestratörü: dosya sistemine göre uygun tarayıcıyı seçer.</summary>
public class DiskScannerService
{
    private readonly NtfsScanner _ntfs = new();
    private readonly Fat32Scanner _fat = new();

    public string? LastError { get; private set; }
    public bool AccessDenied { get; private set; }

    public List<DriveInfoModel> GetAvailableDrives()
    {
        var list = new List<DriveInfoModel>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.Network) continue;
                string letter = d.Name.Substring(0, 1);
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Yerel Disk" : d.VolumeLabel;
                list.Add(new DriveInfoModel
                {
                    Letter = letter,
                    Label = label,
                    FileSystem = d.DriveFormat,
                    TotalSize = d.TotalSize,
                    FreeSpace = d.AvailableFreeSpace,
                    DisplayName = $"{letter}:\\ - {label} ({d.DriveFormat})"
                });
            }
            catch { /* erişilemeyen sürücüleri atla */ }
        }
        return list;
    }

    public async Task<List<RecoverableFile>> ScanDriveAsync(DriveInfoModel drive, IProgress<int>? progress, CancellationToken ct)
    {
        LastError = null; AccessDenied = false;
        if (drive.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            var r = await _ntfs.ScanAsync(drive.Letter, progress, ct);
            LastError = _ntfs.LastError; AccessDenied = _ntfs.AccessDenied;
            return r;
        }
        var f = await _fat.ScanAsync(drive.Letter, progress, ct);
        LastError = _fat.LastError;
        return f;
    }
}
