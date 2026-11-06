using System.Text;
using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;
using static DataRecoveryApp.Helpers.WinApiHelper;

namespace DataRecoveryApp.Services;

public class CarverService
{
    private readonly int _sectorSize = 4096;

    public async Task<List<RecoverableFile>> CarveAsync(string driveLetter, CarverSettings settings, 
        IProgress<int>? progress, CancellationToken ct)
    {
        return await Task.Run(() => Carve(driveLetter, settings, progress, ct), ct);
    }

    private List<RecoverableFile> Carve(string driveLetter, CarverSettings settings, IProgress<int>? progress, CancellationToken ct)
    {
        var results = new List<RecoverableFile>();
        try
        {
            using var h = OpenVolume(driveLetter);
            var vol = GetNtfsVolumeData(h);
            var sigs = settings.GetActiveSignatures();
            if (sigs.Count == 0) return results;

            long totalSectors = vol.NumberSectors;
            var buffer = new byte[_sectorSize];
            int foundCount = 0;

            for (long sector = 0; sector < totalSectors && !ct.IsCancellationRequested; sector++)
            {
                try
                {
                    var data = ReadSectors(h, sector * vol.BytesPerSector, _sectorSize);
                    
                    foreach (var (sig, ext) in sigs)
                    {
                        for (int offset = 0; offset <= data.Length - sig.Length; offset++)
                        {
                            if (data.AsSpan(offset, sig.Length).SequenceEqual(sig))
                            {
                                results.Add(new RecoverableFile
                                {
                                    FileName = $"carved_{ext}_{DateTime.Now.Ticks}",
                                    Extension = ext,
                                    Size = 0,
                                    FilePath = $"Sektör {sector} + {offset}",
                                    DeletedDate = null,
                                    FileCategory = FileTypeHelper.GetCategory(ext).ToString(),
                                    StartSector = sector,
                                    RecoverabilityPercent = 45,
                                    FileReferenceNumber = (ulong)(sector * 1000 + offset)
                                });
                                foundCount++;
                                break;
                            }
                        }
                    }
                }
                catch { /* sektör okunamazsa atla */ }

                if (sector % 100 == 0) progress?.Report((int)(sector * 100 / totalSectors));
            }
            progress?.Report(100);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Carving error: {ex.Message}");
        }
        return results;
    }
}
