using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataRecoveryApp.Services;

/// <summary>
/// Rust çekirdeği (native\x64\recovery_engine.dll) için P/Invoke katmanı.
/// DLL bulunamazsa <see cref="IsAvailable"/> false olur ve servisler saf C# yoluna döner.
/// Tüm dizgiler UTF-8 olarak aktarılır.
/// </summary>
public static class RustBridge
{
    public const string DllPath = "native/x64/recovery_engine.dll";

    // Hata kodları (rust/recovery_engine/src/lib.rs ile aynı)
    public const int RE_OK = 0;
    public const int RE_ERR_NULL_PTR = -1;
    public const int RE_ERR_INVALID_UTF8 = -2;
    public const int RE_ERR_OPEN_FAILED = -3;
    public const int RE_ERR_READ_FAILED = -4;
    public const int RE_ERR_INVALID_JSON = -5;
    public const int RE_ERR_BUFFER_TOO_SMALL = -6;
    public const int RE_ERR_NO_TABLE = -7;
    public const int RE_ERR_NO_SIGNATURES = -8;
    public const int RE_ERR_CANCELLED = -9;
    public const int RE_ERR_ACCESS_DENIED = -10;
    public const int RE_ERR_PANIC = -99;

    /// <summary>Callback'te sig_id'nin en üst biti set ise boyut tahminidir.</summary>
    public const uint EstimatedFlag = 0x8000_0000;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ScanCallback(ulong offset, uint sigId, ulong estimatedSize);

    [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr re_version();

    [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
    public static extern int re_scan_signatures(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string drivePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? settingsJson,
        ScanCallback? callback);

    [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
    public static extern void re_cancel_scan();

    [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint re_scan_progress();

    [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
    public static extern int re_parse_partitions(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string diskPath,
        byte[]? outJson, out uint outLen, uint bufSize);

    [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr re_parse_partitions_alloc(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string diskPath, out int error);

    [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
    public static extern int re_read_sectors(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string drivePath,
        ulong startLba, uint sectorCount, byte[] outBuf, uint bufLen);

    [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
    public static extern void re_free_string(IntPtr ptr);

    private static readonly Lazy<string?> _version = new(LoadVersion);

    static RustBridge()
    {
        // Göreli DllImport yolu bazı başlatma senaryolarında (ör. farklı çalışma dizini) çözülemeyebilir;
        // bu durumda uygulama dizinindeki native\x64 klasörü açıkça denenir.
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(RustBridge).Assembly, (name, asm, path) =>
            {
                if (!name.Contains("recovery_engine", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                string full = Path.Combine(AppContext.BaseDirectory, "native", "x64", "recovery_engine.dll");
                return NativeLibrary.TryLoad(full, out var h) ? h : IntPtr.Zero;
            });
        }
        catch (InvalidOperationException) { /* çözücü zaten ayarlanmış */ }
    }

    /// <summary>DLL yüklenip re_version çağrılabiliyorsa true (sonuç önbelleğe alınır).</summary>
    public static bool IsAvailable => _version.Value != null;

    public static string? Version => _version.Value;

    /// <summary>Arayüzde gösterilecek kısa durum metni.</summary>
    public static string StatusMessage => IsAvailable
        ? $"⚙ Rust çekirdeği v{Version} etkin"
        : "⚙ Rust çekirdeği yok — C# motoru kullanılıyor";

    private static string? LoadVersion()
    {
        try
        {
            var p = re_version();
            string? v = p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
            Debug.WriteLine($"[RustBridge] Rust çekirdeği yüklendi, sürüm {v}.");
            return v;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Debug.WriteLine($"[RustBridge] Rust çekirdeği yüklenemedi ({DllPath}); saf C# motoru kullanılacak. Ayrıntı: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RustBridge] Rust çekirdeği beklenmeyen hata: {ex.Message}; saf C# motoru kullanılacak.");
            return null;
        }
    }

    public static string ErrorText(int code) => code switch
    {
        RE_ERR_NULL_PTR => "Geçersiz (NULL) parametre.",
        RE_ERR_INVALID_UTF8 => "Yol UTF-8 değil.",
        RE_ERR_OPEN_FAILED => "Aygıt açılamadı.",
        RE_ERR_ACCESS_DENIED => "Erişim reddedildi (yönetici olarak çalıştırın).",
        RE_ERR_READ_FAILED => "Aygıt okunamadı.",
        RE_ERR_INVALID_JSON => "Ayar JSON'u geçersiz.",
        RE_ERR_BUFFER_TOO_SMALL => "Arabellek yetersiz.",
        RE_ERR_NO_TABLE => "Geçerli bölüm tablosu (55 AA) bulunamadı.",
        RE_ERR_NO_SIGNATURES => "Hiç kazıma formatı seçilmedi.",
        RE_ERR_CANCELLED => "Tarama iptal edildi.",
        RE_ERR_PANIC => "Rust çekirdeğinde iç hata.",
        _ => $"Bilinmeyen hata ({code})."
    };

    /// <summary>re_parse_partitions'ı boyut sorgusu + yeniden deneme ile çağırıp JSON döndürür.</summary>
    public static string? ParsePartitionsJson(string diskPath, out int error)
    {
        var buf = new byte[16 * 1024];
        error = re_parse_partitions(diskPath, buf, out uint len, (uint)buf.Length);
        if (error == RE_ERR_BUFFER_TOO_SMALL && len > buf.Length)
        {
            buf = new byte[len];
            error = re_parse_partitions(diskPath, buf, out len, (uint)buf.Length);
        }
        return error == RE_OK ? Encoding.UTF8.GetString(buf, 0, (int)len) : null;
    }

    // ── JSON modeli (Rust partition.rs ile aynı alanlar) ──
    public sealed class NativePartitionTable
    {
        [JsonPropertyName("style")] public string Style { get; set; } = "";
        [JsonPropertyName("sector_size")] public int SectorSize { get; set; }
        [JsonPropertyName("partitions")] public List<NativePartition> Partitions { get; set; } = new();
        [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
    }

    public sealed class NativePartition
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("scheme")] public string Scheme { get; set; } = "";
        [JsonPropertyName("type_name")] public string TypeName { get; set; } = "";
        [JsonPropertyName("file_system")] public string FileSystem { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("start_lba")] public long StartLba { get; set; }
        [JsonPropertyName("sector_count")] public long SectorCount { get; set; }
        [JsonPropertyName("bootable")] public bool Bootable { get; set; }
        [JsonPropertyName("type_id")] public string TypeId { get; set; } = "";
    }

    public static NativePartitionTable? ParsePartitions(string diskPath, out int error)
    {
        var json = ParsePartitionsJson(diskPath, out error);
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<NativePartitionTable>(json); }
        catch (JsonException) { error = RE_ERR_INVALID_JSON; return null; }
    }

    /// <summary>Rust imza kimliği → dosya uzantısı (scanner.rs SIG_* sabitleri).</summary>
    public static string ExtensionForSig(uint sigId) => (sigId & ~EstimatedFlag) switch
    {
        1 => "jpg", 2 => "png", 3 => "pdf", 4 => "gif", 5 => "zip", 6 => "bmp", 7 => "avi",
        8 => "7z", 9 => "rar", 10 => "docx", 11 => "xlsx", 12 => "pptx", 13 => "tif", 14 => "mp4", 15 => "exe",
        _ => "bin"
    };

    public static readonly (uint Id, string Ext)[] AllSigs =
        Enumerable.Range(1, 15).Select(i => ((uint)i, ExtensionForSig((uint)i))).ToArray();
}
