using System.Windows;
using DataRecoveryApp.ViewModels;

namespace DataRecoveryApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Loaded += Window_Loaded;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Title = "Veri Kurtarma";
    }
}
