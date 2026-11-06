# 🔍 Veri Kurtarma — v0.3.0 (Faz 3)

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
| **Rapor dışa aktarma** | Sonuçları koyu temalı **HTML** veya Excel uyumlu **CSV** (UTF-8 BOM, `;` ayırıcı) olarak kaydeder. |

## Windows'ta çalıştırma

### 1. Gereksinimler
- Windows 10 / 11 (64 bit)
- Geliştirme ve derleme için: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Yalnızca hazır derlenmiş uygulamayı çalıştırmak için: **.NET 8 Desktop Runtime** (tek dosyalı, self-contained yayın kullanılırsa gerekmez)

### 2. Kaynaktan çalıştırma
Komut İstemi'ni (cmd) veya PowerShell'i **Yönetici olarak** açın:

```powershell
cd DataRecoveryApp
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

## Proje yapısı
```
Models/      RecoverableFile, DriveInfoModel, PreviewData/CarverSettings, PartitionInfo
Services/    NtfsScanner, Fat32Scanner, CarverService, FileDataReader, RecoveryService,
             PreviewService, PartitionService, DiskImageService, ReportService, DiskScannerService
ViewModels/  MainViewModel, DriveViewModel
Helpers/     WinApiHelper (P/Invoke), FileSizeHelper, ObservableObject, RelayCommand
Converters/  WPF değer dönüştürücüleri
docs/        RUST_CORE_PLAN.md (isteğe bağlı gelecek Rust çekirdeği planı)
```
