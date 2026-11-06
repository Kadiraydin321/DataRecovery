# Rust Çekirdeği (recovery_engine) — Uygulama Belgesi

> **Durum (v0.3.1):** Rust çekirdeği artık yalnızca plan değil, **gerçekten yazıldı**.
> `rust/recovery_engine/` bir `cdylib` kasasıdır; Linux'ta `cargo test` ile 7 birim testi geçer ve
> `x86_64-pc-windows-gnu` hedefi için `recovery_engine.dll` olarak çapraz derlenip `native/x64/` altına konmuştur.
> **Gerçek Windows'ta test edilmedi.**

## Mimari

```
C# (WPF)                                   Rust (recovery_engine.dll)
CarverService ──┐                          lib.rs       C ABI, hata kodları, catch_unwind
PartitionService┼─ RustBridge (P/Invoke) ─▶ scanner.rs   imza tablosu, 4 MB bloklar, footer tespiti
                │   DLL yoksa ↓            partition.rs MBR + EBR zinciri + GPT, FS tespiti, JSON
                └─ saf C# yolu (fallback)  reader.rs    std::fs::File ile hizalı ham okuma
```

- `RustBridge.IsAvailable` ilk erişimde `re_version()` çağırır; `DllNotFoundException` vb. yakalanırsa
  `false` olur, Türkçe bir `Debug` günlük mesajı yazılır ve servisler mevcut C# kodunu kullanır.
- Rust çağrısı çalışma anında hata döndürürse (aygıt açılamadı, iç hata) servisler yine C# yoluna geçer.
- Üst barda "Rust çekirdeği v1.0.0 etkin" / "Rust çekirdeği yok — C# motoru kullanılıyor" yazar.

## Dışa aktarılan C fonksiyonları

