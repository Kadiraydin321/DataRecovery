using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;
using Microsoft.Win32.SafeHandles;

namespace DataRecoveryApp.Services;

/// <summary>
/// İmza tabanlı veri kazıma. Birim 4 MB'lık bloklar halinde okunur, imzalar 512 baytlık
/// sektör başlarında aranır (dosyalar küme/sektör başında başlar). Bulunan dosyanın gerçek
/// boyutu sonlandırıcı/başlık bilgisinden belirlenir ve tarama dosyanın sonuna atlar.
/// </summary>
public class CarverService
{
    private const int BlockSize = 4 * 1024 * 1024;
    private const int Sector = 512;
    private const int ScanChunk = 1024 * 1024;
    private const long MB = 1024 * 1024;

    public string? LastError { get; private set; }

    private static readonly byte[] JpegEoi = { 0xFF, 0xD9 };

    private sealed record Sig(string Ext, byte[] Magic, int MagicOffset, long MaxSize);

    // Her türün en fazla boyutu (sonlandırıcı bulunamazsa ya da sınırı aşarsa kullanılır).
    private static readonly Sig[] AllSignatures =
    {
        new("jpg", new byte[] { 0xFF, 0xD8, 0xFF }, 0, 50 * MB),
        new("png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 50 * MB),
        new("gif", Encoding.ASCII.GetBytes("GIF8"), 0, 20 * MB),
        new("bmp", new byte[] { 0x42, 0x4D }, 0, 100 * MB),
        new("tif", new byte[] { 0x49, 0x49, 0x2A, 0x00 }, 0, 10 * MB),
        new("pdf", Encoding.ASCII.GetBytes("%PDF-"), 0, 200 * MB),
        new("zip", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, 0, 500 * MB),
        new("rar", Encoding.ASCII.GetBytes("Rar!\x1A\x07"), 0, 10 * MB),
        new("7z", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, 0, 1024 * MB),
        new("mp4", Encoding.ASCII.GetBytes("ftyp"), 4, 4096 * MB),
        new("exe", new byte[] { 0x4D, 0x5A }, 0, 200 * MB),
    };

    public Task<List<RecoverableFile>> CarveAsync(string driveLetter, CarverSettings settings,
        IProgress<int>? progress, CancellationToken ct)
        => Task.Run(() => Carve(driveLetter, settings, progress, ct), ct);

    private List<RecoverableFile> Carve(string driveLetter, CarverSettings settings, IProgress<int>? progress, CancellationToken ct)
    {
        LastError = null;
        if (RustBridge.IsAvailable)
        {
            var native = CarveWithRust(driveLetter, settings, progress, ct);
            if (native != null) return native;
            Debug.WriteLine($"[CarverService] Rust taraması başarısız ({LastError}); C# kazıyıcıya dönülüyor.");
            LastError = null;
        }

        var results = new List<RecoverableFile>();
        var sigs = AllSignatures.Where(s => s.Ext == "zip" ? settings.AnyZipFamily : settings.IsEnabled(s.Ext)).ToArray();
        if (sigs.Length == 0) { LastError = "Hiç kazıma formatı seçilmedi."; return results; }

        try
        {
            using var h = WinApiHelper.OpenVolume(driveLetter);
            long total = GetVolumeLength(h, driveLetter);
            if (total <= 0) { LastError = "Birim boyutu okunamadı."; return results; }

            byte[] block = Array.Empty<byte>();
            long blockStart = -1, pos = 0;
            int lastPct = -1, counter = 0;

            while (pos < total)
            {
                ct.ThrowIfCancellationRequested();
                // Geçerli konum blokta değilse (veya imza okumak için yeterli bayt yoksa) yeni blok oku
                if (blockStart < 0 || pos < blockStart || pos + 16 > blockStart + block.Length)
                {
                    blockStart = pos;
                    int len = (int)Math.Min(BlockSize, total - pos);
                    try { block = WinApiHelper.ReadAligned(h, pos, len); }
                    catch { block = Array.Empty<byte>(); }
                    if (block.Length < 16) { pos += Math.Max(Sector, len); blockStart = -1; continue; } // okunamayan bölge
                }

                int rel = (int)(pos - blockStart);
                long advance = Sector;
                foreach (var sig in sigs)
                {
                    if (!Matches(block, rel, sig)) continue;
                    var found = Inspect(h, pos, total, sig, settings, ct);
                    if (found == null) continue;
                    found.FileName = $"kazinan_{results.Count + 1:D5}.{found.Extension}";
                    results.Add(found);
                    advance = Math.Max(Sector, (found.Size + Sector - 1) / Sector * Sector); // dosyanın sonuna atla
                    break;
                }
                pos += advance;

                if (++counter % 2048 == 0 || advance > Sector)
                {
                    int pct = (int)(pos * 100 / total);
                    if (pct != lastPct) { progress?.Report(Math.Min(pct, 100)); lastPct = pct; }
                }
            }
            progress?.Report(100);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException ex) { LastError = ex.Message; }
        catch (Exception ex) { LastError = $"Kazıma hatası: {ex.Message}"; }
        return results;
    }

    /// <summary>
    /// Rust çekirdeği (re_scan_signatures) ile kazıma. İlerleme re_scan_progress ile yoklanır,
    /// iptal re_cancel_scan ile iletilir. Aygıt açılamazsa/iç hata olursa null döner (C# yoluna geçilir).
    /// </summary>
    private List<RecoverableFile>? CarveWithRust(string driveLetter, CarverSettings settings, IProgress<int>? progress, CancellationToken ct)
    {
        var enabled = RustBridge.AllSigs.Where(s => settings.IsEnabled(s.Ext)).Select(s => s.Id).ToArray();
        if (enabled.Length == 0) { LastError = "Hiç kazıma formatı seçilmedi."; return new List<RecoverableFile>(); }

        long total;
        try
        {
            using var h = WinApiHelper.OpenVolume(driveLetter);
            total = GetVolumeLength(h, driveLetter);
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
        if (total <= 0) { LastError = "Birim boyutu okunamadı."; return null; }

        string path = $@"\\.\{driveLetter.TrimEnd(':', '\\')}:";
        string json = JsonSerializer.Serialize(new { enabled, volume_size = total });
        var results = new List<RecoverableFile>();

        // Callback Rust iş parçacığında (re_scan_signatures'ı çağıran Task) senkron çalışır.
        // Yerel koda istisna sızmaması için gövde try/catch ile korunur.
        RustBridge.ScanCallback cb = (offset, sigId, size) =>
        {
            try
            {
                bool estimated = (sigId & RustBridge.EstimatedFlag) != 0;
                string ext = RustBridge.ExtensionForSig(sigId);
                long off = (long)offset;
                results.Add(new RecoverableFile
                {
                    FileName = $"kazinan_{results.Count + 1:D5}.{ext}",
                    Extension = ext,
                    Size = (long)size,
                    SizeIsEstimated = estimated,
                    Source = RecoverySource.Carved,
                    DataOffset = off,
                    StartSector = off / Sector,
                    FilePath = $"Ofset 0x{off:X} (sektör {off / Sector})",
                    FileCategory = RecoverableFile.CategoryNameFor(ext),
                    RecoverabilityPercent = estimated ? 35 : 65,
                    FileReferenceNumber = offset
                });
            }
            catch (Exception ex) { Debug.WriteLine($"[CarverService] Rust callback hatası: {ex.Message}"); }
        };

        int rc;
        using (var done = new ManualResetEventSlim(false))
        using (ct.Register(RustBridge.re_cancel_scan))
        {
            // İlerleme yoklayıcı: Rust tarafı binde cinsinden ilerleme yayınlar.
            var poller = Task.Run(() =>
            {
                while (!done.Wait(250)) progress?.Report((int)Math.Min(100, RustBridge.re_scan_progress() / 10));
            });
            try { rc = RustBridge.re_scan_signatures(path, json, cb); }
            finally { done.Set(); poller.Wait(); GC.KeepAlive(cb); }
        }

        if (rc == RustBridge.RE_ERR_CANCELLED) throw new OperationCanceledException(ct);
        if (rc != RustBridge.RE_OK)
        {
            LastError = $"Rust çekirdeği: {RustBridge.ErrorText(rc)}";
            return rc is RustBridge.RE_ERR_NO_SIGNATURES ? results : null;
        }
        progress?.Report(100);
        return results;
    }

    private static long GetVolumeLength(SafeFileHandle h, string driveLetter)
    {
        try { return WinApiHelper.GetLength(h); }
        catch
        {
            try { return new DriveInfo(driveLetter + ":\\").TotalSize; } catch { return 0; }
        }
    }

    private static bool Matches(byte[] block, int rel, Sig sig)
    {
        int start = rel + sig.MagicOffset;
        return start + sig.Magic.Length <= block.Length && block.AsSpan(start, sig.Magic.Length).SequenceEqual(sig.Magic);
    }

    /// <summary>Başlık eşleşmesini doğrular, boyutu belirler, türü sınıflandırır. Geçersizse null.</summary>
    private static RecoverableFile? Inspect(SafeFileHandle h, long offset, long total, Sig sig, CarverSettings settings, CancellationToken ct)
    {
        long maxSize = Math.Min(sig.MaxSize, total - offset);
        var head = SafeRead(h, offset, 4096);
        if (head.Length < 64) return null;

        string ext = sig.Ext;
        long size;
        bool estimated = false;

        switch (sig.Ext)
        {
            case "jpg": size = JpegSize(h, offset, head, maxSize, ct); break;
            case "png": size = FindFooter(h, offset, maxSize, Encoding.ASCII.GetBytes("IEND"), 8, ct); break; // IEND + CRC
            case "gif":
                if (head[4] != (byte)'7' && head[4] != (byte)'9') return null;
                size = FindFooter(h, offset, maxSize, new byte[] { 0x00, 0x3B }, 2, ct); break;
            case "pdf": size = FindFooter(h, offset, maxSize, Encoding.ASCII.GetBytes("%%EOF"), 5, ct); break;
            case "bmp":
                size = BitConverter.ToUInt32(head, 2);
                uint dataOff = BitConverter.ToUInt32(head, 10), dibSize = BitConverter.ToUInt32(head, 14);
                if (size < 64 || size > maxSize || dataOff >= size || dibSize is not (12 or 40 or 52 or 56 or 108 or 124)) return null;
                break;
            case "zip":
                (size, ext) = ZipSizeAndKind(h, offset, maxSize, ct);
                if (size > 0 && !settings.IsEnabled(ext)) return null;
                break;
            case "7z":
                long nextOff = BitConverter.ToInt64(head, 12), nextSize = BitConverter.ToInt64(head, 20);
                size = 32 + nextOff + nextSize;
                if (nextOff < 0 || nextSize <= 0 || size > maxSize) return null;
                break;
            case "mp4": size = Mp4Size(h, offset, head, maxSize); break;
            case "exe": size = PeSize(head, maxSize); break;
            default: size = 0; break; // tif, rar: sonlandırıcı yok
        }

        if (size <= 0)
        {
            if (sig.Ext is "jpg" or "png" or "gif" or "pdf" or "zip" or "mp4" or "exe" or "bmp" or "7z") return null; // doğrulanamadı → yanlış pozitif
            size = Math.Min(sig.MaxSize, maxSize);
            estimated = true;
        }

        return new RecoverableFile
        {
            Extension = ext,
            Size = size,
            SizeIsEstimated = estimated,
            Source = RecoverySource.Carved,
            DataOffset = offset,
            StartSector = offset / Sector,
            FilePath = $"Ofset 0x{offset:X} (sektör {offset / Sector})",
            FileCategory = RecoverableFile.CategoryNameFor(ext),
            RecoverabilityPercent = estimated ? 35 : 65,
            FileReferenceNumber = (ulong)offset
        };
    }

    private static byte[] SafeRead(SafeFileHandle h, long offset, int length)
    {
        try { return WinApiHelper.ReadAligned(h, offset, length); }
        catch { return Array.Empty<byte>(); }
    }

    /// <summary>Bir sonlandırıcı dizisini parça parça arar; dosya boyutunu (sonlandırıcı + ek) döndürür.</summary>
    private static long FindFooter(SafeFileHandle h, long offset, long maxSize, byte[] footer, int footerTotalLen, CancellationToken ct)
    {
        int overlap = footer.Length - 1;
        for (long done = 0; done < maxSize; done += ScanChunk - overlap)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = SafeRead(h, offset + done, (int)Math.Min(ScanChunk, maxSize - done));
            if (chunk.Length < footer.Length) return 0;
            int idx = chunk.AsSpan().IndexOf(footer);
            if (idx >= 0) return Math.Min(done + idx + footerTotalLen, maxSize);
            if (chunk.Length < ScanChunk) return 0;
        }
        return 0;
    }

    /// <summary>JPEG: segmentleri SOS'a kadar atla (EXIF küçük resmi APP1 içinde kalır), sonra FFD9 ara.</summary>
    private static long JpegSize(SafeFileHandle h, long offset, byte[] _, long maxSize, CancellationToken ct)
    {
        // APP segmentleri (EXIF) 64 KB'a kadar olabilir; başlık yapısı için 256 KB okunur.
        var head = SafeRead(h, offset, (int)Math.Min(256 * 1024, maxSize));
        int p = 2;
        bool sos = false;
        while (p + 4 < head.Length)
        {
            if (head[p] != 0xFF) return 0; // geçersiz yapı → yanlış pozitif
            byte marker = head[p + 1];
            if (marker == 0xFF) { p++; continue; } // dolgu baytı
            if (marker == 0xDA) { sos = true; break; } // SOS: sıkıştırılmış veri başlıyor
            int segLen = (head[p + 2] << 8) | head[p + 3];
            if (segLen < 2) return 0;
            p += 2 + segLen;
        }
        if (!sos) return 0;
        long start = p;
        for (long done = start; done < maxSize; done += ScanChunk - 1)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = SafeRead(h, offset + done, (int)Math.Min(ScanChunk, maxSize - done));
            if (chunk.Length < 2) return 0;
            int i = chunk.AsSpan().IndexOf(JpegEoi); // vektörize arama (bayt bayt döngü yerine)
            if (i >= 0) return done + i + 2;
            if (chunk.Length < ScanChunk) return 0;
        }
        return 0;
    }

