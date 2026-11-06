using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;
using static DataRecoveryApp.Helpers.WinApiHelper;

namespace DataRecoveryApp.Services;

public class DiskImageService
{
    public async Task<bool> CreateImageAsync(string driveLetter, string outputPath, 
        IProgress<DiskImageProgress>? progress, CancellationToken ct)
    {
        return await Task.Run(() => CreateImage(driveLetter, outputPath, progress, ct), ct);
    }

    private bool CreateImage(string driveLetter, string outputPath, IProgress<DiskImageProgress>? progress, CancellationToken ct)
    {
        try
        {
            using var h = OpenVolume(driveLetter);
            var vol = GetNtfsVolumeData(h);
            long totalBytes = vol.NumberSectors * vol.BytesPerSector;
            
            using var fs = File.Create(outputPath);
            const int bufSize = 1024 * 1024;
            long readBytes = 0;

            for (long offset = 0; offset < totalBytes && !ct.IsCancellationRequested; offset += bufSize)
            {
                int toRead = (int)Math.Min(bufSize, totalBytes - offset);
                try
                {
                    var data = ReadSectors(h, offset, toRead);
                    fs.Write(data, 0, data.Length);
                    readBytes += data.Length;
                }
                catch { break; }

                progress?.Report(new DiskImageProgress { BytesRead = readBytes, TotalBytes = totalBytes });
            }
            fs.Flush();
            return readBytes > 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Image create error: {ex.Message}");
            return false;
        }
    }
}
