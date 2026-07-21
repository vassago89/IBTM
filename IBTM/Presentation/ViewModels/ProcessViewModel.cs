using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IBTM.Presentation.ViewModels;

public partial class ProcessViewModel : ObservableObject, IDisposable
{
    private readonly ProcessOrchestrator _orchestrator;
    private readonly ProcessEventHub _events;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _targetTorque;

    public ProcessPresentationState State { get; }

    public ProcessViewModel(
        ProcessOrchestrator orchestrator,
        ProcessEventHub events,
        BoltFasteningOptions boltOptions)
    {
        _orchestrator = orchestrator;
        _events = events;
        TargetTorque = boltOptions.DefaultTorqueNm;
        State = new ProcessPresentationState(
            orchestrator.CurrentRecipe,
            orchestrator.NgStackCapacity);
        SubscribeEvents();
        _orchestrator.RecipeChanged += OnRecipeChanged;
    }

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
    }

    public void Dispose()
    {
        _events.StageChanged -= OnStageChanged;
        _events.StatsUpdated -= OnStatsUpdated;
        _events.ZonePositionChanged -= OnZonePositionChanged;
        _events.FiducialDetected -= OnFiducialDetected;
        _events.BoltCompleted -= OnBoltCompleted;
        _events.BoltProgress -= OnBoltProgress;
        _events.InspectionCompleted -= OnInspectionCompleted;
        _events.NgStackChanged -= OnNgStackChanged;
        _events.GripperChanged -= OnGripperChanged;
        _events.PcbPlaced -= OnPcbPlaced;
        _orchestrator.RecipeChanged -= OnRecipeChanged;
    }

    private void SubscribeEvents()
    {
        _events.StageChanged += OnStageChanged;
        _events.StatsUpdated += OnStatsUpdated;
        _events.ZonePositionChanged += OnZonePositionChanged;
        _events.FiducialDetected += OnFiducialDetected;
        _events.BoltCompleted += OnBoltCompleted;
        _events.BoltProgress += OnBoltProgress;
        _events.InspectionCompleted += OnInspectionCompleted;
        _events.NgStackChanged += OnNgStackChanged;
        _events.GripperChanged += OnGripperChanged;
        _events.PcbPlaced += OnPcbPlaced;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
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

    private void OnRecipeChanged(object? sender, Recipe recipe) =>
        RunOnUi(() => State.ApplyRecipe(recipe));

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

    private void OnInspectionCompleted(object? sender, InspectionOutcome outcome) =>
        RunOnUi(() =>
        {
            State.CaptureInspectionImage(outcome.Image.ToImageSource());
            State.ApplyInspection(outcome.Result);
            State.ApplyRoute(outcome.Result);
        });

    private void OnNgStackChanged(object? sender, NgStackState state) =>
        RunOnUi(() => State.UpdateNgStack(
            state.Count,
            state.Alarm,
            _orchestrator.NgStackCapacity));

    private void OnGripperChanged(object? sender, (int Zone, bool Active) state) =>
        RunOnUi(() => State.ApplyGripper(state));

    private void OnPcbPlaced(object? sender, EventArgs eventArgs) =>
        RunOnUi(State.PcbPlaced);
}
