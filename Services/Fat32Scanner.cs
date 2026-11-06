using System.Text;
using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;
using Microsoft.Win32.SafeHandles;

namespace DataRecoveryApp.Services;

/// <summary>
/// FAT32 tarayıcı: kök dizinden başlayarak tüm alt dizinleri küme zincirleri üzerinden gezer,
/// silinmiş (0xE5) girişleri uzun dosya adlarıyla (LFN) birlikte toplar.
/// </summary>
public class Fat32Scanner
{
    // Güvenlik sınırları (bozuk/döngüsel yapılarda sonsuz döngüyü engeller)
    private const int MaxDirectories = 200_000;
    private const int MaxDepth = 64;
    private const int MaxClustersPerDir = 65_536;
    private const int MaxResults = 1_000_000;
    private const int FatPageSize = 64 * 1024;

    public string? LastError { get; private set; }

    public Task<List<RecoverableFile>> ScanAsync(string driveLetter, IProgress<int>? progress, CancellationToken ct)
        => Task.Run(() => Scan(driveLetter, progress, ct), ct);

    private sealed class Ctx
    {
        public required SafeFileHandle H;
        public int BytesPerSector, SectorsPerCluster;
        public long FatOffset, DataOffset, ClusterBytes;
        public uint TotalClusters;
        public readonly Dictionary<long, byte[]> FatPages = new();

        public long ClusterOffset(uint cluster) => DataOffset + (cluster - 2L) * ClusterBytes;

        public uint FatEntry(uint cluster)
        {
            long byteOff = FatOffset + cluster * 4L;
            long page = byteOff / FatPageSize;
            if (!FatPages.TryGetValue(page, out var data))
            {
                if (FatPages.Count > 4096) FatPages.Clear(); // en fazla ~256 MB önbellek
                data = WinApiHelper.ReadAligned(H, page * FatPageSize, FatPageSize);
                FatPages[page] = data;
            }
            int idx = (int)(byteOff - page * FatPageSize);
            return idx + 4 <= data.Length ? BitConverter.ToUInt32(data, idx) & 0x0FFFFFFF : 0x0FFFFFFF;
        }
    }

