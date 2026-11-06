using DataRecoveryApp.Helpers;
using DataRecoveryApp.Models;

namespace DataRecoveryApp.ViewModels;

public class DriveViewModel : ObservableObject
{
    private bool _isSelected;

    public DriveViewModel(DriveInfoModel drive)
    {
        Drive = drive;
        DisplayText = $"{drive.Letter}:\\ - {FileSizeHelper.Format(drive.TotalSize)} / {FileSizeHelper.Format(drive.FreeSpace)} boş ({drive.FileSystem})";
    }

    public DriveInfoModel Drive { get; }
    public string DisplayText { get; }
    public string Title => $"{Drive.Letter}:\\  {Drive.Label}";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
