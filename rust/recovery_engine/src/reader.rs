//! Ham aygıt / imaj dosyası okuyucu.
//!
//! Windows'ta `\\.\C:` (birim) ve `\\.\PhysicalDrive0` (fiziksel disk) yolları doğrudan
//! `std::fs::File` ile açılır (Rust'ın standart kütüphanesi `CreateFileW`'yi
//! FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE paylaşım moduyla çağırır).
//! Ham aygıtlar yalnızca sektör hizalı ofset/uzunluk kabul ettiğinden tüm okumalar
//! hizalanır; hizalanmış okuma başarısız olursa (ör. birim sonu) 512 bayt hizasıyla
//! tekrar denenir. Linux/macOS'ta aynı kod normal imaj dosyalarıyla çalışır (testler).

use std::fs::{File, OpenOptions};
use std::io::{self, Read, Seek, SeekFrom};

/// Varsayılan hizalama (4K sektörlü diskler dahil tüm aygıtlar için güvenli).
pub const DEFAULT_ALIGN: u64 = 4096;
/// Asgari hizalama (klasik 512 baytlık sektör).
pub const MIN_ALIGN: u64 = 512;

pub struct RawReader {
    file: File,
    align: u64,
}

impl RawReader {
    /// Aygıtı veya imaj dosyasını salt okunur açar.
    pub fn open(path: &str) -> io::Result<Self> {
        let file = OpenOptions::new().read(true).open(path)?;
        Ok(Self { file, align: DEFAULT_ALIGN })
    }

    /// Aygıt/dosya uzunluğu. Windows ham aygıtlarında `SeekFrom::End` desteklenmeyebilir;
    /// bu durumda 0 döner (çağıran taraf boyutu ayarlarla vermeli ya da okuma bitene kadar taramalı).
    pub fn len(&mut self) -> u64 {
        if let Ok(meta) = self.file.metadata() {
            if meta.is_file() && meta.len() > 0 {
                return meta.len();
            }
        }
        match self.file.seek(SeekFrom::End(0)) {
            Ok(n) => n,
            Err(_) => 0,
        }
    }

    /// `offset` konumundan `len` bayt okur. Aygıt sonunda kısa dönebilir; hiç okunamazsa boş vektör.
    pub fn read_at(&mut self, offset: u64, len: usize) -> io::Result<Vec<u8>> {
        if len == 0 {
            return Ok(Vec::new());
        }
        match self.read_aligned(offset, len, self.align) {
            Ok(v) => Ok(v),
            Err(e) if self.align > MIN_ALIGN => self.read_aligned(offset, len, MIN_ALIGN).map_err(|_| e),
            Err(e) => Err(e),
        }
    }

    fn read_aligned(&mut self, offset: u64, len: usize, align: u64) -> io::Result<Vec<u8>> {
        let start = offset - offset % align;
        let end = (offset + len as u64).div_ceil(align) * align;
        let total = (end - start) as usize;
        self.file.seek(SeekFrom::Start(start))?;

        let mut buf = vec![0u8; total];
        let mut filled = 0usize;
        while filled < total {
            match self.file.read(&mut buf[filled..]) {
                Ok(0) => break, // dosya/aygıt sonu
                Ok(n) => filled += n,
                Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
                Err(e) if filled > 0 => {
                    let _ = e; // kısmi okuma: elde edileni döndür
                    break;
                }
                Err(e) => return Err(e),
            }
        }
        let skip = (offset - start) as usize;
        if filled <= skip {
            return Ok(Vec::new());
        }
        let avail = (filled - skip).min(len);
        buf.truncate(skip + avail);
        if skip > 0 {
            buf.drain(..skip);
        }
        Ok(buf)
    }

    /// `start_lba`'dan itibaren `sector_count` sektörü (sektör boyutu `sector_size`) okur.
    pub fn read_sectors(&mut self, start_lba: u64, sector_count: u32, sector_size: u32) -> io::Result<Vec<u8>> {
        let offset = start_lba
            .checked_mul(sector_size as u64)
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "LBA taşması"))?;
        self.read_at(offset, sector_count as usize * sector_size as usize)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    #[test]
    fn unaligned_reads_return_exact_slice() {
        let dir = std::env::temp_dir().join(format!("re_reader_{}", std::process::id()));
        let data: Vec<u8> = (0..20_000u32).map(|i| (i % 251) as u8).collect();
        File::create(&dir).unwrap().write_all(&data).unwrap();

        let mut r = RawReader::open(dir.to_str().unwrap()).unwrap();
        assert_eq!(r.len(), 20_000);
        assert_eq!(r.read_at(5000, 300).unwrap(), &data[5000..5300]);
        assert_eq!(r.read_at(4095, 2).unwrap(), &data[4095..4097]);
        // Sonu aşan okuma kısa döner
        assert_eq!(r.read_at(19_990, 100).unwrap(), &data[19_990..]);
        assert!(r.read_at(30_000, 10).unwrap().is_empty());
        assert_eq!(r.read_sectors(2, 1, 512).unwrap(), &data[1024..1536]);
        std::fs::remove_file(dir).ok();
    }
}
