using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Models;
using IBTM.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace IBTM.ViewModels;

// ── 라우팅 경로 서브 스테이지 아이템 ──────────────────────────────────────
public partial class RouteStageItem : ObservableObject
{
    public ProcessStage Stage { get; init; }
    public string Label { get; init; } = string.Empty;

    [ObservableProperty] private StageStatus _status = StageStatus.Idle;
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

    // ── 업스트림 라인 도착 센서 ───────────────────────────────────────────────
    [ObservableProperty] private bool _isLine1Ready;
    [ObservableProperty] private bool _isLine2Ready;

    // ── 라우팅 분기 ───────────────────────────────────────────────────────────
    [ObservableProperty] private InspectionResult _lastRoute = InspectionResult.Unknown;
    [ObservableProperty] private bool _isNgPath;
    [ObservableProperty] private bool _isGoodPath;
    [ObservableProperty] private string _lastRouteText = "──";

    // ── 축 위치 ──────────────────────────────────────────────────────────────
    [ObservableProperty] private string _transferPos = "X: ──.─── mm   Y: ──.─── mm   Z: ─.─── mm";
    [ObservableProperty] private string _fiducialPos = "X: ──.─── mm   Y: ──.─── mm   Z: ─.─── mm";

    // ── 마지막 Fiducial 결과 ──────────────────────────────────────────────────
    [ObservableProperty] private string _lastFiducialResult = string.Empty;

    // ── 설정 ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private double _targetTorque = 15.0;

    // ── 컬렉션 ───────────────────────────────────────────────────────────────
    public ObservableCollection<StageCardViewModel> Stages { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];

    // ── 라우팅 경로 서브 스테이지 ─────────────────────────────────────────────
    public ObservableCollection<RouteStageItem> NgRoutePath   { get; } = [];
    public ObservableCollection<RouteStageItem> GoodRoutePath { get; } = [];

    // ── NG 스택 슬롯 (시각화용) ────────────────────────────────────────────────
    public ObservableCollection<NgSlotViewModel> NgStackSlots { get; } = [];

    public ProcessViewModel(ProcessOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        BuildStages();
        BuildRoutePaths();
        SubscribeEvents();

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => Uptime = _orchestrator.Stats.UptimeFormatted;
        _uptimeTimer.Start();
    }

    // ── 메인 공정 스테이지 카드 ─────────────────────────────────────────────
    private void BuildStages()
    {
        Stages.Add(new StageCardViewModel
        {
            Stage = ProcessStage.LineArrivalCheck,
            Title = "라인 도착 체크",
            Subtitle = "IO Sensor",
            Icon = "⬥",
            Info1 = "대기"
        });
        Stages.Add(new StageCardViewModel
        {
            Stage = ProcessStage.FiducialForPick,
            Title = "Fiducial (픽업)",
            Subtitle = "X,Y,Z + Camera",
            Icon = "⊕",
            Info1 = "대기"
        });
        Stages.Add(new StageCardViewModel
        {
            Stage = ProcessStage.PickAndTransfer,
            Title = "픽업 → 방열판",
            Subtitle = "Transfer X,Y,Z",
            Icon = "→",
            Info1 = "대기"
        });
        Stages.Add(new StageCardViewModel
        {
            Stage = ProcessStage.ConveyorToBoltStation,
            Title = "컨베이어",
            Subtitle = "→ 볼트 스테이션",
            Icon = "⬦",
            Info1 = "대기"
        });
        Stages.Add(new StageCardViewModel
        {
            Stage = ProcessStage.FiducialForBolt,
            Title = "Fiducial (볼트)",
            Subtitle = "X,Y,Z + Camera",
            Icon = "⊕",
            Info1 = "대기"
        });
        Stages.Add(new StageCardViewModel
        {
            Stage = ProcessStage.BoltTighten,
            Title = "볼트 체결",
            Subtitle = "체결 → 검사 반복",
            Icon = "⚙",
            Info1 = "대기"
        });
        Stages.Add(new StageCardViewModel
        {
            Stage = ProcessStage.FinalVisionInspect,
            Title = "최종 검사",
            Subtitle = "NG / GOOD 판정",
            Icon = "◈",
            Info1 = "대기",
            IsLastCard = true
        });
    }

