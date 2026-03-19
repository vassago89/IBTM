using IBTM.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace IBTM.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private SettingsViewModel? VM => DataContext as SettingsViewModel;

    // ── 라이프사이클 ────────────────────────────────────────────────
    private void OnLoaded(object sender, RoutedEventArgs e) =>
        VM?.LoadConfigCommand.Execute(null);

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        VM?.StopPolling();

    // ── Zone 선택 ───────────────────────────────────────────────────
    private void Zone1_Checked(object sender, RoutedEventArgs e) { if (VM != null) VM.SelectedZone = 1; }
    private void Zone2_Checked(object sender, RoutedEventArgs e) { if (VM != null) VM.SelectedZone = 2; }
    private void Zone3_Checked(object sender, RoutedEventArgs e) { if (VM != null) VM.SelectedZone = 3; }

    // ── 조그 (MouseDown → 연속이동, MouseUp → 정지) ────────────────
    private void JogXPlus_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => VM?.JogXPlusCommand.Execute(null);
    private void JogXMinus_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => VM?.JogXMinusCommand.Execute(null);
    private void JogYPlus_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => VM?.JogYPlusCommand.Execute(null);
    private void JogYMinus_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => VM?.JogYMinusCommand.Execute(null);
    private void JogZPlus_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => VM?.JogZPlusCommand.Execute(null);
    private void JogZMinus_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => VM?.JogZMinusCommand.Execute(null);
    private void Jog_Up(object sender, System.Windows.Input.MouseButtonEventArgs e) => VM?.JogStopCommand.Execute(null);

    // ── 속도 선택 ───────────────────────────────────────────────────
    private void SpeedSlow_Checked(object sender, RoutedEventArgs e) { if (VM != null) VM.JogSpeedIndex = 0; }
    private void SpeedMed_Checked(object sender, RoutedEventArgs e) { if (VM != null) VM.JogSpeedIndex = 1; }
    private void SpeedFast_Checked(object sender, RoutedEventArgs e) { if (VM != null) VM.JogSpeedIndex = 2; }
}