Tüm dizgiler UTF-8 + NUL sonlandırmalı; çağrı kuralı `cdecl` (x64'te tek kural).

| Fonksiyon | Açıklama |
|---|---|
| `const char* re_version()` | Statik `"1.0.0"`. Serbest bırakılmaz. |
| `int32 re_scan_signatures(const char* drive_path, const char* settings_json, cb)` | Sektör (512 B) başlarında imza taraması. Her bulguda `cb(uint64 offset, uint32 sig_id, uint64 estimated_size)`. 0 = başarı. |
| `void re_cancel_scan()` | Süren taramanın `-9` (iptal) ile dönmesini ister. |
| `uint32 re_scan_progress()` | İlerleme, binde (0–1000). |
| `int32 re_parse_partitions(const char* disk_path, char* out_json, uint32* out_len, uint32 buf_size)` | Sektör 0 → MBR/GPT → JSON. Arabellek küçükse `*out_len` = gereken boyut (NUL dahil) ve `-6`. |
| `char* re_parse_partitions_alloc(const char* disk_path, int32* out_error)` | Aynı JSON, Rust'ın ayırdığı dizgi olarak; `re_free_string` ile serbest bırakılmalı. |
| `int32 re_read_sectors(const char* drive_path, uint64 start_lba, uint32 sector_count, uint8* out_buf, uint32 buf_len)` | Ham okuma (512 B/sektör). Dönüş: okunan bayt sayısı veya negatif hata. |
| `void re_free_string(char* ptr)` | Rust'ın ayırdığı `CString`'i serbest bırakır (NULL güvenli). |

### Hata kodları
`0` OK · `-1` NULL işaretçi · `-2` geçersiz UTF-8 · `-3` açılamadı · `-4` okunamadı · `-5` geçersiz JSON ·
`-6` arabellek küçük · `-7` bölüm tablosu yok · `-8` imza seçilmedi · `-9` iptal · `-10` erişim reddedildi · `-99` panik (yakalandı)

### `settings_json`
```json
{ "enabled": [1, 2, 3], "volume_size": 64023257088, "start_offset": 0, "max_results": 100000 }
```
Tüm alanlar isteğe bağlıdır; NULL/boş = tüm türler, boyut aygıttan okunur. Windows ham birimlerinde boyut
okunamayabildiği için C# tarafı `volume_size`'ı her zaman gönderir.

### İmza kimlikleri (`sig_id`)
| id | tür | boyut tespiti |
|---|---|---|
| 1 | JPEG `FF D8 FF` | segmentler SOS'a kadar atlanır, sonra `FF D9` |
| 2 | PNG `89 50 4E 47 0D 0A 1A 0A` | `IEND` + 4 bayt CRC |
| 3 | PDF `%PDF-` | ilk `%%EOF` |
| 4 | GIF `GIF87a`/`GIF89a` | `00 3B` |
| 5 / 10 / 11 / 12 | ZIP / DOCX / XLSX / PPTX `PK 03 04` | merkezi dizin sonu `PK 05 06` + yorum; içerikteki `[Content_Types].xml`, `word/`, `xl/`, `ppt/` ile sınıflandırma |
| 6 | BMP `BM` | başlıktaki dosya boyutu (DIB başlığı doğrulanır) |
| 7 | AVI `RIFF....AVI ` | RIFF boyutu + 8 |
| 8 | 7z `37 7A BC AF 27 1C` | 32 + sonraki başlık ofseti + boyutu |
| 9 | RAR `Rar!\x1A\x07` | RAR4/RAR5 arşiv sonu bloğu; yoksa tahmini |
| 13 | TIFF `II*\0` | tahmini (10 MB) |
| 14 | MP4/MOV `....ftyp` | üst düzey kutular |
| 15 | EXE `MZ` | PE bölüm tablosu |

`sig_id`'nin en üst biti (`0x80000000`) set ise boyut tahminidir. Boyutu doğrulanamayan yapısal türler
yanlış pozitif sayılır ve bildirilmez. Kesin boyutlu bir dosya bulununca tarama dosyanın sonuna atlar.

### Bölüm JSON'u
```json
{"style":"GPT","sector_size":512,"warnings":[],
 "partitions":[{"index":1,"scheme":"GPT","type_name":"EFI Sistem Bölümü","file_system":"FAT32",
                "name":"EFI system partition","start_lba":2048,"sector_count":204800,"bootable":false,
                "type_id":"C12A7328-F81F-11D2-BA4B-00A0C93EC93B"}]}
```
Dosya sistemi tespiti: NTFS, exFAT, FAT32, FAT16/FAT12, `Linux (ext)` (süper blok `0xEF53`), aksi halde `Bilinmiyor`.
Tür adları C# `PartitionService` ile aynı Türkçe metinlerdir.

## Derleme (çapraz derleme, Linux/WSL)

```bash
rustup target add x86_64-pc-windows-gnu
sudo apt-get install -y gcc-mingw-w64-x86-64
cd rust/recovery_engine
cargo test
cargo build --target x86_64-pc-windows-gnu --release
cp target/x86_64-pc-windows-gnu/release/recovery_engine.dll ../../native/x64/recovery_engine.dll
```
`.cargo/config.toml` bağlayıcıyı (`x86_64-w64-mingw32-gcc`) ve `+crt-static`'i ayarlar; DLL yalnızca
Windows sistem kitaplıklarına (KERNEL32, ntdll, msvcrt, bcryptprimitives) bağımlıdır, ek MinGW DLL'i gerekmez.

## Windows'ta DLL konumu
`DataRecoveryApp.csproj`, `native\x64\recovery_engine.dll` varsa onu çıktıya kopyalar. Çalışma zamanında
beklenen konum: **`<uygulama klasörü>\native\x64\recovery_engine.dll`**. `RustBridge` önce göreli
`DllImport` yolunu, ardından `AppContext.BaseDirectory\native\x64\` yolunu dener.

## Bilinen sınırlamalar
- Windows'ta çalıştırılmadı: DLL yükleme, P/Invoke marshaling, `\\.\E:` / `\\.\PhysicalDriveN` açma doğrulanmadı.
- `re_read_sectors` ve `re_parse_partitions` MBR için 512 baytlık sektör varsayar (GPT 512/4096 denenir;
  C# tarafı LBA'ları Windows'un bildirdiği sektör boyutuna dönüştürür).
- Kayıp bölüm araması C# tarafında kalır. Tarama iptali/ilerleme süreç genelinde tek bir küresel bayrakla
  yönetilir (aynı anda tek tarama varsayılır).
- Kazıma, C# yolundaki sınırlamaları paylaşır (parçalı dosyalar birleştirilemez, PDF ilk `%%EOF`).
