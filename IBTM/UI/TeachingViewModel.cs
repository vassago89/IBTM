using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public partial class TeachingViewModel : ObservableObject
{
    private readonly ILogger<TeachingViewModel> _logger;
    private readonly MachineSettings _settings;
    private readonly IAsyncRelayCommand[] _commands;
    private readonly PcbSupplier _pcbSupply;
    private readonly PcbPlacer _pcbPlacement;
    private readonly BoltFasteningStation _fasteningStation;
    private readonly MachineStore _store;
    private readonly IReadOnlyDictionary<HardwareArea, IoStatus[]> _ioGroups;
    private readonly IReadOnlyDictionary<HardwareArea, IReadOnlyDictionary<OutputIo, TeachingOutput>> _teachingOutputs;
    private CancellationTokenSource _viewCancellation;
    private readonly Dictionary<HardwareArea, TeachingIoGroup[]> _teachingIoGroups;
    private int _manualCommandRefreshQueued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInspectionSelected))]
    [NotifyPropertyChangedFor(nameof(ActiveMotionGroup))]
    [NotifyPropertyChangedFor(nameof(TeachingIoGroups))]
    [NotifyCanExecuteChangedFor(nameof(ToggleLiveViewCommand))]
    [NotifyCanExecuteChangedFor(nameof(TeachCurrentPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBoltPointCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReturnFromPickupCommand))]
    public partial HardwareArea SelectedTeachingUnit { get; set; } = HardwareArea.InspectionGantry;

    public TeachingViewModel(
        MachineSettings settings,
        PcbSupplier pcbSupply,
        PcbPlacer pcbPlacement,
        BoltFasteningStation fasteningStation,
        InspectionStation inspectionStation,
        MachineState state,
        MachineController machine,
        OperationCancellation operations,
        RecipeEditor recipeEditor,
        RecipeManager recipes,
        MachineStore store,
        IReadOnlyDictionary<HardwareArea, IoStatus[]> ioGroups,
        IReadOnlyDictionary<HardwareArea, IReadOnlyDictionary<OutputIo, TeachingOutput>> teachingOutputs,
        ILogger<TeachingViewModel> logger)
    {
        _logger = logger;
        _settings = settings;
        _liveImageGate = new();
        _viewCancellation = new();
        _teachingIoGroups = [];
        MoveModes = Enum.GetValues<TeachingMoveMode>();
        _recipeImageCancellation = new();
        FilteredPoints = [];
        TeachingUnits = [
            HardwareArea.PcbSupply,
            HardwareArea.PcbPlacementHandler,
            HardwareArea.BoltFastening,
            HardwareArea.InspectionGantry,
        ];
        FasteningHeads = Enum.GetValues<FasteningHead>();
        HeatSinkSlots = Enum.GetValues<HeatSinkSlot>();

        TeachCurrentPositionCommand = new AsyncRelayCommand(TeachCurrentPositionAsync, () => IsTeachCurrentPositionAllowed);
        MoveToPointCommand = new AsyncRelayCommand(MoveToPointAsync, () => IsMoveToPointAllowed);
        SelectPreviousPointCommand = new RelayCommand(SelectPreviousPoint, () => IsSelectPreviousPointAllowed);
        SelectNextPointCommand = new RelayCommand(SelectNextPoint, () => IsSelectNextPointAllowed);
        JogCommand = new AsyncRelayCommand<TeachingDirection>(JogAsync, IsMoveDirectionAllowed);
        StepCommand = new AsyncRelayCommand<TeachingDirection>(StepAsync, IsStepAllowed);
        JogStopCommand = new RelayCommand(JogStop);
        HomeCommand = new AsyncRelayCommand(HomeAsync, () => IsHomeAllowed);
        MoveToHorizontalZCommand = new AsyncRelayCommand(MoveToHorizontalZAsync, () => IsMoveToHorizontalZAllowed);

        State = state;
        Machine = machine;
        Operations = operations;
        _store = store;
        _ioGroups = ioGroups;
        _teachingOutputs = teachingOutputs;

        ToggleLiveViewCommand = new AsyncRelayCommand(ToggleLiveViewAsync, () => IsToggleLiveViewAllowed);
        GrabCommand = new AsyncRelayCommand(GrabAsync, () => IsGrabAllowed);
        ApplyLightCommand = new AsyncRelayCommand(ApplyLightAsync, () => IsApplyLightAllowed);
        AddBoltPointCommand = new RelayCommand(AddBoltPoint, () => IsAddBoltPointAllowed);
        RemoveBoltPointCommand = new RelayCommand(RemoveBoltPoint, () => IsRemoveBoltPointAllowed);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsSaveAllowed);
        ReturnFromPickupCommand = new AsyncRelayCommand(ReturnFromPickupAsync, () => IsReturnFromPickupAllowed);

        _commands = [
            ToggleLiveViewCommand,
            GrabCommand,
            ApplyLightCommand,
            JogCommand,
            HomeCommand,
            StepCommand,
            MoveToHorizontalZCommand,
            MoveToPointCommand,
            ReturnFromPickupCommand,
            TeachCurrentPositionCommand,
            SaveCommand,
        ];
        foreach (var command in _commands)
            command.PropertyChanged += OnCommandChanged;

        _pcbSupply = pcbSupply;
        _pcbPlacement = pcbPlacement;
        _fasteningStation = fasteningStation;
        Inspection = inspectionStation;
        RecipeEditor = recipeEditor;
        Recipes = recipes;
        LiveLightLevel = InspectionRecipe.LightLevel;
        CarrierImages = [];

        inspectionStation.FrameReady += UpdateLiveImage;
        inspectionStation.LiveViewChanged += OnLiveViewChanged;
        state.PropertyChanged += OnMachineStateChanged;
        recipes.Changed += OnRecipeChanged;
        recipeEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RecipeEditor.IsSaveAllowed) or nameof(RecipeEditor.IsBusy))
                SaveCommand.NotifyCanExecuteChanged();
            if (e.PropertyName != nameof(RecipeEditor.IsSaveAllowed))
                return;
            TeachCurrentPositionCommand.NotifyCanExecuteChanged();
            GrabCommand.NotifyCanExecuteChanged();
        };

        RefreshTeachingPoints();
        ShowRecipeImages();
    }

    public bool IsBusy => Array.Exists(_commands, static command => command.IsRunning);

    public InspectionStation Inspection { get; }

    public RecipeEditor RecipeEditor { get; }
    public RecipeManager Recipes { get; }

    public HardwareArea[] TeachingUnits { get; }

    public BoltInspectionRecipe InspectionRecipe => Recipes.Current.BoltInspection;

    public bool IsInspectionSelected => SelectedTeachingUnit == HardwareArea.InspectionGantry;

    public bool IsFasteningSelected => SelectedTeachingUnit == HardwareArea.BoltFastening;

    partial void OnSelectedTeachingUnitChanging(HardwareArea value)
    {
        if (PositionUpdatesActive)
            UnsubscribeMotionChanges();
    }

    partial void OnSelectedTeachingUnitChanged(HardwareArea value)
    {
        if (PositionUpdatesActive)
            SubscribeMotionChanges();
        CancelTeaching();

        if (Inspection.IsLiveView || ToggleLiveViewCommand.IsRunning)
            _ = RequestCameraStopAsync();

        RefreshTeachingPoints();
        ShowRecipeImages();
        OnPropertyChanged(nameof(Motion));
        OnPropertyChanged(nameof(MoveToHorizontalZLabel));
        OnPropertyChanged(nameof(BoltPointEditorVisible));
        OnPropertyChanged(nameof(IsFasteningSelected));
        NotifyManualTeachingCommands();
    }

    public void Activate()
    {
        CameraError = null;
        RecipeEditor.Refresh();
        RefreshTeachingPoints();
        if (!PositionUpdatesActive)
            SubscribeMotionChanges();
        PositionUpdatesActive = true;
        OnPropertyChanged(nameof(Motion));
        ShowRecipeImages();
        NotifyManualTeachingCommands();
    }

    public void Deactivate()
    {
        UnsubscribeMotionChanges();
        PositionUpdatesActive = false;
        // Page/application shutdown must still receive an unconfirmed device stop.
        CancelTeaching(reportDeviceFailure: false);
        _recipeImageCancellation.Cancel();
        CarrierImages = [];

        _ = RequestCameraStopAsync();
    }

    public async Task ShutdownAsync()
    {
        IAsyncRelayCommand[] commands = [.. _commands, .. OutputCommands];
        var pending = CommandShutdown.Capture(commands);
        Task deactivated;
        try
        {
            Deactivate();
            deactivated = Task.CompletedTask;
        }
        catch (Exception exception)
        {
            deactivated = Task.FromException(exception);
        }

        var commandsStopped = CommandShutdown.CancelAndWaitAsync(
            commands,
            deactivated,
            pending);
        try
        {
            await commandsStopped;
        }
        finally
        {
            Task imageUpdate;
            lock (_liveImageGate)
            {
                imageUpdate = _liveImageUpdate;
            }

            // Commands can queue image work while stopping. Capture it after they drain,
            // and preserve their failure alongside any image/camera shutdown failures.
            await CommandShutdown.WaitAsync(commandsStopped, imageUpdate, _recipeImageUpdate, _cameraStop);
        }
    }

    public ManualControlBlock ManualBlock
    {
        get
        {
            switch (true)
            {
                case true when IsTeachingEditAllowed:
                    return ManualControlBlock.None;
                case true when State.AutoMode:
                    return ManualControlBlock.AutoMode;
                default:
                    return ManualControlBlock.Busy;
            }
        }
    }

    public bool IsTeachingEditAllowed => State.SetupEditingEnabled;

    public IReadOnlyList<TeachingIoGroup> TeachingIoGroups
    {
        get
        {
            if (!_teachingIoGroups.TryGetValue(SelectedTeachingUnit, out var groups))
            {
                groups = _ioGroups[SelectedTeachingUnit].Select(
                    io =>
                        new TeachingIoGroup(
                            io,
                            _teachingOutputs[SelectedTeachingUnit],
                            Machine))
                    .ToArray();
                foreach (var row in groups.SelectMany(group => group.Outputs))
                    row.ViewCancellation = ViewCancellation;
                _teachingIoGroups.Add(SelectedTeachingUnit, groups);
            }

            return groups;
        }
    }

    private IAsyncRelayCommand[] OutputCommands
    {
        get
        {
            return _teachingIoGroups.Values.SelectMany(groups => groups)
                .SelectMany(group => group.Outputs)
                .Select(row => row.ToggleOutputCommand)
                .ToArray();
        }
    }

    private MachineController Machine { get; }
    private MachineState State { get; }
    private OperationCancellation Operations { get; }

    private bool PositionUpdatesActive { get; set; }

    private CancellationToken ViewCancellation => _viewCancellation.Token;

    private void CancelTeaching(bool reportDeviceFailure = true)
    {
        var cancellation = _viewCancellation;
        _viewCancellation = new CancellationTokenSource();
        try
        {
            cancellation.Cancel();
        }
        catch (Exception exception) when (reportDeviceFailure
            && (exception is IOException or MotionException
                || exception is AggregateException aggregate
                    && aggregate.Flatten().InnerExceptions.Any(error => error is IOException or MotionException)))
        {
            if (!State.IsError)
                State.SetError(MachineAlarm.StopFailed, exception);
            else
                _logger.LogError(exception, "Teaching STOP also failed.");
        }
        finally
        {
            cancellation.Dispose();
            foreach (var row in TeachingIoGroups.SelectMany(group => group.Outputs))
            {
                row.ViewCancellation = ViewCancellation;
                row.ToggleOutputCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void OnMachineStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        QueueManualCommandRefresh();
    }

    private void OnTeachingMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MotionStatus.Position) or nameof(AxisStatus.State))
            QueueManualCommandRefresh();
    }

    private void SubscribeMotionChanges()
    {
        Motion.PropertyChanged += OnTeachingMotionChanged;
        foreach (var axis in Motion.Axes.Values)
            axis.PropertyChanged += OnTeachingMotionChanged;
    }

    private void UnsubscribeMotionChanges()
    {
        Motion.PropertyChanged -= OnTeachingMotionChanged;
        foreach (var axis in Motion.Axes.Values)
            axis.PropertyChanged -= OnTeachingMotionChanged;
    }

    private void QueueManualCommandRefresh()
    {
        if (!PositionUpdatesActive
            || Interlocked.Exchange(ref _manualCommandRefreshQueued, 1) != 0)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(
            () =>
            {
                Interlocked.Exchange(ref _manualCommandRefreshQueued, 0);
                if (PositionUpdatesActive)
                {
                    NotifyManualTeachingCommands();
                }
            });
    }

    private void OnCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning))
            return;
        OnPropertyChanged(nameof(IsBusy));
        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        GrabCommand.NotifyCanExecuteChanged();
        ApplyLightCommand.NotifyCanExecuteChanged();
    }

    private void NotifyManualTeachingCommands()
    {
        OnPropertyChanged(nameof(HomeBlock));
        if (!State.ManualMode
            && (Inspection.IsLiveView || ToggleLiveViewCommand.IsRunning))
        {
            _ = RequestCameraStopAsync();
        }

        HomeCommand.NotifyCanExecuteChanged();
        JogCommand.NotifyCanExecuteChanged();
        StepCommand.NotifyCanExecuteChanged();
        MoveToHorizontalZCommand.NotifyCanExecuteChanged();
        foreach (var row in TeachingIoGroups.SelectMany(group => group.Outputs))
            row.ToggleOutputCommand.NotifyCanExecuteChanged();
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        MoveToPointCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ManualBlock));
        OnPropertyChanged(nameof(IsTeachingEditAllowed));
        OnPropertyChanged(nameof(MotionHint));
        SaveCommand.NotifyCanExecuteChanged();
        ReturnFromPickupCommand.NotifyCanExecuteChanged();
        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        GrabCommand.NotifyCanExecuteChanged();
        ApplyLightCommand.NotifyCanExecuteChanged();
        AddBoltPointCommand.NotifyCanExecuteChanged();
        RemoveBoltPointCommand.NotifyCanExecuteChanged();
    }
}
