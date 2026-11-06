using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DataRecoveryApp.Helpers;

/// <summary>Ham disk erişimi için Windows API P/Invoke tanımları.</summary>
public static class WinApiHelper
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    public const uint FSCTL_ENUM_USN_DATA = 0x000900B3;

    public const uint USN_REASON_FILE_DELETE = 0x00000200;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_JOURNAL_NOT_ACTIVE = 1179;

    [StructLayout(LayoutKind.Sequential)]
    public struct NTFS_VOLUME_DATA_BUFFER
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct USN_JOURNAL_DATA_V0
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct READ_USN_JOURNAL_DATA_V0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer,
        uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    /// <summary>\\.\C: biçiminde ham birim tanıtıcısı açar.</summary>
    public static SafeFileHandle OpenVolume(string driveLetter)
    {
        string path = $@"\\.\{driveLetter.TrimEnd(':', '\\')}:";
        var handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_ACCESS_DENIED)
                throw new UnauthorizedAccessException("Yönetici izni gerekiyor. Uygulamayı yönetici olarak çalıştırın.");
            throw new Win32Exception(err, $"Birim açılamadı: {path}");
        }
        return handle;
    }

    public static T IoControl<T>(SafeFileHandle h, uint code) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(h, code, IntPtr.Zero, 0, buf, (uint)size, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStructure<T>(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static NTFS_VOLUME_DATA_BUFFER GetNtfsVolumeData(SafeFileHandle h) => IoControl<NTFS_VOLUME_DATA_BUFFER>(h, FSCTL_GET_NTFS_VOLUME_DATA);

    /// <summary>Belirtilen bayt ofsetinden (sektör hizalı) okuma yapar.</summary>
    public static byte[] ReadSectors(SafeFileHandle h, long byteOffset, int length)
    {
        if (!SetFilePointerEx(h, byteOffset, out _, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new byte[length];
        if (!ReadFile(h, buffer, (uint)length, out uint read, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (read < length) Array.Resize(ref buffer, (int)read);
        return buffer;
    }
}
