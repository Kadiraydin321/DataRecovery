using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;
using static DataRecoveryApp.Helpers.WinApiHelper;

namespace DataRecoveryApp.Services;

/// <summary>NTFS USN Journal üzerinden silinen dosya kayıtlarını bulur.</summary>
public class NtfsScanner
{
    public string? LastError { get; private set; }
    public bool AccessDenied { get; private set; }

    public Task<List<RecoverableFile>> ScanAsync(string driveLetter, IProgress<int>? progress, CancellationToken ct)
        => Task.Run(() => Scan(driveLetter, progress, ct), ct);

    private List<RecoverableFile> Scan(string driveLetter, IProgress<int>? progress, CancellationToken ct)
    {
        var results = new Dictionary<ulong, RecoverableFile>();
        LastError = null; AccessDenied = false;
        try
        {
            using var h = OpenVolume(driveLetter);
            var vol = GetNtfsVolumeData(h);
            var journal = IoControl<USN_JOURNAL_DATA_V0>(h, FSCTL_QUERY_USN_JOURNAL);

            var req = new READ_USN_JOURNAL_DATA_V0
            {
                StartUsn = journal.FirstUsn,
                ReasonMask = USN_REASON_FILE_DELETE,
                UsnJournalID = journal.UsnJournalID
            };
            const int bufSize = 64 * 1024;
            IntPtr inBuf = Marshal.AllocHGlobal(Marshal.SizeOf<READ_USN_JOURNAL_DATA_V0>());
            IntPtr outBuf = Marshal.AllocHGlobal(bufSize);
            try
            {
                long range = Math.Max(1, journal.NextUsn - journal.FirstUsn);
                while (!ct.IsCancellationRequested)
                {
                    Marshal.StructureToPtr(req, inBuf, false);
                    if (!DeviceIoControl(h, FSCTL_READ_USN_JOURNAL, inBuf, (uint)Marshal.SizeOf<READ_USN_JOURNAL_DATA_V0>(),
                            outBuf, bufSize, out uint returned, IntPtr.Zero))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == ERROR_HANDLE_EOF) break;
                        throw new System.ComponentModel.Win32Exception(err);
                    }
                    if (returned <= 8) break;
                    long nextUsn = Marshal.ReadInt64(outBuf);
                    int offset = 8;
                    while (offset < returned)
                    {
                        int recLen = Marshal.ReadInt32(outBuf, offset);
                        if (recLen <= 0) break;
                        ParseRecord(outBuf + offset, results, vol);
                        offset += recLen;
                    }
                    if (nextUsn <= req.StartUsn) break;
                    req.StartUsn = nextUsn;
                    progress?.Report((int)Math.Clamp((nextUsn - journal.FirstUsn) * 100 / range, 0, 100));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inBuf);
                Marshal.FreeHGlobal(outBuf);
            }
            progress?.Report(100);
        }
        catch (UnauthorizedAccessException ex)
        {
            AccessDenied = true; LastError = ex.Message; Debug.WriteLine(ex);
        }
        catch (Exception ex)
        {
            LastError = $"NTFS taraması başarısız: {ex.Message}"; Debug.WriteLine(ex);
        }
        return results.Values.ToList();
    }

    private static void ParseRecord(IntPtr rec, Dictionary<ulong, RecoverableFile> results, NTFS_VOLUME_DATA_BUFFER vol)
    {
        // USN_RECORD_V2 düzeni
        ushort major = (ushort)Marshal.ReadInt16(rec, 4);
        if (major != 2) return;
        ulong frn = (ulong)Marshal.ReadInt64(rec, 8);
        long timestamp = Marshal.ReadInt64(rec, 32);
        uint reason = (uint)Marshal.ReadInt32(rec, 40);
        uint attrs = (uint)Marshal.ReadInt32(rec, 52);
        ushort nameLen = (ushort)Marshal.ReadInt16(rec, 56);
        ushort nameOff = (ushort)Marshal.ReadInt16(rec, 58);
        if ((reason & USN_REASON_FILE_DELETE) == 0 || (attrs & 0x10) != 0) return; // klasörleri atla

        var bytes = new byte[nameLen];
        Marshal.Copy(rec + nameOff, bytes, 0, nameLen);
        string name = Encoding.Unicode.GetString(bytes);
        if (name.StartsWith("~$") || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;

        string ext = FileTypeHelper.GetExtension(name);
        DateTime? deleted = null;
        try { deleted = DateTime.FromFileTime(timestamp); } catch { }
        long mftIndex = (long)(frn & 0x0000FFFFFFFFFFFF);
        long mftOffsetBytes = vol.MftStartLcn * vol.BytesPerCluster + mftIndex * vol.BytesPerFileRecordSegment;
        double ageDays = deleted.HasValue ? (DateTime.Now - deleted.Value).TotalDays : 365;

        results[frn] = new RecoverableFile
        {
            FileName = name,
            Extension = ext,
            FilePath = $"MFT #{mftIndex}",
            DeletedDate = deleted,
            FileCategory = RecoverableFile.CategoryName(FileTypeHelper.GetCategory(ext)),
            StartSector = vol.BytesPerSector == 0 ? 0 : mftOffsetBytes / vol.BytesPerSector,
            FileReferenceNumber = frn,
            RecoverabilityPercent = ageDays < 1 ? 90 : ageDays < 7 ? 75 : ageDays < 30 ? 55 : 30
        };
    }
}