    // ── 라우팅 경로 서브 스테이지 구성 ────────────────────────────────────────
    private void BuildRoutePaths()
    {
        NgRoutePath.Add(new RouteStageItem { Stage = ProcessStage.ConveyorToNg,  Label = "Conveyor → NG" });
        NgRoutePath.Add(new RouteStageItem { Stage = ProcessStage.NgTransfer,     Label = "Transfer Y,Z" });

        GoodRoutePath.Add(new RouteStageItem { Stage = ProcessStage.ConveyorToGood, Label = "Conveyor" });
        GoodRoutePath.Add(new RouteStageItem { Stage = ProcessStage.SmemaWait,      Label = "SMEMA Wait" });
        GoodRoutePath.Add(new RouteStageItem { Stage = ProcessStage.Discharge,      Label = "Discharge" });

        // NG 스택 슬롯 초기화 (레시피 기본값 3개)
        int maxSlots = _orchestrator.CurrentRecipe.NgStackAlarmCount;
        for (int i = 0; i < maxSlots; i++)
            NgStackSlots.Add(new NgSlotViewModel());
    }

    // ── 이벤트 구독 ───────────────────────────────────────────────────────────
    private void SubscribeEvents()
    {
        _orchestrator.StageChanged             += OnStageChanged;
        _orchestrator.LogAdded                 += OnLogAdded;
        _orchestrator.StatsUpdated             += OnStatsUpdated;
        _orchestrator.TransferPositionChanged  += (_, p) =>
            Application.Current.Dispatcher.Invoke(() => TransferPos = FormatPos(p));
        _orchestrator.FiducialPositionChanged  += (_, p) =>
            Application.Current.Dispatcher.Invoke(() => FiducialPos = FormatPos(p));
        _orchestrator.FiducialDetected         += OnFiducialDetected;
        _orchestrator.BoltCompleted            += OnBoltCompleted;
        _orchestrator.BoltProgress             += OnBoltProgress;
        _orchestrator.InspectionDone           += OnInspectionDone;
        _orchestrator.RouteDecided             += OnRouteDecided;
        _orchestrator.NgStackUpdated           += OnNgStackUpdated;
        _orchestrator.NgStackAlarm             += OnNgStackAlarm;
        _orchestrator.Line1SensorChanged       += (_, v) =>
            Application.Current.Dispatcher.Invoke(() => { IsLine1Ready = v; UpdateLineArrivalCard(); });
        _orchestrator.Line2SensorChanged       += (_, v) =>
            Application.Current.Dispatcher.Invoke(() => { IsLine2Ready = v; UpdateLineArrivalCard(); });
    }

