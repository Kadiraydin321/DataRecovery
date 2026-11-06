using DataRecoveryApp.Models;

namespace DataRecoveryApp.Services;

/// <summary>FAT32/exFAT basit tarayıcı (Faz 1: yer tutucu). İleride dizin girişi (0xE5) analizi eklenecek.</summary>
public class Fat32Scanner
{
    public string? LastError { get; private set; }

    public Task<List<RecoverableFile>> ScanAsync(string driveLetter, IProgress<int>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            LastError = null;
            var list = new List<RecoverableFile>();
            try
            {
                var drive = new DriveInfo(driveLetter.TrimEnd(':', '\\') + ":\\");
                if (!drive.IsReady) { LastError = "Sürücü hazır değil."; return list; }
                // Kök dizini doğrulama amaçlı okunur; silinmiş girişler ham erişimle sonraki fazda eklenecek.
                _ = drive.RootDirectory.EnumerateFileSystemInfos().Take(1).ToList();
                for (int i = 0; i <= 100; i += 20)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(i);
                }
                LastError = "FAT32 için derin tarama henüz desteklenmiyor (sonraki sürümde eklenecek).";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { LastError = $"FAT32 taraması başarısız: {ex.Message}"; }
            return list;
        }, ct);
}
