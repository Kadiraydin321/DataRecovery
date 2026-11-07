# 🔍 Veri Kurtarma — v0.3.1 (Faz 3 + Rust Çekirdeği)

Windows için C# + WPF (.NET 8) ile geliştirilen, koyu temalı, Türkçe arayüzlü silinmiş dosya kurtarma aracı.

> ⚠️ **Durum:** Uygulama Linux üzerinde Windows hedefli olarak derlenmektedir (`dotnet build` 0 hata).
> **Gerçek bir Windows makinesinde henüz test edilmemiştir.** Önemli verilerde kullanmadan önce
> test amaçlı bir USB bellekte deneyin ve mümkünse önce **disk imajı** alın.

## Özellikler

| Özellik | Açıklama |
|---|---|
| **NTFS taraması** | USN Journal'daki silme kayıtlarından silinmiş dosyaları listeler; MFT kaydındaki veri parçalarıyla (data runs) kurtarır. |
| **FAT32 taraması** | Kök dizinden başlayıp tüm alt dizinleri küme zincirleri üzerinden özyinelemeli gezer; uzun dosya adlarını (LFN) okur, silinmiş klasörlerin içine de bakar. |
| **Veri kazıma (carving)** | 4 MB'lık bloklarla okuma; JPEG/PNG/GIF/BMP/TIFF/PDF/ZIP/DOCX/XLSX/PPTX/RAR/7Z/MP4/EXE imzaları. Sonlandırıcı ve başlık bilgisinden gerçek boyut tespiti (JPEG `FFD9`, PNG `IEND`, PDF `%%EOF`, GIF `00 3B`, ZIP merkezi dizin sonu, BMP/7z/MP4/PE başlıkları), türe göre azami boyut, ZIP ailesinin içerik incelemesiyle ayrılması, bulunan dosyanın sonuna atlama. |
| **Gerçek önizleme** | Seçilen dosyanın baytları diskten okunur: resimler için küçük resim, metin dosyaları için ilk ~4 KB, diğerleri için onaltılık (hex) döküm. |
| **Kurtarma** | NTFS, FAT32 ve kazınmış dosyalar seçilen klasöre yazılır. Hedef, taranan sürücüyle aynıysa işlem reddedilir. |
| **Bölüm analizi** | Fiziksel diskin MBR/GPT tablosunu (genişletilmiş bölümler dahil) listeler; ayrılmamış alanlarda NTFS/FAT32 önyükleme sektörü arayarak **kayıp bölüm** adaylarını gösterir. |
| **Disk imajı** | Sürücünün ham kopyasını `.img` dosyasına alır. |
| **Rust çekirdeği** | `rust/recovery_engine` (cdylib) imza taramasını ve MBR/GPT ayrıştırmasını yerel kodda yapar. `native\x64\recovery_engine.dll` bulunursa otomatik kullanılır, bulunmazsa saf C# motoru devreye girer. Üst barda hangi motorun etkin olduğu yazar. |
| **Rapor dışa aktarma** | Sonuçları koyu temalı **HTML** veya Excel uyumlu **CSV** (UTF-8 BOM, `;` ayırıcı) olarak kaydeder. |

## Windows'ta çalıştırma

