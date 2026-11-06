//! MBR (+EBR zinciri) ve GPT bölüm tablosu ayrıştırıcı. Sonuç serde_json ile JSON'a çevrilir.
//! Tür adları C# tarafındaki `PartitionService` ile aynı Türkçe metinleri kullanır.

use crate::reader::RawReader;
use serde::Serialize;

#[derive(Debug, Serialize, Clone, PartialEq)]
pub struct Partition {
    pub index: u32,
    /// "MBR", "EBR" veya "GPT"
    pub scheme: String,
    pub type_name: String,
    pub file_system: String,
    pub name: String,
    pub start_lba: u64,
    pub sector_count: u64,
    pub bootable: bool,
    /// GPT için tür GUID'i, MBR için "0x07" gibi tür baytı
    pub type_id: String,
}

#[derive(Debug, Serialize)]
pub struct PartitionTable {
    /// "GPT", "MBR" veya "Yok"
    pub style: String,
    pub sector_size: u32,
    pub partitions: Vec<Partition>,
    pub warnings: Vec<String>,
}

#[derive(Debug, PartialEq)]
pub enum PartError {
    Read,
    NoTable,
}

fn u16le(b: &[u8], o: usize) -> u16 {
    u16::from_le_bytes([b[o], b[o + 1]])
}
fn u32le(b: &[u8], o: usize) -> u32 {
    u32::from_le_bytes(b[o..o + 4].try_into().unwrap())
}
fn u64le(b: &[u8], o: usize) -> u64 {
    u64::from_le_bytes(b[o..o + 8].try_into().unwrap())
}

/// Diski/imajı ayrıştırır. Sektör 0'da 55 AA yoksa `PartError::NoTable` döner.
pub fn parse(reader: &mut RawReader) -> Result<PartitionTable, PartError> {
    let mbr = reader.read_at(0, 512).map_err(|_| PartError::Read)?;
    if mbr.len() < 512 {
        return Err(PartError::Read);
    }
    if mbr[510] != 0x55 || mbr[511] != 0xAA {
        return Err(PartError::NoTable);
    }

    let mut table = PartitionTable { style: "MBR".into(), sector_size: 512, partitions: Vec::new(), warnings: Vec::new() };
    let protective = (0..4).any(|i| mbr[446 + i * 16 + 4] == 0xEE);
    if protective {
        // GPT başlığı LBA 1'dedir; 4K yerel sektörlü disklerde ofset 4096 olur.
        for ss in [512u32, 4096] {
            if parse_gpt(reader, ss, &mut table) {
                table.style = "GPT".into();
                table.sector_size = ss;
                break;
            }
        }
        if table.style != "GPT" {
            table.warnings.push("Koruyucu MBR var fakat GPT başlığı okunamadı; MBR olarak gösteriliyor.".into());
        }
    }
    if table.style == "MBR" {
        parse_mbr(reader, &mbr, &mut table);
    }

    let ss = table.sector_size as u64;
    for p in table.partitions.iter_mut() {
        p.file_system = detect_fs(reader, p.start_lba * ss);
    }
    Ok(table)
}

fn parse_gpt(reader: &mut RawReader, ss: u32, t: &mut PartitionTable) -> bool {
    let hdr = match reader.read_at(ss as u64, ss as usize) {
        Ok(h) if h.len() >= 92 => h,
        _ => return false,
    };
    if &hdr[0..8] != b"EFI PART" {
        return false;
    }
    let entries_lba = u64le(&hdr, 72);
    let count = u32le(&hdr, 80);
    let esize = u32le(&hdr, 84);
    if !(128..=4096).contains(&esize) || count == 0 || count > 1024 {
        return false;
    }
    let entries = match reader.read_at(entries_lba * ss as u64, (count * esize) as usize) {
        Ok(e) => e,
        Err(_) => return false,
    };
    for i in 0..count as usize {
        let o = i * esize as usize;
        if o + 128 > entries.len() {
            break;
        }
        let e = &entries[o..o + 128];
        if e[0..16].iter().all(|&b| b == 0) {
            continue;
        }
        let guid = guid_string(&e[0..16]);
        let first = u64le(e, 32);
        let last = u64le(e, 40);
        let attrs = u64le(e, 48);
        let name_u16: Vec<u16> = (0..36).map(|k| u16le(e, 56 + k * 2)).take_while(|&c| c != 0).collect();
        t.partitions.push(Partition {
            index: t.partitions.len() as u32 + 1,
            scheme: "GPT".into(),
            type_name: gpt_type_name(&guid),
            file_system: String::new(),
            name: String::from_utf16_lossy(&name_u16),
            start_lba: first,
            sector_count: last.saturating_sub(first) + 1,
            bootable: attrs & 0x4 != 0, // "legacy BIOS bootable" özniteliği
            type_id: guid,
        });
    }
    true
}

