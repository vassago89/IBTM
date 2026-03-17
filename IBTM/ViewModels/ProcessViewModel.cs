using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Models;
using IBTM.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace IBTM.ViewModels;

// ── 볼트 마커 (캔버스 위 동적 표시) ──────────────────────────────────────────
public partial class BoltMarkerViewModel : ObservableObject
{
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

    public System.Windows.Media.Brush MarkerBrush => State switch
    {
        1  => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0x88, 0x3E)), // 체결중: 주황
        2  => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0xB9, 0x50)), // OK: 초록
        -1 => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF8, 0x51, 0x49)), // NG: 빨강
        _  => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x30, 0x28, 0x18)), // 대기: 어둠
    };

    public BoltMarkerViewModel(string name, double xMm, double yMm)
    {
        Name = name;
        var (cx, cy) = ZoneVisualState.MmToCanvas(xMm, yMm);
        CanvasLeft = cx - 3;  // 마커 중심 보정 (6px 원)
        CanvasTop = cy - 3;
    }
}

// ── 구간별 2D 레이아웃 상태 ──────────────────────────────────────────────────
public partial class ZoneVisualState : ObservableObject
{
    private const double MaxCoord = 200.0;
    private const double MaxZ = 60.0;

    // 캔버스 작업 영역 (Y축 컬럼 안쪽)
    private const double WorkX0 = 12.0, WorkXRange = 186.0;
    private const double WorkY0 = 10.0, WorkYRange = 160.0;
    private const double GaugeTrack = 156.0;

    // ── 애니메이션 보간 ────────────────────────────────────────────────────
    private const double AnimSpeed = 10.0;  // 감쇠 속도 (높을수록 빠른 수렴)
    private const double SnapThreshold = 0.3; // mm 이하 차이면 즉시 스냅

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

    // 헤드 위치 (Canvas 좌표)
    [ObservableProperty] private double _headLeft;
    [ObservableProperty] private double _headTop;
    [ObservableProperty] private double _headSize = 12;
    [ObservableProperty] private double _headCenterLeft;  // 중심점 위치
    [ObservableProperty] private double _headCenterTop;
    [ObservableProperty] private double _zRatio;
    [ObservableProperty] private string _coordText = "";

    // 갠트리 브릿지 (X축 레일, Y축 따라 이동)
    [ObservableProperty] private double _bridgeTop;

