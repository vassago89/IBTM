using System.Windows;
using System.Windows.Controls;
using IBTM.Presentation.ViewModels;

namespace IBTM.Presentation.Views;

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

    private void JogXPlus_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => ViewModel.JogXPlusCommand.Execute(null);
    private void JogXMinus_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => ViewModel.JogXMinusCommand.Execute(null);
    private void JogYPlus_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => ViewModel.JogYPlusCommand.Execute(null);
    private void JogYMinus_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => ViewModel.JogYMinusCommand.Execute(null);
    private void JogZPlus_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => ViewModel.JogZPlusCommand.Execute(null);
    private void JogZMinus_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => ViewModel.JogZMinusCommand.Execute(null);
    private void Jog_Up(object sender, System.Windows.Input.MouseButtonEventArgs e) => ViewModel.JogStopCommand.Execute(null);
    private void Jog_Cancel(object sender, System.Windows.Input.MouseEventArgs e) => ViewModel.JogStopCommand.Execute(null);
}
