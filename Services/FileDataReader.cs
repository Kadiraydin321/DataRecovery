using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;
using Microsoft.Win32.SafeHandles;

namespace DataRecoveryApp.Services;

/// <summary>
/// Silinmiş bir dosyanın ham verisini birimden okur. Kurtarma ve önizleme aynı yolu kullanır:
/// NTFS → MFT kaydındaki $DATA veri parçaları; FAT32/Kazıma → DataOffset'ten bitişik Size bayt.
/// </summary>
public sealed class FileDataReader : IDisposable
{
    private const int ChunkSize = 1024 * 1024; // 1 MB tamponlu okuma
    private static readonly byte[] Zeros = new byte[64 * 1024]; // seyrek parçalar için paylaşılan sıfır tamponu
    private readonly SafeFileHandle _h;
    private WinApiHelper.NTFS_VOLUME_DATA_BUFFER? _ntfs;

    public FileDataReader(string driveLetter) => _h = WinApiHelper.OpenVolume(driveLetter);

    public void Dispose() => _h.Dispose();

    /// <summary>Dosya verisini hedef akışa kopyalar; yazılan bayt sayısını döndürür.</summary>
    public long CopyTo(RecoverableFile file, Stream destination, long maxBytes, CancellationToken ct = default)
    {
        return file.Source == RecoverySource.Ntfs
            ? CopyNtfs(file, destination, maxBytes, ct)
            : CopyContiguous(file.DataOffset, file.Size, destination, maxBytes, ct);
    }

    /// <summary>Dosyanın ilk <paramref name="maxBytes"/> baytını belleğe okur.</summary>
    public byte[] ReadHead(RecoverableFile file, int maxBytes, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        CopyTo(file, ms, maxBytes, ct);
        return ms.ToArray();
    }

    private long CopyContiguous(long offset, long size, Stream dst, long maxBytes, CancellationToken ct)
    {
        if (size <= 0) return 0;
        long remaining = Math.Min(size, maxBytes), written = 0;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(ChunkSize, remaining);
            var data = WinApiHelper.ReadAligned(_h, offset + written, n);
            if (data.Length == 0) break;
            dst.Write(data, 0, data.Length);
            written += data.Length;
            remaining -= data.Length;
            if (data.Length < n) break; // birim sonu
        }
        return written;
    }

    private long CopyNtfs(RecoverableFile file, Stream dst, long maxBytes, CancellationToken ct)
    {
        _ntfs ??= WinApiHelper.GetNtfsVolumeData(_h);
        var vol = _ntfs.Value;
        int recSize = (int)vol.BytesPerFileRecordSegment;
        var rec = WinApiHelper.ReadAligned(_h, file.StartSector * vol.BytesPerSector, recSize);
        if (rec.Length < recSize || BitConverter.ToUInt32(rec, 0) != 0x454C4946) // "FILE"
            throw new InvalidDataException("MFT kaydı geçersiz veya üzerine yazılmış.");
        ApplyFixup(rec, (int)vol.BytesPerSector);

        int attr = BitConverter.ToUInt16(rec, 0x14);
        while (attr + 16 < rec.Length)
        {
            uint type = BitConverter.ToUInt32(rec, attr);
            if (type == 0xFFFFFFFF) break;
            int len = BitConverter.ToInt32(rec, attr + 4);
            if (len <= 0 || attr + len > rec.Length) break;
            if (type == 0x80 && rec[attr + 9] == 0) // isimsiz $DATA
            {
                if (rec[attr + 8] == 0) // resident: veri kaydın içinde
                {
                    int cLen = BitConverter.ToInt32(rec, attr + 0x10);
                    int cOff = BitConverter.ToUInt16(rec, attr + 0x14);
                    int n = (int)Math.Min(cLen, maxBytes);
                    if (attr + cOff + n > rec.Length) throw new InvalidDataException("Resident veri bozuk.");
                    dst.Write(rec, attr + cOff, n);
                    file.Size = cLen;
                    return n;
                }
                long realSize = BitConverter.ToInt64(rec, attr + 0x30);
                if (file.Size <= 0) file.Size = realSize;
                return CopyRuns(rec, attr, len, BitConverter.ToUInt16(rec, attr + 0x20), realSize, vol, dst, maxBytes, ct);
            }
            attr += len;
        }
        throw new InvalidDataException("$DATA özniteliği bulunamadı.");
    }

    private long CopyRuns(byte[] rec, int attr, int len, int runOff, long realSize,
        WinApiHelper.NTFS_VOLUME_DATA_BUFFER vol, Stream dst, long maxBytes, CancellationToken ct)
    {
        long remaining = Math.Min(realSize, maxBytes), written = 0, lcn = 0;
        long cluster = vol.BytesPerCluster;
        int p = attr + runOff;
        while (p < attr + len && rec[p] != 0 && remaining > 0)
        {
            int lenBytes = rec[p] & 0xF, offBytes = rec[p] >> 4;
            if (p + 1 + lenBytes + offBytes > rec.Length) break;
            long clusters = ReadVar(rec, p + 1, lenBytes, false);
            long delta = ReadVar(rec, p + 1 + lenBytes, offBytes, true);
            p += 1 + lenBytes + offBytes;

            long runBytes = clusters * cluster;
            if (offBytes == 0) // seyrek (sparse) parça: sıfırlarla doldur
            {
                long z = Math.Min(runBytes, remaining);
                for (long left = z; left > 0; left -= Zeros.Length)
                    dst.Write(Zeros, 0, (int)Math.Min(left, Zeros.Length));
                written += z; remaining -= z;
                continue;
            }
            lcn += delta;
            long runStart = lcn * cluster, done = 0;
            while (done < runBytes && remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(Math.Min(ChunkSize, runBytes - done), remaining);
                var data = WinApiHelper.ReadAligned(_h, runStart + done, n);
                if (data.Length == 0) return written;
                dst.Write(data, 0, data.Length);
                done += data.Length; written += data.Length; remaining -= data.Length;
            }
        }
        return written;
    }

    private static void ApplyFixup(byte[] rec, int sectorSize)
    {
        int usaOff = BitConverter.ToUInt16(rec, 4), usaCount = BitConverter.ToUInt16(rec, 6);
        for (int i = 1; i < usaCount && i * sectorSize <= rec.Length && usaOff + i * 2 + 1 < rec.Length; i++)
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
        if (signed && (b[start + count - 1] & 0x80) != 0 && count < 8) v -= 1L << (count * 8);
        return v;
    }
}