    // 그리퍼 상태
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GripperVisibility))]
    private bool _gripperActive;

    /// <summary>그리퍼 활성 표시 Visibility</summary>
    public Visibility GripperVisibility =>
        GripperActive ? Visibility.Visible : Visibility.Collapsed;

    // 셔틀 상태
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiftIndicatorVisibility))]
    private bool _shuttlePresent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiftIndicatorVisibility))]
    [NotifyPropertyChangedFor(nameof(ShuttleStrokeThickness))]
    private bool _isLifted;

    // 셔틀 위 PCB 수량 (기본 2, NG 이송 시 감소)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Pcb1Visibility))]
    [NotifyPropertyChangedFor(nameof(Pcb2Visibility))]
    private int _pcbCount = 2;

    public Visibility Pcb1Visibility => PcbCount >= 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Pcb2Visibility => PcbCount >= 2 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>리프트 상태 표시 (▲) Visibility</summary>
    public Visibility LiftIndicatorVisibility =>
        ShuttlePresent && IsLifted ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>리프트 시 셔틀 테두리 두꺼워짐</summary>
    public double ShuttleStrokeThickness => IsLifted ? 2.5 : 1.0;

    // 피듀셜 보정 오프셋 (캔버스 좌표 변환용)
    [ObservableProperty] private double _fiducialOffsetLeft;
    [ObservableProperty] private double _fiducialOffsetTop;
    [ObservableProperty] private double _fiducialCrossH1;  // 십자선 가로 X1
    [ObservableProperty] private double _fiducialCrossH2;  // 십자선 가로 X2
    [ObservableProperty] private double _fiducialCrossV1;  // 십자선 세로 Y1
    [ObservableProperty] private double _fiducialCrossV2;  // 십자선 세로 Y2
    [ObservableProperty] private Visibility _fiducialOffsetVisibility = Visibility.Collapsed;

    // Z 게이지 (사이드뷰, 위→아래 채움)
    [ObservableProperty] private double _zGaugeHeight;
    [ObservableProperty] private double _zToolTop;

    // 헤드 십자선 (디지털 트윈 — 위치 추적 보조선)
    [ObservableProperty] private double _crosshairLeft;   // 중심 X
    [ObservableProperty] private double _crosshairTop;    // 중심 Y
    [ObservableProperty] private double _crosshairH1;     // 수평선 X1
    [ObservableProperty] private double _crosshairH2;     // 수평선 X2
    [ObservableProperty] private double _crosshairV1;     // 수직선 Y1
    [ObservableProperty] private double _crosshairV2;     // 수직선 Y2

    /// <summary>목표 좌표 설정 → 부드러운 애니메이션 시작</summary>
    public void UpdatePosition(double xMm, double yMm, double zMm)
    {
        _targetX = xMm;
        _targetY = yMm;
        _targetZ = zMm;

        // 좌표 텍스트는 목표값 즉시 표시
        CoordText = $"X:{xMm:F0}  Y:{yMm:F0}  Z:{zMm:F0}";

        // 첫 호출이면 즉시 스냅 (초기 위치)
        if (!_initialized)
        {
            _initialized = true;
            _currentX = xMm; _currentY = yMm; _currentZ = zMm;
            ApplyPosition(_currentX, _currentY, _currentZ);
            return;
        }

        // 아주 작은 변화면 즉시 스냅
        if (Math.Abs(_currentX - xMm) < SnapThreshold &&
            Math.Abs(_currentY - yMm) < SnapThreshold &&
            Math.Abs(_currentZ - zMm) < SnapThreshold)
        {
            _currentX = xMm; _currentY = yMm; _currentZ = zMm;
            ApplyPosition(_currentX, _currentY, _currentZ);
            return;
        }

        // 애니메이션 루프 시작
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
        if (dt <= 0 || dt > 0.1) dt = 0.016; // 프레임 보정 (최대 100ms)

        // 지수 감쇠 보간: factor = 1 - e^(-speed * dt)
        var factor = 1.0 - Math.Exp(-AnimSpeed * dt);
        _currentX += (_targetX - _currentX) * factor;
        _currentY += (_targetY - _currentY) * factor;
        _currentZ += (_targetZ - _currentZ) * factor;

        ApplyPosition(_currentX, _currentY, _currentZ);

        // 목표 도달 시 애니메이션 중지
        if (Math.Abs(_currentX - _targetX) < 0.1 &&
            Math.Abs(_currentY - _targetY) < 0.1 &&
            Math.Abs(_currentZ - _targetZ) < 0.1)
        {
            _currentX = _targetX; _currentY = _targetY; _currentZ = _targetZ;
            ApplyPosition(_currentX, _currentY, _currentZ);
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }
    }

    /// <summary>보간된 좌표를 UI 프로퍼티에 적용</summary>
    private void ApplyPosition(double xMm, double yMm, double zMm)
    {
        var size = Math.Clamp(10.0 + (zMm / MaxZ) * 16.0, 10.0, 26.0);
        HeadSize = size;

        var (xCanvas, yCanvas) = ToCanvas(xMm, yMm);

        HeadLeft = xCanvas - size / 2;
        HeadTop = yCanvas - size / 2;
        HeadCenterLeft = xCanvas - 2;  // 중심점 (4px 원)
        HeadCenterTop = yCanvas - 2;
        BridgeTop = yCanvas - 2;   // 4px 브릿지 중심

        // 십자선 (헤드 중심에서 ±30px, 캔버스 범위 클램프)
        CrosshairLeft = xCanvas;
        CrosshairTop = yCanvas;
        CrosshairH1 = Math.Max(8, xCanvas - 30);
        CrosshairH2 = Math.Min(210, xCanvas + 30);
        CrosshairV1 = Math.Max(0, yCanvas - 30);
        CrosshairV2 = Math.Min(180, yCanvas + 30);

        ZRatio = Math.Clamp(zMm / MaxZ, 0, 1);
        ZGaugeHeight = ZRatio * GaugeTrack;
        ZToolTop = WorkY0 + ZGaugeHeight;
    }
}

