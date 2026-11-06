//! recovery_engine — veri kurtarma uygulamasının yerel (native) çekirdeği.
//!
//! C ABI ile dışa aktarılan fonksiyonlar C# tarafından `Services/RustBridge.cs` üzerinden
//! P/Invoke ile çağrılır. Tüm dizgiler UTF-8, NUL sonlandırmalıdır. Hiçbir panik C sınırını
//! geçmez: her dışa aktarılan fonksiyon `catch_unwind` ile sarılıdır ve `RE_ERR_PANIC` döner.

pub mod partition;
pub mod reader;
pub mod scanner;

use std::ffi::{c_char, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::Ordering;

use reader::RawReader;
use scanner::ScanSettings;

pub const RE_OK: i32 = 0;
pub const RE_ERR_NULL_PTR: i32 = -1;
pub const RE_ERR_INVALID_UTF8: i32 = -2;
pub const RE_ERR_OPEN_FAILED: i32 = -3;
pub const RE_ERR_READ_FAILED: i32 = -4;
pub const RE_ERR_INVALID_JSON: i32 = -5;
pub const RE_ERR_BUFFER_TOO_SMALL: i32 = -6;
pub const RE_ERR_NO_TABLE: i32 = -7;
pub const RE_ERR_NO_SIGNATURES: i32 = -8;
pub const RE_ERR_CANCELLED: i32 = -9;
pub const RE_ERR_ACCESS_DENIED: i32 = -10;
pub const RE_ERR_PANIC: i32 = -99;

/// Sektör boyutu (ham okuma için). 4K yerel diskler 512e emülasyonuyla da 512 bayt hizasında okunabilir.
const SECTOR_SIZE: u32 = 512;

static VERSION: &[u8] = b"1.0.0\0";

pub type ScanCallback = unsafe extern "C" fn(offset: u64, sig_id: u32, estimated_size: u64);

fn guard(f: impl FnOnce() -> i32) -> i32 {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or(RE_ERR_PANIC)
}

unsafe fn cstr<'a>(p: *const c_char) -> Result<&'a str, i32> {
    if p.is_null() {
        return Err(RE_ERR_NULL_PTR);
    }
    CStr::from_ptr(p).to_str().map_err(|_| RE_ERR_INVALID_UTF8)
}

fn open(path: &str) -> Result<RawReader, i32> {
    RawReader::open(path).map_err(|e| match e.kind() {
        std::io::ErrorKind::PermissionDenied => RE_ERR_ACCESS_DENIED,
        _ => RE_ERR_OPEN_FAILED,
    })
}

/// Kütüphane sürümü (statik, serbest bırakılmamalı).
#[no_mangle]
pub extern "C" fn re_version() -> *const c_char {
    VERSION.as_ptr() as *const c_char
}

/// Sektör sınırlarında imza taraması yapar; her bulguda `callback(offset, sig_id, size)` çağrılır.
/// `sig_id`'nin en üst biti (0x80000000) set ise boyut tahminidir.
/// `settings_json` NULL olabilir (tüm türler). Dönüş: 0 başarı, negatif hata kodu.
///
/// # Safety
/// `drive_path` geçerli bir UTF-8 C dizgisi olmalı; `settings_json` NULL veya geçerli C dizgisi olmalı.
/// Callback, C tarafında istisna fırlatmamalıdır.
#[no_mangle]
pub unsafe extern "C" fn re_scan_signatures(
    drive_path: *const c_char,
    settings_json: *const c_char,
    callback: Option<ScanCallback>,
) -> i32 {
    guard(|| {
        let path = match cstr(drive_path) {
            Ok(p) => p,
            Err(e) => return e,
        };
        let settings: ScanSettings = if settings_json.is_null() {
            ScanSettings::default()
        } else {
            match cstr(settings_json) {
                Ok(s) if s.trim().is_empty() => ScanSettings::default(),
                Ok(s) => match serde_json::from_str(s) {
                    Ok(v) => v,
                    Err(_) => return RE_ERR_INVALID_JSON,
                },
                Err(e) => return e,
            }
        };
        let mut reader = match open(path) {
            Ok(r) => r,
            Err(e) => return e,
        };
        scanner::CANCEL.store(false, Ordering::SeqCst);
        let res = scanner::scan(&mut reader, &settings, |h| {
            if let Some(cb) = callback {
                cb(h.offset, h.sig_id, h.size);
            }
        });
        match res {
            Ok(_) => RE_OK,
            Err(scanner::ScanError::NoSignatures) => RE_ERR_NO_SIGNATURES,
            Err(scanner::ScanError::Cancelled) => RE_ERR_CANCELLED,
        }
    })
}

/// Süren `re_scan_signatures` çağrısının en kısa sürede `RE_ERR_CANCELLED` ile dönmesini ister.
#[no_mangle]
pub extern "C" fn re_cancel_scan() {
    scanner::CANCEL.store(true, Ordering::SeqCst);
}

/// Son/süren taramanın ilerlemesi (binde, 0–1000). Birim boyutu bilinmiyorsa 0 kalır.
#[no_mangle]
pub extern "C" fn re_scan_progress() -> u32 {
    scanner::PROGRESS.load(Ordering::Relaxed)
}

fn partitions_json(disk_path: *const c_char) -> Result<String, i32> {
    let path = unsafe { cstr(disk_path) }?;
    let mut reader = open(path)?;
    let table = partition::parse(&mut reader).map_err(|e| match e {
        partition::PartError::Read => RE_ERR_READ_FAILED,
        partition::PartError::NoTable => RE_ERR_NO_TABLE,
    })?;
    serde_json::to_string(&table).map_err(|_| RE_ERR_PANIC)
}

/// Sektör 0'ı okur, MBR/GPT ayrıştırır ve bölüm listesini JSON olarak `out_json`'a yazar (NUL dahil).
/// Başarıda `*out_len` = JSON bayt sayısı (NUL hariç). Arabellek küçükse (veya `out_json` NULL ise)
/// `*out_len` = gereken arabellek boyutu (NUL dahil) ve `RE_ERR_BUFFER_TOO_SMALL` döner.
///
/// # Safety
/// `out_json` en az `buf_size` bayt yazılabilir olmalı; `out_len` geçerli bir işaretçi olmalı.
#[no_mangle]
pub unsafe extern "C" fn re_parse_partitions(
    disk_path: *const c_char,
    out_json: *mut c_char,
    out_len: *mut u32,
    buf_size: u32,
) -> i32 {
    guard(|| {
        if out_len.is_null() {
            return RE_ERR_NULL_PTR;
        }
        let json = match partitions_json(disk_path) {
            Ok(j) => j,
            Err(e) => return e,
        };
        let needed = json.len() + 1;
        if out_json.is_null() || needed > buf_size as usize {
            *out_len = needed as u32;
            return RE_ERR_BUFFER_TOO_SMALL;
        }
        std::ptr::copy_nonoverlapping(json.as_ptr(), out_json as *mut u8, json.len());
        *out_json.add(json.len()) = 0;
        *out_len = json.len() as u32;
        RE_OK
    })
}

/// `re_parse_partitions` ile aynı JSON'u Rust tarafında ayrılmış bir dizgi olarak döndürür.
/// Hata durumunda NULL döner ve (verildiyse) `*out_error` hata koduyla doldurulur.
/// Dönen işaretçi mutlaka `re_free_string` ile serbest bırakılmalıdır.
///
/// # Safety
/// `out_error` NULL veya geçerli bir işaretçi olmalı.
#[no_mangle]
pub unsafe extern "C" fn re_parse_partitions_alloc(disk_path: *const c_char, out_error: *mut i32) -> *mut c_char {
    let mut code = RE_OK;
    let ptr = catch_unwind(AssertUnwindSafe(|| match partitions_json(disk_path) {
        Ok(j) => CString::new(j).map(|c| c.into_raw()).unwrap_or(std::ptr::null_mut()),
        Err(e) => {
            code = e;
            std::ptr::null_mut()
        }
    }))
    .unwrap_or_else(|_| {
        code = RE_ERR_PANIC;
        std::ptr::null_mut()
    });
    if !out_error.is_null() {
        *out_error = code;
    }
    ptr
}

/// Ham sektör okur (512 bayt/sektör). Dönüş: okunan bayt sayısı (aygıt sonunda kısa olabilir) veya negatif hata.
///
/// # Safety
/// `out_buf` en az `buf_len` bayt yazılabilir olmalı.
#[no_mangle]
pub unsafe extern "C" fn re_read_sectors(
    drive_path: *const c_char,
    start_lba: u64,
    sector_count: u32,
    out_buf: *mut u8,
    buf_len: u32,
) -> i32 {
    guard(|| {
        if out_buf.is_null() {
            return RE_ERR_NULL_PTR;
        }
        let needed = sector_count as u64 * SECTOR_SIZE as u64;
        if needed > buf_len as u64 || needed > i32::MAX as u64 {
            return RE_ERR_BUFFER_TOO_SMALL;
        }
        let path = match cstr(drive_path) {
            Ok(p) => p,
            Err(e) => return e,
        };
        let mut reader = match open(path) {
            Ok(r) => r,
            Err(e) => return e,
        };
        match reader.read_sectors(start_lba, sector_count, SECTOR_SIZE) {
            Ok(data) => {
                std::ptr::copy_nonoverlapping(data.as_ptr(), out_buf, data.len());
                data.len() as i32
            }
            Err(_) => RE_ERR_READ_FAILED,
        }
    })
}

/// Rust tarafında ayrılmış (`re_parse_partitions_alloc`) dizgiyi serbest bırakır. NULL güvenlidir.
///
/// # Safety
/// `ptr` yalnızca bu kütüphanenin döndürdüğü ve henüz serbest bırakılmamış bir işaretçi olmalı.
#[no_mangle]
pub unsafe extern "C" fn re_free_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        drop(CString::from_raw(ptr));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use std::sync::Mutex;

    static LOCK: Mutex<()> = Mutex::new(());
    static HITS: Mutex<Vec<(u64, u32, u64)>> = Mutex::new(Vec::new());

    unsafe extern "C" fn cb(offset: u64, sig: u32, size: u64) {
        HITS.lock().unwrap().push((offset, sig, size));
    }

    fn temp(name: &str, data: &[u8]) -> CString {
        let p = std::env::temp_dir().join(format!("re_lib_{}_{}", name, std::process::id()));
        std::fs::File::create(&p).unwrap().write_all(data).unwrap();
        CString::new(p.to_str().unwrap()).unwrap()
    }

    #[test]
    fn version_is_static() {
        let v = unsafe { CStr::from_ptr(re_version()) };
        assert_eq!(v.to_str().unwrap(), "1.0.0");
    }

    #[test]
    fn ffi_scan_parse_read() {
        let _g = LOCK.lock().unwrap();
        let mut img = vec![0u8; 1024 * 1024];
        img[510] = 0x55;
        img[511] = 0xAA;
        img[446 + 4] = 0x07;
        img[446 + 8..446 + 12].copy_from_slice(&64u32.to_le_bytes());
        img[446 + 12..446 + 16].copy_from_slice(&1000u32.to_le_bytes());
        img[64 * 512 + 3..64 * 512 + 11].copy_from_slice(b"NTFS    ");
        let png: Vec<u8> = [&[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A][..], &[7; 80], b"IEND", &[0; 4]].concat();
        img[0x20000..0x20000 + png.len()].copy_from_slice(&png);
        let path = temp("ffi", &img);

        unsafe {
            HITS.lock().unwrap().clear();
            let settings = CString::new(r#"{"enabled":[2]}"#).unwrap();
            assert_eq!(re_scan_signatures(path.as_ptr(), settings.as_ptr(), Some(cb)), RE_OK);
            assert_eq!(*HITS.lock().unwrap(), vec![(0x20000, 2, png.len() as u64)]);

            let bad = CString::new("{bozuk").unwrap();
            assert_eq!(re_scan_signatures(path.as_ptr(), bad.as_ptr(), None), RE_ERR_INVALID_JSON);
            assert_eq!(re_scan_signatures(std::ptr::null(), std::ptr::null(), None), RE_ERR_NULL_PTR);

            // Boyut sorgusu → yeterli arabellekle tekrar
            let mut len = 0u32;
            assert_eq!(re_parse_partitions(path.as_ptr(), std::ptr::null_mut(), &mut len, 0), RE_ERR_BUFFER_TOO_SMALL);
            let mut buf = vec![0u8; len as usize];
            assert_eq!(re_parse_partitions(path.as_ptr(), buf.as_mut_ptr() as *mut c_char, &mut len, buf.len() as u32), RE_OK);
            let json = std::str::from_utf8(&buf[..len as usize]).unwrap();
            assert!(json.contains("\"file_system\":\"NTFS\""), "{json}");

            let mut err = 1;
            let p = re_parse_partitions_alloc(path.as_ptr(), &mut err);
            assert_eq!(err, RE_OK);
            assert_eq!(CStr::from_ptr(p).to_str().unwrap(), json);
            re_free_string(p);

            let mut sec = vec![0u8; 1024];
            assert_eq!(re_read_sectors(path.as_ptr(), 0, 2, sec.as_mut_ptr(), 1024), 1024);
            assert_eq!(&sec[510..512], &[0x55, 0xAA]);
            assert_eq!(re_read_sectors(path.as_ptr(), 0, 3, sec.as_mut_ptr(), 1024), RE_ERR_BUFFER_TOO_SMALL);

            let missing = CString::new("/yok/boyle/bir/dosya").unwrap();
            assert_eq!(re_read_sectors(missing.as_ptr(), 0, 1, sec.as_mut_ptr(), 1024), RE_ERR_OPEN_FAILED);
        }
        std::fs::remove_file(path.to_str().unwrap()).ok();
    }
}
