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
    private CancellationTokenSource? _cts;

    private DriveViewModel? _selectedDrive;
    private string _selectedCategory = "Hepsi";
    private int _scanProgress;
    private string _statusMessage = "Hazır. Bir sürücü seçip 'Tara' butonuna basın.";
    private bool _isScanning;
    private string _destinationPath = string.Empty;

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
        LoadDrives();
    }

    public string AppVersion => "v0.1.0";
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

    public ICommand StartScanCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand RecoverCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand BrowseDestinationCommand { get; }
    public ICommand SetCategoryCommand { get; }
    public ICommand RefreshDrivesCommand { get; }

    private void LoadDrives()
    {
        try
        {
            Drives.Clear();
            foreach (var d in _scanner.GetAvailableDrives()) Drives.Add(new DriveViewModel(d));
            StatusMessage = Drives.Count == 0 ? "Hiç sürücü bulunamadı." : $"{Drives.Count} sürücü bulundu. Taramak için birini seçin.";
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
        var progress = new Progress<int>(p => ScanProgress = p); // UI iş parçacığında çalışır
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
        catch (OperationCanceledException) { StatusMessage = "Tarama kullanıcı tarafından iptal edildi."; }
        catch (Exception ex) { StatusMessage = $"Tarama sırasında hata oluştu: {ex.Message}"; }
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
        if (string.IsNullOrWhiteSpace(DestinationPath)) { StatusMessage = "Lütfen bir hedef klasör seçin."; return; }
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
            StatusMessage = $"Kurtarma bitti: {s.Success} başarılı, {s.Failed} başarısız.";
            if (s.Errors.Count > 0)
                MessageBox.Show(string.Join(Environment.NewLine, s.Errors.Take(15)), "Kurtarma Hataları",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) { StatusMessage = "Kurtarma iptal edildi."; }
        catch (UnauthorizedAccessException) { StatusMessage = "Yönetici izni gerekiyor."; }
        catch (Exception ex) { StatusMessage = $"Kurtarma sırasında hata: {ex.Message}"; }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void BrowseDestination()
    {
        var dlg = new OpenFolderDialog { Title = "Kurtarılan dosyalar için hedef klasör seçin" };
        if (dlg.ShowDialog() == true)
        {
            DestinationPath = dlg.FolderName;
            if (SelectedDrive != null && DestinationPath.StartsWith(SelectedDrive.Drive.Letter + ":", StringComparison.OrdinalIgnoreCase))
                StatusMessage = "Uyarı: Hedef klasör taranan sürücüden farklı olmalıdır!";
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
