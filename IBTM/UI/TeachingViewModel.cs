using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
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

public enum TeachingMoveMode
{
    [Description("Jog · hold to move")]
    Jog,
    [Description("Step · move a set distance")]
    Step,
}

public enum TeachingDirection
{
    [Description("X−")]
    XMinus,
    [Description("X+")]
    XPlus,
    [Description("Y−")]
    YMinus,
    [Description("Y+")]
    YPlus,
    [Description("Z−")]
    ZMinus,
    [Description("Z+")]
    ZPlus,
}

public enum TeachingMotionHint
{
    [Description("")]
    None,
    [Description("This unit is disabled in Settings.")]
    UnitDisabled,
    [Description("Raise the NG pickup before moving XY.")]
    RaiseNgPickup,
    [Description("Z Jog/Step is available with the handler lowered. Raise the handler before X/Y, Move to Position or Move Z to Standby Height.")]
    RaisePlacementCylinders,
    [Description("Jog/Step adjust one axis at the current height. Raise both heads before moving to a teaching position.")]
    BoltAdjustment,
    [Description("Home this unit before jogging or moving to a teaching position.")]
    HomeRequired,
    [Description("Turn on this unit's axis servos before moving.")]
    ServoOff,
    [Description("Clear this unit's axis alarm or emergency signal before moving.")]
    AxisFault,
    [Description("Motion feedback is unavailable for this unit.")]
    MotionUnavailable,
    [Description("Record Carrier Pickup (S3) X/Y before moving to a carrier.")]
    NgPickupPositionRequired,
}

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
    private readonly object _liveImageGate;
    private ImageFrame? _pendingLiveFrame;
    private bool _liveImageUpdateQueued;
    private CancellationTokenSource _recipeImageCancellation;

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
        LiveLightLevel = Recipes.Current.BoltInspection.LightLevel;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInspectionSelected))]
    [NotifyPropertyChangedFor(nameof(ActiveMotionGroup))]
    [NotifyPropertyChangedFor(nameof(TeachingIoGroups))]
    [NotifyCanExecuteChangedFor(nameof(ToggleLiveViewCommand))]
    [NotifyCanExecuteChangedFor(nameof(TeachCurrentPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBoltPointCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReturnFromPickupCommand))]
    public partial HardwareArea SelectedTeachingUnit { get; set; } = HardwareArea.InspectionGantry;

    public bool IsBusy => Array.Exists(_commands, static command => command.IsRunning);

    public InspectionStation Inspection { get; }

    public RecipeEditor RecipeEditor { get; }

    public RecipeManager Recipes { get; }

    public HardwareArea[] TeachingUnits { get; }

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
            if (State.SetupEditingEnabled)
                return ManualControlBlock.None;
            if (State.AutoMode)
                return ManualControlBlock.AutoMode;
            return ManualControlBlock.Busy;
        }
    }

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
                    row.ViewCancellation = _viewCancellation.Token;
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

    public MachineState State { get; }

    private OperationCancellation Operations { get; }

    private bool PositionUpdatesActive { get; set; }

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
                row.ViewCancellation = _viewCancellation.Token;
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
        OnPropertyChanged(nameof(MotionHint));
        SaveCommand.NotifyCanExecuteChanged();
        ReturnFromPickupCommand.NotifyCanExecuteChanged();
        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        GrabCommand.NotifyCanExecuteChanged();
        ApplyLightCommand.NotifyCanExecuteChanged();
        AddBoltPointCommand.NotifyCanExecuteChanged();
        RemoveBoltPointCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    public partial FasteningHead NewFasteningHead { get; set; } = FasteningHead.Shooting;

    [ObservableProperty]
    public partial HeatSinkSlot SelectedPcb { get; set; } = HeatSinkSlot.HeatSink1;

    partial void OnSelectedPcbChanged(HeatSinkSlot value)
    {
        RefreshTeachingPoints();
        NotifyManualTeachingCommands();
    }

    [ObservableProperty]
    public partial IReadOnlyList<TeachingPoint> FilteredPoints { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TeachCurrentPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToPointCommand))]
    [NotifyPropertyChangedFor(nameof(TeachingIoGroups))]
    public partial TeachingPoint? SelectedPoint { get; set; }

    partial void OnSelectedPointChanged(TeachingPoint? oldValue, TeachingPoint? newValue)
    {
        CancelTeaching();
        if (Inspection.IsLiveView || ToggleLiveViewCommand.IsRunning)
            _ = RequestCameraStopAsync();
        LiveLightLevel = (SelectedBarcode is { } pcb
            ? Recipes.Current.BoltInspection.GetDataMatrix(pcb).LightLevel : newValue?.Position.Bolt?.LightLevel)
            ?? Recipes.Current.BoltInspection.LightLevel;
        OnPropertyChanged(nameof(CameraImage));
        SelectPreviousPointCommand.NotifyCanExecuteChanged();
        SelectNextPointCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SaveBehavior));
        NotifyManualTeachingCommands();
        OnPropertyChanged(nameof(SelectedBarcode));
        OnPropertyChanged(nameof(IsDataMatrixSelected));
        OnPropertyChanged(nameof(IsBoltSelected));
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        RemoveBoltPointCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    public partial string? SaveError { get; set; }

    public FasteningHead[] FasteningHeads { get; }

    public HeatSinkSlot[] HeatSinkSlots { get; }

    public HeatSinkSlot? SelectedBarcode => SelectedPoint?.Position.Target == TeachingTarget.DataMatrix ? SelectedPcb : null;

    public bool IsDataMatrixSelected => SelectedBarcode is not null;

    public bool BoltPointEditorVisible => IsFasteningSelected || IsInspectionSelected;

    public bool IsBoltSelected => IsInspectionSelected && SelectedPoint?.Position.Bolt is not null;

    public TeachingSaveBehavior SaveBehavior
    {
        get
        {
            switch (SelectedPoint)
            {
                case { Position.Target: TeachingTarget.BoltPickup }:
                    return TeachingSaveBehavior.BoltPickup;
                case { Position.Target: TeachingTarget.ShootingHeadFasteningZ or TeachingTarget.PickupHeadFasteningZ }:
                    return TeachingSaveBehavior.FasteningZ;
                case { Position.Target: TeachingTarget.DataMatrix }:
                    return TeachingSaveBehavior.BarcodeFov;
                case { Position.Target: TeachingTarget.SupplyHandoff }:
                    return TeachingSaveBehavior.SupplyHandoff;
                case { Position.Target: TeachingTarget.PlacementHandoff }:
                    return TeachingSaveBehavior.PlacementHandoff;
                case { Position.Target: TeachingTarget.PlacementReceiveZ }:
                    return TeachingSaveBehavior.PlacementReceiveZ;
                case { Position.Target: TeachingTarget.NgCarrierPickup }:
                    return TeachingSaveBehavior.NgPickup;
                case { Position.Target: TeachingTarget.BoltPosition }:
                    return TeachingSaveBehavior.BoltPosition;
                case { Position.Target: TeachingTarget.CarrierUpperLeftLocatingPin or TeachingTarget.CarrierLowerRightLocatingPin }:
                    return TeachingSaveBehavior.CameraCenter;
                case { Position.Mode: TeachMode.Image }:
                    return TeachingSaveBehavior.Image;
                case { Storage: TeachingStorage.Handoff }:
                    return TeachingSaveBehavior.Handoff;
                case { Storage: TeachingStorage.Machine }:
                    return TeachingSaveBehavior.Machine;
                default:
                    return TeachingSaveBehavior.Recipe;
            }
        }
    }

    private int CurrentPointIndex
    {
        get
        {
            for (var index = 0; index < FilteredPoints.Count; index++)
                if (FilteredPoints[index] == SelectedPoint)
                    return index;
            return -1;
        }
    }

    private TeachingPoint? NextTeachingPoint
    {
        get
        {
            if (!IsInspectionSelected)
                return null;
            if (!Inspection.HasBarcodePosition(SelectedPcb))
                return FilteredPoints.FirstOrDefault(
                    point => point.Position.Target == TeachingTarget.DataMatrix);
            return Recipes.Current.CarrierImages.Count == 0
                ? null
                : FilteredPoints.FirstOrDefault(
                    point => point.Position.Bolt is { } bolt && !Inspection.HasPosition(bolt));
        }
    }

    private void RefreshTeachingPoints()
    {
        var selectedTarget = SelectedPoint?.Position.Target;
        var selectedBolt = SelectedPoint?.Position.Bolt;
        TeachingPoint Point(TeachingTarget target, TeachMode mode, BoltPoint? bolt = null)
        {
            return new(new(target, ActiveMotionGroup, mode) { Bolt = bolt }, _settings, Recipes, SelectedPcb);
        }
        TeachingPoint[] points = SelectedTeachingUnit switch
        {
            HardwareArea.PcbSupply => [
                Point(TeachingTarget.SafeZ, TeachMode.ZOnly),
                Point(TeachingTarget.SupplyPcb1Pick, TeachMode.Full),
                Point(TeachingTarget.SupplyPcb2Pick, TeachMode.Full),
                Point(TeachingTarget.SupplyHandoff, TeachMode.Full),
            ],
            HardwareArea.PcbPlacementHandler => [
                Point(TeachingTarget.PlacementHandoff, TeachMode.Full),
                Point(TeachingTarget.PlacementReceiveZ, TeachMode.ZOnly),
                Point(TeachingTarget.HeatSink1PcbPlacement, TeachMode.Full),
                Point(TeachingTarget.HeatSink2PcbPlacement, TeachMode.Full),
            ],
            HardwareArea.BoltFastening => [
                Point(TeachingTarget.SafeZ, TeachMode.ZOnly),
                Point(TeachingTarget.ShootingHeadFasteningZ, TeachMode.ZOnly),
                Point(TeachingTarget.ShootingHeadUpperLeftLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.ShootingHeadLowerRightLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.PickupHeadFasteningZ, TeachMode.ZOnly),
                Point(TeachingTarget.PickupHeadUpperLeftLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.PickupHeadLowerRightLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.BoltPickup, TeachMode.Full),
                .. Recipes.Current.Pcb.GetBolts(SelectedPcb).OrderBy(bolt => bolt.Head)
                    .Select(bolt => Point(TeachingTarget.BoltPosition, TeachMode.XYOnly, bolt)),
            ],
            HardwareArea.InspectionGantry => [
                Point(TeachingTarget.CarrierUpperLeftLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.CarrierLowerRightLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.InspectionWaiting, TeachMode.XYOnly),
                Point(TeachingTarget.NgCarrierPickup, TeachMode.XYOnly),
                Point(TeachingTarget.NgShuttlePlace, TeachMode.XYOnly),
                Point(TeachingTarget.DataMatrix, TeachMode.Image),
                .. Recipes.Current.Pcb.GetBolts(SelectedPcb)
                    .Select(bolt => Point(TeachingTarget.BoltReference, TeachMode.Image, bolt)),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(SelectedTeachingUnit)),
        };
        FilteredPoints = points
            .OrderBy(point => point.Position.Target == TeachingTarget.BoltPosition ? 0 : 1)
            .ThenBy(point => point.Group)
            .ThenBy(point => point.Position.Target == TeachingTarget.PlacementHandoff ? 0 : 1)
            .ToArray();
        SelectedPoint = FilteredPoints.FirstOrDefault(
            point => selectedBolt is not null
                ? point.Position.Bolt?.Number == selectedBolt.Number
                : point.Position.Target == selectedTarget)
            ?? NextTeachingPoint
                ?? FilteredPoints.FirstOrDefault();
    }

    private void RefreshPointPositions()
    {
        foreach (var point in FilteredPoints)
            point.Refresh();
    }

    private void OnRecipeChanged()
    {
        CameraError = null;
        if (Inspection.IsLiveView || ToggleLiveViewCommand.IsRunning)
            _ = RequestCameraStopAsync();
        SelectedPoint = null;
        if (SelectedPcb == HeatSinkSlot.HeatSink1)
            RefreshTeachingPoints();
        else
            SelectedPcb = HeatSinkSlot.HeatSink1;
        ShowRecipeImages();
    }

    public IRelayCommand AddBoltPointCommand { get; }

    private void AddBoltPoint()
    {
        var number = Recipes.Current.Pcb.GetBolts(SelectedPcb).Select(bolt => bolt.Number).DefaultIfEmpty().Max() + 1;
        var bolt = new BoltPoint
        {
            Number = number,
            HeatSink = SelectedPcb,
            Head = NewFasteningHead,
            BrightnessThreshold = Recipes.Current.BoltInspection.BrightnessThreshold,
            MinimumBrightRatio = Recipes.Current.BoltInspection.MinimumBrightRatio,
        };
        Recipes.Current.Pcb.BoltPoints.Add(bolt);
        RefreshTeachingPoints();
        SelectedPoint = FilteredPoints.First(
            point => point.BoltNumber == number && point.Position.Target == TeachingTarget.BoltReference);
    }

    private bool IsAddBoltPointAllowed => State.SetupEditingEnabled && IsInspectionSelected;

    public IRelayCommand RemoveBoltPointCommand { get; }

    private void RemoveBoltPoint()
    {
        var number = SelectedPoint!.BoltNumber;
        Recipes.Current.Pcb.BoltPoints.RemoveAll(bolt => bolt.Number == number && bolt.HeatSink == SelectedPcb);
        Recipes.Current.CarrierImages.RemoveAll(fov =>
            !fov.IsBarcode && fov.BoltNumber == number && fov.HeatSink == SelectedPcb);
        CarrierImages = CarrierImages.Where(image =>
            image.Metadata.IsBarcode || image.Metadata.BoltNumber != number || image.Metadata.HeatSink != SelectedPcb).ToArray();
        RefreshTeachingPoints();
    }

    private bool IsRemoveBoltPointAllowed
    {
        get
        {
            return State.SetupEditingEnabled
                && IsInspectionSelected
                && SelectedPoint?.Position.Target == TeachingTarget.BoltReference;
        }
    }

    public IRelayCommand SelectPreviousPointCommand { get; }

    private void SelectPreviousPoint()
    {
        SelectedPoint = FilteredPoints[CurrentPointIndex - 1];
    }

    private bool IsSelectPreviousPointAllowed => CurrentPointIndex > 0;

    public IRelayCommand SelectNextPointCommand { get; }

    private void SelectNextPoint()
    {
        SelectedPoint = FilteredPoints[CurrentPointIndex + 1];
    }

    private bool IsSelectNextPointAllowed => CurrentPointIndex < FilteredPoints.Count - 1;

    public IAsyncRelayCommand TeachCurrentPositionCommand { get; }

    private async Task TeachCurrentPositionAsync(CancellationToken cancellationToken)
    {
        if (SelectedPoint?.Position.Mode == TeachMode.Image)
        {
            if (IsRecordImagePositionAllowed)
                await CaptureTeachingImageAsync(recordPosition: true, cancellationToken);
            return;
        }
        var viewToken = _viewCancellation.Token;
        var activeToken = cancellationToken;
        try
        {
            if (!State.SetupEditingEnabled)
                return;
            using var operation = Machine.BeginManualOperation(
                () => State.ManualMode,
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            switch (SelectedPoint)
            {
                case { Position.Mode: not TeachMode.Image, Position.IsTeachAllowed: true } point
                    when Motion.Feedback.IsReady && IsReadTeachingPositionAllowed(point, live: true):
                    SaveError = null;
                    var current = Motion.Feedback.Position;
                    point.Teach(current.X, current.Y, current.Z);
                    RefreshPointPositions();
                    if (point.Storage == TeachingStorage.Machine
                        && !await SaveSettingsAsync(operation.Token, point.Setting!))
                        return;
                    operation.Token.ThrowIfCancellationRequested();
                    OnPointTaught(point);
                    NotifyManualTeachingCommands();
                    break;
                case { Position.Mode: not TeachMode.Image, Position.IsTeachAllowed: true }:
                    SaveError = "Home the axes used by this teaching position and wait for them to stop before teaching.";
                    break;
            }
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(MachineAlarm.IoCommunication, exception);
        }
    }

    private bool IsTeachCurrentPositionAllowed
    {
        get
        {
            if (SelectedPoint?.Position.Mode == TeachMode.Image)
                return IsRecordImagePositionAllowed;
            return SelectedPoint is { Position.Mode: not TeachMode.Image, Position.IsTeachAllowed: true } point
                && State.SetupEditingEnabled
                && IsReadTeachingPositionAllowed(point, live: false);
        }
    }

    private bool IsReadTeachingPositionAllowed(TeachingPoint point, bool live)
    {
        MotionAxis[] axes = point.Position.Mode switch
        {
            TeachMode.Full => [MotionAxis.X, MotionAxis.Y, MotionAxis.Z],
            TeachMode.XYOnly => [MotionAxis.X, MotionAxis.Y],
            TeachMode.XOnly => [MotionAxis.X],
            TeachMode.YOnly => [MotionAxis.Y],
            TeachMode.ZOnly => [MotionAxis.Z],
            _ => [],
        };
        return axes.Length > 0 && axes.All(axis =>
            (live ? Motion.Feedback.GetAxisState(axis) : Motion.Axes[axis].State)
                is { Homed: true, InMotion: false });
    }

    private void OnPointTaught(TeachingPoint point)
    {
        if (point.Position.Target == TeachingTarget.CarrierUpperLeftLocatingPin)
        {
            SelectedPoint = FilteredPoints.First(
                candidate => candidate.Position.Target == TeachingTarget.CarrierLowerRightLocatingPin);
        }
    }

    public IAsyncRelayCommand SaveCommand { get; }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var viewToken = _viewCancellation.Token;
        var activeToken = cancellationToken;
        try
        {
            if (!IsSaveAllowed)
                return;
            using var operation = Machine.BeginManualOperation(
                () => State.ManualMode,
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            RecipeEditor.Error = null;
            if (await SaveSettingsAsync(operation.Token,
                    _settings.PcbSupply, _settings.PcbPlacementHandler, _settings.BoltFastening,
                    _settings.InspectionGantry, _settings.CarrierReference, _settings.NgCarrierTransfer)
                && !await RecipeEditor.SaveAsync(operation.Token))
            {
                SaveError = "Teaching settings were saved, but the recipe was not saved. "
                    + (RecipeEditor.Error ?? "Save was cancelled. Save again to finish.");
            }
            NotifyManualTeachingCommands();
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(MachineAlarm.IoCommunication, exception);
        }
    }

    private bool IsSaveAllowed => State.SetupEditingEnabled && RecipeEditor.IsSaveAllowed && !RecipeEditor.IsBusy;

    private async Task<bool> SaveSettingsAsync(
        CancellationToken cancellationToken,
        params Setting[] settings)
    {
        SaveError = null;
        try
        {
            await _store.SaveSettingsAsync(settings, cancellationToken);
            _logger.LogInformation(
                "Teaching settings saved: {Group}.",
                ActiveMotionGroup);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SaveError = "Teaching save cancelled. Values have not been saved.";
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception,
                "Teaching settings save failed: {Group}.", ActiveMotionGroup);
            SaveError = $"Teaching values were not saved: {exception.GetBaseException().Message}";
            return false;
        }
    }

    [ObservableProperty]
    public partial double JogSpeed { get; set; } = 10.0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StepCommand))]
    public partial double StepDistance { get; set; } = 0.1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualSpeedLabel))]
    public partial TeachingMoveMode MoveMode { get; set; }

    public TeachingMoveMode[] MoveModes { get; }

    public string ManualSpeedLabel => MoveMode == TeachingMoveMode.Step ? "Step speed" : "Jog speed";

    public MotionStatus Motion => State.GetMotionStatus(ActiveMotionGroup);

    public string MoveToHorizontalZLabel
    {
        get
        {
            switch (ActiveMotionGroup)
            {
                case MotionGroup.PcbSupply:
                    return "Move Z to Rotation Height";
                case MotionGroup.PcbPlacementHandler:
                    return "Move Z to Standby Height";
                default:
                    return "Move Z to Safe Z";
            }
        }
    }

    public TeachingMotionHint MotionHint
    {
        get
        {
            if (!State.Available)
                return IsInspectionSelected ? TeachingMotionHint.None : TeachingMotionHint.MotionUnavailable;
            if (!IsInspectionSelected)
            {
                if (HomeBlock == HomeBlockReason.UnitDisabled)
                    return TeachingMotionHint.UnitDisabled;
                if (Motion.Axes.Values.Any(axis => axis.State is null))
                    return TeachingMotionHint.MotionUnavailable;
                if (Motion.Axes.Values.Any(axis => axis.State is { Alarm: true } or { Emergency: true }))
                    return TeachingMotionHint.AxisFault;
                if (Motion.Axes.Values.Any(axis => axis.State is { ServoOn: false }))
                    return TeachingMotionHint.ServoOff;
                if (Motion.Axes.Values.Any(axis => axis.State is { Homed: false }))
                    return TeachingMotionHint.HomeRequired;
            }
            if (SelectedPoint?.Position.Target == TeachingTarget.NgCarrierPickup
                && _settings.NgCarrierTransfer.CarrierPickupPosition is null)
                return TeachingMotionHint.NgPickupPositionRequired;
            switch (ActiveMotionGroup)
            {
                case MotionGroup.PcbPlacementHandler when _pcbPlacement.Lift != PlacementCylinderState.Up:
                    return TeachingMotionHint.RaisePlacementCylinders;
                case MotionGroup.BoltFastening:
                    return TeachingMotionHint.BoltAdjustment;
                case MotionGroup.InspectionGantry when !Inspection.IsRaised:
                    return TeachingMotionHint.RaiseNgPickup;
                default:
                    return TeachingMotionHint.None;
            }
        }
    }

    public HomeBlockReason HomeBlock => IsInspectionSelected ? HomeBlockReason.None : Machine.GetHomeBlock(ActiveMotionGroup);

    public MotionGroup ActiveMotionGroup
    {
        get
        {
            switch (SelectedTeachingUnit)
            {
                case HardwareArea.PcbSupply:
                    return MotionGroup.PcbSupply;
                case HardwareArea.PcbPlacementHandler:
                    return MotionGroup.PcbPlacementHandler;
                case HardwareArea.BoltFastening:
                    return MotionGroup.BoltFastening;
                case HardwareArea.InspectionGantry:
                    return MotionGroup.InspectionGantry;
                default:
                    throw new ArgumentOutOfRangeException(nameof(SelectedTeachingUnit));
            }
        }
    }

    public IAsyncRelayCommand HomeCommand { get; }

    private async Task HomeAsync(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _viewCancellation.Token);
        var group = ActiveMotionGroup;
        await Machine.HomeAsync(group, cancellation.Token);
    }

    private bool IsHomeAllowed => Motion.Feedback.Axes.All(axis => Machine.IsHomeAxisAllowed(ActiveMotionGroup, axis));

    private bool IsJogAllowed(MotionAxis axis)
    {
        return !State.IsRunning
            && Machine.IsManualMotionReady(ActiveMotionGroup, live: false)
            && Motion.Feedback.Axes.Contains(axis)
            && ActiveMotionGroup switch
            {
                MotionGroup.PcbSupply or MotionGroup.BoltFastening => true,
                MotionGroup.PcbPlacementHandler => axis == MotionAxis.Z
                    || _pcbPlacement.Lift == PlacementCylinderState.Up,
                MotionGroup.InspectionGantry => Inspection.IsRaised,
                _ => false,
            };
    }

    public IAsyncRelayCommand<TeachingDirection> JogCommand { get; }

    private async Task JogAsync(TeachingDirection direction, CancellationToken cancellationToken)
    {
        var group = ActiveMotionGroup;
        var (axis, sign) = Resolve(direction);
        var velocity = sign * JogSpeed;
        var viewCancellation = _viewCancellation.Token;
        var activeCancellation = cancellationToken;
        try
        {
            SaveError = null;
            if (State.IsRunningFor())
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(group),
                cancellationToken,
                viewCancellation);
            if (operation is null)
                return;
            activeCancellation = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            switch (group)
            {
                case MotionGroup.PcbSupply:
                    await _pcbSupply.JogAsync(axis, velocity, operation.Token);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    await _pcbPlacement.JogAsync(axis, velocity, operation.Token);
                    break;
                case MotionGroup.BoltFastening:
                    await _fasteningStation.JogAsync(axis, velocity, operation.Token);
                    break;
                case MotionGroup.InspectionGantry:
                    await Inspection.JogAsync(axis, velocity, operation.Token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(group));
            }
        }
        catch (OperationCanceledException) when (activeCancellation.IsCancellationRequested
            || viewCancellation.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (ArgumentException exception)
        {
            SaveError = $"Motion settings or target are invalid: {exception.Message}";
            _logger.LogWarning(exception, "Teaching motion rejected invalid settings or target.");
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(Machine.GetMotionAlarm(group), exception);
        }
    }

    public IRelayCommand JogStopCommand { get; }

    private void JogStop()
    {
        CancelTeaching();
    }

    private bool IsMoveToHorizontalZAllowed => IsJogAllowed(MotionAxis.Z)
        && (ActiveMotionGroup != MotionGroup.PcbPlacementHandler
            || _pcbPlacement.Lift == PlacementCylinderState.Up);

    public IAsyncRelayCommand MoveToHorizontalZCommand { get; }

    private async Task MoveToHorizontalZAsync(CancellationToken cancellationToken)
    {
        var commandGroup = ActiveMotionGroup;
        var viewToken = _viewCancellation.Token;
        var activeToken = cancellationToken;
        try
        {
            SaveError = null;
            if (State.IsRunningFor())
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            switch (commandGroup)
            {
                case MotionGroup.PcbSupply:
                    await _pcbSupply.MoveAxisAsync(
                        MotionAxis.Z, _settings.PcbSupply.RotationZ, operation.Token);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    await _pcbPlacement.MoveAxisAsync(
                        MotionAxis.Z, _settings.PcbPlacementHandler.HandoffPosition.Z, operation.Token);
                    break;
                case MotionGroup.BoltFastening:
                    await _fasteningStation.MoveZAsync(_settings.BoltFastening.SafeZ, operation.Token);
                    break;
                case MotionGroup.InspectionGantry:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ActiveMotionGroup));
            }
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (ArgumentException exception)
        {
            SaveError = $"Motion settings or target are invalid: {exception.Message}";
            _logger.LogWarning(exception, "Teaching motion rejected invalid settings or target.");
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(Machine.GetMotionAlarm(commandGroup), exception);
        }
    }

    private bool IsStepAllowed(TeachingDirection direction)
    {
        if (!IsMoveDirectionAllowed(direction))
            return false;
        var position = Motion.Position;
        var (axis, sign) = Resolve(direction);
        var current = axis switch
        {
            MotionAxis.X => position.X,
            MotionAxis.Y => position.Y,
            MotionAxis.Z => position.Z,
            _ => null,
        };
        if (current is null)
            return false;
        return double.IsFinite(current.Value + sign * StepDistance);
    }

    private static (MotionAxis Axis, int Sign) Resolve(TeachingDirection direction)
    {
        switch (direction)
        {
            case TeachingDirection.XMinus:
                return (MotionAxis.X, -1);
            case TeachingDirection.XPlus:
                return (MotionAxis.X, 1);
            case TeachingDirection.YMinus:
                return (MotionAxis.Y, -1);
            case TeachingDirection.YPlus:
                return (MotionAxis.Y, 1);
            case TeachingDirection.ZMinus:
                return (MotionAxis.Z, -1);
            case TeachingDirection.ZPlus:
                return (MotionAxis.Z, 1);
            default:
                throw new ArgumentOutOfRangeException(nameof(direction));
        }
    }

    private bool IsMoveDirectionAllowed(TeachingDirection direction)
    {
        return IsJogAllowed(Resolve(direction).Axis);
    }

    public IAsyncRelayCommand<TeachingDirection> StepCommand { get; }

    private async Task StepAsync(TeachingDirection direction, CancellationToken cancellationToken)
    {
        var commandGroup = ActiveMotionGroup;
        var viewToken = _viewCancellation.Token;
        var activeToken = cancellationToken;
        try
        {
            SaveError = null;
            if (State.IsRunningFor())
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            var current = Motion.Feedback.Position;
            var (axis, sign) = Resolve(direction);
            var position = axis switch
            {
                MotionAxis.X => current.X,
                MotionAxis.Y => current.Y,
                MotionAxis.Z => current.Z,
                _ => throw new ArgumentOutOfRangeException(nameof(direction)),
            };
            var target = position + sign * StepDistance;
            switch (commandGroup)
            {
                case MotionGroup.PcbSupply:
                    await _pcbSupply.AdjustAxisAsync(axis, target, JogSpeed, operation.Token);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    await _pcbPlacement.AdjustAxisAsync(axis, target, JogSpeed, operation.Token);
                    break;
                case MotionGroup.BoltFastening:
                    await _fasteningStation.AdjustAxisAsync(axis, target, JogSpeed, operation.Token);
                    break;
                case MotionGroup.InspectionGantry:
                    await Inspection.MoveAxisAsync(axis, target, _settings.InspectionGantry.Motion.HorizontalSpeed, operation.Token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ActiveMotionGroup));
            }
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (ArgumentException exception)
        {
            SaveError = $"Motion settings or target are invalid: {exception.Message}";
            _logger.LogWarning(exception, "Teaching motion rejected invalid settings or target.");
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(Machine.GetMotionAlarm(commandGroup), exception);
        }
    }

    public IAsyncRelayCommand ReturnFromPickupCommand { get; }

    private async Task ReturnFromPickupAsync(CancellationToken cancellationToken)
    {
        var commandGroup = ActiveMotionGroup;
        var viewToken = _viewCancellation.Token;
        var activeToken = cancellationToken;
        try
        {
            SaveError = null;
            if (State.IsRunningFor())
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            await _fasteningStation.ReturnFromPickupAsync(operation.Token);
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (ArgumentException exception)
        {
            SaveError = $"Motion settings or target are invalid: {exception.Message}";
            _logger.LogWarning(exception, "Teaching motion rejected invalid settings or target.");
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(Machine.GetMotionAlarm(commandGroup), exception);
        }
    }

    private bool IsReturnFromPickupAllowed
    {
        get
        {
            return ActiveMotionGroup == MotionGroup.BoltFastening
                && !State.IsRunning
                && Machine.IsManualMotionReady(ActiveMotionGroup, live: false);
        }
    }

    public IAsyncRelayCommand MoveToPointCommand { get; }

    private async Task MoveToPointAsync(CancellationToken cancellationToken)
    {
        var point = SelectedPoint!;
        var commandGroup = ActiveMotionGroup;
        var viewToken = _viewCancellation.Token;
        var activeToken = cancellationToken;
        try
        {
            SaveError = null;
            if (State.IsRunningFor())
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            switch (point.Position.MotionGroup)
            {
                case MotionGroup.PcbSupply:
                    await _pcbSupply.MoveToTeachingPositionAsync(point.Position, point.Read(), operation.Token);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    await _pcbPlacement.MoveToTeachingPositionAsync(point.Position, point.Read(), operation.Token);
                    break;
                case MotionGroup.BoltFastening:
                    await _fasteningStation.MoveToTeachingPositionAsync(point.Position, point.Read(), operation.Token);
                    break;
                case MotionGroup.InspectionGantry when point.Position.Target == TeachingTarget.NgCarrierPickup:
                    await Inspection.MoveToCarrierAsync(NgTransferDestination.Station, operation.Token);
                    break;
                case MotionGroup.InspectionGantry when point.Position.Bolt is { } bolt:
                    await Inspection.MoveToBoltAsync(bolt, operation.Token);
                    break;
                case MotionGroup.InspectionGantry when point.Position.Target == TeachingTarget.DataMatrix:
                    await Inspection.MoveToBarcodeAsync(SelectedPcb, operation.Token);
                    break;
                case MotionGroup.InspectionGantry:
                    await Inspection.MoveToAsync(new AxisPosition { X = point.X, Y = point.Y }, cancellationToken: operation.Token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(point));
            }
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (ArgumentException exception)
        {
            SaveError = $"Motion settings or target are invalid: {exception.Message}";
            _logger.LogWarning(exception, "Teaching motion rejected invalid settings or target.");
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(Machine.GetMotionAlarm(commandGroup), exception);
        }
    }

    private bool IsMoveToPointAllowed
    {
        get
        {
            switch (SelectedPoint)
            {
                case null:
                    return false;
                case { } when State.IsRunning
                    || !Machine.IsManualMotionReady(ActiveMotionGroup, live: false):
                    return false;
                case { } when ActiveMotionGroup == MotionGroup.PcbPlacementHandler
                    && _pcbPlacement.Lift != PlacementCylinderState.Up:
                    return false;
                case { } point when ActiveMotionGroup == MotionGroup.PcbSupply:
                    return _pcbSupply.IsMoveToTeachingPositionAllowed(point.Position);
                case { } point:
                    return (point.Position.Mode == TeachMode.ZOnly || IsHorizontalMoveAllowed)
                        && point.Position.HasPosition;
            }
        }
    }

    private bool IsHorizontalMoveAllowed
    {
        get
        {
            switch (ActiveMotionGroup)
            {
                case MotionGroup.PcbPlacementHandler:
                    return _pcbPlacement.Lift == PlacementCylinderState.Up;
                case MotionGroup.BoltFastening:
                    return _fasteningStation.IsHorizontalMoveAllowed;
                case MotionGroup.InspectionGantry:
                    return Inspection.IsRaised;
                default:
                    return false;
            }
        }
    }

    private Task _liveImageUpdate = Task.CompletedTask;
    private Task _cameraStop = Task.CompletedTask;
    private Task _recipeImageUpdate = Task.CompletedTask;

    [ObservableProperty]
    public partial int LiveLightLevel { get; set; }

    partial void OnLiveLightLevelChanging(int value)
    {
        if (value is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 255.");
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraImage))]
    public partial BitmapSource? LiveImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraImage))]
    [NotifyCanExecuteChangedFor(nameof(GrabCommand))]
    public partial IReadOnlyList<CarrierImageTileView> CarrierImages { get; set; }

    public string? CameraError
    {
        get => field ?? Inspection.LiveViewError?.Message;
        private set => SetProperty(ref field, value);
    }

    public BitmapSource? CameraImage => Inspection.IsLiveView ? LiveImage : CarrierImages.FirstOrDefault(tile =>
        tile.Metadata.HeatSink == SelectedPcb
        && (IsDataMatrixSelected ? tile.Metadata.IsBarcode
            : IsBoltSelected && !tile.Metadata.IsBarcode && tile.Metadata.BoltNumber == SelectedPoint!.BoltNumber))?.Image;

    public IAsyncRelayCommand GrabCommand { get; }

    private async Task GrabAsync(CancellationToken cancellationToken)
    {
        if (IsGrabAllowed)
            await CaptureTeachingImageAsync(recordPosition: false, cancellationToken);
    }

    private bool IsGrabAllowed => IsRecordImagePositionAllowed && SelectedPoint?.Position.HasPosition == true;

    public IAsyncRelayCommand ApplyLightCommand { get; }

    private async Task ApplyLightAsync(CancellationToken cancellationToken)
    {
        if (!IsApplyLightAllowed)
            return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _viewCancellation.Token);
        try
        {
            CameraError = null;
            await Inspection.ApplyLiveLightAsync(LiveLightLevel, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Teaching light adjustment failed.");
            CameraError = exception.Message;
        }
    }

    private bool IsApplyLightAllowed => IsInspectionSelected && State.SetupEditingEnabled && Inspection.IsLiveView;

    public IAsyncRelayCommand ToggleLiveViewCommand { get; }

    private async Task ToggleLiveViewAsync(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _viewCancellation.Token);
        try
        {
            if (Inspection.IsLiveView)
            {
                await StopCameraLiveAsync();
                return;
            }

            CameraError = null;
            await _cameraStop;
            cancellation.Token.ThrowIfCancellationRequested();
            await Inspection.StartLiveViewAsync(cancellation.Token, LiveLightLevel);
            if (!State.ManualMode || !IsInspectionSelected)
                await StopCameraLiveAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Camera live view operation failed.");
            CameraError = exception.Message;
        }
    }

    private bool IsToggleLiveViewAllowed => Inspection.IsLiveView
        || IsInspectionSelected && State.ManualMode && !TeachCurrentPositionCommand.IsRunning && !GrabCommand.IsRunning;

    private async Task CaptureTeachingImageAsync(bool recordPosition, CancellationToken cancellationToken)
    {
        var point = SelectedPoint;
        var bolt = point?.Position.Bolt;
        var barcode = SelectedBarcode is not null;
        if (bolt is null && !barcode)
            return;
        var pcb = SelectedPcb;
        var lightLevel = LiveLightLevel;
        var commandGroup = ActiveMotionGroup;
        var viewToken = _viewCancellation.Token;
        var activeToken = cancellationToken;
        _logger.LogInformation(
            "Teaching capture requested: recipe={Recipe}, PCB={Pcb}, point={Point}, recordPosition={RecordPosition}, live={Live}.",
            RecipeEditor.Name, pcb, point!.Name, recordPosition, Inspection.IsLiveView);
        try
        {
            CameraError = null;
            if (State.IsRunningFor())
                throw new InvalidOperationException("Recording was not started because the machine is busy. Wait for motion to stop, then record again.");
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                throw new InvalidOperationException("Recording was not started because another operation is active. Try again after it finishes.");
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            await _recipeImageUpdate;
            operation.Token.ThrowIfCancellationRequested();
            if (CarrierImages.Count != Recipes.Current.CarrierImages.Count)
                throw new InvalidOperationException("Wait for the saved teaching images to load before capturing.");
            await _cameraStop;
            operation.Token.ThrowIfCancellationRequested();
            var captured = await Inspection.CaptureCarrierImageAsync(operation.Token, lightLevel);
            _logger.LogInformation("Teaching image captured: PCB={Pcb}, point={Point}, X={X}, Y={Y}.",
                pcb, point.Name, captured.Center.X, captured.Center.Y);
            var image = await Task.Run(() => InspectionPreview.CreateBitmap(captured.Frame), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var images = CarrierImages.ToList();
            var index = images.FindIndex(tile => tile.Metadata.HeatSink == pcb
                && (barcode ? tile.Metadata.IsBarcode : !tile.Metadata.IsBarcode && tile.Metadata.BoltNumber == bolt!.Number));
            var previous = index >= 0 ? images[index].Metadata : null;
            if (!recordPosition && previous is null)
                throw new InvalidOperationException("Record Position first, then use Grab to update its reference image.");
            var metadata = new CarrierImageTile
            {
                Number = previous?.Number ?? (images.Count == 0 ? 1 : images.Max(tile => tile.Metadata.Number) + 1),
                Center = barcode ? (recordPosition ? captured.Center : previous!.Center) : null,
                HeatSink = pcb,
                BoltNumber = bolt?.Number,
                IsBarcode = barcode,
                Region = previous?.Region ?? PixelRegion.CenteredSquare(
                    image.PixelWidth, image.PixelHeight, Math.Min(image.PixelWidth, image.PixelHeight) / 4),
            };
            var replacement = new CarrierImageTileView(metadata, image, bolt);
            if (index >= 0)
                images[index] = replacement;
            else
                images.Add(replacement);

            var previousX = bolt?.X;
            var previousY = bolt?.Y;
            var previousFasteningX = bolt?.FasteningX;
            var previousFasteningY = bolt?.FasteningY;
            var dataMatrix = barcode ? Recipes.Current.BoltInspection.GetDataMatrix(pcb) : null;
            var previousLight = dataMatrix is not null ? dataMatrix.LightLevel : bolt!.LightLevel;
            if (dataMatrix is not null)
                dataMatrix.LightLevel = lightLevel;
            else
                bolt!.LightLevel = lightLevel;
            if (recordPosition && bolt is not null)
            {
                // The bolt is centered on the camera crosshair. ROI pixels do not alter its machine XY.
                bolt.X = captured.Center.X;
                bolt.Y = captured.Center.Y;
                _settings.BoltFastening.InitializeBoltPosition(bolt, _settings.CarrierReference);
            }
            if (await RecipeEditor.SaveCarrierImagesAsync(images, operation.Token))
            {
                CarrierImages = images;
                RefreshPointPositions();
                var position = Recipes.Current.GetInspectionPosition(metadata);
                _logger.LogInformation(
                    "Teaching image saved: recipe={Recipe}, PCB={Pcb}, point={Point}, X={X}, Y={Y}, image={Image}, database={Database}.",
                    RecipeEditor.ActiveName, pcb, point.Name, position.X, position.Y,
                    metadata.Number, _store.DatabaseFile);
            }
            else
            {
                if (dataMatrix is not null)
                    dataMatrix.LightLevel = previousLight;
                else
                    bolt!.LightLevel = previousLight;
                if (recordPosition && bolt is not null)
                {
                    bolt.X = previousX;
                    bolt.Y = previousY;
                    bolt.FasteningX = previousFasteningX;
                    bolt.FasteningY = previousFasteningY;
                }
                CameraError = RecipeEditor.Error ?? "Recording was cancelled before saving. Record the point again.";
                _logger.LogInformation("Teaching image was not saved: PCB={Pcb}, point={Point}, reason={Reason}.",
                    pcb, point.Name, CameraError);
            }
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
            _logger.LogInformation("Teaching capture cancelled before saving: PCB={Pcb}, point={Point}.", pcb, point.Name);
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            CameraError = exception.GetBaseException().Message;
            _logger.LogError(exception, "Teaching capture failed: PCB={Pcb}, point={Point}.", pcb, point.Name);
            Machine.ReportManualFailure(Machine.GetMotionAlarm(commandGroup), exception);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Teaching capture failed: PCB={Pcb}, point={Point}.", pcb, point.Name);
            if (!activeToken.IsCancellationRequested)
                CameraError = exception.Message;
        }
    }

    private bool IsRecordImagePositionAllowed
    {
        get
        {
            return IsInspectionSelected
                && (IsBoltSelected || IsDataMatrixSelected)
                && !State.IsRunning
                && Machine.IsManualMotionReady(ActiveMotionGroup, live: false)
                && Motion.Axes.Values.All(axis => axis.State is { InMotion: false, InPosition: true })
                && RecipeEditor.IsSaveAllowed;
        }
    }

    private void ShowRecipeImages()
    {
        _recipeImageCancellation.Cancel();
        _recipeImageCancellation.Dispose();
        _recipeImageCancellation = new();
        CarrierImages = [];
        _recipeImageUpdate = LoadRecipeImagesAsync(_recipeImageUpdate, _recipeImageCancellation.Token);
    }

    private async Task LoadRecipeImagesAsync(Task previous, CancellationToken cancellationToken)
    {
        try
        {
            await previous;
            cancellationToken.ThrowIfCancellationRequested();
            if (!PositionUpdatesActive || !IsInspectionSelected)
                return;
            var images = await RecipeEditor.LoadCarrierImagesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            CarrierImages = images;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Teaching recipe image load failed.");
            if (!cancellationToken.IsCancellationRequested)
                CameraError ??= exception.Message;
        }
    }

    private Task StopCameraLiveAsync()
    {
        ToggleLiveViewCommand.Cancel();
        if (!_cameraStop.IsCompleted)
            return _cameraStop;

        lock (_liveImageGate)
        {
            LiveImage = null;
            _pendingLiveFrame = null;
        }

        _cameraStop = Inspection.StopLiveViewAsync();
        return _cameraStop;
    }

    private async Task RequestCameraStopAsync()
    {
        try
        {
            await StopCameraLiveAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Camera live view stop failed.");
        }
    }

    private void OnLiveViewChanged()
    {
        if (!PositionUpdatesActive)
            return;
        Application.Current.Dispatcher.BeginInvoke(RefreshLiveView);
    }

    private void RefreshLiveView()
    {
        OnPropertyChanged(nameof(Inspection));
        OnPropertyChanged(nameof(CameraError));
        if (!Inspection.IsLiveView)
        {
            lock (_liveImageGate)
            {
                LiveImage = null;
                _pendingLiveFrame = null;
            }
        }

        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        GrabCommand.NotifyCanExecuteChanged();
        ApplyLightCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CameraImage));
    }

    private async Task HandlePreviewFailureAsync(Exception exception)
    {
        _logger.LogError(exception, "Camera preview conversion failed.");
        await RequestCameraStopAsync();
        CameraError = exception.Message;
        lock (_liveImageGate)
        {
            _pendingLiveFrame = null;
            _liveImageUpdateQueued = false;
        }
    }

    private void UpdateLiveImage(ImageFrame frame)
    {
        if (!Inspection.IsLiveView)
        {
            return;
        }

        lock (_liveImageGate)
        {
            _pendingLiveFrame = frame;
            if (_liveImageUpdateQueued)
            {
                return;
            }

            _liveImageUpdateQueued = true;
            _liveImageUpdate = Task.Run(UpdateLiveImagesAsync);
        }
    }

    private async Task UpdateLiveImagesAsync()
    {
        try
        {
            while (true)
            {
                ImageFrame frame;
                lock (_liveImageGate)
                {
                    if (_pendingLiveFrame is null)
                    {
                        _liveImageUpdateQueued = false;
                        return;
                    }

                    frame = _pendingLiveFrame;
                    _pendingLiveFrame = null;
                }

                var image = InspectionPreview.CreateBitmap(frame);
                // Frozen frames can cross threads; WPF marshals the scalar binding.
                lock (_liveImageGate)
                {
                    if (Inspection.IsLiveView && IsInspectionSelected)
                        LiveImage = image;
                }
            }
        }
        catch (Exception exception)
        {
            await Application.Current.Dispatcher.InvokeAsync(
                () => HandlePreviewFailureAsync(exception)).Task.Unwrap();
        }
    }
}
