using System.Windows;
using System.Windows.Controls;

namespace IBTM.Presentation.Views;

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

    // ── 탭 전환 ───────────────────────────────────────────────────
    private void Tab0_Checked(object sender, RoutedEventArgs e) => SwitchTab(0);
    private void Tab1_Checked(object sender, RoutedEventArgs e) => SwitchTab(1);
    private void Tab2_Checked(object sender, RoutedEventArgs e) => SwitchTab(2);
    private void Tab3_Checked(object sender, RoutedEventArgs e) => SwitchTab(3);
    private void Tab4_Checked(object sender, RoutedEventArgs e) => SwitchTab(4);
    private void Tab5_Checked(object sender, RoutedEventArgs e) => SwitchTab(5);

    private void SwitchTab(int index)
    {
        if (VM != null) VM.SelectedTab = index;

        // 모든 탭 Collapsed 후 선택된 탭만 Visible
        if (TabCalibration != null) TabCalibration.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (TabMotion != null) TabMotion.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (TabBolt != null) TabBolt.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (TabVision != null) TabVision.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (TabIO != null) TabIO.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        if (TabSystem != null) TabSystem.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
    }

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
