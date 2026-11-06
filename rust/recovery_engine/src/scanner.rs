//! İmza tabanlı veri kazıma (file carving).
//!
//! Birim/imaj 4 MB'lık bloklar halinde okunur; imzalar 512 baytlık sektör başlarında aranır.
//! Her eşleşmede dosya boyutu sonlandırıcıdan (footer) veya başlık alanlarından belirlenir,
//! callback çağrılır ve tarama dosyanın sonuna atlar. Boyutu doğrulanamayan "zorunlu" türler
//! (JPEG, PNG, PDF...) yanlış pozitif sayılıp atlanır; sonlandırıcısı olmayan türler (TIFF, bazı RAR)
//! tahmini boyutla bildirilir ve `sig_id`'nin en üst biti (`ESTIMATED_FLAG`) set edilir.

use crate::reader::RawReader;
use serde::Deserialize;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};

pub const BLOCK_SIZE: usize = 4 * 1024 * 1024;
pub const SECTOR: u64 = 512;
const CHUNK: usize = 1024 * 1024;
const MB: u64 = 1024 * 1024;

/// `sig_id`'de bu bit set ise boyut tahminidir (sonlandırıcı bulunamadı).
pub const ESTIMATED_FLAG: u32 = 0x8000_0000;

pub const SIG_JPG: u32 = 1;
pub const SIG_PNG: u32 = 2;
pub const SIG_PDF: u32 = 3;
pub const SIG_GIF: u32 = 4;
pub const SIG_ZIP: u32 = 5;
pub const SIG_BMP: u32 = 6;
pub const SIG_AVI: u32 = 7;
pub const SIG_7Z: u32 = 8;
pub const SIG_RAR: u32 = 9;
pub const SIG_DOCX: u32 = 10;
pub const SIG_XLSX: u32 = 11;
pub const SIG_PPTX: u32 = 12;
pub const SIG_TIF: u32 = 13;
pub const SIG_MP4: u32 = 14;
pub const SIG_EXE: u32 = 15;

/// Tarama iptali (C# tarafı `re_cancel_scan` ile set eder) ve ilerleme (binde).
pub static CANCEL: AtomicBool = AtomicBool::new(false);
pub static PROGRESS: AtomicU32 = AtomicU32::new(0);

pub struct Signature {
    pub id: u32,
    pub magic: &'static [u8],
    pub magic_offset: usize,
    pub max_size: u64,
}

pub static SIGNATURES: &[Signature] = &[
    Signature { id: SIG_JPG, magic: &[0xFF, 0xD8, 0xFF], magic_offset: 0, max_size: 50 * MB },
    Signature { id: SIG_PNG, magic: &[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], magic_offset: 0, max_size: 50 * MB },
    Signature { id: SIG_GIF, magic: b"GIF8", magic_offset: 0, max_size: 20 * MB },
    Signature { id: SIG_BMP, magic: b"BM", magic_offset: 0, max_size: 100 * MB },
    Signature { id: SIG_TIF, magic: &[0x49, 0x49, 0x2A, 0x00], magic_offset: 0, max_size: 10 * MB },
    Signature { id: SIG_PDF, magic: b"%PDF-", magic_offset: 0, max_size: 200 * MB },
    Signature { id: SIG_ZIP, magic: &[0x50, 0x4B, 0x03, 0x04], magic_offset: 0, max_size: 500 * MB },
    Signature { id: SIG_RAR, magic: b"Rar!\x1A\x07", magic_offset: 0, max_size: 10 * MB },
    Signature { id: SIG_7Z, magic: &[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], magic_offset: 0, max_size: 1024 * MB },
    Signature { id: SIG_AVI, magic: b"RIFF", magic_offset: 0, max_size: 4096 * MB },
    Signature { id: SIG_MP4, magic: b"ftyp", magic_offset: 4, max_size: 4096 * MB },
    Signature { id: SIG_EXE, magic: b"MZ", magic_offset: 0, max_size: 200 * MB },
];

/// `settings_json` içeriği. Tüm alanlar isteğe bağlıdır; boş/NULL ayar = tüm türler.
#[derive(Debug, Default, Deserialize)]
pub struct ScanSettings {
    /// Etkin imza kimlikleri (SIG_*). ZIP ailesi için 5/10/11/12'den biri yeterli.
    #[serde(default)]
    pub enabled: Option<Vec<u32>>,
    /// Birim boyutu (bayt). Windows ham birimlerinde boyut okunamayabileceği için C# tarafı verir.
    #[serde(default)]
    pub volume_size: Option<u64>,
    #[serde(default)]
    pub start_offset: Option<u64>,
    #[serde(default)]
    pub max_results: Option<u32>,
}

