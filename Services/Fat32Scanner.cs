using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;
using static DataRecoveryApp.Helpers.WinApiHelper;

namespace DataRecoveryApp.Services;

public class Fat32Scanner
{
    public string? LastError { get; private set; }

    public Task<List<RecoverableFile>> ScanAsync(string driveLetter, IProgress<int>? progress, CancellationToken ct)
        => Task.Run(() => Scan(driveLetter, progress, ct), ct);

    private List<RecoverableFile> Scan(string driveLetter, IProgress<int>? progress, CancellationToken ct)
    {
        var results = new List<RecoverableFile>();
        LastError = null;
        try
        {
            using var h = OpenVolume(driveLetter);
            var vol = GetNtfsVolumeData(h);
            
            // FAT32 önyükleme sektörü oku (sektör 0)
            var bootSector = ReadSectors(h, 0, (int)vol.BytesPerSector);
            if (bootSector.Length < 90) { LastError = "FAT32 önyükleme sektörü geçersiz."; return results; }

            // FAT32 yapısını çözümle
            ushort bytesPerSector = BitConverter.ToUInt16(bootSector, 11);
            byte sectorsPerCluster = bootSector[13];
            ushort reservedSectors = BitConverter.ToUInt16(bootSector, 14);
            byte numFats = bootSector[16];
            uint sectorsPerFat = BitConverter.ToUInt32(bootSector, 36);
            uint rootCluster = BitConverter.ToUInt32(bootSector, 44);

            if (bytesPerSector != 512 && bytesPerSector != 4096)
            {
                LastError = "FAT32 sektör boyutu desteklenmiyor.";
                return results;
            }

            // Kök dizin başlangıç sektörü
            long fatStartSector = reservedSectors;
            long dataSector = reservedSectors + (numFats * sectorsPerFat);
            long clusterToSector(uint cluster) => dataSector + (cluster - 2) * sectorsPerCluster;

            // Kök dizini oku
            var dirSector = clusterToSector(rootCluster);
            int dirsPerSector = bytesPerSector / 32;
            int deletedCount = 0;

            for (int i = 0; i < 100 && !ct.IsCancellationRequested; i++)
            {
                try
                {
                    var dirData = ReadSectors(h, dirSector * bytesPerSector + i * 32, 32);
                    if (dirData.Length < 32 || dirData[0] == 0) break;

                    // 0xE5 = silinen giriş
                    if (dirData[0] == 0xE5)
                    {
                        string name = ExtractDosName(dirData);
                        uint startCluster = BitConverter.ToUInt32(dirData, 26);
                        uint size = BitConverter.ToUInt32(dirData, 28);
                        
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            string ext = FileTypeHelper.GetExtension(name);
                            results.Add(new RecoverableFile
                            {
                                FileName = name,
                                Extension = ext,
                                Size = size,
                                FilePath = $"Küme {startCluster}",
                                DeletedDate = null,
                                FileCategory = FileTypeHelper.GetCategory(ext).ToString(),
                                StartSector = startCluster,
                                RecoverabilityPercent = 60,
                                FileReferenceNumber = startCluster
                            });
                            deletedCount++;
                        }
                    }
                }
                catch { }
                
                progress?.Report((i * 100) / 100);
            }
            
            if (deletedCount == 0)
                LastError = $"FAT32'de silinmiş dosya bulunamadı ({deletedCount} tarandı).";
        }
        catch (UnauthorizedAccessException)
        {
            LastError = "Yönetici izni gerekiyor.";
        }
        catch (Exception ex)
        {
            LastError = $"FAT32 taraması başarısız: {ex.Message}";
        }
        progress?.Report(100);
        return results;
    }

    private static string ExtractDosName(byte[] dirEntry)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i < 8; i++)
        {
            byte b = dirEntry[i];
            if (b == 0x20 || b == 0) break;
            sb.Append((char)b);
        }
        if (dirEntry[8] != 0x20 && dirEntry[8] != 0)
        {
            sb.Append('.');
            for (int i = 8; i < 11; i++)
            {
                byte b = dirEntry[i];
                if (b == 0x20 || b == 0) break;
                sb.Append((char)b);
            }
        }
        return sb.ToString();
    }
}