fn parse_mbr(reader: &mut RawReader, mbr: &[u8], t: &mut PartitionTable) {
    for i in 0..4 {
        let o = 446 + i * 16;
        let typ = mbr[o + 4];
        let start = u32le(mbr, o + 8) as u64;
        let count = u32le(mbr, o + 12) as u64;
        if typ == 0 || count == 0 {
            continue;
        }
        if matches!(typ, 0x05 | 0x0F | 0x85) {
            parse_extended(reader, start, t);
            continue;
        }
        let bootable = mbr[o] & 0x80 != 0;
        t.partitions.push(Partition {
            index: t.partitions.len() as u32 + 1,
            scheme: "MBR".into(),
            type_name: mbr_type_name(typ),
            file_system: String::new(),
            name: if bootable { "Etkin (önyüklenebilir)".into() } else { String::new() },
            start_lba: start,
            sector_count: count,
            bootable,
            type_id: format!("0x{typ:02X}"),
        });
    }
}

/// Genişletilmiş bölümdeki EBR zincirini izler (en fazla 128 mantıksal bölüm, döngü korumalı).
fn parse_extended(reader: &mut RawReader, ext_start: u64, t: &mut PartitionTable) {
    let mut ebr_lba = ext_start;
    let mut seen = std::collections::HashSet::new();
    for _ in 0..128 {
        if !seen.insert(ebr_lba) {
            break;
        }
        let ebr = match reader.read_at(ebr_lba * 512, 512) {
            Ok(b) if b.len() == 512 && b[510] == 0x55 && b[511] == 0xAA => b,
            _ => break,
        };
        let typ = ebr[446 + 4];
        let rel = u32le(&ebr, 446 + 8) as u64;
        let count = u32le(&ebr, 446 + 12) as u64;
        if typ != 0 && count != 0 {
            t.partitions.push(Partition {
                index: t.partitions.len() as u32 + 1,
                scheme: "EBR".into(),
                type_name: format!("{} (mantıksal)", mbr_type_name(typ)),
                file_system: String::new(),
                name: String::new(),
                start_lba: ebr_lba + rel,
                sector_count: count,
                bootable: false,
                type_id: format!("0x{typ:02X}"),
            });
        }
        let next_rel = u32le(&ebr, 462 + 8) as u64;
        if ebr[462 + 4] == 0 || next_rel == 0 {
            break;
        }
        ebr_lba = ext_start + next_rel;
    }
}

/// Bölümün ilk sektör(ler)ine bakarak dosya sistemini tespit eder.
pub fn detect_fs(reader: &mut RawReader, offset: u64) -> String {
    let b = match reader.read_at(offset, 2048) {
        Ok(b) if b.len() >= 512 => b,
        Ok(_) => return "-".into(),
        Err(_) => return "Okunamadı".into(),
    };
    if &b[3..11] == b"NTFS    " {
        return "NTFS".into();
    }
    if &b[3..11] == b"EXFAT   " {
        return "exFAT".into();
    }
    if &b[82..90] == b"FAT32   " {
        return "FAT32".into();
    }
    if &b[54..59] == b"FAT16" || &b[54..59] == b"FAT12" {
        return String::from_utf8_lossy(&b[54..59]).into_owned();
    }
    // ext2/3/4 süper bloğu: bölüm başından 1024 bayt sonra, sihirli sayı 0xEF53 (ofset 56)
    if b.len() >= 1024 + 58 && u16le(&b, 1024 + 56) == 0xEF53 {
        return "Linux (ext)".into();
    }
    "Bilinmiyor".into()
}

/// GPT GUID'i (karışık endian) metne çevirir: ilk üç alan küçük-endian.
pub fn guid_string(g: &[u8]) -> String {
    format!(
        "{:08X}-{:04X}-{:04X}-{:02X}{:02X}-{:02X}{:02X}{:02X}{:02X}{:02X}{:02X}",
        u32le(g, 0),
        u16le(g, 4),
        u16le(g, 6),
        g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]
    )
}

pub fn mbr_type_name(t: u8) -> String {
    match t {
        0x01 => "FAT12",
        0x04 | 0x06 | 0x0E => "FAT16",
        0x07 => "NTFS / exFAT",
        0x0B | 0x0C => "FAT32",
        0x11 | 0x14 | 0x16 | 0x17 | 0x1B | 0x1C => "Gizli Windows bölümü",
        0x27 => "Windows Kurtarma",
        0x82 => "Linux takas (swap)",
        0x83 => "Linux",
        0x8E => "Linux LVM",
        0xA5 | 0xA6 | 0xA9 => "BSD",
        0xAF => "macOS HFS+",
        0xEE => "GPT koruyucu",
        0xEF => "EFI Sistem",
        _ => return format!("Tür 0x{t:02X}"),
    }
    .into()
}

