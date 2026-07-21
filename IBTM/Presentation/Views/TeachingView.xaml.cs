using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IBTM.Presentation.Views;

public partial class TeachingView : UserControl
{
    public TeachingView()
    {
        InitializeComponent();
    }

    private TeachingViewModel? VM => DataContext as TeachingViewModel;

    // ── 라이프사이클 ────────────────────────────────────────────────
    private void OnLoaded(object sender, RoutedEventArgs e) => VM?.Activate();
    private void OnUnloaded(object sender, RoutedEventArgs e) => VM?.Deactivate();

    // ── Zone 선택 ───────────────────────────────────────────────────
    private void Zone1_Checked(object sender, RoutedEventArgs e) { if (VM != null) VM.SelectedZone = 1; }
    private void Zone2_Checked(object sender, RoutedEventArgs e) { if (VM != null) VM.SelectedZone = 2; }
    private void Zone3_Checked(object sender, RoutedEventArgs e) { if (VM != null) VM.SelectedZone = 3; }

    // ── 조그 (MouseDown → 연속이동, MouseUp → 정지) ────────────────
    private void JogXPlus_Down(object sender, MouseButtonEventArgs e) => VM?.JogXPlusCommand.Execute(null);
    private void JogXMinus_Down(object sender, MouseButtonEventArgs e) => VM?.JogXMinusCommand.Execute(null);
    private void JogYPlus_Down(object sender, MouseButtonEventArgs e) => VM?.JogYPlusCommand.Execute(null);
    private void JogYMinus_Down(object sender, MouseButtonEventArgs e) => VM?.JogYMinusCommand.Execute(null);
    private void JogZPlus_Down(object sender, MouseButtonEventArgs e) => VM?.JogZPlusCommand.Execute(null);
    private void JogZMinus_Down(object sender, MouseButtonEventArgs e) => VM?.JogZMinusCommand.Execute(null);
    private void Jog_Up(object sender, MouseButtonEventArgs e) => VM?.JogStopCommand.Execute(null);

    // ── 속도 선택 ───────────────────────────────────────────────────
    private void SpeedSlow_Checked(object sender, RoutedEventArgs e) { if (VM != null) VM.JogSpeedIndex = 0; }
    private void SpeedMed_Checked(object sender, RoutedEventArgs e) { if (VM != null) VM.JogSpeedIndex = 1; }
    private void SpeedFast_Checked(object sender, RoutedEventArgs e) { if (VM != null) VM.JogSpeedIndex = 2; }

    // ── 카메라 클릭 ─────────────────────────────────────────────────
    private void CameraImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image { Source: ImageSource source } image
            || image.ActualWidth <= 0
            || image.ActualHeight <= 0)
        {
            return;
        }

        // 클릭 위치를 이미지 원본 좌표로 변환
        var pos = e.GetPosition(image);
        var scaleX = source.Width / image.ActualWidth;
        var scaleY = source.Height / image.ActualHeight;
        var imgPos = new Point(pos.X * scaleX, pos.Y * scaleY);

        VM?.CameraClickCommand.Execute(imgPos);
        e.Handled = true;
    }

    // ── 레시피 파일 선택 ────────────────────────────────────────────
    private void RecipeFile_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is string filePath)
            VM?.LoadRecipeCommand.Execute(filePath);
    }
}