    // ── StageChanged ─────────────────────────────────────────────────────────
    private void OnStageChanged(object? sender, StageChangedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsError = e.Status == StageStatus.Error;
            StatusMessage = GetStatusMessage(e.Stage, e.Status);

            // 메인 스테이지 카드 업데이트
            UpdateMainCard(e.Stage, e.Status);

            // 라우팅 경로 서브 스테이지 업데이트
            UpdateRoutePath(e.Stage, e.Status);

            // 새 픽업 사이클 시작 시 Transfer 경로 카드 초기화
            if (e.Stage == ProcessStage.LineArrivalCheck && e.Status == StageStatus.Running)
            {
                IsLine1Ready = false;
                IsLine2Ready = false;
                var transferStages = new[]
                {
                    ProcessStage.LineArrivalCheck, ProcessStage.FiducialForPick,
                    ProcessStage.PickAndTransfer,  ProcessStage.ConveyorToBoltStation
                };
                foreach (var card in Stages.Where(c => transferStages.Contains(c.Stage)))
                {
                    if (card.Status == StageStatus.Done) card.Status = StageStatus.Idle;
                    card.Info1 = "대기";
                    card.Info2 = string.Empty;
                }
                var lineCard = FindCard(ProcessStage.LineArrivalCheck);
                if (lineCard != null)
                {
                    lineCard.Info1 = "Line 1 ⬜  Line 2 ⬜";
                    lineCard.Info2 = "도착 대기 중...";
                }
            }

            // 새 볼트 사이클 시작 시 Bolt 경로 카드 초기화
            if (e.Stage == ProcessStage.FiducialForBolt && e.Status == StageStatus.Running)
            {
                var boltStages = new[]
                {
                    ProcessStage.FiducialForBolt, ProcessStage.BoltTighten, ProcessStage.FinalVisionInspect
                };
                foreach (var card in Stages.Where(c => boltStages.Contains(c.Stage)))
                {
                    if (card.Status == StageStatus.Done) card.Status = StageStatus.Idle;
                }
                // 라우팅 경로도 초기화
                foreach (var item in NgRoutePath.Concat(GoodRoutePath))
                    item.Status = StageStatus.Idle;
                NgStackAlarm = false;
                LastRoute = InspectionResult.Unknown;
                IsNgPath = false;
                IsGoodPath = false;
                LastRouteText = "──";
            }

            // 전체 Idle 복귀
            if (e.Stage == ProcessStage.Idle && e.Status == StageStatus.Idle)
            {
                foreach (var c in Stages) c.Status = StageStatus.Idle;
                foreach (var item in NgRoutePath.Concat(GoodRoutePath))
                    item.Status = StageStatus.Idle;
            }
        });
    }

    private void UpdateMainCard(ProcessStage stage, StageStatus status)
    {
        // 라우팅 스테이지는 라우팅 경로 아이템이 처리하므로 메인 카드에서 제외
        var routingStages = new[]
        {
            ProcessStage.ConveyorToNg, ProcessStage.NgTransfer,
            ProcessStage.ConveyorToGood, ProcessStage.SmemaWait, ProcessStage.Discharge
        };
        if (routingStages.Contains(stage)) return;

        foreach (var card in Stages)
        {
            if (IsCardForStage(card.Stage, stage))
                card.Status = status;
        }
    }

    private void UpdateRoutePath(ProcessStage stage, StageStatus status)
    {
        var ngItem   = NgRoutePath.FirstOrDefault(r => r.Stage == stage);
        var goodItem = GoodRoutePath.FirstOrDefault(r => r.Stage == stage);
        if (ngItem   != null) ngItem.Status   = status;
        if (goodItem != null) goodItem.Status = status;
    }

    // ── RouteDecided ─────────────────────────────────────────────────────────
    private void OnRouteDecided(object? sender, InspectionResult result)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LastRoute = result;
            IsNgPath   = result == InspectionResult.Ng;
            IsGoodPath  = result == InspectionResult.Good;
            LastRouteText = result switch
            {
                InspectionResult.Good => "GOOD",
                InspectionResult.Ng   => "NG",
                _                     => "──"
            };
        });
    }

    // ── NgStackUpdated ───────────────────────────────────────────────────────
    private void OnNgStackUpdated(object? sender, int count)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            NgStackCount = count;
            UpdateNgStackSlots(count);
        });
    }

    private void UpdateNgStackSlots(int count)
    {
        int max = _orchestrator.CurrentRecipe.NgStackAlarmCount;
        // 슬롯 수 조정
        while (NgStackSlots.Count < max) NgStackSlots.Add(new NgSlotViewModel());
        while (NgStackSlots.Count > max) NgStackSlots.RemoveAt(NgStackSlots.Count - 1);
        // 채움 상태 갱신
        for (int i = 0; i < NgStackSlots.Count; i++)
            NgStackSlots[i].IsFilled = i < count;
    }

    // ── 기타 이벤트 핸들러 ────────────────────────────────────────────────────
    private void OnFiducialDetected(object? sender, FiducialResult r)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var stage = _orchestrator.CurrentStage;
            var cardStage = stage == ProcessStage.FiducialForPick
                ? ProcessStage.FiducialForPick
                : ProcessStage.FiducialForBolt;

            var card = FindCard(cardStage);
            if (card == null) return;

            if (r.Found)
            {
                card.Info1 = $"dX: {r.OffsetX:+0.000;-0.000} mm";
                card.Info2 = $"dY: {r.OffsetY:+0.000;-0.000} mm  ({r.Confidence:P0})";
                LastFiducialResult = $"dX:{r.OffsetX:+0.000;-0.000}  dY:{r.OffsetY:+0.000;-0.000}";
            }
            else
            {
                card.Info1 = "검출 실패";
                card.Info2 = string.Empty;
                LastFiducialResult = "검출 실패";
            }
        });
    }

    private void OnBoltProgress(object? sender, BoltProgressEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            BoltProgress = $"{e.BoltName}  {e.Current}/{e.Total}";
            var card = FindCard(ProcessStage.BoltTighten);
            if (card != null) card.Info1 = $"볼트 {e.Current}/{e.Total}  [{e.BoltName}]";
        });
    }

    private void OnBoltCompleted(object? sender, BoltResult r)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var card = FindCard(ProcessStage.BoltTighten);
            if (card == null) return;
            card.Info2 = $"토크: {r.Torque:F2} Nm  {r.Message}";
            if (!r.Success) card.Status = StageStatus.Warning;
        });
    }

    private void OnInspectionDone(object? sender, InspectionResult result)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var card = FindCard(ProcessStage.FinalVisionInspect);
            if (card == null) return;
            card.Info1 = result switch
            {
                InspectionResult.Good => "▶ GOOD ◀",
                InspectionResult.Ng   => "▶  NG  ◀",
                _                     => "UNKNOWN"
            };
            if (result == InspectionResult.Ng) card.Status = StageStatus.Warning;
        });
    }

    private void OnNgStackAlarm(object? sender, int count)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            NgStackCount = count;
            NgStackAlarm = true;
            UpdateNgStackSlots(count);
        });
    }

    private void OnLogAdded(object? sender, LogEntry entry)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Logs.Insert(0, entry);
            if (Logs.Count > 500) Logs.RemoveAt(Logs.Count - 1);
        });
    }

    private void OnStatsUpdated(object? sender, ProductionStats stats)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            TotalCount    = stats.TotalCount;
            GoodCount     = stats.GoodCount;
            NgCount       = stats.NgCount;
            NgRate        = stats.NgRate;
            LastCycleTime = stats.LastCycleTimeSeconds;
            AvgCycleTime  = stats.AverageCycleTimeSeconds;
            NgStackCount  = _orchestrator.NgStackCount;
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
        NgStackCount = 0;
        NgStackAlarm = false;
        UpdateNgStackSlots(0);
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────
    private static string GetStatusMessage(ProcessStage stage, StageStatus status)
    {
        if (status == StageStatus.Error) return $"오류 발생! [{stage}]";
        return stage switch
        {
            ProcessStage.LineArrivalCheck      => "라인 PCB 도착 체크 중...",
            ProcessStage.FiducialForPick       => "픽업용 Fiducial 검출 중 (X,Y,Z)...",
            ProcessStage.PickAndTransfer       => "PCB 픽업 → 방열판 Transfer 중...",
            ProcessStage.ConveyorToBoltStation => "컨베이어 → 볼트 체결 스테이션...",
            ProcessStage.FiducialForBolt       => "볼트용 Fiducial 검출 중 (X,Y,Z)...",
            ProcessStage.BoltTighten           => "볼트 체결 중 (X,Y,Z)...",
            ProcessStage.BoltVisionInspect     => "체결 위치 비전 검사 중...",
            ProcessStage.FinalVisionInspect    => "최종 비전 검사 중...",
            ProcessStage.ConveyorToNg          => "NG → 컨베이어 이동...",
            ProcessStage.NgTransfer            => "NG Transfer Y,Z → 적재...",
            ProcessStage.ConveyorToGood        => "GOOD → 컨베이어 이동...",
            ProcessStage.SmemaWait             => "SMEMA 신호 대기 중...",
            ProcessStage.Discharge             => "배출 중...",
            ProcessStage.Complete              => "사이클 완료",
            _                                  => "대기 중"
        };
    }

    private void UpdateLineArrivalCard()
    {
        var card = FindCard(ProcessStage.LineArrivalCheck);
        if (card == null) return;
        string l1 = IsLine1Ready ? "Line 1 ✔" : "Line 1 ⬜";
        string l2 = IsLine2Ready ? "Line 2 ✔" : "Line 2 ⬜";
        card.Info1 = $"{l1}   {l2}";
        card.Info2 = (IsLine1Ready && IsLine2Ready) ? "2개 라인 준비 완료" : "도착 대기 중...";
    }

    private static string FormatPos((double X, double Y, double Z) p)
        => $"X: {p.X,8:F3} mm   Y: {p.Y,8:F3} mm   Z: {p.Z,6:F3} mm";

    private StageCardViewModel? FindCard(ProcessStage stage)
        => Stages.FirstOrDefault(s => s.Stage == stage);

    private static bool IsCardForStage(ProcessStage card, ProcessStage current)
    {
        return card switch
        {
            ProcessStage.LineArrivalCheck      => current == ProcessStage.LineArrivalCheck,
            ProcessStage.FiducialForPick       => current == ProcessStage.FiducialForPick,
            ProcessStage.PickAndTransfer       => current == ProcessStage.PickAndTransfer,
            ProcessStage.ConveyorToBoltStation => current == ProcessStage.ConveyorToBoltStation,
            ProcessStage.FiducialForBolt       => current == ProcessStage.FiducialForBolt,
            ProcessStage.BoltTighten           => current is ProcessStage.BoltTighten
                                                           or ProcessStage.BoltVisionInspect,
            ProcessStage.FinalVisionInspect    => current == ProcessStage.FinalVisionInspect,
            _ => false
        };
    }
}
