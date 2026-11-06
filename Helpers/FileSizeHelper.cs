using System.Globalization;
using DataRecoveryApp.Models;

namespace DataRecoveryApp.Helpers;

public static class FileSizeHelper
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };
    private static readonly CultureInfo Tr = new("tr-TR");

    public static string Format(long bytes)
    {
        if (bytes < 0) return "?";
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes.ToString("N0", Tr)} {Units[0]}"
            : $"{value.ToString(value >= 100 ? "N0" : "N1", Tr)} {Units[unit]}";
    }
}

public static class FileTypeHelper
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "raw", "cr2", "nef", "arw", "dng", "psd", "svg", "ico" };
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
        { "doc", "docx", "xls", "xlsx", "ppt", "pptx", "pdf", "txt", "rtf", "odt", "ods", "odp", "csv", "md", "xml", "json", "html", "htm", "epub" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "3gp", "ts" };
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { "mp3", "wav", "flac", "aac", "ogg", "wma", "m4a", "opus", "aiff", "mid", "midi" };

    public static string GetExtension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot >= 0 && dot < fileName.Length - 1 ? fileName[(dot + 1)..].ToLowerInvariant() : string.Empty;
    }

    public static FileCategory GetCategory(string extension)
    {
        if (Images.Contains(extension)) return FileCategory.Image;
        if (Documents.Contains(extension)) return FileCategory.Document;
        if (Videos.Contains(extension)) return FileCategory.Video;
        if (Audio.Contains(extension)) return FileCategory.Audio;
        return FileCategory.Other;
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "adsiz_dosya" : result;
    }
}
