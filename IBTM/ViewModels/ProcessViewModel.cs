using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Models;
using IBTM.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace IBTM.ViewModels;

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
    [ObservableProperty] private int _currentBoltIndex = -1;
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
    [ObservableProperty] private bool _shuttleTransit12;
    [ObservableProperty] private bool _shuttleTransit23;
    [ObservableProperty] private bool _shuttleTransitOut;

    // ── SMEMA / 이전장비 상태 ────────────────────────────────────────────────
    [ObservableProperty] private bool _smemaWaiting;
    [ObservableProperty] private bool _smemaReady;
    [ObservableProperty] private bool _prevEquipReady;

    // ── 존별 타이밍 ──────────────────────────────────────────────────────────
    [ObservableProperty] private string _zone1Timing = "";
    [ObservableProperty] private string _zone2Timing = "";
    [ObservableProperty] private string _zone3Timing = "";
    private readonly DateTime[] _zoneStarts = new DateTime[4]; // [1],[2],[3]

    // ── 마지막 Fiducial 결과 ──────────────────────────────────────────────────
    [ObservableProperty] private string _lastFiducialResult = string.Empty;

    // ── 설정 ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private double _targetTorque = 15.0;

    // ── 2D 설비 레이아웃 (각 Zone Canvas: 240×180) ───────────────────────────
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

    // ── UI 스레드 헬퍼 ──────────────────────────────────────────────────────
    private static void RunOnUI(Action action) =>
        Application.Current.Dispatcher.Invoke(action);

    // ── Zone 번호 ↔ 인스턴스 매핑 ────────────────────────────────────────────
    private static int GetZoneNumber(ProcessStage stage) => (int)stage / 100 switch
    {
        1 => 1, 2 => 2, 3 => 3, _ => 0
    };

    private ZoneVisualState? GetZoneVisual(int zone) => zone switch
    {
        1 => Zone1Visual, 2 => Zone2Visual, 3 => Zone3Visual, _ => null
    };

    private ObservableCollection<StageCardViewModel> GetZoneStages(int zone) => zone switch
    {
        1 => Zone1Stages, 2 => Zone2Stages, 3 => Zone3Stages,
        _ => throw new ArgumentOutOfRangeException(nameof(zone))
    };

    private void SetZoneTiming(int zone, string value)
    {
        switch (zone)
        {
            case 1: Zone1Timing = value; break;
            case 2: Zone2Timing = value; break;
            case 3: Zone3Timing = value; break;
        }
    }

    // ── 초기화 ───────────────────────────────────────────────────────────────
    private void BuildStages()
    {
        Zone1Stages.Add(new() { Stage = ProcessStage.Zone1_WaitShuttle, Title = "셔틀 대기", Subtitle = "센서 감지", Icon = "1" });
        Zone1Stages.Add(new() { Stage = ProcessStage.Zone1_StopAlignLift, Title = "정렬·리프트", Subtitle = "STP·ALN·LIFT", Icon = "2" });
        Zone1Stages.Add(new() { Stage = ProcessStage.Zone1_PickPlace, Title = "PCB 픽업", Subtitle = "2개 순차 배치", Icon = "3" });
        Zone1Stages.Add(new() { Stage = ProcessStage.Zone1_Release, Title = "릴리즈", Subtitle = "리프트 다운", Icon = "4", IsLastCard = true });

        Zone2Stages.Add(new() { Stage = ProcessStage.Zone2_WaitShuttle, Title = "셔틀 대기", Subtitle = "센서 감지", Icon = "1" });
        Zone2Stages.Add(new() { Stage = ProcessStage.Zone2_StopAlignLift, Title = "정렬·리프트", Subtitle = "STP·ALN·LIFT", Icon = "2" });
        Zone2Stages.Add(new() { Stage = ProcessStage.Zone2_Fiducial, Title = "Fiducial", Subtitle = "보정 검출", Icon = "3" });
        Zone2Stages.Add(new() { Stage = ProcessStage.Zone2_BoltTighten, Title = "볼트 체결", Subtitle = "N회 반복", Icon = "4" });
        Zone2Stages.Add(new() { Stage = ProcessStage.Zone2_Release, Title = "릴리즈", Subtitle = "리프트 다운", Icon = "5", IsLastCard = true });

        Zone3Stages.Add(new() { Stage = ProcessStage.Zone3_WaitShuttle, Title = "셔틀 대기", Subtitle = "센서 감지", Icon = "1" });
        Zone3Stages.Add(new() { Stage = ProcessStage.Zone3_StopAlignLift, Title = "정렬·리프트", Subtitle = "STP·ALN·LIFT", Icon = "2" });
        Zone3Stages.Add(new() { Stage = ProcessStage.Zone3_Inspect, Title = "검사", Subtitle = "볼트 유무 확인", Icon = "3" });
        Zone3Stages.Add(new() { Stage = ProcessStage.Zone3_NgTransfer, Title = "NG 적재", Subtitle = "뒤쪽 이송", Icon = "4" });
        Zone3Stages.Add(new() { Stage = ProcessStage.Zone3_SmemaWait, Title = "SMEMA", Subtitle = "뒤 설비 대기", Icon = "5" });
        Zone3Stages.Add(new() { Stage = ProcessStage.Zone3_Discharge, Title = "배출", Subtitle = "컨베이어 OUT", Icon = "6", IsLastCard = true });
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
        _orchestrator.ZonePositionChanged += OnZonePositionChanged;
        _orchestrator.FiducialDetected += OnFiducialDetected;
        _orchestrator.BoltCompleted += OnBoltCompleted;
        _orchestrator.BoltProgress += OnBoltProgress;
        _orchestrator.InspectionDone += OnInspectionDone;
        _orchestrator.RouteDecided += OnRouteDecided;
        _orchestrator.NgStackUpdated += OnNgStackUpdated;
        _orchestrator.NgStackAlarm += OnNgStackAlarm;
        _orchestrator.GripperChanged += OnGripperChanged;
    }

    // ── 이벤트 핸들러 ─────────────────────────────────────────────────────────

    private void OnZonePositionChanged(object? sender, ZonePositionEventArgs e) =>
        RunOnUI(() => GetZoneVisual(e.Zone)?.UpdatePosition(e.X, e.Y, e.Z));

    private void OnStageChanged(object? sender, StageChangedEventArgs e) => RunOnUI(() =>
    {
        IsError = e.Status == StageStatus.Error;
        StatusMessage = GetStatusMessage(e.Stage, e.Status);
        UpdateZoneCard(e.Stage, e.Status);
        UpdateZoneVisualState(e.Stage, e.Status);
        UpdateZoneTiming(e.Stage, e.Status);
    });

    private void OnStatsUpdated(object? sender, ProductionStats stats) => RunOnUI(() =>
    {
        TotalCount = stats.TotalCount;
        GoodCount = stats.GoodCount;
        NgCount = stats.NgCount;
        NgRate = stats.NgRate;
        LastCycleTime = stats.LastCycleTimeSeconds;
        AvgCycleTime = stats.AverageCycleTimeSeconds;
        NgStackCount = _orchestrator.NgStackCount;
    });

    private void OnRouteDecided(object? sender, InspectionResult result) => RunOnUI(() =>
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
            SetStageStatus(ProcessStage.Zone3_NgTransfer, StageStatus.Skipped);
        else if (result == InspectionResult.Ng)
        {
            SetStageStatus(ProcessStage.Zone3_SmemaWait, StageStatus.Skipped);
            SetStageStatus(ProcessStage.Zone3_Discharge, StageStatus.Skipped);
        }
    });

    private void OnFiducialDetected(object? sender, FiducialResult r) => RunOnUI(() =>
    {
        var card = FindStageCard(ProcessStage.Zone2_Fiducial);
        if (card == null) return;

        if (r.Found)
        {
            card.Info1 = $"dX: {r.OffsetX:+0.000;-0.000} mm";
            card.Info2 = $"dY: {r.OffsetY:+0.000;-0.000} mm  ({r.Confidence:P0})";
            LastFiducialResult = $"dX:{r.OffsetX:+0.000;-0.000}  dY:{r.OffsetY:+0.000;-0.000}";

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
            card.Info1 = "검출 실패";
            card.Info2 = "";
            LastFiducialResult = "검출 실패";
            Zone2Visual.FiducialOffsetVisibility = Visibility.Collapsed;
        }
    });

    private void OnBoltProgress(object? sender, BoltProgressEventArgs e) => RunOnUI(() =>
    {
        BoltProgress = $"{e.BoltName}  {e.Current}/{e.Total}";
        CurrentBoltIndex = e.Current - 1;

        if (CurrentBoltIndex >= 0 && CurrentBoltIndex < BoltMarkers.Count)
            BoltMarkers[CurrentBoltIndex].State = 1; // 체결중

        var card = FindStageCard(ProcessStage.Zone2_BoltTighten);
        if (card != null) card.Info1 = $"볼트 {e.Current}/{e.Total}  [{e.BoltName}]";
    });

    private void OnBoltCompleted(object? sender, BoltResult r) => RunOnUI(() =>
    {
        var card = FindStageCard(ProcessStage.Zone2_BoltTighten);
        if (card == null) return;

        card.Info2 = $"토크: {r.Torque:F2} Nm  {r.Message}";
        if (!r.Success) card.Status = StageStatus.Warning;

        if (CurrentBoltIndex >= 0 && CurrentBoltIndex < BoltMarkers.Count)
        {
            BoltMarkers[CurrentBoltIndex].State = r.Success ? 2 : -1;
            BoltMarkers[CurrentBoltIndex].TorqueText = $"{r.Torque:F1}";
        }
    });

    private void OnInspectionDone(object? sender, InspectionResult result) => RunOnUI(() =>
    {
        var card = FindStageCard(ProcessStage.Zone3_Inspect);
        if (card == null) return;

        card.Info1 = result switch
        {
            InspectionResult.Good => "GOOD",
            InspectionResult.Ng => "NG",
            _ => "?"
        };
        if (result == InspectionResult.Ng) card.Status = StageStatus.Warning;
    });

    private void OnNgStackUpdated(object? sender, int count) =>
        RunOnUI(() => { NgStackCount = count; SyncNgSlots(count); });

    private void OnNgStackAlarm(object? sender, int count) =>
        RunOnUI(() => { NgStackCount = count; NgStackAlarm = true; SyncNgSlots(count); });

    private void OnGripperChanged(object? sender, (int Zone, bool Active) e) => RunOnUI(() =>
    {
        var visual = GetZoneVisual(e.Zone);
        if (visual != null) visual.GripperActive = e.Active;

        // Zone 1: 그리퍼 OFF = PCB 배치 완료 → 셔틀 PCB 증가
        if (e.Zone == 1 && !e.Active)
            Zone1Visual.PcbCount = Math.Min(2, Zone1Visual.PcbCount + 1);
    });

    // ── 스테이지 카드 헬퍼 ──────────────────────────────────────────────────
    private StageCardViewModel? FindStageCard(ProcessStage stage) =>
        Zone1Stages.Concat(Zone2Stages).Concat(Zone3Stages)
            .FirstOrDefault(c => c.Stage == stage);

    private void SetStageStatus(ProcessStage stage, StageStatus status)
    {
        var card = FindStageCard(stage);
        if (card != null) card.Status = status;
    }

    // ── 카드 업데이트 ─────────────────────────────────────────────────────────
    private void UpdateZoneCard(ProcessStage stage, StageStatus status)
    {
        // 새 사이클 시작 시 Zone 카드 리셋
        if (status == StageStatus.Running)
        {
            if (stage == ProcessStage.Zone1_WaitShuttle)
                ResetZoneCards(1);
            else if (stage == ProcessStage.Zone2_WaitShuttle)
                ResetZone2Cycle();
            else if (stage == ProcessStage.Zone3_WaitShuttle)
                ResetZone3Cycle();
        }

        SetStageStatus(stage, status);
    }

    private void ResetZoneCards(int zone)
    {
        foreach (var c in GetZoneStages(zone))
            c.Status = StageStatus.Idle;
    }

    private void ResetZone2Cycle()
    {
        ResetZoneCards(2);
        Zone2Visual.FiducialOffsetVisibility = Visibility.Collapsed;
        LastFiducialResult = string.Empty;
        CurrentBoltIndex = -1;
        BoltProgress = string.Empty;
        BuildBoltMarkers();
    }

    private void ResetZone3Cycle()
    {
        ResetZoneCards(3);
        LastRoute = InspectionResult.Unknown;
        IsNgPath = false;
        IsGoodPath = false;
        LastRouteText = "──";
    }

    // ── 셔틀/리프트 상태 업데이트 ────────────────────────────────────────────
    private void UpdateZoneVisualState(ProcessStage stage, StageStatus status)
    {
        // 이전장비 상태
        if (stage == ProcessStage.Zone1_WaitShuttle && status == StageStatus.Running)
            PrevEquipReady = false;
        if (stage == ProcessStage.Zone1_StopAlignLift && status == StageStatus.Running)
            PrevEquipReady = true;

        // 구간별 셔틀/리프트 (공통 패턴)
        UpdateShuttleState(stage, status, 1, Zone1Visual, 0,
            () => ShuttleTransit12 = true, null);
        UpdateShuttleState(stage, status, 2, Zone2Visual, 2,
            () => ShuttleTransit23 = true, () => ShuttleTransit12 = false);
        UpdateShuttleState(stage, status, 3, Zone3Visual, 2,
            () => ShuttleTransitOut = true, () => ShuttleTransit23 = false);

        // SMEMA 상태
        if (stage == ProcessStage.Zone3_SmemaWait && status == StageStatus.Running)
        { SmemaWaiting = true; SmemaReady = false; }
        if (stage == ProcessStage.Zone3_SmemaWait && status == StageStatus.Done)
        { SmemaWaiting = false; SmemaReady = true; }
        if (stage == ProcessStage.Zone3_Discharge && status == StageStatus.Running)
            SmemaReady = false;

        // NG 이송 완료 시 PCB 1개 감소
        if (stage == ProcessStage.Zone3_NgTransfer && status == StageStatus.Done)
            Zone3Visual.PcbCount = Math.Max(0, Zone3Visual.PcbCount - 1);

        if (stage == ProcessStage.Zone3_WaitShuttle && status == StageStatus.Running)
            ShuttleTransitOut = false;
    }

    /// <summary>구간별 셔틀 도착/리프트/릴리즈 공통 패턴</summary>
    private static void UpdateShuttleState(
        ProcessStage stage, StageStatus status,
        int zone, ZoneVisualState visual, int arrivalPcbCount,
        Action? onReleaseDone, Action? onArrival)
    {
        var stopAlignLift = (ProcessStage)(zone * 100 + 2); // Zone*_StopAlignLift
        var release = (ProcessStage)(zone * 100 + (zone == 1 ? 4 : zone == 2 ? 5 : 7)); // Zone*_Release

        if (stage == stopAlignLift && status == StageStatus.Running)
        {
            visual.ShuttlePresent = true;
            visual.PcbCount = arrivalPcbCount;
            onArrival?.Invoke();
        }
        if (stage == stopAlignLift && status == StageStatus.Done)
            visual.IsLifted = true;
        if (stage == release && status == StageStatus.Running)
            visual.IsLifted = false;
        if (stage == release && status == StageStatus.Done)
        {
            visual.ShuttlePresent = false;
            onReleaseDone?.Invoke();
        }
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

    private void SyncNgSlots(int count)
    {
        int max = _orchestrator.CurrentRecipe.NgStackMaxCount;
        while (NgStackSlots.Count < max) NgStackSlots.Add(new NgSlotViewModel());
        while (NgStackSlots.Count > max) NgStackSlots.RemoveAt(NgStackSlots.Count - 1);
        for (int i = 0; i < NgStackSlots.Count; i++)
            NgStackSlots[i].IsFilled = i < count;
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
        NgStackCount = 0;
        NgStackAlarm = false;
        SyncNgSlots(0);
    }

    private static string GetStatusMessage(ProcessStage stage, StageStatus status)
    {
        if (status == StageStatus.Error) return $"오류 발생! [{stage}]";
        return stage switch
        {
            ProcessStage.Zone1_WaitShuttle    => "[구간1] 셔틀 대기...",
            ProcessStage.Zone1_StopAlignLift  => "[구간1] 정렬/리프트...",
            ProcessStage.Zone1_PickPlace      => "[구간1] PCB 픽업 중...",
            ProcessStage.Zone1_Release        => "[구간1] 릴리즈...",
            ProcessStage.Zone2_WaitShuttle    => "[구간2] 셔틀 대기...",
            ProcessStage.Zone2_StopAlignLift  => "[구간2] 정렬/리프트...",
            ProcessStage.Zone2_Fiducial       => "[구간2] Fiducial...",
            ProcessStage.Zone2_BoltTighten    => "[구간2] 볼트 체결 중...",
            ProcessStage.Zone2_Release        => "[구간2] 릴리즈...",
            ProcessStage.Zone3_WaitShuttle    => "[구간3] 셔틀 대기...",
            ProcessStage.Zone3_StopAlignLift  => "[구간3] 정렬/리프트...",
            ProcessStage.Zone3_Inspect        => "[구간3] 검사 중...",
            ProcessStage.Zone3_NgTransfer     => "[구간3] NG 적재...",
            ProcessStage.Zone3_SmemaWait      => "[구간3] SMEMA...",
            ProcessStage.Zone3_Discharge      => "[구간3] 배출...",
            ProcessStage.Zone3_Release        => "[구간3] 릴리즈...",
            ProcessStage.Complete             => "사이클 완료",
            _ => "대기 중"
        };
    }
}