    private List<RecoverableFile> Scan(string driveLetter, IProgress<int>? progress, CancellationToken ct)
    {
        var results = new List<RecoverableFile>();
        LastError = null;
        try
        {
            using var h = WinApiHelper.OpenVolume(driveLetter);
            var boot = WinApiHelper.ReadAligned(h, 0, 512);
            if (boot.Length < 512 || boot[510] != 0x55 || boot[511] != 0xAA)
            { LastError = "Önyükleme sektörü geçersiz."; return results; }
            if (Encoding.ASCII.GetString(boot, 82, 8) != "FAT32   ")
            { LastError = Encoding.ASCII.GetString(boot, 3, 8).StartsWith("EXFAT")
                ? "exFAT henüz desteklenmiyor. Bunun yerine 'Veri Kazıma' kullanabilirsiniz."
                : "Bu birim FAT32 değil."; return results; }

            int bps = BitConverter.ToUInt16(boot, 11), spc = boot[13];
            int reserved = BitConverter.ToUInt16(boot, 14), numFats = boot[16];
            uint totalSectors = BitConverter.ToUInt32(boot, 32), fatSectors = BitConverter.ToUInt32(boot, 36);
            uint rootCluster = BitConverter.ToUInt32(boot, 44);
            if (bps is not (512 or 1024 or 2048 or 4096) || spc == 0 || numFats == 0 || fatSectors == 0)
            { LastError = "FAT32 önyükleme parametreleri geçersiz."; return results; }

            long dataSector = reserved + (long)numFats * fatSectors;
            var ctx = new Ctx
            {
                H = h, BytesPerSector = bps, SectorsPerCluster = spc,
                FatOffset = (long)reserved * bps,
                DataOffset = dataSector * bps,
                ClusterBytes = (long)spc * bps,
                TotalClusters = (uint)Math.Max(0, (totalSectors - dataSector) / spc)
            };

            var visited = new HashSet<uint>();
            var queue = new Queue<(uint cluster, string path, int depth, bool deletedDir)>();
            queue.Enqueue((rootCluster, "\\", 0, false));
            int dirCount = 0;

            while (queue.Count > 0 && dirCount < MaxDirectories && results.Count < MaxResults)
            {
                ct.ThrowIfCancellationRequested();
                var (cluster, path, depth, deletedDir) = queue.Dequeue();
                if (!visited.Add(cluster)) continue;
                dirCount++;
                ScanDirectory(ctx, cluster, path, depth, deletedDir, results, queue);
                if (dirCount % 50 == 0)
                    progress?.Report(Math.Min(95, dirCount * 100 / Math.Max(1, dirCount + queue.Count)));
            }

            if (results.Count == 0) LastError = $"FAT32'de silinmiş dosya bulunamadı ({dirCount} dizin tarandı).";
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { LastError = "Yönetici izni gerekiyor. Uygulamayı yönetici olarak çalıştırın."; }
        catch (Exception ex) { LastError = $"FAT32 taraması başarısız: {ex.Message}"; }
        progress?.Report(100);
        return results;
    }

    private static void ScanDirectory(Ctx ctx, uint startCluster, string path, int depth, bool deletedDir,
        List<RecoverableFile> results, Queue<(uint, string, int, bool)> queue)
    {
        var lfnParts = new List<string>();
        uint cluster = startCluster;
        var chainSeen = new HashSet<uint>();

        for (int n = 0; n < MaxClustersPerDir; n++)
        {
            if (cluster < 2 || cluster >= ctx.TotalClusters + 2 || !chainSeen.Add(cluster)) return;
            byte[] data;
            try { data = WinApiHelper.ReadAligned(ctx.H, ctx.ClusterOffset(cluster), (int)ctx.ClusterBytes); }
            catch { return; }

            // Silinmiş dizinlerde ilk giriş "." olmalı; değilse küme başka veriyle dolmuş
            if (n == 0 && deletedDir && !(data.Length >= 32 && data[0] == (byte)'.' && data[1] == (byte)' ')) return;

            for (int off = 0; off + 32 <= data.Length; off += 32)
            {
                byte first = data[off];
                if (first == 0x00) return; // dizin sonu
                byte attr = data[off + 11];

                if (attr == 0x0F) // LFN girişi (silinmişse ilk bayt 0xE5 olur ama karakterler korunur)
                {
                    lfnParts.Insert(0, ReadLfnPart(data, off));
                    continue;
                }
                string longName = string.Concat(lfnParts);
                lfnParts.Clear();
                if ((attr & 0x08) != 0) continue; // birim etiketi

                bool deleted = first == 0xE5;
                string shortName = ShortName(data, off, deleted);
                if (shortName is "." or "..") continue;
                string name = string.IsNullOrWhiteSpace(longName) ? shortName : longName;
                uint fileCluster = ((uint)BitConverter.ToUInt16(data, off + 20) << 16) | BitConverter.ToUInt16(data, off + 26);
                uint size = BitConverter.ToUInt32(data, off + 28);
                string fullPath = path.TrimEnd('\\') + "\\" + name;

                if ((attr & 0x10) != 0) // dizin
                {
                    if (depth + 1 < MaxDepth && fileCluster >= 2)
                        queue.Enqueue((fileCluster, fullPath, depth + 1, deleted || deletedDir));
                    continue;
                }
                if (!deleted && !deletedDir) continue;
                if (results.Count >= MaxResults) return;

                string ext = FileTypeHelper.GetExtension(name);
                bool clusterValid = fileCluster >= 2 && fileCluster < ctx.TotalClusters + 2;
                // Başlangıç kümesi FAT'ta hâlâ boşsa veri büyük ihtimalle sağlam
                bool free = clusterValid && SafeFat(ctx, fileCluster) == 0;
                results.Add(new RecoverableFile
                {
                    FileName = name,
                    Extension = ext,
                    Size = size,
                    FilePath = fullPath,
                    DeletedDate = DosDate(data, off),
                    FileCategory = RecoverableFile.CategoryNameFor(ext),
                    Source = RecoverySource.Fat32,
                    DataOffset = clusterValid ? ctx.ClusterOffset(fileCluster) : 0,
                    StartSector = clusterValid ? ctx.ClusterOffset(fileCluster) / ctx.BytesPerSector : 0,
                    FileReferenceNumber = fileCluster,
                    RecoverabilityPercent = !clusterValid || size == 0 ? 5 : free ? (size <= ctx.ClusterBytes ? 85 : 70) : 15
                });
            }

            if (deletedDir) return; // silinmiş dizinin zinciri FAT'ta silinmiştir: yalnızca ilk küme
            uint next = SafeFat(ctx, cluster);
            if (next >= 0x0FFFFFF8 || next < 2) return;
            cluster = next;
        }
    }

    private static uint SafeFat(Ctx ctx, uint cluster)
    {
        try { return ctx.FatEntry(cluster); } catch { return 0x0FFFFFFF; }
    }

    private static string ReadLfnPart(byte[] d, int off)
    {
        var sb = new StringBuilder(13);
        foreach (var (start, count) in new[] { (1, 5), (14, 6), (28, 2) })
            for (int i = 0; i < count; i++)
            {
                char c = (char)BitConverter.ToUInt16(d, off + start + i * 2);
                if (c == '\0' || c == '\uFFFF') return sb.ToString();
                sb.Append(c);
            }
        return sb.ToString();
    }

    private static string ShortName(byte[] d, int off, bool deleted)
    {
        string baseName = Encoding.ASCII.GetString(d, off, 8).TrimEnd();
        if (deleted && baseName.Length > 0) baseName = "_" + baseName[1..]; // ilk karakter silinmede kaybolur
        string ext = Encoding.ASCII.GetString(d, off + 8, 3).TrimEnd();
        return ext.Length > 0 ? $"{baseName}.{ext}" : baseName;
    }

    /// <summary>Son değiştirme tarihi (FAT silinme tarihini tutmaz).</summary>
    private static DateTime? DosDate(byte[] d, int off)
    {
        ushort time = BitConverter.ToUInt16(d, off + 22), date = BitConverter.ToUInt16(d, off + 24);
        if (date == 0) return null;
        try
        {
            return new DateTime(1980 + (date >> 9), Math.Max(1, (date >> 5) & 0xF), Math.Max(1, date & 0x1F),
                time >> 11, (time >> 5) & 0x3F, Math.Min(59, (time & 0x1F) * 2));
        }
        catch { return null; }
    }
}
