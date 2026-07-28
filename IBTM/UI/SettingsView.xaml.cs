using System.Windows;
using System.Windows.Controls;

namespace IBTM.UI;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        ViewModel.Activate();

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        ViewModel.Deactivate();
}