impl ScanSettings {
    pub fn is_enabled(&self, id: u32) -> bool {
        match &self.enabled {
            None => true,
            Some(v) => v.contains(&id),
        }
    }
    fn zip_family(&self) -> bool {
        [SIG_ZIP, SIG_DOCX, SIG_XLSX, SIG_PPTX].iter().any(|&i| self.is_enabled(i))
    }
}

#[derive(Debug, PartialEq)]
pub enum ScanError {
    NoSignatures,
    Cancelled,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Hit {
    pub offset: u64,
    pub sig_id: u32,
    pub size: u64,
}

/// Taramayı çalıştırır, her bulguda `on_hit` çağrılır. Bulunan dosya sayısını döndürür.
pub fn scan(reader: &mut RawReader, settings: &ScanSettings, mut on_hit: impl FnMut(Hit)) -> Result<u32, ScanError> {
    let sigs: Vec<&Signature> = SIGNATURES
        .iter()
        .filter(|s| if s.id == SIG_ZIP { settings.zip_family() } else { settings.is_enabled(s.id) })
        .collect();
    if sigs.is_empty() {
        return Err(ScanError::NoSignatures);
    }

    PROGRESS.store(0, Ordering::Relaxed);
    let total = settings.volume_size.filter(|&v| v > 0).unwrap_or_else(|| reader.len());
    let mut pos = settings.start_offset.unwrap_or(0) / SECTOR * SECTOR;
    let max_results = settings.max_results.unwrap_or(u32::MAX);

    let mut block: Vec<u8> = Vec::new();
    let mut block_start: u64 = 0;
    let mut have_block = false;
    let mut found: u32 = 0;
    let mut counter: u64 = 0;

    while total == 0 || pos < total {
        if CANCEL.load(Ordering::Relaxed) {
            return Err(ScanError::Cancelled);
        }
        if !have_block || pos < block_start || pos + 16 > block_start + block.len() as u64 {
            let len = if total > 0 { (total - pos).min(BLOCK_SIZE as u64) as usize } else { BLOCK_SIZE };
            block = reader.read_at(pos, len).unwrap_or_default();
            block_start = pos;
            have_block = block.len() >= 16;
            if !have_block {
                if total == 0 {
                    break; // boyut bilinmiyor ve okuma bitti → aygıt sonu
                }
                pos += (len as u64).max(SECTOR); // okunamayan bölgeyi atla
                continue;
            }
        }

        let rel = (pos - block_start) as usize;
        let mut advance = SECTOR;
        for sig in &sigs {
            if !matches(&block, rel, sig) {
                continue;
            }
            let max = if total > 0 { sig.max_size.min(total - pos) } else { sig.max_size };
            if let Some(hit) = inspect(reader, pos, max, sig, settings) {
                on_hit(hit);
                found += 1;
                if hit.sig_id & ESTIMATED_FLAG == 0 {
                    advance = hit.size.div_ceil(SECTOR).max(1) * SECTOR; // dosyanın sonuna atla
                }
                break;
            }
        }
        if found >= max_results {
            break;
        }
        pos += advance;
        counter += 1;
        if total > 0 && (counter % 2048 == 0 || advance > SECTOR) {
            PROGRESS.store(((pos.min(total) as u128 * 1000) / total as u128) as u32, Ordering::Relaxed);
        }
    }
    PROGRESS.store(1000, Ordering::Relaxed);
    Ok(found)
}

fn matches(block: &[u8], rel: usize, sig: &Signature) -> bool {
    let start = rel + sig.magic_offset;
    start + sig.magic.len() <= block.len() && &block[start..start + sig.magic.len()] == sig.magic
}

fn u16le(b: &[u8], o: usize) -> u64 {
    u16::from_le_bytes([b[o], b[o + 1]]) as u64
}
fn u32le(b: &[u8], o: usize) -> u64 {
    u32::from_le_bytes(b[o..o + 4].try_into().unwrap()) as u64
}
fn u64le(b: &[u8], o: usize) -> u64 {
    u64::from_le_bytes(b[o..o + 8].try_into().unwrap())
}
fn u32be(b: &[u8], o: usize) -> u64 {
    u32::from_be_bytes(b[o..o + 4].try_into().unwrap()) as u64
}

fn find(hay: &[u8], needle: &[u8]) -> Option<usize> {
    if needle.is_empty() || hay.len() < needle.len() {
        return None;
    }
    hay.windows(needle.len()).position(|w| w == needle)
}

/// Başlık eşleşmesini doğrular ve boyutu belirler. Geçersizse `None`.
fn inspect(r: &mut RawReader, offset: u64, max: u64, sig: &Signature, settings: &ScanSettings) -> Option<Hit> {
    let head = r.read_at(offset, 4096).ok()?;
    if head.len() < 64 {
        return None;
    }
    let mut id = sig.id;
    let size: u64 = match sig.id {
        SIG_JPG => jpeg_size(r, offset, max),
        SIG_PNG => find_footer(r, offset, 0, max, &[b"IEND"]).map(|(p, _)| p + 8).unwrap_or(0), // IEND + CRC
        SIG_GIF => {
            if head[4] != b'7' && head[4] != b'9' {
                return None;
            }
            find_footer(r, offset, 6, max, &[&[0x00, 0x3B]]).map(|(p, _)| p + 2).unwrap_or(0)
        }
        SIG_PDF => find_footer(r, offset, 0, max, &[b"%%EOF"]).map(|(p, _)| p + 5).unwrap_or(0),
        SIG_BMP => {
            let s = u32le(&head, 2);
            let data_off = u32le(&head, 10);
            let dib = u32le(&head, 14);
            if s < 64 || s > max || data_off >= s || ![12, 40, 52, 56, 108, 124].contains(&dib) {
                return None;
            }
            s
        }
        SIG_ZIP => {
            let (s, kind) = zip_size_and_kind(r, offset, max);
            if s > 0 {
                if !settings.is_enabled(kind) {
                    return None;
                }
                id = kind;
            }
            s
        }
        SIG_7Z => {
            let next_off = u64le(&head, 12);
            let next_size = u64le(&head, 20);
            let s = 32u64.checked_add(next_off)?.checked_add(next_size)?;
            if next_size == 0 || s > max {
                return None;
            }
            s
        }
        SIG_AVI => {
            if &head[8..12] != b"AVI " {
                return None;
            }
            let s = u32le(&head, 4) + 8;
            if s < 64 || s > max {
                return None;
            }
            s
        }
        SIG_RAR => {
            const RAR4_END: &[u8] = &[0xC4, 0x3D, 0x7B, 0x00, 0x40, 0x07, 0x00];
            const RAR5_END: &[u8] = &[0x1D, 0x77, 0x56, 0x51, 0x03, 0x05, 0x04, 0x00];
            find_footer(r, offset, 7, max, &[RAR4_END, RAR5_END]).map(|(p, k)| p + [7, 8][k]).unwrap_or(0)
        }
        SIG_MP4 => mp4_size(r, offset, max),
        SIG_EXE => pe_size(&head, max),
        _ => 0, // TIFF: sonlandırıcı yok
    };
    let size = size.min(max);

    if size == 0 {
        // Boyutu doğrulanamayan yapısal türler yanlış pozitif kabul edilir.
        if !matches!(sig.id, SIG_TIF | SIG_RAR) {
            return None;
        }
        return Some(Hit { offset, sig_id: sig.id | ESTIMATED_FLAG, size: sig.max_size.min(max) });
    }
    Some(Hit { offset, sig_id: id, size })
}

/// `offset + skip`'ten başlayarak verilen sonlandırıcılardan ilkini arar.
/// Dönüş: (dosya başına göre konum, eşleşen desen indeksi).
fn find_footer(r: &mut RawReader, offset: u64, skip: u64, max: u64, pats: &[&[u8]]) -> Option<(u64, usize)> {
    let overlap = pats.iter().map(|p| p.len()).max().unwrap_or(1) - 1;
    let mut done = skip;
    while done < max {
        if CANCEL.load(Ordering::Relaxed) {
            return None;
        }
        let want = (max - done).min(CHUNK as u64) as usize;
        let chunk = r.read_at(offset + done, want).ok()?;
        let best = pats
            .iter()
            .enumerate()
            .filter_map(|(k, p)| find(&chunk, p).map(|i| (i, k)))
            .min_by_key(|&(i, _)| i);
        if let Some((i, k)) = best {
            return Some((done + i as u64, k));
        }
        if chunk.len() < want || chunk.len() <= overlap {
            return None;
        }
        done += (chunk.len() - overlap) as u64;
    }
    None
}

/// JPEG: segmentleri SOS'a kadar atla (EXIF küçük resmi APP1 içinde kalır), sonra FFD9 ara.
fn jpeg_size(r: &mut RawReader, offset: u64, max: u64) -> u64 {
    let head = match r.read_at(offset, max.min(256 * 1024) as usize) {
        Ok(h) => h,
        Err(_) => return 0,
    };
    let mut p = 2usize;
    let mut sos = false;
    while p + 4 < head.len() {
        if head[p] != 0xFF {
            return 0;
        }
        let marker = head[p + 1];
        if marker == 0xFF {
            p += 1;
            continue;
        }
        if marker == 0xDA {
            sos = true;
            break;
        }
        let seg = ((head[p + 2] as usize) << 8) | head[p + 3] as usize;
        if seg < 2 {
            return 0;
        }
        p += 2 + seg;
    }
    if !sos {
        return 0;
    }
    find_footer(r, offset, p as u64, max, &[&[0xFF, 0xD9]]).map(|(i, _)| i + 2).unwrap_or(0)
}

/// ZIP: merkezi dizin sonu (PK\x05\x06) ile boyut; içerikteki yollarla DOCX/XLSX/PPTX ayrımı.
fn zip_size_and_kind(r: &mut RawReader, offset: u64, max: u64) -> (u64, u32) {
    const EOCD: &[u8] = &[0x50, 0x4B, 0x05, 0x06];
    let (mut ct, mut word, mut xl, mut ppt) = (false, false, false, false);
    let mut done = 0u64;
    while done < max {
        if CANCEL.load(Ordering::Relaxed) {
            break;
        }
        let want = (max - done).min(CHUNK as u64) as usize;
        let chunk = match r.read_at(offset + done, want) {
            Ok(c) if c.len() >= 22 => c,
            _ => break,
        };
        ct |= find(&chunk, b"[Content_Types].xml").is_some();
        word |= find(&chunk, b"word/").is_some();
        xl |= find(&chunk, b"xl/").is_some();
        ppt |= find(&chunk, b"ppt/").is_some();
        if let Some(i) = find(&chunk, EOCD) {
            if i + 22 <= chunk.len() {
                let comment = u16le(&chunk, i + 20);
                let kind = if !ct {
                    SIG_ZIP
                } else if word {
                    SIG_DOCX
                } else if xl {
                    SIG_XLSX
                } else if ppt {
                    SIG_PPTX
                } else {
                    SIG_ZIP
                };
                return ((done + i as u64 + 22 + comment).min(max), kind);
            }
        }
        if chunk.len() < want {
            break;
        }
        done += (chunk.len() - 64) as u64;
    }
    (0, SIG_ZIP)
}

/// MP4/MOV: üst düzey kutuları (box) gezerek boyutu hesaplar.
fn mp4_size(r: &mut RawReader, offset: u64, max: u64) -> u64 {
    const KNOWN: &[&[u8; 4]] = &[
        b"ftyp", b"moov", b"mdat", b"free", b"skip", b"wide", b"uuid", b"moof", b"mfra", b"meta", b"pdin", b"styp",
        b"sidx", b"pnot",
    ];
    let mut pos = 0u64;
    for _ in 0..10_000 {
        if pos >= max {
            break;
        }
        let hdr = match r.read_at(offset + pos, 16) {
            Ok(h) if h.len() >= 8 => h,
            _ => break,
        };
        if !KNOWN.iter().any(|k| &hdr[4..8] == *k) {
            break;
        }
        let mut size = u32be(&hdr, 0);
        if size == 1 && hdr.len() >= 16 {
            size = u64::from_be_bytes(hdr[8..16].try_into().unwrap());
        }
        if size < 8 {
            break;
        }
        pos = pos.saturating_add(size);
    }
    if pos >= 16 && pos <= max { pos } else { 0 }
}

/// PE (EXE/DLL): bölüm tablosundan dosya sonu = max(PointerToRawData + SizeOfRawData).
fn pe_size(head: &[u8], max: u64) -> u64 {
    let pe = u32le(head, 0x3C) as usize;
    if pe == 0 || pe + 24 > head.len() || &head[pe..pe + 4] != b"PE\0\0" {
        return 0;
    }
    let sections = u16le(head, pe + 6) as usize;
    let opt = u16le(head, pe + 20) as usize;
    let table = pe + 24 + opt;
    let mut end = 0u64;
    for i in 0..sections {
        let s = table + i * 40;
        if s + 40 > head.len() {
            break;
        }
        end = end.max(u32le(head, s + 20) + u32le(head, s + 16));
    }
    if end > 0 && end <= max { end } else { 0 }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    fn image() -> (Vec<u8>, Vec<(u64, u32, u64)>) {
        let mut img = vec![0u8; 5 * 1024 * 1024];
        let mut expect = Vec::new();

        // JPEG @ 0x2000: SOI, APP0 (16 bayt), SOS, veri, EOI
        let o = 0x2000;
        let jpg: Vec<u8> = [
            &[0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10][..],
            &[0u8; 14],
            &[0xFF, 0xDA, 0x00, 0x08],
            &[0x11; 200],
            &[0xFF, 0xD9],
        ]
        .concat();
        img[o..o + jpg.len()].copy_from_slice(&jpg);
        expect.push((o as u64, SIG_JPG, jpg.len() as u64));

        // PNG @ 0x10000
        let o = 0x10000;
        let png: Vec<u8> = [&[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A][..], &[0x22; 100], b"IEND", &[1, 2, 3, 4]].concat();
        img[o..o + png.len()].copy_from_slice(&png);
        expect.push((o as u64, SIG_PNG, png.len() as u64));

        // PDF @ 0x20000
        let o = 0x20000;
        let pdf: Vec<u8> = [&b"%PDF-1.4\n"[..], &[b'x'; 300], b"%%EOF"].concat();
        img[o..o + pdf.len()].copy_from_slice(&pdf);
        expect.push((o as u64, SIG_PDF, pdf.len() as u64));

        // BMP @ 0x30000 (boyut 1000)
        let o = 0x30000;
        img[o..o + 2].copy_from_slice(b"BM");
        img[o + 2..o + 6].copy_from_slice(&1000u32.to_le_bytes());
        img[o + 10..o + 14].copy_from_slice(&54u32.to_le_bytes());
        img[o + 14..o + 18].copy_from_slice(&40u32.to_le_bytes());
        expect.push((o as u64, SIG_BMP, 1000));

        // DOCX (ZIP) @ 0x40000
        let o = 0x40000;
        let zip: Vec<u8> =
            [&[0x50, 0x4B, 0x03, 0x04][..], b"[Content_Types].xml", &[0; 50], b"word/document.xml", &[0; 50], &[0x50, 0x4B, 0x05, 0x06], &[0; 18]]
                .concat();
        img[o..o + zip.len()].copy_from_slice(&zip);
        expect.push((o as u64, SIG_DOCX, zip.len() as u64));

        // AVI @ 0x50000 (RIFF boyutu 4000 → 4008)
        let o = 0x50000;
        img[o..o + 4].copy_from_slice(b"RIFF");
        img[o + 4..o + 8].copy_from_slice(&4000u32.to_le_bytes());
        img[o + 8..o + 12].copy_from_slice(b"AVI ");
        expect.push((o as u64, SIG_AVI, 4008));

        // Yanlış pozitif: SOS'suz JPEG başlığı @ 0x60000 → bildirilmemeli
        img[0x60000..0x60004].copy_from_slice(&[0xFF, 0xD8, 0xFF, 0x00]);

        // 4 MB blok sınırına taşan GIF @ 0x3FFE00 (sonlandırıcı ikinci blokta)
        let o = 0x3FFE00;
        let gif: Vec<u8> = [&b"GIF89a"[..], &[0x33; 400], &[0x00, 0x3B]].concat();
        img[o..o + gif.len()].copy_from_slice(&gif);
        expect.push((o as u64, SIG_GIF, gif.len() as u64));
        (img, expect)
    }

    #[test]
    fn finds_all_signatures_with_sizes() {
        let (img, expect) = image();
        let p = std::env::temp_dir().join(format!("re_scan_{}", std::process::id()));
        std::fs::File::create(&p).unwrap().write_all(&img).unwrap();
        let mut r = RawReader::open(p.to_str().unwrap()).unwrap();

        let mut hits = Vec::new();
        let n = scan(&mut r, &ScanSettings::default(), |h| hits.push((h.offset, h.sig_id, h.size))).unwrap();
        assert_eq!(n as usize, hits.len());
        assert_eq!(hits, expect);

        // Yalnızca PNG etkin
        let s: ScanSettings = serde_json::from_str(r#"{"enabled":[2]}"#).unwrap();
        let mut only = Vec::new();
        scan(&mut r, &s, |h| only.push(h.sig_id)).unwrap();
        assert_eq!(only, vec![SIG_PNG]);

        // ZIP ailesinde yalnız ZIP etkinse DOCX bildirilmez
        let s: ScanSettings = serde_json::from_str(r#"{"enabled":[5]}"#).unwrap();
        let mut z = Vec::new();
        scan(&mut r, &s, |h| z.push(h.sig_id)).unwrap();
        assert!(z.is_empty());

        assert_eq!(scan(&mut r, &serde_json::from_str(r#"{"enabled":[]}"#).unwrap(), |_| {}), Err(ScanError::NoSignatures));
        std::fs::remove_file(p).ok();
    }
}
