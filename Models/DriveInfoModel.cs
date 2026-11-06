namespace DataRecoveryApp.Models;

public class DriveInfoModel
{
    public string Letter { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string FileSystem { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public long FreeSpace { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public class RecoverySummary
{
    public int Success { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class RecoveryProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public int Percent => Total == 0 ? 0 : (int)(Current * 100L / Total);
}
