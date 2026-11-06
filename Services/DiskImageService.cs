using DataRecoveryApp.Models;
using Microsoft.Win32.SafeHandles;
using static DataRecoveryApp.Helpers.WinApiHelper;

namespace DataRecoveryApp.Services;

/// <summary>
/// Birimin bire bir ham imajını (.img) oluşturur. Her dosya sistemiyle çalışır (boyut IOCTL ile alınır).
/// Okunamayan bölgeler 4 KB'lık parçalara bölünerek yeniden denenir; yine okunamayanlar sıfırla
/// doldurulur, böylece imajdaki ofsetler diskle birebir aynı kalır (kazıma/kurtarma için şart).
/// </summary>
public class DiskImageService
{
    private const int BufSize = 4 * 1024 * 1024;
    private const int RetryUnit = 4096;

    public Task<bool> CreateImageAsync(string driveLetter, string outputPath,
        IProgress<DiskImageProgress>? progress, CancellationToken ct)
        => Task.Run(() => CreateImage(driveLetter, outputPath, progress, ct), ct);

    private static bool CreateImage(string driveLetter, string outputPath, IProgress<DiskImageProgress>? progress, CancellationToken ct)
    {
        using var h = OpenVolume(driveLetter);
        long totalBytes = GetVolumeSize(h, driveLetter);
        if (totalBytes <= 0) throw new IOException("Birim boyutu okunamadı.");

        var buffer = new byte[BufSize]; // tek tampon, döngüde yeniden kullanılır
        long readBytes = 0, badBytes = 0;
        int lastPct = -1;
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
        try { fs.SetLength(totalBytes); } catch { /* yer ayırma isteğe bağlı */ }

        while (readBytes < totalBytes)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(BufSize, totalBytes - readBytes);
            int got;
            try { got = ReadInto(h, readBytes, buffer, toRead); }
            catch { got = -1; }

            if (got == toRead) fs.Write(buffer, 0, got);
            else badBytes += RetrySmall(h, readBytes, toRead, buffer, fs);
            readBytes += toRead;

            int pct = (int)(readBytes * 100 / totalBytes);
            if (pct != lastPct) // ilerlemeyi yalnızca yüzde değişince bildir (UI'yı boğmaz)
            {
                lastPct = pct;
                progress?.Report(new DiskImageProgress { BytesRead = readBytes, TotalBytes = totalBytes, BadBytes = badBytes });
            }
        }
        fs.Flush();
        return true;
    }

    /// <summary>Hatalı bloğu 4 KB'lık parçalarla okur; okunamayan parçaları sıfırla yazar. Kayıp bayt sayısını döndürür.</summary>
    private static long RetrySmall(SafeFileHandle h, long offset, int length, byte[] buffer, Stream dst)
    {
        long bad = 0;
        var zeros = new byte[RetryUnit];
        for (int done = 0; done < length; done += RetryUnit)
        {
            int n = Math.Min(RetryUnit, length - done);
            int got;
            try { got = ReadInto(h, offset + done, buffer, n); } catch { got = 0; }
            if (got == n) dst.Write(buffer, 0, n);
            else { dst.Write(zeros, 0, n); bad += n; }
        }
        return bad;
    }

    private static long GetVolumeSize(SafeFileHandle h, string driveLetter)
    {
        try { return GetLength(h); } catch { }
        try { var v = GetNtfsVolumeData(h); return v.NumberSectors * v.BytesPerSector; } catch { }
        try { return new DriveInfo(driveLetter + ":\\").TotalSize; } catch { return 0; }
    }
}
