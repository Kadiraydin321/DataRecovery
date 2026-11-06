using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;
using Microsoft.Win32.SafeHandles;

namespace DataRecoveryApp.Services;

/// <summary>MFT kaydındaki $DATA özniteliğini çözümleyip kümeleri hedefe yazar.</summary>
public class RecoveryService
{
    private readonly string _driveLetter;
    public RecoveryService(string driveLetter) => _driveLetter = driveLetter;

    public Task<bool> RecoverFileAsync(RecoverableFile file, string destinationPath)
        => Task.Run(() =>
        {
            using var h = WinApiHelper.OpenVolume(_driveLetter);
            var vol = WinApiHelper.GetNtfsVolumeData(h);
            return RecoverInternal(h, vol, file, destinationPath);
        });

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
            SafeFileHandle? h = null;
            try
            {
                h = WinApiHelper.OpenVolume(_driveLetter);
                var vol = WinApiHelper.GetNtfsVolumeData(h);
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var f = files[i];
                    progress?.Report(new RecoveryProgress { Current = i, Total = files.Count, CurrentFile = f.FileName });
                    try
                    {
                        if (RecoverInternal(h, vol, f, destinationPath)) summary.Success++;
                        else { summary.Failed++; summary.Errors.Add($"{f.FileName}: veri bulunamadı veya üzerine yazılmış."); }
                    }
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
            finally { h?.Dispose(); }
        }, ct);
        return summary;
    }

    private static bool RecoverInternal(SafeFileHandle h, WinApiHelper.NTFS_VOLUME_DATA_BUFFER vol, RecoverableFile file, string dest)
    {
        int recSize = (int)vol.BytesPerFileRecordSegment;
        long offset = file.StartSector * vol.BytesPerSector;
        var rec = WinApiHelper.ReadSectors(h, offset, recSize);
        if (rec.Length < recSize || BitConverter.ToUInt32(rec, 0) != 0x454C4946) return false; // "FILE"
        ApplyFixup(rec, (int)vol.BytesPerSector);

        string outPath = UniquePath(Path.Combine(dest, FileTypeHelper.SanitizeFileName(file.FileName)));
        int attr = BitConverter.ToUInt16(rec, 0x14);
        while (attr + 8 < rec.Length)
        {
            uint type = BitConverter.ToUInt32(rec, attr);
            if (type == 0xFFFFFFFF) break;
            int len = BitConverter.ToInt32(rec, attr + 4);
            if (len <= 0) break;
            if (type == 0x80 && rec[attr + 9] == 0) // isimsiz $DATA
            {
                if (rec[attr + 8] == 0) // resident
                {
                    int cLen = BitConverter.ToInt32(rec, attr + 0x10);
                    int cOff = BitConverter.ToUInt16(rec, attr + 0x14);
                    File.WriteAllBytes(outPath, rec.AsSpan(attr + cOff, cLen).ToArray());
                    return true;
                }
                long realSize = BitConverter.ToInt64(rec, attr + 0x30);
                int runOff = BitConverter.ToUInt16(rec, attr + 0x20);
                using var fs = File.Create(outPath);
                long remaining = realSize, lcn = 0;
                int p = attr + runOff;
                while (p < attr + len && rec[p] != 0 && remaining > 0)
                {
                    int lenBytes = rec[p] & 0xF, offBytes = rec[p] >> 4;
                    long clusters = ReadVar(rec, p + 1, lenBytes, false);
                    long delta = ReadVar(rec, p + 1 + lenBytes, offBytes, true);
                    p += 1 + lenBytes + offBytes;
                    if (offBytes == 0) { remaining -= clusters * vol.BytesPerCluster; continue; } // seyrek
                    lcn += delta;
                    for (long c = 0; c < clusters && remaining > 0; c++)
                    {
                        var data = WinApiHelper.ReadSectors(h, (lcn + c) * vol.BytesPerCluster, (int)vol.BytesPerCluster);
                        int n = (int)Math.Min(remaining, data.Length);
                        fs.Write(data, 0, n);
                        remaining -= n;
                    }
                }
                return fs.Length > 0;
            }
            attr += len;
        }
        return false;
    }

    private static void ApplyFixup(byte[] rec, int sectorSize)
    {
        int usaOff = BitConverter.ToUInt16(rec, 4), usaCount = BitConverter.ToUInt16(rec, 6);
        for (int i = 1; i < usaCount && i * sectorSize <= rec.Length; i++)
        {
            int pos = i * sectorSize - 2;
            rec[pos] = rec[usaOff + i * 2];
            rec[pos + 1] = rec[usaOff + i * 2 + 1];
        }
    }

    private static long ReadVar(byte[] b, int start, int count, bool signed)
    {
        if (count == 0) return 0;
        long v = 0;
        for (int i = count - 1; i >= 0; i--) v = (v << 8) | b[start + i];
        if (signed && (b[start + count - 1] & 0x80) != 0) v -= 1L << (count * 8);
        return v;
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