    /// <summary>ZIP: Merkezi dizin sonu (PK\x05\x06) ile boyut; içerikteki yollarla DOCX/XLSX/PPTX ayrımı.</summary>
    private static (long size, string ext) ZipSizeAndKind(SafeFileHandle h, long offset, long maxSize, CancellationToken ct)
    {
        byte[] eocd = { 0x50, 0x4B, 0x05, 0x06 };
        bool contentTypes = false, word = false, xl = false, ppt = false;
        for (long done = 0; done < maxSize; done += ScanChunk - 64)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = SafeRead(h, offset + done, (int)Math.Min(ScanChunk, maxSize - done));
            if (chunk.Length < 22) break;
            var span = chunk.AsSpan();
            contentTypes |= span.IndexOf("[Content_Types].xml"u8) >= 0;
            word |= span.IndexOf("word/"u8) >= 0;
            xl |= span.IndexOf("xl/"u8) >= 0;
            ppt |= span.IndexOf("ppt/"u8) >= 0;
            int idx = span.IndexOf(eocd);
            if (idx >= 0 && idx + 22 <= chunk.Length)
            {
                int commentLen = BitConverter.ToUInt16(chunk, idx + 20);
                string ext = !contentTypes ? "zip" : word ? "docx" : xl ? "xlsx" : ppt ? "pptx" : "zip";
                return (Math.Min(done + idx + 22 + commentLen, maxSize), ext);
            }
            if (chunk.Length < ScanChunk) break;
        }
        return (0, "zip");
    }

    /// <summary>MP4/MOV: üst düzey kutuları (box) gezerek boyutu hesaplar.</summary>
    private static long Mp4Size(SafeFileHandle h, long offset, byte[] head, long maxSize)
    {
        string[] known = { "ftyp", "moov", "mdat", "free", "skip", "wide", "uuid", "moof", "mfra", "meta", "pdin", "styp", "sidx", "pnot" };
        long pos = 0;
        int boxes = 0;
        while (pos < maxSize && boxes++ < 10000)
        {
            var hdr = SafeRead(h, offset + pos, 16);
            if (hdr.Length < 8) break;
            long boxSize = (uint)((hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3]);
            string type = Encoding.ASCII.GetString(hdr, 4, 4);
            if (!known.Contains(type)) break;
            if (boxSize == 1 && hdr.Length >= 16)
                boxSize = (long)((ulong)hdr[8] << 56 | (ulong)hdr[9] << 48 | (ulong)hdr[10] << 40 | (ulong)hdr[11] << 32 |
                                 (ulong)hdr[12] << 24 | (ulong)hdr[13] << 16 | (ulong)hdr[14] << 8 | hdr[15]);
            if (boxSize < 8) break;
            pos += boxSize;
        }
        return pos >= 16 && pos <= maxSize ? pos : 0;
    }

    /// <summary>PE (EXE/DLL): bölüm tablosundan dosya sonu = max(PointerToRawData + SizeOfRawData).</summary>
    private static long PeSize(byte[] head, long maxSize)
    {
        int peOff = BitConverter.ToInt32(head, 0x3C);
        if (peOff <= 0 || peOff + 24 > head.Length || BitConverter.ToUInt32(head, peOff) != 0x00004550) return 0; // "PE\0\0"
        int sections = BitConverter.ToUInt16(head, peOff + 6);
        int optSize = BitConverter.ToUInt16(head, peOff + 20);
        int table = peOff + 24 + optSize;
        long end = 0;
        for (int i = 0; i < sections && table + i * 40 + 40 <= head.Length; i++)
        {
            int s = table + i * 40;
            long e = (long)BitConverter.ToUInt32(head, s + 20) + BitConverter.ToUInt32(head, s + 16);
            end = Math.Max(end, e);
        }
        return end > 0 && end <= maxSize ? end : 0;
    }
}
