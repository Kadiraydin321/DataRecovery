using System.Diagnostics;
using System.Text;
using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;
using Microsoft.Win32.SafeHandles;

namespace DataRecoveryApp.Services;

/// <summary>
/// Fiziksel diskin 0. sektörünü okuyup MBR/GPT bölüm tablosunu çözümler ve bölümler arasındaki
/// ayrılmamış boşluklarda NTFS/FAT32 önyükleme sektörü arayarak kayıp bölümleri tespit eder.
/// </summary>
public class PartitionService
{
    private const long OneMiB = 1024 * 1024;
    private const int MaxCandidatesPerGap = 500_000;

    public Task<PartitionAnalysisResult> AnalyzeAsync(int diskNumber, bool scanLost, IProgress<int>? progress, CancellationToken ct)
        => Task.Run(() => Analyze(diskNumber, scanLost, progress, ct), ct);

    private PartitionAnalysisResult Analyze(int diskNumber, bool scanLost, IProgress<int>? progress, CancellationToken ct)
    {
        var result = new PartitionAnalysisResult { DiskNumber = diskNumber };
        using var h = WinApiHelper.OpenPhysicalDrive(diskNumber);
        int bps = WinApiHelper.GetBytesPerSector(h);
        result.BytesPerSector = bps;
        try { result.DiskSize = WinApiHelper.GetLength(h); } catch { result.Warnings.Add("Disk boyutu okunamadı."); }

        if (!(RustBridge.IsAvailable && TryParseWithRust(diskNumber, bps, result)))
            ParseWithCSharp(h, bps, result);
        progress?.Report(10);

        if (scanLost && result.DiskSize > 0) FindLost(h, bps, result, progress, ct);
        progress?.Report(100);
        return result;
    }

    /// <summary>
    /// Bölüm tablosunu Rust çekirdeğiyle (re_parse_partitions) çözümler. Başarısızsa false döner ve
    /// C# ayrıştırıcısı devreye girer. Kayıp bölüm araması her iki durumda da C# tarafında yapılır.
    /// </summary>
    private static bool TryParseWithRust(int diskNumber, int bps, PartitionAnalysisResult result)
    {
        try
        {
            var table = RustBridge.ParsePartitions($@"\\.\PhysicalDrive{diskNumber}", out int rc);
            if (table == null)
            {
                Debug.WriteLine($"[PartitionService] Rust ayrıştırıcı başarısız: {RustBridge.ErrorText(rc)}; C# ayrıştırıcıya dönülüyor.");
                return false;
            }
            result.PartitionStyle = table.Style;
            result.Warnings.AddRange(table.Warnings);
            // Rust GPT'yi 512/4096 sektörle dener; MBR'de 512 varsayar. Windows'un bildirdiği bps farklıysa LBA'lar dönüştürülür.
            int nativeSs = table.SectorSize > 0 ? table.SectorSize : 512;
            foreach (var p in table.Partitions)
            {
                result.Partitions.Add(new PartitionInfo
                {
                    Index = result.Partitions.Count + 1,
                    Scheme = p.Scheme,
                    TypeName = p.TypeName,
                    FileSystem = p.FileSystem,
                    Name = p.Name,
                    StartLba = p.StartLba * nativeSs / bps,
                    SectorCount = p.SectorCount * nativeSs / bps,
                    BytesPerSector = bps
                });
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PartitionService] Rust çağrısı hata verdi: {ex.Message}; C# ayrıştırıcıya dönülüyor.");
            return false;
        }
    }

    private static void ParseWithCSharp(SafeFileHandle h, int bps, PartitionAnalysisResult result)
    {
        var mbr = WinApiHelper.ReadAligned(h, 0, bps);
        if (mbr.Length < 512 || mbr[510] != 0x55 || mbr[511] != 0xAA)
        {
            result.PartitionStyle = "Bölüm tablosu yok / bozuk";
            result.Warnings.Add("Sektör 0'da geçerli MBR imzası (55 AA) bulunamadı.");
        }
        else
        {
            bool protective = Enumerable.Range(0, 4).Any(i => mbr[446 + i * 16 + 4] == 0xEE);
            if (protective && ParseGpt(h, bps, result)) result.PartitionStyle = "GPT";
            else
            {
                result.PartitionStyle = "MBR";
                if (protective) result.Warnings.Add("Koruyucu MBR var fakat GPT başlığı okunamadı; MBR olarak gösteriliyor.");
                ParseMbr(h, bps, mbr, result);
            }
        }

        foreach (var p in result.Partitions) p.FileSystem = DetectFs(h, p.StartOffset);
    }

