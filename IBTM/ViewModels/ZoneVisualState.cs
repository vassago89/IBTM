using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;

namespace IBTM.ViewModels;

/// <summary>구간별 2D 레이아웃 상태 (Canvas 240×180 기준)</summary>
public partial class ZoneVisualState : ObservableObject
{
    private const double MaxCoord = 200.0;
    private const double MaxZ = 60.0;

    // 캔버스 작업 영역
    private const double WorkX0 = 12.0, WorkXRange = 186.0;
    private const double WorkY0 = 10.0, WorkYRange = 160.0;
    private const double GaugeTrack = 156.0;

    // 십자선 범위
    private const double CrosshairSpan = 30.0;
    private const double CanvasXMin = 8.0, CanvasXMax = 210.0;
    private const double CanvasYMin = 0.0, CanvasYMax = 180.0;


    public ZoneVisualState(double canvasW, double canvasH)
    {
        HeadLeft = canvasW / 2 - 6;
        HeadTop = canvasH / 2 - 6;
    }

    /// <summary>장비 mm 좌표 → 캔버스 픽셀 좌표 (static — 외부에서도 사용)</summary>
    public static (double x, double y) MmToCanvas(double xMm, double yMm)
    {
        var xNorm = Math.Clamp(xMm / MaxCoord, 0, 1);
        var yNorm = Math.Clamp(yMm / MaxCoord, 0, 1);
        return (WorkX0 + xNorm * WorkXRange, WorkY0 + yNorm * WorkYRange);
    }

    /// <summary>장비 mm 좌표 → 캔버스 픽셀 좌표 (인스턴스 래퍼)</summary>
    public (double x, double y) ToCanvas(double xMm, double yMm) => MmToCanvas(xMm, yMm);

    // ── 헤드 위치 (Canvas 좌표) ──────────────────────────────────────────
    [ObservableProperty] private double _headLeft;
    [ObservableProperty] private double _headTop;
    [ObservableProperty] private double _headSize = 12;
    [ObservableProperty] private double _headCenterLeft;
    [ObservableProperty] private double _headCenterTop;
    [ObservableProperty] private double _zRatio;
    [ObservableProperty] private string _coordText = "";

    // 갠트리 브릿지 (X축 레일, Y축 따라 이동)
    [ObservableProperty] private double _bridgeTop;

    // ── 그리퍼 상태 ─────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GripperVisibility))]
    private bool _gripperActive;

    public Visibility GripperVisibility =>
        GripperActive ? Visibility.Visible : Visibility.Collapsed;

