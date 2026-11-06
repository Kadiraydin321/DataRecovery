using System.Net;
using System.Text;
using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;

namespace DataRecoveryApp.Services;

/// <summary>Tarama sonuçlarını HTML (koyu tema) veya CSV (UTF-8 BOM, Excel uyumlu) olarak dışa aktarır.</summary>
public static class ReportService
{
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    public static Task ExportAsync(string path, IReadOnlyList<RecoverableFile> files, string driveText, string appVersion)
        => Task.Run(() =>
        {
            string content = Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                ? BuildCsv(files)
                : BuildHtml(files, driveText, appVersion);
            File.WriteAllText(path, content, Utf8Bom);
        });

    /// <summary>Türkçe Excel'in varsayılan ayırıcısı ';' olduğu için noktalı virgül kullanılır.</summary>
    public static string BuildCsv(IReadOnlyList<RecoverableFile> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Dosya Adı;Uzantı;Tür;Boyut (bayt);Boyut;Tarih;Kurtarılabilirlik (%);Kaynak;Konum");
        foreach (var f in files)
            sb.AppendLine(string.Join(";", Csv(f.FileName), Csv(f.Extension), Csv(f.FileCategory), f.Size.ToString(),
                Csv(f.SizeText), Csv(f.DateText), f.RecoverabilityPercent.ToString(), Csv(f.SourceText), Csv(f.FilePath)));
        return sb.ToString();
    }

    private static string Csv(string? v)
    {
        v ??= string.Empty;
        return v.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static string BuildHtml(IReadOnlyList<RecoverableFile> files, string driveText, string appVersion)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var sb = new StringBuilder();
        sb.Append("""
<!DOCTYPE html>
<html lang="tr">
<head>
<meta charset="utf-8">
<title>Veri Kurtarma Raporu</title>
<style>
  body { background:#1E1E2E; color:#E0E0F0; font-family:'Segoe UI',Arial,sans-serif; margin:24px; }
  h1 { color:#7C6AF7; margin-bottom:4px; }
  .meta { color:#9090B0; margin-bottom:16px; }
  .cards { display:flex; gap:12px; flex-wrap:wrap; margin-bottom:18px; }
  .card { background:#252535; border:1px solid #3A3A5C; border-radius:8px; padding:10px 16px; min-width:120px; }
  .card b { display:block; font-size:20px; color:#7C6AF7; }
  table { width:100%; border-collapse:collapse; background:#252535; }
  th { background:#2E2E48; color:#E0E0F0; text-align:left; padding:8px; border-bottom:2px solid #7C6AF7; position:sticky; top:0; }
  td { padding:6px 8px; border-bottom:1px solid #3A3A5C; font-size:13px; word-break:break-all; }
  tr:nth-child(even) td { background:#2B2B3F; }
  .hi { color:#32CD32; font-weight:600; } .mid { color:#FFA500; font-weight:600; } .lo { color:#FF6347; font-weight:600; }
  footer { color:#9090B0; margin-top:16px; font-size:12px; }
</style>
</head>
<body>
""");
        sb.Append("<h1>🔍 Veri Kurtarma Raporu</h1>");
        sb.Append($"<div class=\"meta\">Sürücü: {E(driveText)} &nbsp;|&nbsp; Oluşturulma: {DateTime.Now:dd.MM.yyyy HH:mm} &nbsp;|&nbsp; {E(appVersion)}</div>");

        sb.Append("<div class=\"cards\">");
        sb.Append($"<div class=\"card\"><b>{files.Count}</b>Toplam dosya</div>");
        foreach (var g in files.GroupBy(f => f.FileCategory).OrderByDescending(g => g.Count()))
            sb.Append($"<div class=\"card\"><b>{g.Count()}</b>{E(g.Key)}</div>");
        sb.Append($"<div class=\"card\"><b>{E(FileSizeHelper.Format(files.Sum(f => Math.Max(0, f.Size))))}</b>Toplam boyut</div>");
        sb.Append("</div>");

        sb.Append("<table><thead><tr><th>#</th><th>Dosya Adı</th><th>Tür</th><th>Boyut</th><th>Tarih</th><th>Kurtarılabilirlik</th><th>Kaynak</th><th>Konum</th></tr></thead><tbody>");
        int i = 0;
        foreach (var f in files)
        {
            string cls = f.RecoverabilityPercent >= 80 ? "hi" : f.RecoverabilityPercent >= 50 ? "mid" : "lo";
            sb.Append($"<tr><td>{++i}</td><td>{E(f.FileName)}</td><td>{E(f.FileCategory)}</td><td>{E(f.SizeText)}</td>" +
                      $"<td>{E(f.DateText)}</td><td class=\"{cls}\">%{f.RecoverabilityPercent}</td><td>{E(f.SourceText)}</td><td>{E(f.FilePath)}</td></tr>");
        }
        sb.Append("</tbody></table>");
        sb.Append("<footer>Kurtarılabilirlik yüzdeleri tahminidir; kesin sonuç için dosyaları kurtarıp kontrol edin.</footer>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
