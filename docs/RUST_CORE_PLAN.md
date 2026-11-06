# İsteğe Bağlı Rust Çekirdeği Planı

> **Durum: Yalnızca plan.** Bu belgede anlatılanların hiçbiri henüz uygulanmadı. Mevcut uygulama tamamen C# ile çalışıyor.

## 1. Neden Rust?
| İhtiyaç | C# ile durum | Rust'ın katkısı |
|---|---|---|
| Çok büyük disklerde (TB) hızlı kazıma | GC ve dizi kopyaları ek yük getirir | Kopyasız (zero-copy) tamponlar, SIMD ile imza arama (`memchr`, `aho-corasick`) |
| Bozuk yapıları güvenle çözümleme | Taşma/indeks hataları istisna fırlatır | Bellek güvenliği + `fuzz` testleri ile ayrıştırıcıların sağlamlaştırılması |
| Paralel okuma | Mümkün ama karmaşık | `rayon` ile kolay veri paralelliği |

Arayüz (WPF/MVVM) C#'ta kalır; yalnızca **yoğun hesaplama yapan çekirdek** Rust'a taşınır.

## 2. Mimari
```
WPF (C#)  ──P/Invoke──►  recovery_core.dll (Rust, cdylib, C ABI)
  MainViewModel              ├─ carve::   imza arama + boyut tespiti
  Services (ince sarmalayıcı) ├─ ntfs::    MFT ayrıştırma, data runs
                              ├─ fat::     FAT32 / exFAT dizin gezinme
                              └─ part::    MBR / GPT / kayıp bölüm
```
- Rust tarafı `crate-type = ["cdylib"]` ile `recovery_core.dll` üretir.
- Disk tanıtıcısı C# tarafında açılabilir (yönetici izni, UAC) ve `HANDLE` olarak Rust'a verilir; ya da Rust `windows` crate'i ile kendisi açar.

## 3. C ABI tasarımı
Tüm dışa açık fonksiyonlar `extern "C"`, `#[no_mangle]`, panik sınırında `catch_unwind` ile korunur.

```rust
#[repr(C)]
pub struct RcCarvedFile {
    pub offset: u64,
    pub size: u64,
    pub ext: [u8; 8],        // NUL ile biten ASCII
    pub size_estimated: u8,
}

pub type RcProgressCb = extern "C" fn(percent: i32, user: *mut c_void) -> i32; // 0 dışı dönerse iptal

#[no_mangle]
pub extern "C" fn rc_carve(
    volume_path: *const u16,          // UTF-16, örn. \\.\E:
    enabled_mask: u32,                // tür bit maskesi
    out_files: *mut *mut RcCarvedFile,
    out_count: *mut usize,
    progress: Option<RcProgressCb>,
    user: *mut c_void,
) -> i32;                              // 0 = başarılı, <0 = hata kodu

#[no_mangle]
pub extern "C" fn rc_free_files(ptr: *mut RcCarvedFile, count: usize);

#[no_mangle]
pub extern "C" fn rc_last_error(buf: *mut u16, len: usize) -> usize; // Türkçe hata mesajı
```

C# tarafı:
```csharp
[StructLayout(LayoutKind.Sequential)]
struct RcCarvedFile { public ulong Offset, Size; public fixed byte Ext[8]; public byte SizeEstimated; }

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int RcProgressCb(int percent, IntPtr user);

[DllImport("recovery_core.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
static extern int rc_carve(string volumePath, uint mask, out IntPtr files, out nuint count, RcProgressCb? cb, IntPtr user);
```

**Kurallar**
- Belleği ayıran taraf serbest bırakır (`rc_free_files`).
- Geri çağırma (callback) delegesi C#'ta alan olarak tutulur (GC toplamasın).
- Hata kodları sabit tablo + `rc_last_error` ile Türkçe mesaj.
- İptal: callback'in dönüş değeri veya ayrı `rc_cancel(handle)`.

## 4. Aşamalar
| Aşama | İçerik | Kabul ölçütü |
|---|---|---|
| R0 | Cargo çalışma alanı, `cdylib`, CI'da `x86_64-pc-windows-msvc` derlemesi | DLL üretiliyor, C#'tan `rc_version()` çağrılıyor |
| R1 | `carve` modülü (mevcut C# algoritmasının birebir karşılığı) | Aynı test imajında C# ile aynı sonuç, ≥2× hız |
| R2 | `part` (MBR/GPT/kayıp bölüm) | Örnek imajlarda doğru bölüm listesi |
| R3 | `ntfs` tam MFT taraması (USN'den bağımsız) | Journal'ı silinmiş diskte dosya bulma |
| R4 | `fat` + exFAT | exFAT USB'de silinmiş dosya listesi |
| R5 | `cargo fuzz` ile ayrıştırıcı fuzz testleri | 24 saat çökme yok |

## 5. Test stratejisi
- Rust: birim testleri + hazır küçük disk imajları (`tests/images/*.img`) ile entegrasyon testleri.
- C#: Rust DLL yoksa mevcut C# uygulamasına geri dönüş (feature flag `UseRustCore`).
- Karşılaştırmalı test: aynı imajda C# ve Rust sonuçlarının eşitliği.

## 6. Dağıtım
- `recovery_core.dll` uygulama klasörüne kopyalanır (`<None Include="native\recovery_core.dll" CopyToOutputDirectory="PreserveNewest" />`).
- Tek dosyalı yayında `IncludeNativeLibrariesForSelfExtract=true`.

## 7. Riskler
- İki dil = iki araç zinciri; derleme süreci karmaşıklaşır.
- FFI sınırındaki hatalar (yanlış struct düzeni) sessiz bellek bozulmasına yol açabilir → struct boyutu için iki tarafta da `size_of` testleri.
- Kazanç ölçülmeden geçiş yapılmamalı: önce C# tarafında profil çıkarılmalı.
