using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;
using System.Windows.Media;

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

    // ── 애니메이션 보간 ────────────────────────────────────────────────────
    private const double AnimSpeed = 10.0;    // 감쇠 속도 (높을수록 빠른 수렴)
    private const double SnapThreshold = 0.3; // mm 이하 차이면 즉시 스냅
    private const double ConvergeThreshold = 0.1;

    private double _targetX, _targetY, _targetZ;
    private double _currentX, _currentY, _currentZ;
    private bool _animating;
    private bool _initialized;
    private DateTime _lastFrame;

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
    [NotifyPropertyChangedFor(nameof(Pcb1Visibility))]
    [NotifyPropertyChangedFor(nameof(Pcb2Visibility))]
    private int _pcbCount = 2;

    public Visibility Pcb1Visibility => PcbCount >= 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Pcb2Visibility => PcbCount >= 2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LiftIndicatorVisibility =>
        ShuttlePresent && IsLifted ? Visibility.Visible : Visibility.Collapsed;
    public double ShuttleStrokeThickness => IsLifted ? 2.5 : 1.0;

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

    /// <summary>목표 좌표 설정 → 부드러운 애니메이션 시작</summary>
    public void UpdatePosition(double xMm, double yMm, double zMm)
    {
        _targetX = xMm;
        _targetY = yMm;
        _targetZ = zMm;

        CoordText = $"X:{xMm:F0}  Y:{yMm:F0}  Z:{zMm:F0}";

        if (!_initialized)
        {
            _initialized = true;
            SnapToTarget();
            return;
        }

        if (IsCloseEnough(_currentX, xMm, SnapThreshold) &&
            IsCloseEnough(_currentY, yMm, SnapThreshold) &&
            IsCloseEnough(_currentZ, zMm, SnapThreshold))
        {
            SnapToTarget();
            return;
        }

        if (!_animating)
        {
            _animating = true;
            _lastFrame = DateTime.Now;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        var dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (dt <= 0 || dt > 0.1) dt = 0.016;

        var factor = 1.0 - Math.Exp(-AnimSpeed * dt);
        _currentX += (_targetX - _currentX) * factor;
        _currentY += (_targetY - _currentY) * factor;
        _currentZ += (_targetZ - _currentZ) * factor;

        ApplyPosition(_currentX, _currentY, _currentZ);

        if (IsCloseEnough(_currentX, _targetX, ConvergeThreshold) &&
            IsCloseEnough(_currentY, _targetY, ConvergeThreshold) &&
            IsCloseEnough(_currentZ, _targetZ, ConvergeThreshold))
        {
            SnapToTarget();
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }
    }

    private void SnapToTarget()
    {
        _currentX = _targetX;
        _currentY = _targetY;
        _currentZ = _targetZ;
        ApplyPosition(_currentX, _currentY, _currentZ);
    }

    private static bool IsCloseEnough(double a, double b, double threshold) =>
        Math.Abs(a - b) < threshold;

    /// <summary>보간된 좌표를 UI 프로퍼티에 적용</summary>
    private void ApplyPosition(double xMm, double yMm, double zMm)
    {
        var size = Math.Clamp(10.0 + (zMm / MaxZ) * 16.0, 10.0, 26.0);
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
