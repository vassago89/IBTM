using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core.Process;
using IBTM.Orchestration;
using IBTM.Presentation.Imaging;
using IBTM.Presentation.Models;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;

namespace IBTM.Presentation.ViewModels;

public partial class ProcessViewModel : ObservableObject, IDisposable
{
    private readonly ProcessOrchestrator _orchestrator;
    private readonly ProcessEvents _events;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetNgStackCommand))]
    [NotifyPropertyChangedFor(nameof(ManualControlsEnabled))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private double _targetTorque;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [NotifyPropertyChangedFor(nameof(ManualControlsEnabled))]
    private bool _isError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetNgStackCommand))]
    [NotifyPropertyChangedFor(nameof(ManualControlsEnabled))]
    private bool _ngStackAlarm;

    [ObservableProperty] private string _statusMessage = "Waiting";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _goodCount;
    [ObservableProperty] private int _ngCount;
    [ObservableProperty] private double _ngRate;
    [ObservableProperty] private double _lastCycleTime;

    [ObservableProperty] private string _pcbPlacementActivity = "Waiting";
    [ObservableProperty] private string _boltFasteningActivity = "Waiting";
    [ObservableProperty] private string _inspectionActivity = "Waiting";
    [ObservableProperty] private string _pcbPlacementPosition = "X 0.000   Y 0.000   Z 0.000";
    [ObservableProperty] private string _boltFasteningPosition = "X 0.000   Y 0.000   Z 0.000";
    [ObservableProperty] private string _inspectionPosition = "X 0.000   Y 0.000   Z 0.000";

    [ObservableProperty] private string _boltProgress = string.Empty;
    [ObservableProperty] private string _lastBoltResult = string.Empty;
    [ObservableProperty] private string _lastFiducialResult = string.Empty;

    [ObservableProperty] private int _ngStackCount;
    [ObservableProperty] private int _ngStackMaxCount;
    [ObservableProperty] private bool _isNgPath;
    [ObservableProperty] private bool _isGoodPath;
    [ObservableProperty] private string _lastRouteText = "—";
    [ObservableProperty] private ImageSource? _inspectionImage;
    [ObservableProperty] private bool _smemaWaiting;
    [ObservableProperty] private bool _smemaReady;

    public ProcessViewModel(
        ProcessOrchestrator orchestrator,
        ProcessEvents events,
        BoltFasteningOptions boltOptions)
    {
        _orchestrator = orchestrator;
        _events = events;
        TargetTorque = boltOptions.DefaultTorqueNm;
        NgStackMaxCount = orchestrator.NgStackCapacity;
        SubscribeEvents();
    }

    public bool ManualControlsEnabled => !IsRunning && !IsError && !NgStackAlarm;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsRunning = true;
        try
        {
            _orchestrator.CurrentRecipe.BoltFastening.BoltPoints.ForEach(
                bolt => bolt.TargetTorqueNm = TargetTorque);
            await _orchestrator.StartAsync();
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanStart() =>
        !IsRunning
        && !IsError
        && !NgStackAlarm
        && double.IsFinite(TargetTorque)
        && TargetTorque > 0;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _orchestrator.Stop();

    private bool CanStop() => IsRunning;

    [RelayCommand]
    private void EStop() => _orchestrator.EmergencyStop();

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset() => _orchestrator.Reset();

    private bool CanReset() => !IsRunning && IsError;

    [RelayCommand(CanExecute = nameof(CanResetNgStack))]
    private void ResetNgStack() => _orchestrator.ResetNgStack();

    private bool CanResetNgStack() => !IsRunning && NgStackAlarm;

    public void Dispose()
    {
        _events.StageChanged -= OnStageChanged;
        _events.StatsUpdated -= OnStatsUpdated;
        _events.StationPositionChanged -= OnStationPositionChanged;
        _events.FiducialDetected -= OnFiducialDetected;
        _events.BoltCompleted -= OnBoltCompleted;
        _events.BoltProgress -= OnBoltProgress;
        _events.InspectionCompleted -= OnInspectionCompleted;
        _events.NgStackChanged -= OnNgStackChanged;
    }

    private void SubscribeEvents()
    {
        _events.StageChanged += OnStageChanged;
        _events.StatsUpdated += OnStatsUpdated;
        _events.StationPositionChanged += OnStationPositionChanged;
        _events.FiducialDetected += OnFiducialDetected;
        _events.BoltCompleted += OnBoltCompleted;
        _events.BoltProgress += OnBoltProgress;
        _events.InspectionCompleted += OnInspectionCompleted;
        _events.NgStackChanged += OnNgStackChanged;
    }

    private void OnStageChanged(string stage, StageStatus status) =>
        RunOnUi(() => ApplyStage(stage, status));

    private void ApplyStage(string stage, StageStatus status)
    {
        if (status == StageStatus.Error)
        {
            IsError = true;
        }
        else if (stage == SystemStages.Idle && status == StageStatus.Idle)
        {
            IsError = false;
        }

        if (!IsError || status == StageStatus.Error || stage == SystemStages.Idle)
        {
            StatusMessage = GetStatusMessage(stage, status);
        }

        if (stage is SystemStages.Idle or SystemStages.Complete or SystemStages.Error)
        {
            return;
        }

        var definition = ProcessStageCatalog.Get(stage);
        SetActivity(definition.Station, status switch
        {
            StageStatus.Running => definition.ActivityText,
            StageStatus.Done => "Complete",
            StageStatus.Error => "Error",
            _ => "Stopped",
        });

        if (stage == InspectionStages.SendCarrierJig)
        {
            SmemaWaiting = status == StageStatus.Running;
            SmemaReady = status == StageStatus.Done;
        }
    }

    private void OnStatsUpdated(ProductionStats stats) =>
        RunOnUi(() =>
        {
            TotalCount = stats.TotalCount;
            GoodCount = stats.GoodCount;
            NgCount = stats.NgCount;
            NgRate = stats.NgRate;
            LastCycleTime = stats.LastCycleTimeSeconds;
            NgStackCount = _orchestrator.NgStackCount;
            NgStackMaxCount = _orchestrator.NgStackCapacity;
        });

    private void OnStationPositionChanged(int station, double x, double y, double z) =>
        RunOnUi(() =>
        {
            var position = $"X {x:F3}   Y {y:F3}   Z {z:F3}";
            switch (station)
            {
                case 1: PcbPlacementPosition = position; break;
                case 2: BoltFasteningPosition = position; break;
                case 3: InspectionPosition = position; break;
            }
        });

    private void OnFiducialDetected(FiducialResult result) =>
        RunOnUi(() => LastFiducialResult = result.Found
            ? $"dX {result.OffsetX:+0.000;-0.000}   dY {result.OffsetY:+0.000;-0.000}"
            : "Detection failed");

    private void OnBoltCompleted(BoltResult result) =>
        RunOnUi(() => LastBoltResult =
            $"{(result.Success ? "PASS" : "FAIL")}   {result.Torque:F1} Nm");

    private void OnBoltProgress(int current, int total, string boltName) =>
        RunOnUi(() => BoltProgress = $"{boltName}   {current}/{total}");

    private void OnInspectionCompleted(InspectionOutcome outcome) =>
        RunOnUi(() =>
        {
            InspectionImage = outcome.Image.ToImageSource();
            IsGoodPath = outcome.Result == InspectionResult.Good;
            IsNgPath = !IsGoodPath;
            LastRouteText = IsGoodPath ? "GOOD" : "NG";
        });

    private void OnNgStackChanged(int count, bool alarm) =>
        RunOnUi(() =>
        {
            NgStackCount = count;
            NgStackMaxCount = _orchestrator.NgStackCapacity;
            NgStackAlarm = alarm;
        });

    private void SetActivity(int station, string activity)
    {
        switch (station)
        {
            case 1: PcbPlacementActivity = activity; break;
            case 2: BoltFasteningActivity = activity; break;
            case 3: InspectionActivity = activity; break;
        }
    }

    private static string GetStatusMessage(string stage, StageStatus status)
    {
        if (status == StageStatus.Error)
        {
            return stage == SystemStages.Idle
                ? "Emergency stop active"
                : $"Error [{stage}]";
        }

        if (status == StageStatus.Idle && stage != SystemStages.Idle)
        {
            return "Stopped";
        }

        return stage switch
        {
            SystemStages.Idle => "Waiting",
            SystemStages.Complete => "Cycle complete",
            _ => ProcessStageCatalog.Get(stage).StatusText,
        };
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