    private static bool ParseGpt(SafeFileHandle h, int bps, PartitionAnalysisResult r)
    {
        var hdr = WinApiHelper.ReadAligned(h, bps, bps);
        if (hdr.Length < 92 || Encoding.ASCII.GetString(hdr, 0, 8) != "EFI PART") return false;
        long entriesLba = BitConverter.ToInt64(hdr, 72);
        uint count = BitConverter.ToUInt32(hdr, 80), entrySize = BitConverter.ToUInt32(hdr, 84);
        if (entrySize < 128 || entrySize > 4096 || count == 0 || count > 1024) return false;

        var entries = WinApiHelper.ReadAligned(h, entriesLba * bps, (int)(count * entrySize));
        for (int i = 0; i < count && (i + 1) * entrySize <= entries.Length; i++)
        {
            int o = (int)(i * entrySize);
            var typeGuid = new Guid(entries.AsSpan(o, 16));
            if (typeGuid == Guid.Empty) continue;
            long first = BitConverter.ToInt64(entries, o + 32), last = BitConverter.ToInt64(entries, o + 40);
            string name = Encoding.Unicode.GetString(entries, o + 56, 72).TrimEnd('\0');
            r.Partitions.Add(new PartitionInfo
            {
                Index = r.Partitions.Count + 1, Scheme = "GPT", TypeName = GptTypeName(typeGuid),
                Name = name, StartLba = first, SectorCount = last - first + 1, BytesPerSector = bps
            });
        }
        return true;
    }

    private static void ParseMbr(SafeFileHandle h, int bps, byte[] mbr, PartitionAnalysisResult r)
    {
        for (int i = 0; i < 4; i++)
        {
            int o = 446 + i * 16;
            byte type = mbr[o + 4];
            uint start = BitConverter.ToUInt32(mbr, o + 8), count = BitConverter.ToUInt32(mbr, o + 12);
            if (type == 0 || count == 0) continue;
            if (type is 0x05 or 0x0F or 0x85) { ParseExtended(h, bps, start, r); continue; }
            r.Partitions.Add(new PartitionInfo
            {
                Index = r.Partitions.Count + 1, Scheme = "MBR", TypeName = MbrTypeName(type),
                Name = (mbr[o] & 0x80) != 0 ? "Etkin (önyüklenebilir)" : "", StartLba = start, SectorCount = count, BytesPerSector = bps
            });
        }
    }

    /// <summary>Genişletilmiş bölümdeki EBR zincirini izler (en fazla 128 mantıksal bölüm).</summary>
    private static void ParseExtended(SafeFileHandle h, int bps, uint extStart, PartitionAnalysisResult r)
    {
        long ebrLba = extStart;
        var seen = new HashSet<long>();
        for (int n = 0; n < 128 && seen.Add(ebrLba); n++)
        {
            byte[] ebr;
            try { ebr = WinApiHelper.ReadAligned(h, ebrLba * bps, 512); } catch { break; }
            if (ebr.Length < 512 || ebr[510] != 0x55 || ebr[511] != 0xAA) break;
            byte type = ebr[446 + 4];
            uint rel = BitConverter.ToUInt32(ebr, 446 + 8), count = BitConverter.ToUInt32(ebr, 446 + 12);
            if (type != 0 && count != 0)
                r.Partitions.Add(new PartitionInfo
                {
                    Index = r.Partitions.Count + 1, Scheme = "EBR", TypeName = MbrTypeName(type) + " (mantıksal)",
                    StartLba = ebrLba + rel, SectorCount = count, BytesPerSector = bps
                });
            uint nextRel = BitConverter.ToUInt32(ebr, 462 + 8);
            if (ebr[462 + 4] == 0 || nextRel == 0) break;
            ebrLba = extStart + nextRel;
        }
    }

    /// <summary>Ayrılmamış boşluklarda 1 MiB hizalı konumlarda önyükleme sektörü arar.</summary>
    private static void FindLost(SafeFileHandle h, int bps, PartitionAnalysisResult r, IProgress<int>? progress, CancellationToken ct)
    {
        long totalSectors = r.DiskSize / bps;
        long step = OneMiB / bps;
        var used = r.Partitions.Select(p => (p.StartLba, End: p.StartLba + p.SectorCount)).OrderBy(p => p.StartLba).ToList();
        var gaps = new List<(long start, long end)>();
        long cursor = r.PartitionStyle == "GPT" ? 34 : 1;
        foreach (var (s, e) in used)
        {
            if (s - cursor >= step) gaps.Add((cursor, s));
            cursor = Math.Max(cursor, e);
        }
        long tail = r.PartitionStyle == "GPT" ? totalSectors - 34 : totalSectors;
        if (tail - cursor >= step) gaps.Add((cursor, tail));
        if (gaps.Count == 0) return;

        long totalWork = gaps.Sum(g => g.end - g.start), done = 0;
        foreach (var (gStart, gEnd) in gaps)
        {
            long lba = (gStart + step - 1) / step * step; // 1 MiB hizalı ilk konum
            int candidates = 0;
            if (gStart < step && gStart <= 63) TryAddLost(h, bps, 63, gEnd, r); // eski CHS hizası (63)
            while (lba < gEnd && candidates++ < MaxCandidatesPerGap)
            {
                ct.ThrowIfCancellationRequested();
                long found = TryAddLost(h, bps, lba, gEnd, r);
                long adv = found > 0 ? Math.Max(step, (found + step - 1) / step * step) : step;
                lba += adv;
                done += adv;
                if (candidates % 256 == 0) progress?.Report(10 + (int)Math.Min(89, done * 89 / Math.Max(1, totalWork)));
            }
            if (candidates >= MaxCandidatesPerGap)
                r.Warnings.Add($"Boşluk çok büyük; LBA {gStart}–{gEnd} aralığının yalnızca bir kısmı tarandı.");
        }
    }

