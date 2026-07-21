using System.Windows.Media;

namespace IBTM.Presentation.Models;

/// <summary>
/// Zone별 색상 테마 — XAML 리소스로 정의, ZoneCanvasView에서 DynamicResource로 사용
/// </summary>
public class ZoneTheme
{
    // 강조색 (헤드, 게이지, 십자선, LIFT 텍스트)
    public Brush Accent { get; set; } = Brushes.Gray;

    // 갠트리 프레임
    public Brush GantryStroke { get; set; } = Brushes.DarkGray;
    public Brush RailFill { get; set; } = Brushes.DarkGray;
    public Brush BridgeFill { get; set; } = Brushes.DarkGray;

    // 컨베이어
    public Brush ConveyorBg { get; set; } = Brushes.Black;
    public Brush ConveyorBorder { get; set; } = Brushes.DarkGray;

    // 스토퍼
    public Brush StopperFill { get; set; } = Brushes.DarkGray;
    public Brush StopperFg { get; set; } = Brushes.Gray;

    // 얼라인
    public Brush AlignBg { get; set; } = Brushes.Black;
    public Brush AlignBorder { get; set; } = Brushes.DarkGray;
    public Brush AlignFg { get; set; } = Brushes.Gray;

    // 셔틀
    public Brush ShuttleBg { get; set; } = Brushes.Black;
    public Brush ShuttleBorder { get; set; } = Brushes.DarkGray;
    public Brush ShuttleLiftedBg { get; set; } = Brushes.DarkGray;

    // 헤드
    public Brush HeadGlow { get; set; } = Brushes.Gray;

}
