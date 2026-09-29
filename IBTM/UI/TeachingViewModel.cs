using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
    [Description("Jog (hold)")]
    Jog,
    [Description("Step (click)")]
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
    [Description("Raise the handler before XY or position moves.")]
    RaisePlacementCylinders,
    [Description("Raise both heads before moving to a teaching position.")]
    RaiseFasteningHeads,
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

public enum ManualControlBlock
{
    [Description("")]
    None,
    [Description("Switch the machine to Manual mode.")]
    AutoMode = 4,
    [Description("Wait for the current operation to stop.")]
    Busy,
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
    private readonly InspectionImages _images;
    private CancellationTokenSource _viewCancellation;
    private readonly IReadOnlyDictionary<HardwareArea, TeachingIoGroup[]> _teachingIoGroups;
    private int _manualCommandRefreshQueued;
    private readonly object _liveImageGate;
    private ImageFrame? _pendingLiveFrame;
    private bool _liveImageUpdateQueued;
    private CancellationTokenSource _recipeImageCancellation;
    private Task _liveImageUpdate;
    private Task _cameraStop;
    private Task _recipeImageUpdate;
    private long _activationStarted;

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
        InspectionImages images,
        IReadOnlyDictionary<HardwareArea, TeachingIoGroup[]> teachingIoGroups,
        ILogger<TeachingViewModel> logger)
    {
        _liveImageUpdate = Task.CompletedTask;
        _cameraStop = Task.CompletedTask;
        _recipeImageUpdate = Task.CompletedTask;
        _logger = logger;
        _settings = settings;
        _liveImageGate = new();
        _viewCancellation = new();
        _teachingIoGroups = teachingIoGroups;
        foreach (var unit in _teachingIoGroups)
        {
            foreach (var row in unit.Value.SelectMany(group => group.Outputs))
            {
                row.ViewCancellation = unit.Key == SelectedTeachingUnit
                    ? _viewCancellation.Token
                    : new(canceled: true);
            }
        }
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

        State = state;
        Machine = machine;
        Operations = operations;
        _store = store;
        _images = images;

        TeachCurrentPositionCommand = new AsyncRelayCommand(TeachCurrentPositionAsync, () => IsTeachCurrentPositionAllowed);
        MoveToPointCommand = new AsyncRelayCommand(MoveToPointAsync, () => IsMoveToPointAllowed);
        JogCommand = new AsyncRelayCommand<TeachingDirection>(JogAsync, IsMoveDirectionAllowed);
        StepCommand = new AsyncRelayCommand<TeachingDirection>(StepAsync, IsStepAllowed);
        JogStopCommand = new RelayCommand(JogStop);
        HomeCommand = new AsyncRelayCommand(HomeAsync, () => Machine.IsManualHomeAllowed(ActiveMotionGroup));
        MoveToHorizontalZCommand = new AsyncRelayCommand(MoveToHorizontalZAsync, () => IsMoveToHorizontalZAllowed);

        ToggleLiveViewCommand = new AsyncRelayCommand(ToggleLiveViewAsync, () => IsToggleLiveViewAllowed);
        GrabCommand = new AsyncRelayCommand(GrabAsync, () => IsGrabAllowed);
        ApplyLightCommand = new AsyncRelayCommand(ApplyLightAsync, () => IsApplyLightAllowed);
        AddBoltPointCommand = new RelayCommand(AddBoltPoint, () => IsAddBoltPointAllowed);
        RemoveBoltPointCommand = new RelayCommand(RemoveBoltPoint, () => IsRemoveBoltPointAllowed);
        MoveFasteningEarlierCommand = new RelayCommand(MoveFasteningEarlier, () => IsFasteningMoveAllowed(-1));
        MoveFasteningLaterCommand = new RelayCommand(MoveFasteningLater, () => IsFasteningMoveAllowed(1));
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
        recipeEditor.PropertyChanged += OnRecipeEditorChanged;
        recipeEditor.LoadCommand.PropertyChanged += OnRecipeEditorChanged;

        RefreshTeachingPoints();
        ShowRecipeImages();
    }

    private void OnRecipeEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecipeEditor.IsSaveAllowed) or nameof(IAsyncRelayCommand.IsRunning))
            SaveCommand.NotifyCanExecuteChanged();
        if (e.PropertyName != nameof(RecipeEditor.IsSaveAllowed))
            return;
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        GrabCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInspectionSelected))]
    [NotifyPropertyChangedFor(nameof(ActiveMotionGroup))]
    [NotifyPropertyChangedFor(nameof(TeachingIoGroups))]
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
        var started = Stopwatch.GetTimestamp();
        _activationStarted = started;
        _logger.LogInformation("Teaching open: unit={Unit}, PCB={Pcb}, recipe={Recipe}, bolts={Bolts}, images={Images}; begin.",
            SelectedTeachingUnit, SelectedPcb, Recipes.Current.Name, Recipes.Current.Pcb.BoltPoints.Count, Recipes.Current.CarrierImages.Count);
        CameraError = null;
        RecipeEditor.Refresh();
        _logger.LogInformation("Teaching open: recipe list read, elapsed={ElapsedMs:F1} ms.",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        var pointsStarted = Stopwatch.GetTimestamp();
        RefreshTeachingPoints();
        _logger.LogInformation("Teaching open: point lists refreshed, points={Points}, elapsed={ElapsedMs:F1} ms.",
            FilteredPoints.Count, Stopwatch.GetElapsedTime(pointsStarted).TotalMilliseconds);
        if (!PositionUpdatesActive)
            SubscribeMotionChanges();
        PositionUpdatesActive = true;
        OnPropertyChanged(nameof(Motion));
        ShowRecipeImages();
        NotifyManualTeachingCommands();
        _logger.LogInformation("Teaching open: activation finished, elapsed={ElapsedMs:F1} ms; image loading may continue.",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public void ReportViewReady(double xamlMilliseconds, double layoutMilliseconds)
    {
        _logger.LogInformation("Teaching open: UI ready; XAML construction={XamlMs:F1} ms, loaded to dispatcher idle={LayoutMs:F1} ms, since activation={TotalMs:F1} ms.",
            xamlMilliseconds, layoutMilliseconds, Stopwatch.GetElapsedTime(_activationStarted).TotalMilliseconds);
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
        IAsyncRelayCommand[] commands = [
            .. _commands,
            .. _teachingIoGroups.Values.SelectMany(groups => groups)
                .SelectMany(group => group.Outputs)
                .Select(row => row.ToggleOutputCommand),
        ];
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

    public IReadOnlyList<TeachingIoGroup> TeachingIoGroups => _teachingIoGroups[SelectedTeachingUnit];

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
        MoveFasteningEarlierCommand.NotifyCanExecuteChanged();
        MoveFasteningLaterCommand.NotifyCanExecuteChanged();
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
        NotifyManualTeachingCommands();
        OnPropertyChanged(nameof(SelectedBarcode));
        OnPropertyChanged(nameof(IsDataMatrixSelected));
    }

    [ObservableProperty]
    public partial string? SaveError { get; set; }

    public FasteningHead[] FasteningHeads { get; }

    public HeatSinkSlot[] HeatSinkSlots { get; }

    public HeatSinkSlot? SelectedBarcode => SelectedPoint?.Position.Target == TeachingTarget.DataMatrix ? SelectedPcb : null;

    public bool IsDataMatrixSelected => SelectedBarcode is not null;

    public bool BoltPointEditorVisible => IsFasteningSelected || IsInspectionSelected;

    private bool IsBoltSelected => IsInspectionSelected && SelectedPoint?.Position.Bolt is not null;

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
                    point => point.Position.Bolt is { InspectionPosition: null });
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
                Point(TeachingTarget.ShootingSafeZ, TeachMode.ZOnly),
                Point(TeachingTarget.ShootingHeadFasteningZ, TeachMode.ZOnly),
                Point(TeachingTarget.ShootingHeadUpperLeftLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.ShootingHeadLowerRightLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.PickupHeadFasteningZ, TeachMode.ZOnly),
                Point(TeachingTarget.PickupHeadUpperLeftLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.PickupHeadLowerRightLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.BoltPickup, TeachMode.Full),
                .. Recipes.Current.Pcb.FasteningPoints.Where(bolt => bolt.HeatSink == SelectedPcb)
                    .Select(bolt => Point(TeachingTarget.BoltPosition, TeachMode.XYOnly, bolt)),
            ],
            HardwareArea.InspectionGantry => [
                Point(TeachingTarget.CarrierUpperLeftLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.CarrierLowerRightLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.InspectionWaiting, TeachMode.XYOnly),
                Point(TeachingTarget.NgCarrierPickup, TeachMode.XYOnly),
                Point(TeachingTarget.NgShuttlePlace, TeachMode.XYOnly),
                .. InspectionPoint.ForPcb(Recipes.Current, SelectedPcb)
                    .Select(point => Point(point.IsDataMatrix ? TeachingTarget.DataMatrix : TeachingTarget.BoltReference,
                        TeachMode.Image, point.Bolt)),
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
                ? point.Position.Bolt?.Id == selectedBolt.Id
                : point.Position.Target == selectedTarget)
            ?? NextTeachingPoint
                ?? FilteredPoints.FirstOrDefault();
    }

    public IRelayCommand MoveFasteningEarlierCommand { get; }

    private void MoveFasteningEarlier()
    {
        MoveFasteningPoint(-1);
    }

    public IRelayCommand MoveFasteningLaterCommand { get; }

    private void MoveFasteningLater()
    {
        MoveFasteningPoint(1);
    }

    private bool IsFasteningMoveAllowed(int offset)
    {
        if (!State.SetupEditingEnabled || !IsFasteningSelected
            || SelectedPoint?.Position is not { Target: TeachingTarget.BoltPosition, Bolt: { } selected })
            return false;
        var bolts = Recipes.Current.Pcb.FasteningPoints.ToList();
        var index = bolts.FindIndex(bolt => bolt.Id == selected.Id);
        var target = index + offset;
        return index >= 0 && target >= 0 && target < bolts.Count
            && bolts[target].Head == selected.Head
            && bolts[target].HeatSink == selected.HeatSink;
    }

    private void MoveFasteningPoint(int offset)
    {
        if (!IsFasteningMoveAllowed(offset))
            return;
        var pcb = Recipes.Current.Pcb;
        var bolts = pcb.FasteningPoints.ToList();
        var index = bolts.FindIndex(bolt => bolt.Id == SelectedPoint!.Position.Bolt!.Id);
        (bolts[index], bolts[index + offset]) = (bolts[index + offset], bolts[index]);
        pcb.FasteningOrder = bolts.Select(bolt => bolt.Id).ToList();
        RefreshTeachingPoints();
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
        var bolt = new BoltPoint
        {
            HeatSink = SelectedPcb,
            Head = NewFasteningHead,
            BrightnessThreshold = Recipes.Current.BoltInspection.BrightnessThreshold,
            MinimumBrightRatio = Recipes.Current.BoltInspection.MinimumBrightRatio,
        };
        Recipes.Current.Pcb.BoltPoints.Add(bolt);
        RefreshTeachingPoints();
        SelectedPoint = FilteredPoints.First(
            point => point.Position.Target == TeachingTarget.BoltReference
                && ReferenceEquals(point.Position.Bolt, bolt));
    }

    private bool IsAddBoltPointAllowed => State.SetupEditingEnabled && IsInspectionSelected;

    public IRelayCommand RemoveBoltPointCommand { get; }

    private void RemoveBoltPoint()
    {
        if (SelectedPoint?.Position.Bolt is not { } selectedBolt)
            return;
        var boltId = selectedBolt.Id;
        Recipes.Current.Pcb.BoltPoints.Remove(selectedBolt);
        Recipes.Current.Pcb.FasteningOrder.RemoveAll(id => id == boltId);
        Recipes.Current.CarrierImages.RemoveAll(fov =>
            !fov.IsBarcode && fov.BoltId == boltId && fov.HeatSink == SelectedPcb);
        CarrierImages = CarrierImages.Where(image =>
            image.Metadata.IsBarcode || image.Metadata.BoltId != boltId || image.Metadata.HeatSink != SelectedPcb)
            .ToArray();
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
                case { Position.Mode: not TeachMode.Image } point
                    when Motion.Feedback.IsReady && IsReadTeachingPositionAllowed(point, live: true):
                    SaveError = null;
                    var current = Motion.Feedback.Position;
                    operation.Token.ThrowIfCancellationRequested();
                    point.Teach(current.X, current.Y, current.Z);
                    RefreshPointPositions();
                    if (point.Storage == TeachingStorage.Machine
                        && !await SaveSettingsAsync(operation.Token, point.Setting!))
                        return;
                    operation.Token.ThrowIfCancellationRequested();
                    if (point.Position.Target == TeachingTarget.CarrierUpperLeftLocatingPin)
                    {
                        SelectedPoint = FilteredPoints.First(
                            candidate => candidate.Position.Target == TeachingTarget.CarrierLowerRightLocatingPin);
                    }
                    NotifyManualTeachingCommands();
                    break;
                case { Position.Mode: not TeachMode.Image }:
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
            return SelectedPoint is { Position.Mode: not TeachMode.Image } point
                && State.SetupEditingEnabled
                && State.ManualMode
                && IsReadTeachingPositionAllowed(point, live: false);
        }
    }

    private bool IsReadTeachingPositionAllowed(TeachingPoint point, bool live)
    {
        MotionAxis[] axes = point.Position.Mode switch
        {
            TeachMode.Full => [MotionAxis.X, MotionAxis.Y, MotionAxis.Z],
            TeachMode.XYOnly => [MotionAxis.X, MotionAxis.Y],
            TeachMode.ZOnly => [MotionAxis.Z],
            _ => [],
        };
        return axes.Length > 0 && axes.All(axis =>
            (live ? Motion.Feedback.GetAxisState(axis) : Motion.Axes[axis].State)
                is { Homed: true, InMotion: false });
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
                () => !State.AutoMode,
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

    private bool IsSaveAllowed => State.SetupEditingEnabled && RecipeEditor.IsSaveAllowed && !RecipeEditor.LoadCommand.IsRunning;

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
                    return "Z → PCB Rotation Height";
                case MotionGroup.PcbPlacementHandler:
                    return "Z → PCB Handoff Height";
                default:
                    return "Z → Common Safe Z";
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
                case MotionGroup.PcbPlacementHandler when _pcbPlacement.Lift != StationCylinderState.Up:
                    return TeachingMotionHint.RaisePlacementCylinders;
                case MotionGroup.BoltFastening when !_fasteningStation.IsHorizontalMoveAllowed:
                    return TeachingMotionHint.RaiseFasteningHeads;
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

    private bool IsJogAllowed(MotionAxis axis)
    {
        return !State.IsRunning
            && Machine.IsManualMotionReady(ActiveMotionGroup, live: false)
            && Motion.Feedback.Axes.Contains(axis)
            && ActiveMotionGroup switch
            {
                MotionGroup.PcbSupply or MotionGroup.BoltFastening => true,
                MotionGroup.PcbPlacementHandler => axis == MotionAxis.Z
                    || _pcbPlacement.Lift == StationCylinderState.Up,
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
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(group),
                cancellationToken,
                viewCancellation);
            if (operation is null)
                return;
            activeCancellation = operation.Token;
            if (State.IsRunningFor(includeOperations: false))
                return;
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
            || _pcbPlacement.Lift == StationCylinderState.Up);

    public IAsyncRelayCommand MoveToHorizontalZCommand { get; }

    private async Task MoveToHorizontalZAsync(CancellationToken cancellationToken)
    {
        var commandGroup = ActiveMotionGroup;
        var viewToken = _viewCancellation.Token;
        var activeToken = cancellationToken;
        try
        {
            SaveError = null;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            if (State.IsRunningFor(includeOperations: false))
                return;
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
        var (axis, sign) = Resolve(direction);
        if (!IsJogAllowed(axis))
            return false;
        var position = Motion.Position;
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
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            if (State.IsRunningFor(includeOperations: false))
                return;
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
                    await Inspection.MoveAxisAsync(axis, target, JogSpeed, operation.Token);
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
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            if (State.IsRunningFor(includeOperations: false))
                return;
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
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            if (State.IsRunningFor(includeOperations: false))
                return;
            operation.Token.ThrowIfCancellationRequested();
            var position = point.Position;
            switch (position.MotionGroup)
            {
                case MotionGroup.PcbSupply:
                    await _pcbSupply.MoveToTeachingPositionAsync(position, point.MovePosition, operation.Token);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    await _pcbPlacement.MoveToTeachingPositionAsync(position, point.MovePosition, operation.Token);
                    break;
                case MotionGroup.BoltFastening:
                    await _fasteningStation.MoveToTeachingPositionAsync(position, point.MovePosition, operation.Token);
                    break;
                case MotionGroup.InspectionGantry when position.Target == TeachingTarget.NgCarrierPickup:
                    await Inspection.MoveToCarrierAsync(NgTransferDestination.Station, operation.Token);
                    break;
                case MotionGroup.InspectionGantry when position.Bolt is { } bolt:
                    await Inspection.MoveToBoltAsync(bolt, operation.Token);
                    break;
                case MotionGroup.InspectionGantry when position.Target == TeachingTarget.DataMatrix:
                    await Inspection.MoveToBarcodeAsync(SelectedPcb, operation.Token);
                    break;
                case MotionGroup.InspectionGantry:
                    await Inspection.MoveToAsync(point.MovePosition, cancellationToken: operation.Token);
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
                    && _pcbPlacement.Lift != StationCylinderState.Up:
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
                    return _pcbPlacement.Lift == StationCylinderState.Up;
                case MotionGroup.BoltFastening:
                    return _fasteningStation.IsHorizontalMoveAllowed;
                case MotionGroup.InspectionGantry:
                    return Inspection.IsRaised;
                default:
                    return false;
            }
        }
    }

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

    public BitmapSource? CameraImage => Inspection.IsLiveView ? LiveImage
        : SelectedPoint?.Inspection?.GetImage(CarrierImages);

    public IAsyncRelayCommand GrabCommand { get; }

    private async Task GrabAsync(CancellationToken cancellationToken)
    {
        if (IsGrabAllowed)
            await CaptureTeachingImageAsync(recordPosition: false, cancellationToken);
    }

    private bool IsGrabAllowed => IsRecordImagePositionAllowed && SelectedPoint?.Position.HasPosition == true
        && SelectedPoint.Inspection?.GetImage(CarrierImages) is not null;

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
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                throw new InvalidOperationException("Recording was not started because another operation is active. Try again after it finishes.");
            activeToken = operation.Token;
            if (State.IsRunningFor(includeOperations: false))
                throw new InvalidOperationException("Recording was not started because the machine is busy. Wait for motion to stop, then record again.");
            operation.Token.ThrowIfCancellationRequested();
            await _recipeImageUpdate;
            operation.Token.ThrowIfCancellationRequested();
            if (CarrierImages.Count != Recipes.Current.CarrierImages.Count)
                throw new InvalidOperationException("Wait for the saved teaching images to load before capturing.");
            if (point.Inspection!.ImageCount > 1)
                throw new InvalidOperationException("Multiple reference images are linked to this point. Resolve the duplicate before capturing.");
            await _cameraStop;
            operation.Token.ThrowIfCancellationRequested();
            var captured = await Inspection.CaptureCarrierImageAsync(operation.Token, lightLevel);
            _logger.LogInformation("Teaching image captured: PCB={Pcb}, point={Point}, X={X}, Y={Y}.",
                pcb, point.Name, captured.Center.X, captured.Center.Y);
            var image = await Task.Run(() => InspectionPreview.CreateBitmap(captured.Frame), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var images = CarrierImages.ToList();
            var index = images.FindIndex(tile => point.Inspection.Matches(tile.Metadata));
            var previous = index >= 0 ? images[index].Metadata : null;
            if (!recordPosition && previous is null)
                throw new InvalidOperationException("No reference image. Use Move to Selected Point, then Save X/Y + Image.");
            var metadata = new CarrierImageTile
            {
                Number = previous?.Number ?? (images.Count == 0 ? 1 : images.Max(tile => tile.Metadata.Number) + 1),
                Center = barcode ? (recordPosition ? captured.Center : previous!.Center) : null,
                HeatSink = pcb,
                BoltId = bolt?.Id,
                IsBarcode = barcode,
                Region = previous?.Region ?? PixelRegion.CenteredSquare(
                    image.PixelWidth, image.PixelHeight, Math.Min(image.PixelWidth, image.PixelHeight) / 4),
            };
            var replacement = new CarrierImageTileView(metadata, image);
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
        var started = Stopwatch.GetTimestamp();
        try
        {
            await previous;
            _logger.LogInformation("Teaching images: previous load drained, elapsed={ElapsedMs:F1} ms.",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();
            if (!PositionUpdatesActive || !IsInspectionSelected)
                return;
            var loadStarted = Stopwatch.GetTimestamp();
            var images = await _images.LoadRecipeAsync(Recipes.Current, cancellationToken);
            _logger.LogInformation("Teaching images: read/decode finished, count={Count}, elapsed={ElapsedMs:F1} ms.",
                images.Length, Stopwatch.GetElapsedTime(loadStarted).TotalMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();
            // Point edits may finish while image decoding or its UI continuation is pending.
            var publishStarted = Stopwatch.GetTimestamp();
            CarrierImages = images.Where(image => Recipes.Current.CarrierImages.Contains(image.Metadata)).ToArray();
            _logger.LogInformation("Teaching images: UI collection updated, elapsed={ElapsedMs:F1} ms, total={TotalMs:F1} ms.",
                Stopwatch.GetElapsedTime(publishStarted).TotalMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Teaching images: cancelled, elapsed={ElapsedMs:F1} ms.",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
