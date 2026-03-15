using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Models;
using IBTM.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace IBTM.ViewModels;

// ── 구간별 2D 레이아웃 상태 ──────────────────────────────────────────────────
public partial class ZoneVisualState : ObservableObject
{
    private readonly double _canvasW, _canvasH;
    private const double MaxCoord = 200.0;
    private const double MaxZ = 30.0;

    public ZoneVisualState(double canvasW, double canvasH)
    {
        _canvasW = canvasW; _canvasH = canvasH;
        HeadLeft = canvasW / 2 - 6;
        HeadTop = canvasH / 2 - 6;
    }

    // 헤드 위치 (Canvas 좌표)
    [ObservableProperty] private double _headLeft;
    [ObservableProperty] private double _headTop;
    [ObservableProperty] private double _headSize = 12;
    [ObservableProperty] private double _zRatio;
    [ObservableProperty] private string _coordText = "";

    // 갠트리 브릿지 (X축 레일, Y축 따라 이동)
    [ObservableProperty] private double _bridgeTop;

    // 셔틀 상태
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiftIndicatorVisibility))]
    private bool _shuttlePresent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiftIndicatorVisibility))]
    [NotifyPropertyChangedFor(nameof(ShuttleStrokeThickness))]
    private bool _isLifted;

    /// <summary>리프트 상태 표시 (▲) Visibility</summary>
    public Visibility LiftIndicatorVisibility =>
        ShuttlePresent && IsLifted ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>리프트 시 셔틀 테두리 두꺼워짐</summary>
    public double ShuttleStrokeThickness => IsLifted ? 2.5 : 1.0;

    // Z 게이지 (사이드뷰, 위→아래 채움)
    [ObservableProperty] private double _zGaugeHeight;
    [ObservableProperty] private double _zToolTop;

    public void UpdatePosition(double xMm, double yMm, double zMm)
    {
        var size = Math.Clamp(10.0 + (zMm / MaxZ) * 16.0, 10.0, 26.0);
        HeadSize = size;

        var xNorm = Math.Clamp(xMm / MaxCoord, 0, 1);
        var yNorm = Math.Clamp(yMm / MaxCoord, 0, 1);

        // 헤드 XY (갠트리 프레임 내 작업 영역: x 12~198, y 8~92)
        var xCanvas = 12 + xNorm * 186;
        var yCanvas = 10 + yNorm * 160;

        HeadLeft = xCanvas - size / 2;
        HeadTop = yCanvas - size / 2;
        BridgeTop = yCanvas - 2;   // 4px 브릿지 중심

        ZRatio = Math.Clamp(zMm / MaxZ, 0, 1);
        ZGaugeHeight = ZRatio * 156.0; // 156px 게이지 트랙
        ZToolTop = 10 + ZGaugeHeight;  // 툴 암 위치 (게이지 상단 + 채움)
        CoordText = $"X:{xMm:F0}  Y:{yMm:F0}  Z:{zMm:F0}";
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
    [ObservableProperty] private int _ngStackCount;
    [ObservableProperty] private bool _ngStackAlarm;

    // ── 라우팅 분기 ───────────────────────────────────────────────────────────
    [ObservableProperty] private InspectionResult _lastRoute = InspectionResult.Unknown;
    [ObservableProperty] private bool _isNgPath;
    [ObservableProperty] private bool _isGoodPath;
    [ObservableProperty] private string _lastRouteText = "──";

    // ── 마지막 Fiducial 결과 ──────────────────────────────────────────────────
    [ObservableProperty] private string _lastFiducialResult = string.Empty;

    // ── 설정 ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private double _targetTorque = 15.0;

    // ── 2D 설비 레이아웃 ──────────────────────────────────────────────────────
    //   각 Zone의 Gantry Canvas: 240×100 (로컬 좌표, Viewbox로 자동 스케일)
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
        SubscribeEvents();

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => Uptime = _orchestrator.Stats.UptimeFormatted;
        _uptimeTimer.Start();
    }

    // ── 구간별 스테이지 카드 ──────────────────────────────────────────────────
    private void BuildStages()
    {
        Zone1Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone1_WaitShuttle, Title = "셔틀 대기", Subtitle = "셔틀 + 앞장비 센서", Icon = "⬥" });
        Zone1Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone1_StopAlignLift, Title = "정렬/리프트", Subtitle = "IO 제어", Icon = "⬦" });
        Zone1Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone1_PickPlace, Title = "PCB 픽업", Subtitle = "2개 순차", Icon = "→" });
        Zone1Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone1_Release, Title = "릴리즈", Subtitle = "리프트↓", Icon = "↓", IsLastCard = true });

        Zone2Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone2_WaitShuttle, Title = "셔틀 대기", Subtitle = "센서 감지", Icon = "⬥" });
        Zone2Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone2_StopAlignLift, Title = "정렬/리프트", Subtitle = "IO 제어", Icon = "⬦" });
        Zone2Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone2_Fiducial, Title = "Fiducial", Subtitle = "XYZ+Camera", Icon = "⊕" });
        Zone2Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone2_BoltTighten, Title = "볼트 체결", Subtitle = "Shoot→Tighten", Icon = "⚙" });
        Zone2Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone2_Release, Title = "릴리즈", Subtitle = "리프트↓", Icon = "↓", IsLastCard = true });

        Zone3Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone3_WaitShuttle, Title = "셔틀 대기", Subtitle = "센서 감지", Icon = "⬥" });
        Zone3Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone3_StopAlignLift, Title = "정렬/리프트", Subtitle = "IO 제어", Icon = "⬦" });
        Zone3Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone3_Inspect, Title = "카메라 검사", Subtitle = "볼트 유무", Icon = "◈" });
        Zone3Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone3_NgTransfer, Title = "NG 적재", Subtitle = "max 3", Icon = "✕" });
        Zone3Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone3_SmemaWait, Title = "SMEMA", Subtitle = "뒤 설비", Icon = "◇" });
        Zone3Stages.Add(new StageCardViewModel { Stage = ProcessStage.Zone3_Discharge, Title = "배출", Subtitle = "컨베이어", Icon = "▶", IsLastCard = true });
    }

    private void BuildNgSlots()
    {
        int maxSlots = _orchestrator.CurrentRecipe.NgStackMaxCount;
        for (int i = 0; i < maxSlots; i++)
            NgStackSlots.Add(new NgSlotViewModel());
    }

    // ── 이벤트 구독 ───────────────────────────────────────────────────────────
    private void SubscribeEvents()
    {
        _orchestrator.StageChanged += OnStageChanged;
        _orchestrator.LogAdded += OnLogAdded;
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
        });
    }

    private void UpdateZoneCard(ProcessStage stage, StageStatus status)
    {
        var allStages = Zone1Stages.Concat(Zone2Stages).Concat(Zone3Stages);
        var card = allStages.FirstOrDefault(c => c.Stage == stage);
        if (card != null) card.Status = status;
    }

    private void UpdateZoneVisualState(ProcessStage stage, StageStatus status)
    {
        // 구간 1 셔틀 상태
        if (stage == ProcessStage.Zone1_StopAlignLift && status == StageStatus.Running)
            Zone1Visual.ShuttlePresent = true;
        if (stage == ProcessStage.Zone1_StopAlignLift && status == StageStatus.Done)
            Zone1Visual.IsLifted = true;
        if (stage == ProcessStage.Zone1_Release && status == StageStatus.Running)
            Zone1Visual.IsLifted = false;
        if (stage == ProcessStage.Zone1_Release && status == StageStatus.Done)
            Zone1Visual.ShuttlePresent = false;

        // 구간 2 셔틀 상태
        if (stage == ProcessStage.Zone2_StopAlignLift && status == StageStatus.Running)
            Zone2Visual.ShuttlePresent = true;
        if (stage == ProcessStage.Zone2_StopAlignLift && status == StageStatus.Done)
            Zone2Visual.IsLifted = true;
        if (stage == ProcessStage.Zone2_Release && status == StageStatus.Running)
            Zone2Visual.IsLifted = false;
        if (stage == ProcessStage.Zone2_Release && status == StageStatus.Done)
            Zone2Visual.ShuttlePresent = false;

        // 구간 3 셔틀 상태
        if (stage == ProcessStage.Zone3_StopAlignLift && status == StageStatus.Running)
            Zone3Visual.ShuttlePresent = true;
        if (stage == ProcessStage.Zone3_StopAlignLift && status == StageStatus.Done)
            Zone3Visual.IsLifted = true;
        if (stage == ProcessStage.Zone3_Release && status == StageStatus.Running)
            Zone3Visual.IsLifted = false;
        if (stage == ProcessStage.Zone3_Release && status == StageStatus.Done)
            Zone3Visual.ShuttlePresent = false;
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
            }
            else { card.Info1 = "검출 실패"; card.Info2 = ""; LastFiducialResult = "검출 실패"; }
        });
    }

    private void OnBoltProgress(object? sender, BoltProgressEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            BoltProgress = $"{e.BoltName}  {e.Current}/{e.Total}";
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

    private void OnLogAdded(object? sender, LogEntry entry) { }

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