### 1. Gereksinimler
| Bileşen | Ne zaman gerekli | Not |
|---|---|---|
| Windows 10 / 11 (64 bit) | Her zaman | Ham disk erişimi için **yönetici** hesabı |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Kaynaktan derleme | `dotnet --version` → `8.x` |
| .NET 8 **Desktop Runtime** | Yalnızca hazır (framework-dependent) .exe çalıştırma | Self-contained yayında gerekmez |
| [Git](https://git-scm.com/download/win) | Depoyu klonlama | — |
| [Rust (rustup)](https://rustup.rs) | **İsteğe bağlı** — yalnızca Rust DLL'ini yeniden derlemek için | Hazır DLL depoda var; MSVC için *Visual Studio Build Tools (C++)* gerekir |

### 2. Kaynaktan çalıştırma
Komut İstemi'ni (cmd) veya PowerShell'i **Yönetici olarak** açın:

```powershell
git clone https://github.com/<kullanici>/<repo>.git
cd <repo>
dotnet run
```

### 3. Tek dosyalı .exe oluşturma
```powershell
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained
```
Çıktı: `bin\Release\net8.0-windows\win-x64\publish\DataRecoveryApp.exe`

### 4. Yönetici izni
Ham disk erişimi (`\\.\C:`, `\\.\PhysicalDrive0`) için yönetici izni gerekir. `app.manifest` dosyası
`requireAdministrator` istediği için Windows açılışta UAC onayı sorar. Onay verilmezse tarama
"Yönetici izni gerekiyor" mesajıyla başarısız olur.

## Rust çekirdeği (recovery_engine)

Depoda hazır derlenmiş `native/x64/recovery_engine.dll` (x86_64-pc-windows-gnu, statik CRT) bulunur;
`dotnet build`/`publish` bunu çıktı klasöründe **`native\x64\recovery_engine.dll`** konumuna kopyalar
(yani `DataRecoveryApp.exe` ile aynı klasörün altında `native\x64\`). DLL yoksa build yine başarılı olur.

Dışa aktarılan C fonksiyonları: `re_version`, `re_scan_signatures`, `re_cancel_scan`, `re_scan_progress`,
`re_parse_partitions`, `re_parse_partitions_alloc`, `re_read_sectors`, `re_free_string`
(ayrıntılar: `docs/RUST_CORE_PLAN.md`).

DLL'i yeniden derlemek için (Linux/WSL):
```bash
rustup target add x86_64-pc-windows-gnu
sudo apt-get install -y gcc-mingw-w64-x86-64
cd rust/recovery_engine
cargo test                                              # birim testleri (yerel)
cargo build --target x86_64-pc-windows-gnu --release
cp target/x86_64-pc-windows-gnu/release/recovery_engine.dll ../../native/x64/
```
Windows'ta doğrudan MSVC ile (PowerShell):
```powershell
rustup default stable-x86_64-pc-windows-msvc
cd rust\recovery_engine
cargo test --release
cargo build --release
copy target\release\recovery_engine.dll ..\..\native\x64\
```

## Performans notları (v0.3.1)
- Sonuç listeleri `BulkObservableCollection` ile tek bildirimle güncellenir; DataGrid satır/sütun sanallaştırması açıktır.
- Disk imajı 4 MB'lık yeniden kullanılan tamponla alınır; okunamayan bölgeler 4 KB'lık parçalarla yeniden denenir, olmazsa sıfırla doldurulur ve okunamayan bayt sayısı raporlanır.
- NTFS USN okuması 1 MB tamponla, Rust taraması `memchr` (memmem) ile yapılır.
- Bu iyileştirmelerin hız kazancı **ölçülmedi**.

## Kod haritası
`graphify-out/graph.html` tarayıcıda açılarak dosyalar/sınıflar arası bağımlılık grafiği incelenebilir
(`graphify-out/GRAPH_REPORT.md` özet rapor). Yenilemek için: `pip install graphifyy && graphify update .`

## Kullanım ipuçları
1. Veri kaybı fark edilir edilmez o sürücüye **yazmayı bırakın**.
2. Kurtarılan dosyaları **başka bir sürücüye** kaydedin.
3. Değerli veriler için önce **Disk İmajı Oluştur** ile yedek alın.
4. Normal tarama sonuç vermezse **Veri Kazıma**'yı deneyin (biçimlendirilmiş disklerde de çalışır, ancak dosya adlarını kurtaramaz).

## Bilinen sınırlamalar
- Windows'ta test edilmedi; P/Invoke çağrıları ve ham disk okumaları gerçek donanımda doğrulanmalı.
- NTFS taraması USN Journal'a dayanır: kayıt yoksa / journal sıfırlandıysa dosya listelenmez (tam MFT taraması planlanıyor). USN kayıtlarında boyut bilgisi olmadığından boyut, önizleme/kurtarma sırasında MFT'den öğrenilir.
- FAT32'de silinen dosyanın küme zinciri silindiği için veri **bitişik** varsayılır; parçalı dosyalar bozuk çıkabilir. exFAT desteklenmiyor (kazıma kullanılabilir).
- Kazıma parçalanmış dosyaları birleştiremez; PDF'te ilk `%%EOF`'ta durulur (artımlı güncellemeli PDF'ler kısa kesilebilir); TIFF/RAR için boyut tahminidir.
- Kayıp bölüm araması yalnızca 1 MiB hizalı konumlarda (ve eski 63. sektörde) yapılır; çok büyük boşluklarda tarama sınırlıdır. Bulunan bölümler yalnızca **listelenir**, bölüm tablosuna yazılmaz.
- Kurtarılabilirlik yüzdeleri sezgisel tahminlerdir.
- Rust çekirdeği yalnızca Linux'ta birim testleriyle doğrulandı ve Windows için çapraz derlendi; DLL'in Windows'ta yüklenmesi, P/Invoke çağrıları ve ham aygıt (`\\.\E:`, `\\.\PhysicalDriveN`) okumaları **gerçek Windows'ta test edilmedi**. Rust bölüm ayrıştırıcısı MBR'de 512 baytlık sektör varsayar; kayıp bölüm araması C# tarafında kalır.

## Proje yapısı
```
Models/      RecoverableFile, DriveInfoModel, PreviewData/CarverSettings, PartitionInfo
Services/    NtfsScanner, Fat32Scanner, CarverService, FileDataReader, RecoveryService,
             PreviewService, PartitionService, DiskImageService, ReportService, DiskScannerService,
             RustBridge (Rust çekirdeği P/Invoke katmanı)
ViewModels/  MainViewModel, DriveViewModel
Helpers/     WinApiHelper (P/Invoke), BulkObservableCollection, FileSizeHelper, ObservableObject, RelayCommand
Converters/  WPF değer dönüştürücüleri
rust/        recovery_engine — Rust çekirdeği (lib.rs, scanner.rs, partition.rs, reader.rs)
native/x64/  recovery_engine.dll (çapraz derlenmiş)
docs/        RUST_CORE_PLAN.md (Rust çekirdeği mimarisi ve C ABI)
graphify-out/ Kod bağımlılık haritası (graph.html, GRAPH_REPORT.md)
```