// ── NG 스택 슬롯 (시각화용) ────────────────────────────────────────────────
public partial class NgSlotViewModel : ObservableObject
{
    [ObservableProperty] private bool _isFilled;
}

// ── ProcessViewModel ───────────────────────────────────────────────────────
public partial class ProcessViewModel : ObservableObject
{
    private readonly ProcessOrchestrator _orchestrator;
    private readonly DispatcherTimer _uptimeTimer;

    // ── 상태 ─────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _statusMessage = "대기 중";
    [ObservableProperty] private bool _isError;

    // ── 생산 통계 ─────────────────────────────────────────────────────────────
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _goodCount;
    [ObservableProperty] private int _ngCount;
    [ObservableProperty] private double _ngRate;
    [ObservableProperty] private double _lastCycleTime;
    [ObservableProperty] private double _avgCycleTime;
    [ObservableProperty] private string _uptime = "00:00:00";

    // ── 볼트 진행 ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _boltProgress = string.Empty;
    [ObservableProperty] private int _currentBoltIndex = -1;  // 0-based, -1=없음
    public ObservableCollection<BoltMarkerViewModel> BoltMarkers { get; } = [];
    [ObservableProperty] private int _ngStackCount;
    [ObservableProperty] private int _ngStackMaxCount;
    [ObservableProperty] private bool _ngStackAlarm;

    // ── 라우팅 분기 ───────────────────────────────────────────────────────────
    [ObservableProperty] private InspectionResult _lastRoute = InspectionResult.Unknown;
    [ObservableProperty] private bool _isNgPath;
    [ObservableProperty] private bool _isGoodPath;
    [ObservableProperty] private string _lastRouteText = "──";

    // ── 컨베이어 셔틀 이동 상태 ────────────────────────────────────────────────
    [ObservableProperty] private bool _shuttleTransit12;  // Zone1→2 이동 중
    [ObservableProperty] private bool _shuttleTransit23;  // Zone2→3 이동 중
    [ObservableProperty] private bool _shuttleTransitOut;  // Zone3→배출 이동 중

    // ── SMEMA / 이전장비 상태 ────────────────────────────────────────────────
    [ObservableProperty] private bool _smemaWaiting;     // SMEMA 대기 중
    [ObservableProperty] private bool _smemaReady;       // SMEMA 준비 완료
    [ObservableProperty] private bool _prevEquipReady;   // 이전장비 PCB 준비

    // ── 존별 타이밍 ──────────────────────────────────────────────────────────
    [ObservableProperty] private string _zone1Timing = "";
    [ObservableProperty] private string _zone2Timing = "";
    [ObservableProperty] private string _zone3Timing = "";
    private readonly DateTime[] _zoneStarts = new DateTime[4]; // [1],[2],[3]

    // ── 마지막 Fiducial 결과 ──────────────────────────────────────────────────
    [ObservableProperty] private string _lastFiducialResult = string.Empty;

    // ── 설정 ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private double _targetTorque = 15.0;

    // ── 전시회 모드 (2D 레이아웃 풀스크린) ──────────────────────────────────────
    [ObservableProperty] private bool _isExhibitionMode;

    // ── 2D 설비 레이아웃 ──────────────────────────────────────────────────────
    //   각 Zone의 Gantry Canvas: 240×180 (로컬 좌표, Viewbox로 자동 스케일)
    public ZoneVisualState Zone1Visual { get; } = new(240, 180);
    public ZoneVisualState Zone2Visual { get; } = new(240, 180);
    public ZoneVisualState Zone3Visual { get; } = new(240, 180);

    // ── 컬렉션 ───────────────────────────────────────────────────────────────
    public ObservableCollection<StageCardViewModel> Zone1Stages { get; } = [];
    public ObservableCollection<StageCardViewModel> Zone2Stages { get; } = [];
    public ObservableCollection<StageCardViewModel> Zone3Stages { get; } = [];
    public ObservableCollection<NgSlotViewModel> NgStackSlots { get; } = [];

