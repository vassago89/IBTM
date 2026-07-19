using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace IBTM.Presentation.ViewModels;

public partial class ProcessViewModel : ObservableObject, IDisposable
{
    private readonly ProcessOrchestrator _orchestrator;
    private readonly ProcessEventHub _events;
    private readonly DispatcherTimer _uptimeTimer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _targetTorque = 15.0;

    public ProcessPresentationState State { get; }

    public ProcessViewModel(ProcessOrchestrator orchestrator, ProcessEventHub events)
    {
        _orchestrator = orchestrator;
        _events = events;
        State = new ProcessPresentationState(
            orchestrator.CurrentRecipe,
            orchestrator.NgStackCapacity);
        SubscribeEvents();

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += OnUptimeTimerTick;
        _uptimeTimer.Start();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsRunning = true;
        try
        {
            _orchestrator.CurrentRecipe.BoltPoints.ForEach(
                bolt => bolt.TargetTorqueNm = TargetTorque);
            await _orchestrator.StartAsync();
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _orchestrator.Stop();

    private bool CanStop() => IsRunning;

    [RelayCommand]
    private void EStop() => _orchestrator.EmergencyStop();

    [RelayCommand]
    private void ToggleLanguage() => Loc.Instance.ToggleLanguage();

    [RelayCommand]
    private void ResetNgStack()
    {
        _orchestrator.ResetNgStack();
        State.ResetNgStack();
    }

    public void Dispose()
    {
        _uptimeTimer.Stop();
        _uptimeTimer.Tick -= OnUptimeTimerTick;
        _events.StageChanged -= OnStageChanged;
        _events.StatsUpdated -= OnStatsUpdated;
        _events.ZonePositionChanged -= OnZonePositionChanged;
        _events.FiducialDetected -= OnFiducialDetected;
        _events.BoltCompleted -= OnBoltCompleted;
        _events.BoltProgress -= OnBoltProgress;
        _events.InspectionDone -= OnInspectionDone;
        _events.RouteDecided -= OnRouteDecided;
        _events.NgStackUpdated -= OnNgStackUpdated;
        _events.NgStackAlarm -= OnNgStackAlarm;
        _events.GripperChanged -= OnGripperChanged;
        _events.PcbPlaced -= OnPcbPlaced;
        _events.InspectionImageCaptured -= OnInspectionImageCaptured;
    }

    private void SubscribeEvents()
    {
        _events.StageChanged += OnStageChanged;
        _events.StatsUpdated += OnStatsUpdated;
        _events.ZonePositionChanged += OnZonePositionChanged;
        _events.FiducialDetected += OnFiducialDetected;
        _events.BoltCompleted += OnBoltCompleted;
        _events.BoltProgress += OnBoltProgress;
        _events.InspectionDone += OnInspectionDone;
        _events.RouteDecided += OnRouteDecided;
        _events.NgStackUpdated += OnNgStackUpdated;
        _events.NgStackAlarm += OnNgStackAlarm;
        _events.GripperChanged += OnGripperChanged;
        _events.PcbPlaced += OnPcbPlaced;
        _events.InspectionImageCaptured += OnInspectionImageCaptured;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private void OnStageChanged(object? sender, StageChangedEventArgs change) =>
        RunOnUi(() => State.ApplyStage(change, _orchestrator.CurrentRecipe));

    private void OnStatsUpdated(object? sender, ProductionStats stats) =>
        RunOnUi(() => State.ApplyStats(
            stats,
            _orchestrator.NgStackCount,
            _orchestrator.NgStackCapacity));

    private void OnZonePositionChanged(object? sender, ZonePositionEventArgs position) =>
        RunOnUi(() => State.ApplyPosition(position));

    private void OnFiducialDetected(object? sender, FiducialResult result) =>
        RunOnUi(() => State.ApplyFiducial(result, _orchestrator.CurrentRecipe));

    private void OnBoltCompleted(object? sender, BoltResult result) =>
        RunOnUi(() => State.ApplyBoltResult(result));

    private void OnBoltProgress(object? sender, BoltProgressEventArgs progress) =>
        RunOnUi(() => State.ApplyBoltProgress(progress));

    private void OnInspectionDone(object? sender, InspectionResult result) =>
        RunOnUi(() => State.ApplyInspection(result));

    private void OnRouteDecided(object? sender, InspectionResult result) =>
        RunOnUi(() => State.ApplyRoute(result));

    private void OnNgStackUpdated(object? sender, int count) =>
        RunOnUi(() => State.UpdateNgStack(
            count,
            State.NgStackAlarm,
            _orchestrator.NgStackCapacity));

    private void OnNgStackAlarm(object? sender, int count) =>
        RunOnUi(() => State.UpdateNgStack(
            count,
            alarm: true,
            _orchestrator.NgStackCapacity));

    private void OnGripperChanged(object? sender, (int Zone, bool Active) state) =>
        RunOnUi(() => State.ApplyGripper(state));

    private void OnPcbPlaced(object? sender, EventArgs eventArgs) =>
        RunOnUi(State.PcbPlaced);

    private void OnInspectionImageCaptured(object? sender, ImageSource image) =>
        RunOnUi(() => State.CaptureInspectionImage(image));

    private void OnUptimeTimerTick(object? sender, EventArgs eventArgs) =>
        State.UpdateUptime(_orchestrator.Stats.UptimeFormatted);
}
