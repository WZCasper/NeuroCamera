using System.Windows;
using System.Windows.Controls;
using NeuroCamera.ViewModels;

namespace NeuroCamera;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        Closed += (_, _) => _viewModel.Dispose();
    }

    // PasswordBox.Password is deliberately not a bindable DependencyProperty (WPF security
    // design), so it is synced to the view model here instead of via XAML binding.
    private void ObsPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            _viewModel.ObsPassword = passwordBox.Password;
        }
    }
}
