using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;

namespace DataRecoveryApp.Services;

public class PreviewService
{
    public async Task<PreviewData> GetPreviewAsync(RecoverableFile file, string driveLetter, CancellationToken ct)
        => await Task.Run(() => GetPreview(file), ct);

    private PreviewData GetPreview(RecoverableFile file)
    {
        try
        {
            string ext = file.Extension.ToLowerInvariant();
            var cat = FileTypeHelper.GetCategory(ext);

            return cat switch
            {
                FileCategory.Image  => BuildMeta($"🖼️ Resim: {file.FileName}", file, "Resim dosyası"),
                FileCategory.Video  => BuildMeta($"🎬 Video: {file.FileName}", file, "Video dosyası"),
                FileCategory.Audio  => BuildMeta($"🎵 Müzik: {file.FileName}", file, "[ID3 tagları henüz okunmuyor]"),
                FileCategory.Document when ext is "txt" or "csv" or "json" or "xml" or "html"
                                    => BuildText($"📄 Metin: {file.FileName}", file),
                FileCategory.Document => BuildMeta($"📄 Belge: {file.FileName}", file, "Ofis belgesi"),
                _                   => file.Extension is "zip" or "rar" or "7z"
                                        ? BuildMeta($"📦 Arşiv: {file.FileName}", file, "[İçerik listesi mevcut değil]")
                                        : NotAvailable(file.FileName)
            };
        }
        catch
        {
            return NotAvailable(file.FileName);
        }
    }

    private static PreviewData BuildMeta(string title, RecoverableFile f, string note) => new()
    {
        Type = PreviewType.Metadata,
        Title = title,
        Metadata = new List<string>
        {
            $"Dosya:            {f.FileName}",
            $"Boyut:            {FileSizeHelper.Format(f.Size)}",
            $"Silinme tarihi:   {f.DateText}",
            $"Kurtarılabilirlik: %{f.RecoverabilityPercent}",
            $"Konum:            {f.FilePath}",
            note
        }
    };

    private static PreviewData BuildText(string title, RecoverableFile f) => new()
    {
        Type = PreviewType.Text,
        Title = title,
        Content = $"[Metin dosyası — disk okuma gerektiriyor]\n" +
                  $"Dosya: {f.FileName}\n" +
                  $"Boyut: {FileSizeHelper.Format(f.Size)}\n" +
                  $"Tarih: {f.DateText}",
        Metadata = new List<string>
        {
            $"Kategori: {f.FileCategory}",
            $"Konum:    {f.FilePath}"
        }
    };

    private static PreviewData NotAvailable(string name) => new()
    {
        Type = PreviewType.NotAvailable,
        Title = $"{name} — Önizleme mevcut değil"
    };
}