    // ── 셔틀 상태 ───────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiftIndicatorVisibility))]
    private bool _shuttlePresent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiftIndicatorVisibility))]
    [NotifyPropertyChangedFor(nameof(ShuttleStrokeThickness))]
    private bool _isLifted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Carrier1Visibility))]
    [NotifyPropertyChangedFor(nameof(Carrier2Visibility))]
    private int _carrierCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Pcb1Visibility))]
    [NotifyPropertyChangedFor(nameof(Pcb2Visibility))]
    private int _pcbCount = 2;

    public Visibility Carrier1Visibility => CarrierCount >= 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Carrier2Visibility => CarrierCount >= 2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Pcb1Visibility => PcbCount >= 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Pcb2Visibility => PcbCount >= 2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LiftIndicatorVisibility =>
        ShuttlePresent && IsLifted ? Visibility.Visible : Visibility.Collapsed;
    public double ShuttleStrokeThickness => IsLifted ? 2.5 : 1.0;

    // ── 레시피 기반 레이아웃 위치 (캔버스 좌표) ──────────────────────────
    // Zone 1: 상부 PCB 라인 영역
    [ObservableProperty] private double _pcbAreaTop;
    [ObservableProperty] private double _pcbLabelTop;
    [ObservableProperty] private double _pcb1Left;
    [ObservableProperty] private double _pcb1Top;
    [ObservableProperty] private double _pcb2Left;
    [ObservableProperty] private double _pcb2Top;
    // Zone 1: 셔틀 위 캐리어/PCB 위치
    [ObservableProperty] private double _shuttleCarrier1Left;
    [ObservableProperty] private double _shuttleCarrier1Top;
    [ObservableProperty] private double _shuttleCarrier2Left;
    [ObservableProperty] private double _shuttleCarrier2Top;
    [ObservableProperty] private double _shuttlePcb1Left;
    [ObservableProperty] private double _shuttlePcb1Top;
    [ObservableProperty] private double _shuttlePcb2Left;
    [ObservableProperty] private double _shuttlePcb2Top;
    // Zone 3: NG 적재 영역
    [ObservableProperty] private double _ngAreaTop;
    [ObservableProperty] private double _ngLabelTop;

    /// <summary>Zone 1 레시피 좌표 기반 레이아웃 갱신</summary>
    public void UpdateZone1Layout(
        double pcb1X, double pcb1Y, double pcb2X, double pcb2Y,
        double place1X, double place1Y, double place2X, double place2Y)
    {
        // 상부 PCB 라인 영역 (PcbPick Y 기준)
        var (px1, py) = MmToCanvas(pcb1X, pcb1Y);
        var (px2, _) = MmToCanvas(pcb2X, pcb2Y);
        PcbAreaTop = py - 22;
        PcbLabelTop = py - 19;
        Pcb1Left = px1 - 18;
        Pcb1Top = py - 6;
        Pcb2Left = px2 - 18;
        Pcb2Top = py - 6;

        // 셔틀 위 캐리어 (PcbPlace 기준, 캐리어는 셔틀에 실려서 진입)
        var (sx1, sy) = MmToCanvas(place1X, place1Y);
        var (sx2, _2) = MmToCanvas(place2X, place2Y);
        ShuttleCarrier1Left = sx1 - 22;
        ShuttleCarrier1Top = sy - 17;
        ShuttleCarrier2Left = sx2 - 22;
        ShuttleCarrier2Top = sy - 17;

        // 셔틀 위 PCB (캐리어보다 약간 작게)
        ShuttlePcb1Left = sx1 - 19;
        ShuttlePcb1Top = sy - 14;
        ShuttlePcb2Left = sx2 - 19;
        ShuttlePcb2Top = sy - 14;
    }

    /// <summary>Zone 3 레시피 좌표 기반 NG 영역 위치 갱신</summary>
    public void UpdateZone3Layout(double ngPlaceX, double ngPlaceY)
    {
        var (_, ny) = MmToCanvas(ngPlaceX, ngPlaceY);
        NgAreaTop = ny - 22;
        NgLabelTop = ny - 18;
    }

    // ── 피듀셜 보정 오프셋 ──────────────────────────────────────────────
    [ObservableProperty] private double _fiducialOffsetLeft;
    [ObservableProperty] private double _fiducialOffsetTop;
    [ObservableProperty] private double _fiducialCrossH1;
    [ObservableProperty] private double _fiducialCrossH2;
    [ObservableProperty] private double _fiducialCrossV1;
    [ObservableProperty] private double _fiducialCrossV2;
    [ObservableProperty] private Visibility _fiducialOffsetVisibility = Visibility.Collapsed;

    // ── Z 게이지 ────────────────────────────────────────────────────────
    [ObservableProperty] private double _zGaugeHeight;
    [ObservableProperty] private double _zToolTop;

    // ── 현재 동작 라벨 (캔버스 오버레이) ─────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityVisibility))]
    private string _activityLabel = "";

    public Visibility ActivityVisibility =>
        string.IsNullOrEmpty(ActivityLabel) ? Visibility.Collapsed : Visibility.Visible;

    // ── 검사 결과 오버레이 (Zone 3 전용) ─────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InspectResultVisibility))]
    private string _inspectResultText = "";

    public Visibility InspectResultVisibility =>
        string.IsNullOrEmpty(InspectResultText) ? Visibility.Collapsed : Visibility.Visible;

    // ── 헤드 십자선 ─────────────────────────────────────────────────────
    [ObservableProperty] private double _crosshairLeft;
    [ObservableProperty] private double _crosshairTop;
    [ObservableProperty] private double _crosshairH1;
    [ObservableProperty] private double _crosshairH2;
    [ObservableProperty] private double _crosshairV1;
    [ObservableProperty] private double _crosshairV2;

    // ── 위치 갱신 ───────────────────────────────────────────────────────

    /// <summary>좌표 즉시 반영</summary>
    public void UpdatePosition(double xMm, double yMm, double zMm)
    {
        CoordText = $"X:{xMm:F0}  Y:{yMm:F0}  Z:{zMm:F0}";
        ApplyPosition(xMm, yMm, zMm);
    }

    /// <summary>보간된 좌표를 UI 프로퍼티에 적용</summary>
    private void ApplyPosition(double xMm, double yMm, double zMm)
    {
        var size = Math.Clamp(26.0 - (zMm / MaxZ) * 16.0, 10.0, 26.0);
        HeadSize = size;

        var (xCanvas, yCanvas) = ToCanvas(xMm, yMm);

        HeadLeft = xCanvas - size / 2;
        HeadTop = yCanvas - size / 2;
        HeadCenterLeft = xCanvas - 2;
        HeadCenterTop = yCanvas - 2;
        BridgeTop = yCanvas - 2;

        CrosshairLeft = xCanvas;
        CrosshairTop = yCanvas;
        CrosshairH1 = Math.Max(CanvasXMin, xCanvas - CrosshairSpan);
        CrosshairH2 = Math.Min(CanvasXMax, xCanvas + CrosshairSpan);
        CrosshairV1 = Math.Max(CanvasYMin, yCanvas - CrosshairSpan);
        CrosshairV2 = Math.Min(CanvasYMax, yCanvas + CrosshairSpan);

        ZRatio = Math.Clamp(zMm / MaxZ, 0, 1);
        ZGaugeHeight = ZRatio * GaugeTrack;
        ZToolTop = WorkY0 + ZGaugeHeight;
    }
}
