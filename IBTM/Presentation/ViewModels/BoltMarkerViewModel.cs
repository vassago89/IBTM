using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace IBTM.Presentation.ViewModels;

/// <summary>볼트 마커 (캔버스 위 동적 표시)</summary>
public partial class BoltMarkerViewModel : ObservableObject
{
    // 상태별 브러시 (매번 new 방지)
    private static readonly Brush BrushTightening = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E)));
    private static readonly Brush BrushOk = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)));
    private static readonly Brush BrushNg = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49)));
    private static readonly Brush BrushIdle = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0x28, 0x18)));

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public string Name { get; }

    // 캔버스 좌표 (240×180 기준)
    public double CanvasLeft { get; }
    public double CanvasTop { get; }

    // 상태: 0=대기, 1=체결중, 2=OK, -1=NG
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkerBrush))]
    private int _state;

    // 실측 토크값 (체결 완료 후 표시)
    [ObservableProperty] private string _torqueText = string.Empty;

    public Brush MarkerBrush => State switch
    {
        1 => BrushTightening,
        2 => BrushOk,
        -1 => BrushNg,
        _ => BrushIdle,
    };

    public BoltMarkerViewModel(string name, double xMm, double yMm)
    {
        Name = name;
        var (cx, cy) = ZoneVisualState.MmToCanvas(xMm, yMm);
        CanvasLeft = cx - 3;  // 마커 중심 보정 (6px 원)
        CanvasTop = cy - 3;
    }
}