    /// <summary>Konumda geçerli NTFS/FAT32 önyükleme sektörü varsa kayıp bölüm ekler; sektör sayısını döndürür.</summary>
    private static long TryAddLost(SafeFileHandle h, int bps, long lba, long gapEnd, PartitionAnalysisResult r)
    {
        byte[] b;
        try { b = WinApiHelper.ReadAligned(h, lba * bps, 512); } catch { return 0; }
        if (b.Length < 512 || b[510] != 0x55 || b[511] != 0xAA) return 0;
        int sectorSize = BitConverter.ToUInt16(b, 11);
        if (sectorSize is not (512 or 1024 or 2048 or 4096) || b[13] == 0) return 0;

        string fs; long sectors;
        if (Encoding.ASCII.GetString(b, 3, 8) == "NTFS    ") { fs = "NTFS"; sectors = BitConverter.ToInt64(b, 0x28) + 1; }
        else if (Encoding.ASCII.GetString(b, 82, 8) == "FAT32   ") { fs = "FAT32"; sectors = BitConverter.ToUInt32(b, 32); }
        else return 0;

        long diskSectors = sectors * sectorSize / bps;
        if (diskSectors <= 0 || lba + diskSectors > gapEnd + 2048) return 0; // boşluğa sığmıyorsa güvenilmez
        r.Partitions.Add(new PartitionInfo
        {
            Index = r.Partitions.Count + 1, Scheme = "Kayıp", TypeName = $"{fs} (önyükleme sektöründen)", FileSystem = fs,
            StartLba = lba, SectorCount = diskSectors, BytesPerSector = bps, IsLost = true
        });
        return diskSectors;
    }

    private static string DetectFs(SafeFileHandle h, long offset)
    {
        try
        {
            var b = WinApiHelper.ReadAligned(h, offset, 512);
            if (b.Length < 512) return "-";
            string oem = Encoding.ASCII.GetString(b, 3, 8);
            if (oem == "NTFS    ") return "NTFS";
            if (oem == "EXFAT   ") return "exFAT";
            if (Encoding.ASCII.GetString(b, 82, 8) == "FAT32   ") return "FAT32";
            if (Encoding.ASCII.GetString(b, 54, 5) == "FAT16" || Encoding.ASCII.GetString(b, 54, 5) == "FAT12") return Encoding.ASCII.GetString(b, 54, 5);
            return "Bilinmiyor";
        }
        catch { return "Okunamadı"; }
    }

    private static string MbrTypeName(byte t) => t switch
    {
        0x01 => "FAT12", 0x04 or 0x06 or 0x0E => "FAT16",
        0x07 => "NTFS / exFAT", 0x0B or 0x0C => "FAT32",
        0x11 or 0x14 or 0x16 or 0x17 or 0x1B or 0x1C => "Gizli Windows bölümü",
        0x27 => "Windows Kurtarma", 0x82 => "Linux takas (swap)", 0x83 => "Linux",
        0x8E => "Linux LVM", 0xA5 or 0xA6 or 0xA9 => "BSD", 0xAF => "macOS HFS+", 0xEE => "GPT koruyucu", 0xEF => "EFI Sistem",
        _ => $"Tür 0x{t:X2}"
    };

    private static string GptTypeName(Guid g) => g.ToString().ToUpperInvariant() switch
    {
        "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7" => "Temel veri (Windows)",
        "C12A7328-F81F-11D2-BA4B-00A0C93EC93B" => "EFI Sistem Bölümü",
        "E3C9E316-0B5C-4DB8-817D-F92DF00215AE" => "Microsoft Ayrılmış (MSR)",
        "DE94BBA4-06D1-4D40-A16A-BFD50179D6AC" => "Windows Kurtarma",
        "5808C8AA-7E8F-42E0-85D2-E1E90434CFB3" => "LDM meta veri",
        "AF9B60A0-1431-4F62-BC68-3311714A69AD" => "LDM veri",
        "0FC63DAF-8483-4772-8E79-3D69D8477DE4" => "Linux dosya sistemi",
        "0657FD6D-A4AB-43C4-84E5-0933C84B4F4F" => "Linux takas (swap)",
        "E6D6D379-F507-44C2-A23C-238F2A3DF928" => "Linux LVM",
        "48465300-0000-11AA-AA11-00306543ECAC" => "macOS HFS+",
        "7C3457EF-0000-11AA-AA11-00306543ECAC" => "macOS APFS",
        _ => $"GUID {g}"
    };
}