pub fn gpt_type_name(guid: &str) -> String {
    match guid {
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
        _ => return format!("GUID {guid}"),
    }
    .into()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    fn temp(name: &str, data: &[u8]) -> String {
        let p = std::env::temp_dir().join(format!("re_part_{}_{}", name, std::process::id()));
        std::fs::File::create(&p).unwrap().write_all(data).unwrap();
        p.to_str().unwrap().to_string()
    }

    #[test]
    fn mbr_with_ntfs_and_fat32() {
        let mut img = vec![0u8; 4 * 1024 * 1024];
        img[510] = 0x55;
        img[511] = 0xAA;
        // Bölüm 1: NTFS, LBA 2048, 2048 sektör, etkin
        let e1 = 446;
        img[e1] = 0x80;
        img[e1 + 4] = 0x07;
        img[e1 + 8..e1 + 12].copy_from_slice(&2048u32.to_le_bytes());
        img[e1 + 12..e1 + 16].copy_from_slice(&2048u32.to_le_bytes());
        // Bölüm 2: FAT32, LBA 4096
        let e2 = 462;
        img[e2 + 4] = 0x0C;
        img[e2 + 8..e2 + 12].copy_from_slice(&4096u32.to_le_bytes());
        img[e2 + 12..e2 + 16].copy_from_slice(&2048u32.to_le_bytes());
        img[2048 * 512 + 3..2048 * 512 + 11].copy_from_slice(b"NTFS    ");
        img[4096 * 512 + 82..4096 * 512 + 90].copy_from_slice(b"FAT32   ");

        let path = temp("mbr", &img);
        let mut r = RawReader::open(&path).unwrap();
        let t = parse(&mut r).unwrap();
        assert_eq!(t.style, "MBR");
        assert_eq!(t.partitions.len(), 2);
        assert_eq!(t.partitions[0].file_system, "NTFS");
        assert!(t.partitions[0].bootable);
        assert_eq!(t.partitions[1].file_system, "FAT32");
        assert_eq!(t.partitions[1].type_name, "FAT32");
        let json = serde_json::to_string(&t).unwrap();
        assert!(json.contains("\"style\":\"MBR\""));
        std::fs::remove_file(path).ok();
    }

    #[test]
    fn gpt_with_efi_partition() {
        let mut img = vec![0u8; 2 * 1024 * 1024];
        img[510] = 0x55;
        img[511] = 0xAA;
        img[446 + 4] = 0xEE;
        let h = 512;
        img[h..h + 8].copy_from_slice(b"EFI PART");
        img[h + 72..h + 80].copy_from_slice(&2u64.to_le_bytes());
        img[h + 80..h + 84].copy_from_slice(&128u32.to_le_bytes());
        img[h + 84..h + 88].copy_from_slice(&128u32.to_le_bytes());
        let e = 1024;
        // EFI GUID C12A7328-F81F-11D2-BA4B-00A0C93EC93B (karışık endian)
        let guid = [0x28, 0x73, 0x2A, 0xC1, 0x1F, 0xF8, 0xD2, 0x11, 0xBA, 0x4B, 0x00, 0xA0, 0xC9, 0x3E, 0xC9, 0x3B];
        img[e..e + 16].copy_from_slice(&guid);
        img[e + 32..e + 40].copy_from_slice(&2048u64.to_le_bytes());
        img[e + 40..e + 48].copy_from_slice(&3071u64.to_le_bytes());
        for (k, c) in "EFI".encode_utf16().enumerate() {
            img[e + 56 + k * 2..e + 58 + k * 2].copy_from_slice(&c.to_le_bytes());
        }
        img[2048 * 512 + 82..2048 * 512 + 90].copy_from_slice(b"FAT32   ");

        let path = temp("gpt", &img);
        let mut r = RawReader::open(&path).unwrap();
        let t = parse(&mut r).unwrap();
        assert_eq!(t.style, "GPT");
        assert_eq!(t.partitions.len(), 1);
        let p = &t.partitions[0];
        assert_eq!(p.type_name, "EFI Sistem Bölümü");
        assert_eq!(p.name, "EFI");
        assert_eq!(p.sector_count, 1024);
        assert_eq!(p.file_system, "FAT32");
        std::fs::remove_file(path).ok();
    }

    #[test]
    fn no_signature_is_error() {
        let path = temp("none", &vec![0u8; 4096]);
        let mut r = RawReader::open(&path).unwrap();
        assert_eq!(parse(&mut r).unwrap_err(), PartError::NoTable);
        std::fs::remove_file(path).ok();
    }
}