    public ProcessViewModel(ProcessOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        BuildStages();
        BuildNgSlots();
        BuildBoltMarkers();
        SubscribeEvents();

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => Uptime = _orchestrator.Stats.UptimeFormatted;
        _uptimeTimer.Start();
    }

    // ── 구간별 스테이지 카드 ──────────────────────────────────────────────────
    private void BuildStages()
    {
        Zone1Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone1_WaitShuttle, Title = "셔틀 대기", Subtitle = "센서 감지", Icon = "1" });
        Zone1Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone1_StopAlignLift, Title = "정렬·리프트", Subtitle = "STP·ALN·LIFT", Icon = "2" });
        Zone1Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone1_PickPlace, Title = "PCB 픽업", Subtitle = "2개 순차 배치", Icon = "3" });
        Zone1Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone1_Release, Title = "릴리즈", Subtitle = "리프트 다운", Icon = "4", IsLastCard = true });

        Zone2Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone2_WaitShuttle, Title = "셔틀 대기", Subtitle = "센서 감지", Icon = "1" });
        Zone2Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone2_StopAlignLift, Title = "정렬·리프트", Subtitle = "STP·ALN·LIFT", Icon = "2" });
        Zone2Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone2_Fiducial, Title = "Fiducial", Subtitle = "보정 검출", Icon = "3" });
        Zone2Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone2_BoltTighten, Title = "볼트 체결", Subtitle = "N회 반복", Icon = "4" });
        Zone2Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone2_Release, Title = "릴리즈", Subtitle = "리프트 다운", Icon = "5", IsLastCard = true });

        Zone3Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone3_WaitShuttle, Title = "셔틀 대기", Subtitle = "센서 감지", Icon = "1" });
        Zone3Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone3_StopAlignLift, Title = "정렬·리프트", Subtitle = "STP·ALN·LIFT", Icon = "2" });
        Zone3Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone3_Inspect, Title = "검사", Subtitle = "볼트 유무 확인", Icon = "3" });
        Zone3Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone3_NgTransfer, Title = "NG 적재", Subtitle = "뒤쪽 이송", Icon = "4" });
        Zone3Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone3_SmemaWait, Title = "SMEMA", Subtitle = "뒤 설비 대기", Icon = "5" });
        Zone3Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone3_Discharge, Title = "배출", Subtitle = "컨베이어 OUT", Icon = "6", IsLastCard = true });
    }

    private void BuildBoltMarkers()
    {
        BoltMarkers.Clear();
        foreach (var bp in _orchestrator.CurrentRecipe.BoltPoints)
            BoltMarkers.Add(new BoltMarkerViewModel(bp.Name, bp.X, bp.Y));
    }

    private void BuildNgSlots()
    {
        int maxSlots = _orchestrator.CurrentRecipe.NgStackMaxCount;
        NgStackMaxCount = maxSlots;
        for (int i = 0; i < maxSlots; i++)
            NgStackSlots.Add(new NgSlotViewModel());
    }

    // ── 이벤트 구독 ───────────────────────────────────────────────────────────
    private void SubscribeEvents()
    {
        _orchestrator.StageChanged += OnStageChanged;
        _orchestrator.StatsUpdated += OnStatsUpdated;
        _orchestrator.Zone1PositionChanged += (_, p) =>
            Application.Current.Dispatcher.Invoke(() => Zone1Visual.UpdatePosition(p.X, p.Y, p.Z));
        _orchestrator.Zone2PositionChanged += (_, p) =>
            Application.Current.Dispatcher.Invoke(() => Zone2Visual.UpdatePosition(p.X, p.Y, p.Z));
        _orchestrator.Zone3PositionChanged += (_, p) =>
            Application.Current.Dispatcher.Invoke(() => Zone3Visual.UpdatePosition(p.X, p.Y, p.Z));
        _orchestrator.FiducialDetected += OnFiducialDetected;
        _orchestrator.BoltCompleted += OnBoltCompleted;
        _orchestrator.BoltProgress += OnBoltProgress;
        _orchestrator.InspectionDone += OnInspectionDone;
        _orchestrator.RouteDecided += OnRouteDecided;
        _orchestrator.NgStackUpdated += OnNgStackUpdated;
        _orchestrator.NgStackAlarm += OnNgStackAlarm;
        _orchestrator.GripperChanged += OnGripperChanged;
    }

    // ── StageChanged ─────────────────────────────────────────────────────────
    private void OnStageChanged(object? sender, StageChangedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsError = e.Status == StageStatus.Error;
            StatusMessage = GetStatusMessage(e.Stage, e.Status);
            UpdateZoneCard(e.Stage, e.Status);
            UpdateZoneVisualState(e.Stage, e.Status);
            UpdateZoneTiming(e.Stage, e.Status);
        });
    }

    private void UpdateZoneCard(ProcessStage stage, StageStatus status)
    {
        // 새 사이클 시작 시 해당 Zone 카드 전체 리셋
        if (stage == ProcessStage.Zone1_WaitShuttle && status == StageStatus.Running)
            foreach (var c in Zone1Stages) c.Status = StageStatus.Idle;
        if (stage == ProcessStage.Zone2_WaitShuttle && status == StageStatus.Running)
        {
            foreach (var c in Zone2Stages) c.Status = StageStatus.Idle;
            Zone2Visual.FiducialOffsetVisibility = Visibility.Collapsed;
            LastFiducialResult = string.Empty;
            CurrentBoltIndex = -1;
            BoltProgress = string.Empty;
            BuildBoltMarkers();
        }
        if (stage == ProcessStage.Zone3_WaitShuttle && status == StageStatus.Running)
        {
            foreach (var c in Zone3Stages) c.Status = StageStatus.Idle;
            LastRoute = InspectionResult.Unknown;
            IsNgPath = false;
            IsGoodPath = false;
            LastRouteText = "──";
        }

        var allStages = Zone1Stages.Concat(Zone2Stages).Concat(Zone3Stages);
        var card = allStages.FirstOrDefault(c => c.Stage == stage);
        if (card != null) card.Status = status;
    }

    private void UpdateZoneVisualState(ProcessStage stage, StageStatus status)
    {
        // 이전장비 상태
        if (stage == ProcessStage.Zone1_WaitShuttle && status == StageStatus.Running)
            PrevEquipReady = false;
        if (stage == ProcessStage.Zone1_StopAlignLift && status == StageStatus.Running)
            PrevEquipReady = true;  // 센서 감지 → 준비 완료

        // 구간 1 셔틀 상태
        if (stage == ProcessStage.Zone1_StopAlignLift && status == StageStatus.Running)
        {
            Zone1Visual.ShuttlePresent = true;
            Zone1Visual.PcbCount = 0;  // 빈 셔틀 도착
        }
        if (stage == ProcessStage.Zone1_StopAlignLift && status == StageStatus.Done)
            Zone1Visual.IsLifted = true;
        if (stage == ProcessStage.Zone1_Release && status == StageStatus.Running)
            Zone1Visual.IsLifted = false;
        if (stage == ProcessStage.Zone1_Release && status == StageStatus.Done)
        {
            Zone1Visual.ShuttlePresent = false;
            ShuttleTransit12 = true;  // 구간1→2 이동 시작
        }

        // 구간 2 셔틀 상태
        if (stage == ProcessStage.Zone2_StopAlignLift && status == StageStatus.Running)
        {
            Zone2Visual.ShuttlePresent = true;
            Zone2Visual.PcbCount = 2;  // PCB 2개 탑재 상태
            ShuttleTransit12 = false;  // 구간2 도착
        }
        if (stage == ProcessStage.Zone2_StopAlignLift && status == StageStatus.Done)
            Zone2Visual.IsLifted = true;
        if (stage == ProcessStage.Zone2_Release && status == StageStatus.Running)
            Zone2Visual.IsLifted = false;
        if (stage == ProcessStage.Zone2_Release && status == StageStatus.Done)
        {
            Zone2Visual.ShuttlePresent = false;
            ShuttleTransit23 = true;  // 구간2→3 이동 시작
        }

        // 구간 3 셔틀 상태
        if (stage == ProcessStage.Zone3_StopAlignLift && status == StageStatus.Running)
        {
            Zone3Visual.ShuttlePresent = true;
            Zone3Visual.PcbCount = 2;  // 셔틀 도착 시 PCB 2개
            ShuttleTransit23 = false;  // 구간3 도착
        }
        if (stage == ProcessStage.Zone3_StopAlignLift && status == StageStatus.Done)
            Zone3Visual.IsLifted = true;
        if (stage == ProcessStage.Zone3_Release && status == StageStatus.Running)
            Zone3Visual.IsLifted = false;
        if (stage == ProcessStage.Zone3_Release && status == StageStatus.Done)
        {
            Zone3Visual.ShuttlePresent = false;
            ShuttleTransitOut = true;  // 셔틀 배출 이동 (Good/NG 공통)
        }

        // SMEMA 상태
        if (stage == ProcessStage.Zone3_SmemaWait && status == StageStatus.Running)
        { SmemaWaiting = true; SmemaReady = false; }
        if (stage == ProcessStage.Zone3_SmemaWait && status == StageStatus.Done)
        { SmemaWaiting = false; SmemaReady = true; }
        if (stage == ProcessStage.Zone3_Discharge && status == StageStatus.Running)
            SmemaReady = false;

        // 구간3 배출 완료 시 잠깐 배출 이동 표시
        // NG 이송 완료 시 PCB 1개 감소
        if (stage == ProcessStage.Zone3_NgTransfer && status == StageStatus.Done)
            Zone3Visual.PcbCount = Math.Max(0, Zone3Visual.PcbCount - 1);

        if (stage == ProcessStage.Zone3_WaitShuttle && status == StageStatus.Running)
            ShuttleTransitOut = false;
    }

    /// <summary>ProcessStage → Zone 번호 (1~3), 해당 없으면 0</summary>
    private static int GetZoneNumber(ProcessStage stage) => (int)stage / 100 switch
    {
        1 => 1, 2 => 2, 3 => 3, _ => 0
    };

    /// <summary>Zone 번호 → ZoneVisualState</summary>
    private ZoneVisualState? GetZoneVisual(int zone) => zone switch
    {
        1 => Zone1Visual, 2 => Zone2Visual, 3 => Zone3Visual, _ => null
    };

    private void SetZoneTiming(int zone, string value)
    {
        switch (zone) { case 1: Zone1Timing = value; break; case 2: Zone2Timing = value; break; case 3: Zone3Timing = value; break; }
    }

    private void UpdateZoneTiming(ProcessStage stage, StageStatus status)
    {
        int zone = GetZoneNumber(stage);
        if (zone == 0) return;

        var stageName = stage.ToString();
        if (stageName.EndsWith("StopAlignLift") && status == StageStatus.Running)
            _zoneStarts[zone] = DateTime.Now;
        if (stageName.EndsWith("Release") && status == StageStatus.Done)
            SetZoneTiming(zone, $"{(DateTime.Now - _zoneStarts[zone]).TotalSeconds:F1}s");
    }

    // ── RouteDecided ─────────────────────────────────────────────────────────
    private void OnRouteDecided(object? sender, InspectionResult result)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LastRoute = result;
            IsNgPath = result == InspectionResult.Ng;
            IsGoodPath = result == InspectionResult.Good;
            LastRouteText = result switch
            {
                InspectionResult.Good => "GOOD",
                InspectionResult.Ng => "NG",
                _ => "──"
            };

            // 분기에 따라 안 쓰는 스테이지를 Skipped 처리
            if (result == InspectionResult.Good)
            {
                var ngCard = Zone3Stages.FirstOrDefault(c => c.Stage == ProcessStage.Zone3_NgTransfer);
                if (ngCard != null) ngCard.Status = StageStatus.Skipped;
            }
            else if (result == InspectionResult.Ng)
            {
                var smemaCard = Zone3Stages.FirstOrDefault(c => c.Stage == ProcessStage.Zone3_SmemaWait);
                if (smemaCard != null) smemaCard.Status = StageStatus.Skipped;
                var dischargeCard = Zone3Stages.FirstOrDefault(c => c.Stage == ProcessStage.Zone3_Discharge);
                if (dischargeCard != null) dischargeCard.Status = StageStatus.Skipped;
            }
        });
    }

    private void OnNgStackUpdated(object? sender, int count)
    {
        Application.Current.Dispatcher.Invoke(() => { NgStackCount = count; UpdateNgStackSlots(count); });
    }

    private void UpdateNgStackSlots(int count)
    {
        int max = _orchestrator.CurrentRecipe.NgStackMaxCount;
        while (NgStackSlots.Count < max) NgStackSlots.Add(new NgSlotViewModel());
        while (NgStackSlots.Count > max) NgStackSlots.RemoveAt(NgStackSlots.Count - 1);
        for (int i = 0; i < NgStackSlots.Count; i++)
            NgStackSlots[i].IsFilled = i < count;
    }

    private void OnFiducialDetected(object? sender, FiducialResult r)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var card = Zone2Stages.FirstOrDefault(c => c.Stage == ProcessStage.Zone2_Fiducial);
            if (card == null) return;
            if (r.Found)
            {
                card.Info1 = $"dX: {r.OffsetX:+0.000;-0.000} mm";
                card.Info2 = $"dY: {r.OffsetY:+0.000;-0.000} mm  ({r.Confidence:P0})";
                LastFiducialResult = $"dX:{r.OffsetX:+0.000;-0.000}  dY:{r.OffsetY:+0.000;-0.000}";

                // 캔버스 좌표로 보정 오프셋 표시 (피듀셜 위치 + 보정값)
                var fidPos = _orchestrator.CurrentRecipe.Zone2_FiducialPos;
                var (cx, cy) = Zone2Visual.ToCanvas(fidPos.X + r.OffsetX, fidPos.Y + r.OffsetY);
                Zone2Visual.FiducialOffsetLeft = cx;
                Zone2Visual.FiducialOffsetTop = cy;
                Zone2Visual.FiducialCrossH1 = cx - 8;
                Zone2Visual.FiducialCrossH2 = cx + 8;
                Zone2Visual.FiducialCrossV1 = cy - 8;
                Zone2Visual.FiducialCrossV2 = cy + 8;
                Zone2Visual.FiducialOffsetVisibility = Visibility.Visible;
            }
            else
            {
                card.Info1 = "검출 실패"; card.Info2 = "";
                LastFiducialResult = "검출 실패";
                Zone2Visual.FiducialOffsetVisibility = Visibility.Collapsed;
            }
        });
    }

    private void OnBoltProgress(object? sender, BoltProgressEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            BoltProgress = $"{e.BoltName}  {e.Current}/{e.Total}";
            CurrentBoltIndex = e.Current - 1;  // 0-based
            // 현재 볼트 마커 하이라이트
            if (CurrentBoltIndex >= 0 && CurrentBoltIndex < BoltMarkers.Count)
                BoltMarkers[CurrentBoltIndex].State = 1; // 체결중
            var card = Zone2Stages.FirstOrDefault(c => c.Stage == ProcessStage.Zone2_BoltTighten);
            if (card != null) card.Info1 = $"볼트 {e.Current}/{e.Total}  [{e.BoltName}]";
        });
    }

    private void OnBoltCompleted(object? sender, BoltResult r)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var card = Zone2Stages.FirstOrDefault(c => c.Stage == ProcessStage.Zone2_BoltTighten);
            if (card == null) return;
            card.Info2 = $"토크: {r.Torque:F2} Nm  {r.Message}";
            if (!r.Success) card.Status = StageStatus.Warning;

            // 볼트 마커 결과 반영 (토크값 포함)
            if (CurrentBoltIndex >= 0 && CurrentBoltIndex < BoltMarkers.Count)
            {
                BoltMarkers[CurrentBoltIndex].State = r.Success ? 2 : -1;
                BoltMarkers[CurrentBoltIndex].TorqueText = $"{r.Torque:F1}";
            }
        });
    }

    private void OnInspectionDone(object? sender, InspectionResult result)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var card = Zone3Stages.FirstOrDefault(c => c.Stage == ProcessStage.Zone3_Inspect);
            if (card == null) return;
            card.Info1 = result switch
            {
                InspectionResult.Good => "GOOD",
                InspectionResult.Ng => "NG",
                _ => "?"
            };
            if (result == InspectionResult.Ng) card.Status = StageStatus.Warning;
        });
    }

    private void OnNgStackAlarm(object? sender, int count)
    {
        Application.Current.Dispatcher.Invoke(() => { NgStackCount = count; NgStackAlarm = true; UpdateNgStackSlots(count); });
    }

    private void OnGripperChanged(object? sender, (int Zone, bool Active) e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var visual = GetZoneVisual(e.Zone);
            if (visual != null) visual.GripperActive = e.Active;

            // Zone 1: 그리퍼 OFF = PCB 배치 완료 → 셔틀 PCB 증가
            if (e.Zone == 1 && !e.Active)
                Zone1Visual.PcbCount = Math.Min(2, Zone1Visual.PcbCount + 1);
        });
    }

    private void OnStatsUpdated(object? sender, ProductionStats stats)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            TotalCount = stats.TotalCount; GoodCount = stats.GoodCount;
            NgCount = stats.NgCount; NgRate = stats.NgRate;
            LastCycleTime = stats.LastCycleTimeSeconds;
            AvgCycleTime = stats.AverageCycleTimeSeconds;
            NgStackCount = _orchestrator.NgStackCount;
        });
    }

    // ── 커맨드 ───────────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsRunning = true;
        _orchestrator.CurrentRecipe.BoltPoints.ForEach(bp => bp.TargetTorqueNm = TargetTorque);
        await _orchestrator.StartAsync();
        IsRunning = false;
    }
    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _orchestrator.Stop();
    private bool CanStop() => IsRunning;

    [RelayCommand]
    private void EStop() => _orchestrator.EStop();

    [RelayCommand]
    private void ToggleExhibitionMode() => IsExhibitionMode = !IsExhibitionMode;

    [RelayCommand]
    private void ResetNgStack()
    {
        _orchestrator.ResetNgStack();
        NgStackCount = 0; NgStackAlarm = false; UpdateNgStackSlots(0);
    }

    private static string GetStatusMessage(ProcessStage stage, StageStatus status)
    {
        if (status == StageStatus.Error) return $"오류 발생! [{stage}]";
        return stage switch
        {
            ProcessStage.Zone1_WaitShuttle => "[구간1] 셔틀 대기...",
            ProcessStage.Zone1_StopAlignLift => "[구간1] 정렬/리프트...",
            ProcessStage.Zone1_PickPlace => "[구간1] PCB 픽업 중...",
            ProcessStage.Zone1_Release => "[구간1] 릴리즈...",
            ProcessStage.Zone2_WaitShuttle => "[구간2] 셔틀 대기...",
            ProcessStage.Zone2_StopAlignLift => "[구간2] 정렬/리프트...",
            ProcessStage.Zone2_Fiducial => "[구간2] Fiducial...",
            ProcessStage.Zone2_BoltTighten => "[구간2] 볼트 체결 중...",
            ProcessStage.Zone2_Release => "[구간2] 릴리즈...",
            ProcessStage.Zone3_WaitShuttle => "[구간3] 셔틀 대기...",
            ProcessStage.Zone3_StopAlignLift => "[구간3] 정렬/리프트...",
            ProcessStage.Zone3_Inspect => "[구간3] 검사 중...",
            ProcessStage.Zone3_NgTransfer => "[구간3] NG 적재...",
            ProcessStage.Zone3_SmemaWait => "[구간3] SMEMA...",
            ProcessStage.Zone3_Discharge => "[구간3] 배출...",
            ProcessStage.Zone3_Release => "[구간3] 릴리즈...",
            ProcessStage.Complete => "사이클 완료",
            _ => "대기 중"
        };
    }
}
