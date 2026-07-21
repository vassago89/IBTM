using System.Windows;
using System.Windows.Controls;

namespace IBTM.Presentation.Views;

/// <summary>헤드 종류별 렌더링 분기</summary>
public enum ZoneHeadType { Gripper, BoltDriver, Camera }

/// <summary>
/// 3개 Zone 공통 캔버스 — Theme(색상) + ZoneVisual(상태) + HeadType(헤드형상) DP로 파라미터화
/// </summary>
public partial class ZoneCanvasView : UserControl
{
    public static readonly DependencyProperty ZoneVisualProperty =
        DependencyProperty.Register(nameof(ZoneVisual), typeof(ZoneVisualState), typeof(ZoneCanvasView));

    public static readonly DependencyProperty ThemeProperty =
        DependencyProperty.Register(nameof(Theme), typeof(ZoneTheme), typeof(ZoneCanvasView),
            new PropertyMetadata(null, (d, _) => ((ZoneCanvasView)d).ApplyTheme()));

    public static readonly DependencyProperty HeadTypeProperty =
        DependencyProperty.Register(nameof(HeadType), typeof(ZoneHeadType), typeof(ZoneCanvasView));

    public ZoneVisualState? ZoneVisual
    {
        get => (ZoneVisualState?)GetValue(ZoneVisualProperty);
        set => SetValue(ZoneVisualProperty, value);
    }

    public ZoneTheme? Theme
    {
        get => (ZoneTheme?)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    public ZoneHeadType HeadType
    {
        get => (ZoneHeadType)GetValue(HeadTypeProperty);
        set => SetValue(HeadTypeProperty, value);
    }

    public ZoneCanvasView()
    {
        InitializeComponent();
    }

    /// <summary>Theme DP → DynamicResource 동기화</summary>
    private void ApplyTheme()
    {
        var t = Theme;
        if (t == null) return;

        Resources["Accent"] = t.Accent;
        Resources["GantryStroke"] = t.GantryStroke;
        Resources["RailFill"] = t.RailFill;
        Resources["BridgeFill"] = t.BridgeFill;
        Resources["ConveyorBg"] = t.ConveyorBg;
        Resources["ConveyorBorder"] = t.ConveyorBorder;
        Resources["StopperFill"] = t.StopperFill;
        Resources["StopperFg"] = t.StopperFg;
        Resources["AlignBg"] = t.AlignBg;
        Resources["AlignBorder"] = t.AlignBorder;
        Resources["AlignFg"] = t.AlignFg;
        Resources["ShuttleBg"] = t.ShuttleBg;
        Resources["ShuttleBorder"] = t.ShuttleBorder;
        Resources["ShuttleLiftedBg"] = t.ShuttleLiftedBg;
        Resources["HeadGlow"] = t.HeadGlow;
    }
}
