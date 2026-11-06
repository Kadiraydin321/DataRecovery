using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;

namespace DataRecoveryApp.Services;

/// <summary>Seçilen dosyaları (NTFS, FAT32 veya kazınmış) hedef klasöre yazar.</summary>
public class RecoveryService
{
    private readonly string _driveLetter;
    public RecoveryService(string driveLetter) => _driveLetter = driveLetter;

    public Task<bool> RecoverFileAsync(RecoverableFile file, string destinationPath, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var reader = new FileDataReader(_driveLetter);
            return RecoverInternal(reader, file, destinationPath, ct);
        }, ct);

    public async Task<RecoverySummary> RecoverFilesAsync(IList<RecoverableFile> files, string destinationPath,
        IProgress<RecoveryProgress>? progress, CancellationToken ct)
    {
        var summary = new RecoverySummary();
        if (destinationPath.StartsWith(_driveLetter + ":", StringComparison.OrdinalIgnoreCase))
        {
            summary.Failed = files.Count;
            summary.Errors.Add("Hedef klasör, taranan sürücüden farklı olmalıdır (veri üzerine yazılabilir).");
            return summary;
        }
        Directory.CreateDirectory(destinationPath);
        await Task.Run(() =>
        {
            try
            {
                using var reader = new FileDataReader(_driveLetter);
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var f = files[i];
                    progress?.Report(new RecoveryProgress { Current = i, Total = files.Count, CurrentFile = f.FileName });
                    try
                    {
                        if (RecoverInternal(reader, f, destinationPath, ct)) summary.Success++;
                        else { summary.Failed++; summary.Errors.Add($"{f.FileName}: veri bulunamadı veya üzerine yazılmış."); }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { summary.Failed++; summary.Errors.Add($"{f.FileName}: {ex.Message}"); }
                }
                progress?.Report(new RecoveryProgress { Current = files.Count, Total = files.Count });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                summary.Failed = files.Count - summary.Success;
                summary.Errors.Add(ex.Message);
            }
        }, ct);
        return summary;
    }

    private static bool RecoverInternal(FileDataReader reader, RecoverableFile file, string dest, CancellationToken ct)
    {
        string name = FileTypeHelper.SanitizeFileName(file.FileName);
        if (string.IsNullOrEmpty(Path.GetExtension(name)) && !string.IsNullOrEmpty(file.Extension))
            name += "." + file.Extension;
        string outPath = UniquePath(Path.Combine(dest, name));
        long written;
        using (var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        {
            // Boyut biliniyorsa yer önceden ayrılır (hedefte parçalanma ve yeniden boyutlama azalır)
            if (file.Size > 0) try { fs.SetLength(file.Size); } catch { }
            written = reader.CopyTo(file, fs, long.MaxValue, ct);
            if (written > 0 && fs.Length != written) fs.SetLength(written); // kısa okumada fazlayı kes
        }
        if (written > 0) return true;
        File.Delete(outPath);
        return false;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }
}
