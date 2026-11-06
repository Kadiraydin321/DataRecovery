using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;
using DataRecoveryApp.Services;
using Microsoft.Win32;

namespace DataRecoveryApp.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly DiskScannerService _scanner = new();
    private readonly CarverService _carver = new();
    private readonly DiskImageService _imageService = new();
    private readonly PreviewService _previewService = new();
    private readonly PartitionService _partitionService = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _previewCts;
    private bool _showPartitionPanel;
    private string _partitionSummary = string.Empty;

    private DriveViewModel? _selectedDrive;
    private string _selectedCategory = "Hepsi";
    private int _scanProgress;
    private string _statusMessage = "Hazır. Bir sürücü seçip 'Tara' butonuna basın.";
    private bool _isScanning;
    private string _destinationPath = string.Empty;
    private bool _showCarvingOptions;
    private RecoverableFile? _selectedFileForPreview;
    private PreviewData? _currentPreview;
    private CarverSettings _carverSettings = new();

    public MainViewModel()
    {
        StartScanCommand = new RelayCommand(async () => await StartScanAsync(), () => CanScan);
        CancelScanCommand = new RelayCommand(() => _cts?.Cancel(), () => IsScanning);
        RecoverCommand = new RelayCommand(async () => await RecoverAsync(),
            () => !IsScanning && ScannedFiles.Any(f => f.IsSelected));
        SelectAllCommand = new RelayCommand(() => SetSelection(true), () => FilteredFiles.Count > 0);
        ClearSelectionCommand = new RelayCommand(() => SetSelection(false), () => FilteredFiles.Count > 0);
        BrowseDestinationCommand = new RelayCommand(BrowseDestination);
        SetCategoryCommand = new RelayCommand(p => SelectedCategory = p as string ?? "Hepsi");
        RefreshDrivesCommand = new RelayCommand(LoadDrives, () => !IsScanning);
        CreateImageCommand = new RelayCommand(async () => await CreateImageAsync(), () => SelectedDrive != null && !IsScanning);
        StartCarvingCommand = new RelayCommand(async () => await StartCarvingAsync(), () => SelectedDrive != null && !IsScanning);
        AnalyzePartitionsCommand = new RelayCommand(async () => await AnalyzePartitionsAsync(), () => !IsScanning);
        ClosePartitionPanelCommand = new RelayCommand(() => ShowPartitionPanel = false);
        ExportReportCommand = new RelayCommand(async () => await ExportReportAsync(), () => !IsScanning && ScannedFiles.Count > 0);
        
        LoadDrives();
    }

    public string AppVersion => "v0.3.1 (Faz 3 + Rust Çekirdeği)";
    public string EngineStatus => Services.RustBridge.StatusMessage;
    public ObservableCollection<DriveViewModel> Drives { get; } = new();
    public ObservableCollection<RecoverableFile> ScannedFiles { get; } = new();
    public ObservableCollection<RecoverableFile> FilteredFiles { get; } = new();
    public string[] Categories { get; } = { "Hepsi", "Resimler", "Belgeler", "Videolar", "Müzik", "Diğer" };

    public DriveViewModel? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (_selectedDrive != null) _selectedDrive.IsSelected = false;
            if (SetProperty(ref _selectedDrive, value))
            {
                if (value != null) value.IsSelected = true;
                OnPropertyChanged(nameof(CanScan));
            }
        }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set { if (SetProperty(ref _selectedCategory, value)) ApplyFilter(); }
    }

    public int ScanProgress { get => _scanProgress; set => SetProperty(ref _scanProgress, value); }
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public bool IsScanning
    {
        get => _isScanning;
        set { if (SetProperty(ref _isScanning, value)) { OnPropertyChanged(nameof(CanScan)); RelayCommand.Refresh(); } }
    }

    public bool CanScan => SelectedDrive != null && !IsScanning;
    public string DestinationPath { get => _destinationPath; set => SetProperty(ref _destinationPath, value); }
    
    public bool ShowCarvingOptions { get => _showCarvingOptions; set => SetProperty(ref _showCarvingOptions, value); }
    public CarverSettings CarverSettings { get => _carverSettings; set => SetProperty(ref _carverSettings, value); }

    public RecoverableFile? SelectedFileForPreview
    {
        get => _selectedFileForPreview;
        set
        {
            if (SetProperty(ref _selectedFileForPreview, value))
            {
                if (value != null) UpdatePreview(value);
            }
        }
    }

    public PreviewData? CurrentPreview { get => _currentPreview; set => SetProperty(ref _currentPreview, value); }

    public ICommand StartScanCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand RecoverCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand BrowseDestinationCommand { get; }
    public ICommand SetCategoryCommand { get; }
    public ICommand RefreshDrivesCommand { get; }
    public ICommand CreateImageCommand { get; }
    public ICommand StartCarvingCommand { get; }
    public ICommand AnalyzePartitionsCommand { get; }
    public ICommand ClosePartitionPanelCommand { get; }
    public ICommand ExportReportCommand { get; }

    public ObservableCollection<PartitionInfo> Partitions { get; } = new();
    public bool ShowPartitionPanel { get => _showPartitionPanel; set => SetProperty(ref _showPartitionPanel, value); }
    public string PartitionSummary { get => _partitionSummary; set => SetProperty(ref _partitionSummary, value); }

    private void LoadDrives()
    {
        try
        {
            Drives.Clear();
            foreach (var d in _scanner.GetAvailableDrives()) Drives.Add(new DriveViewModel(d));
            StatusMessage = Drives.Count == 0 ? "Hiç sürücü bulunamadı." : $"{Drives.Count} sürücü bulundu.";
        }
        catch (Exception ex) { StatusMessage = $"Sürücüler yüklenemedi: {ex.Message}"; }
    }

    private async Task StartScanAsync()
    {
        if (SelectedDrive == null) { StatusMessage = "Lütfen önce bir sürücü seçin."; return; }
        _cts = new CancellationTokenSource();
        IsScanning = true;
        ScanProgress = 0;
        ScannedFiles.Clear();
        FilteredFiles.Clear();
        StatusMessage = $"{SelectedDrive.Drive.Letter}:\\ taranıyor...";
        var progress = new Progress<int>(p => ScanProgress = p);
        try
        {
            var files = await _scanner.ScanDriveAsync(SelectedDrive.Drive, progress, _cts.Token);
            foreach (var f in files.OrderByDescending(f => f.DeletedDate)) ScannedFiles.Add(f);
            ApplyFilter();
            if (_scanner.AccessDenied)
                StatusMessage = "Yönetici izni gerekiyor. Uygulamayı 'Yönetici olarak çalıştır' ile açın.";
            else if (_scanner.LastError != null && files.Count == 0)
                StatusMessage = _scanner.LastError;
            else
                StatusMessage = $"Tarama tamamlandı: {files.Count} silinmiş dosya bulundu.";
            ScanProgress = 100;
        }
        catch (OperationCanceledException) { StatusMessage = "Tarama iptal edildi."; }
        catch (Exception ex) { StatusMessage = $"Tarama hatası: {ex.Message}"; }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task StartCarvingAsync()
    {
        if (SelectedDrive == null) { StatusMessage = "Sürücü seçilmedi."; return; }
        _cts = new CancellationTokenSource();
        IsScanning = true;
        ScanProgress = 0;
        StatusMessage = $"{SelectedDrive.Drive.Letter}:\\ veri kazıma yapılıyor...";
        var progress = new Progress<int>(p => ScanProgress = p);
        try
        {
            var files = await _carver.CarveAsync(SelectedDrive.Drive.Letter, CarverSettings, progress, _cts.Token);
            foreach (var f in files.OrderByDescending(f => f.Size)) ScannedFiles.Add(f);
            ApplyFilter();
            StatusMessage = files.Count == 0 && _carver.LastError != null
                ? _carver.LastError
                : $"Kazıma tamamlandı: {files.Count} dosya bulundu (boyutları sonlandırıcı/başlık bilgisinden belirlendi).";
            ScanProgress = 100;
        }
        catch (OperationCanceledException) { StatusMessage = "Kazıma iptal edildi."; }
        catch (Exception ex) { StatusMessage = $"Kazıma hatası: {ex.Message}"; }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task CreateImageAsync()
    {
        if (SelectedDrive == null) return;
        var dlg = new SaveFileDialog { Filter = "Raw Disk Image|*.img|Tüm Dosyalar|*.*", FileName = $"disk_{SelectedDrive.Drive.Letter}.img" };
        if (dlg.ShowDialog() != true) return;

        _cts = new CancellationTokenSource();
        IsScanning = true;
        StatusMessage = $"{SelectedDrive.Drive.Letter}:\\ imajı oluşturuluyor...";
        var progress = new Progress<DiskImageProgress>(p => 
        {
            ScanProgress = p.Percent;
            StatusMessage = $"İmaj oluşturuluyor... {FileSizeHelper.Format(p.BytesRead)} / {FileSizeHelper.Format(p.TotalBytes)}";
        });

        try
        {
            bool success = await _imageService.CreateImageAsync(SelectedDrive.Drive.Letter, dlg.FileName, progress, _cts.Token);
            StatusMessage = success ? $"İmaj başarıyla oluşturuldu: {dlg.FileName}" : "İmaj oluşturma başarısız.";
            ScanProgress = 100;
        }
        catch (OperationCanceledException) { StatusMessage = "İmaj oluşturma iptal edildi."; }
        catch (Exception ex) { StatusMessage = $"İmaj hatası: {ex.Message}"; }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task RecoverAsync()
    {
        var selected = ScannedFiles.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0) { StatusMessage = "Kurtarılacak dosya seçilmedi."; return; }
        if (string.IsNullOrWhiteSpace(DestinationPath)) { StatusMessage = "Hedef klasör seçilmedi."; return; }
        if (SelectedDrive == null) return;

        _cts = new CancellationTokenSource();
        IsScanning = true;
        var service = new RecoveryService(SelectedDrive.Drive.Letter);
        var progress = new Progress<RecoveryProgress>(p =>
        {
            ScanProgress = p.Percent;
            StatusMessage = $"Kurtarılıyor ({p.Current}/{p.Total}): {p.CurrentFile}";
        });
        try
        {
            var s = await service.RecoverFilesAsync(selected, DestinationPath, progress, _cts.Token);
            StatusMessage = $"Kurtarma tamamlandı: {s.Success} başarılı, {s.Failed} başarısız.";
            if (s.Errors.Count > 0)
                MessageBox.Show(string.Join(Environment.NewLine, s.Errors.Take(15)), "Kurtarma Hataları",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) { StatusMessage = "Kurtarma iptal edildi."; }
        catch (UnauthorizedAccessException) { StatusMessage = "Yönetici izni gerekiyor."; }
        catch (Exception ex) { StatusMessage = $"Kurtarma hatası: {ex.Message}"; }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private async void UpdatePreview(RecoverableFile file)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        if (SelectedDrive == null) { CurrentPreview = new PreviewData { Type = PreviewType.Metadata, Title = "Önizleme için sürücü seçili değil." }; return; }
        CurrentPreview = new PreviewData { Type = PreviewType.Metadata, Title = $"{file.FileName} — diskten okunuyor..." };
        try
        {
            var preview = await _previewService.GetPreviewAsync(file, SelectedDrive.Drive.Letter, cts.Token);
            if (!cts.IsCancellationRequested) CurrentPreview = preview;
        }
        catch (OperationCanceledException) { /* yeni seçim yapıldı */ }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                CurrentPreview = new PreviewData { Type = PreviewType.Metadata, Title = "Önizleme hatası", Metadata = { ex.Message } };
        }
    }

    private async Task AnalyzePartitionsAsync()
    {
        int disk = SelectedDrive != null ? WinApiHelper.GetDiskNumberForVolume(SelectedDrive.Drive.Letter) : 0;
        if (disk < 0) disk = 0;
        _cts = new CancellationTokenSource();
        IsScanning = true;
        ScanProgress = 0;
        Partitions.Clear();
        ShowPartitionPanel = true;
        PartitionSummary = $"Fiziksel Disk {disk} analiz ediliyor...";
        StatusMessage = $"Bölüm analizi: Fiziksel Disk {disk} (MBR/GPT okunuyor, boş alanlarda kayıp bölüm aranıyor)...";
        var progress = new Progress<int>(p => ScanProgress = p);
        try
        {
            var r = await _partitionService.AnalyzeAsync(disk, true, progress, _cts.Token);
            foreach (var p in r.Partitions.OrderBy(p => p.StartLba)) Partitions.Add(p);
            PartitionSummary = r.Summary + (r.Warnings.Count > 0 ? "\n⚠ " + string.Join("\n⚠ ", r.Warnings) : "");
            StatusMessage = $"Bölüm analizi tamamlandı: {r.Partitions.Count(p => !p.IsLost)} bölüm, {r.Partitions.Count(p => p.IsLost)} kayıp bölüm adayı.";
            ScanProgress = 100;
        }
        catch (OperationCanceledException) { StatusMessage = "Bölüm analizi iptal edildi."; PartitionSummary = "İptal edildi."; }
        catch (UnauthorizedAccessException) { StatusMessage = PartitionSummary = "Fiziksel diske erişim için yönetici izni gerekiyor."; }
        catch (Exception ex) { StatusMessage = PartitionSummary = $"Bölüm analizi hatası: {ex.Message}"; }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task ExportReportAsync()
    {
        var files = (FilteredFiles.Count > 0 ? FilteredFiles : ScannedFiles).ToList();
        if (files.Count == 0) { StatusMessage = "Dışa aktarılacak sonuç yok. Önce tarama yapın."; return; }
        var dlg = new SaveFileDialog
        {
            Title = "Raporu Dışa Aktar",
            Filter = "HTML Raporu (*.html)|*.html|CSV - Excel (*.csv)|*.csv",
            FileName = $"kurtarma_raporu_{DateTime.Now:yyyyMMdd_HHmm}"
        };
        if (dlg.ShowDialog() != true) return;
        string path = dlg.FileName;
        if (string.IsNullOrEmpty(Path.GetExtension(path))) path += dlg.FilterIndex == 2 ? ".csv" : ".html";
        try
        {
            string drive = SelectedDrive?.Drive.DisplayName ?? "-";
            await ReportService.ExportAsync(path, files, drive, AppVersion);
            StatusMessage = $"Rapor kaydedildi ({files.Count} dosya): {path}";
        }
        catch (UnauthorizedAccessException) { StatusMessage = "Rapor kaydedilemedi: klasöre yazma izni yok."; }
        catch (Exception ex) { StatusMessage = $"Rapor kaydedilemedi: {ex.Message}"; }
    }

    private void BrowseDestination()
    {
        var dlg = new OpenFolderDialog { Title = "Hedef klasör seçin" };
        if (dlg.ShowDialog() == true)
        {
            DestinationPath = dlg.FolderName;
            if (SelectedDrive != null && DestinationPath.StartsWith(SelectedDrive.Drive.Letter + ":", StringComparison.OrdinalIgnoreCase))
                StatusMessage = "Uyarı: Hedef, taranan sürücüden farklı olmalıdır!";
        }
    }

    private void SetSelection(bool value)
    {
        foreach (var f in FilteredFiles) f.IsSelected = value;
        RelayCommand.Refresh();
    }

    private void ApplyFilter()
    {
        string? cat = SelectedCategory switch
        {
            "Resimler" => "Resim",
            "Belgeler" => "Belge",
            "Videolar" => "Video",
            "Müzik" => "Müzik",
            "Diğer" => "Diğer",
            _ => null
        };
        FilteredFiles.Clear();
        foreach (var f in ScannedFiles.Where(f => cat == null || f.FileCategory == cat)) FilteredFiles.Add(f);
        RelayCommand.Refresh();
    }
}
