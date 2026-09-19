using System;
using System.Collections.Generic;
using System.ComponentModel;
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
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;

namespace IBTM.UI;

public partial class TeachingViewModel : ObservableObject
{
    private readonly PcbSupplyHandler _supplyHandler;
    private readonly PcbSupplySettings _supplySettings;
    private readonly PcbBufferSettings _bufferSettings;
    private IReadOnlyList<TeachingPoint> _handoffPoints;
    private readonly PcbPlacementHandler _placementHandler;
    private readonly BoltFasteningGantry _fasteningGantry;
    private readonly InspectionGantry _inspectionGantry;
    private readonly InspectionGantrySettings _inspectionGantrySettings;
    private readonly CarrierReferenceSettings _carrierReference;
    private readonly PcbPlacementHandlerSettings _placementSettings;
    private readonly BoltFasteningSettings _fasteningSettings;
    private readonly NgCarrierTransferSettings _ngTransferSettings;
    private readonly NgCarrierTransfer _ngTransfer;
    private readonly NgCarrierMove _ngCarrierMove;
    private CancellationTokenSource _recipeImageCancellation;
    private Task _recipeImageUpdate = Task.CompletedTask;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInspectionSelected))]
    [NotifyPropertyChangedFor(nameof(HandoffSaveVisible))]
    [NotifyPropertyChangedFor(nameof(ActiveMotionGroup))]
    [NotifyPropertyChangedFor(nameof(TeachingIoGroups))]
    [NotifyCanExecuteChangedFor(nameof(ToggleLiveViewCommand))]
    [NotifyCanExecuteChangedFor(nameof(CaptureCarrierImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBoltPointCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReturnFromPickupCommand))]
    public partial HardwareArea SelectedTeachingUnit { get; set; } = HardwareArea.InspectionGantry;

    [ObservableProperty]
    public partial FasteningHead NewFasteningHead { get; set; } = FasteningHead.Shooting;

    [ObservableProperty]
    public partial IReadOnlyList<TeachingPoint> FilteredPoints { get; set; }
    [ObservableProperty]
    public partial HeatSinkSlot SelectedPcb { get; set; } = HeatSinkSlot.HeatSink1;
    [ObservableProperty]
    public partial BitmapSource? LiveImage { get; set; }

    [ObservableProperty]
    public partial int SelectedCameraTab { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRulerResolutionCommand))]
    public partial IReadOnlyList<CarrierImageTileView> CarrierImages { get; set; }

    [ObservableProperty]
    public partial double MillimetersPerPixel { get; set; }

    public TeachingViewModel(
        PcbSupplyHandler supplyHandler,
        PcbSupplySettings supplySettings,
        PcbBufferSettings bufferSettings,
        PcbPlacementHandler placementHandler,
        BoltFasteningGantry fasteningGantry,
        InspectionGantry inspectionGantry,
        BoltInspector boltInspector,
        MachineState state,
        MachineController machine,
        OperationCancellation operations,
        InspectionGantrySettings inspectionGantrySettings,
        CarrierReferenceSettings carrierReference,
        PcbPlacementHandlerSettings placementSettings,
        BoltFasteningSettings fasteningSettings,
        NgCarrierTransferSettings ngTransferSettings,
        NgCarrierTransfer ngTransfer,
        NgCarrierMove ngCarrierMove,
        RecipeEditor recipeEditor,
        RecipeManager recipes,
        MachineStore store,
        IReadOnlyDictionary<HardwareArea, IoStatus[]> ioGroups,
        IReadOnlyDictionary<HardwareArea, IReadOnlyDictionary<OutputIo, TeachingOutput>> teachingOutputs)
    {
        _liveImageGate = new();
        _viewCancellation = new();
        _teachingIoGroups = [];
        MoveModes = Enum.GetValues<TeachingMoveMode>();
        _handoffPoints = [];
        _recipeImageCancellation = new();
        FilteredPoints = [];
        TeachingUnits = [
            HardwareArea.PcbSupply,
            HardwareArea.PcbPlacementHandler,
            HardwareArea.BoltFastening,
            HardwareArea.InspectionGantry,
            HardwareArea.NgCarrierTransfer,
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
        MoveToHorizontalZCommand = new AsyncRelayCommand(MoveToHorizontalZAsync, () => IsJogZAllowed);

        State = state;
        Machine = machine;
        Operations = operations;
        _store = store;
        _ioGroups = ioGroups;
        _teachingOutputs = teachingOutputs;

        MeasureImageCommand = new RelayCommand<ImageRuler>(MeasureImage, IsMeasureImageAllowed);
        ApplyRulerResolutionCommand = new AsyncRelayCommand(ApplyRulerResolutionAsync, () => IsApplyRulerResolutionAllowed);
        ReadDataMatrixCommand = new AsyncRelayCommand(ReadDataMatrixAsync, () => IsReadDataMatrixAllowed);
        DrawFovRegionCommand = new AsyncRelayCommand<Rect>(DrawFovRegionAsync, IsDrawFovRegionAllowed);
        TeachFovRegionCommand = new AsyncRelayCommand<Rect>(TeachFovRegionAsync, IsTeachFovRegionAllowed);
        ToggleLiveViewCommand = new AsyncRelayCommand(ToggleLiveViewAsync, () => IsToggleLiveViewAllowed);
        CaptureCarrierImageCommand = new AsyncRelayCommand(CaptureCarrierImageAsync, () => IsCaptureCarrierImageAllowed);
        CaptureInspectionCommand = new AsyncRelayCommand(CaptureInspectionAsync, () => IsCaptureInspectionAllowed);
        ReinspectImageCommand = new AsyncRelayCommand(ReinspectImageAsync, () => IsReinspectImageAllowed);
        AddBoltPointCommand = new RelayCommand(AddBoltPoint, () => IsAddBoltPointAllowed);
        RemoveBoltPointCommand = new RelayCommand(RemoveBoltPoint, () => IsRemoveBoltPointAllowed);
        SaveHandoffSetupCommand = new AsyncRelayCommand(SaveHandoffSetupAsync, () => IsTeachingEditAllowed);
        ReturnFromPickupCommand = new AsyncRelayCommand(ReturnFromPickupAsync, () => IsReturnFromPickupAllowed);

        _supplyHandler = supplyHandler;
        _supplySettings = supplySettings;
        _bufferSettings = bufferSettings;
        _placementHandler = placementHandler;
        _fasteningGantry = fasteningGantry;
        _inspectionGantry = inspectionGantry;
        Inspector = boltInspector;
        _inspectionGantrySettings = inspectionGantrySettings;
        _carrierReference = carrierReference;
        _placementSettings = placementSettings;
        _fasteningSettings = fasteningSettings;
        _ngTransferSettings = ngTransferSettings;
        _ngTransfer = ngTransfer;
        _ngCarrierMove = ngCarrierMove;
        RecipeEditor = recipeEditor;
        Recipes = recipes;
        Preview = new(boltInspector, recipes);
        CarrierImages = [];
        Preview.PropertyChanged += (_, e) =>
        {
            ReinspectImageCommand.NotifyCanExecuteChanged();
        };

        MillimetersPerPixel = Recipes.Current.CarrierImageMillimetersPerPixel;

        boltInspector.FrameReady += UpdateLiveImage;
        boltInspector.LiveViewChanged += OnLiveViewChanged;
        CaptureCarrierImageCommand.PropertyChanged += OnInspectionCommandChanged;
        CaptureInspectionCommand.PropertyChanged += OnInspectionCommandChanged;
        state.DisplayChanged += QueueManualCommandRefresh;
        recipes.Changed += OnRecipeChanged;
        recipeEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RecipeEditor.IsSaveAllowed))
                return;
            CaptureCarrierImageCommand.NotifyCanExecuteChanged();
            ApplyRulerResolutionCommand.NotifyCanExecuteChanged();
            DrawFovRegionCommand.NotifyCanExecuteChanged();
            TeachFovRegionCommand.NotifyCanExecuteChanged();
        };

        RefreshHandoffPoints();
        RefreshTeachingPoints();
        ShowRecipeImages();
    }

    public string? CameraError
    {
        get => field ?? Inspector.LiveViewError?.Message;
        private set => SetProperty(ref field, value);
    }

    public BoltInspector Inspector { get; }

    public RecipeEditor RecipeEditor { get; }
    public RecipeManager Recipes { get; }
    public InspectionPreview Preview { get; }

    public HeatSinkSlot? SelectedBarcode => SelectedPoint?.Position.Target == TeachingTarget.DataMatrix ? SelectedPcb : null;

    public bool IsDataMatrixSelected => SelectedBarcode is not null;

    public HardwareArea[] TeachingUnits { get; }
    public FasteningHead[] FasteningHeads { get; }
    public HeatSinkSlot[] HeatSinkSlots { get; }

    public BoltFasteningRecipe BoltRecipe => Recipes.Current.BoltFastening;

    public BoltInspectionRecipe InspectionRecipe => Recipes.Current.BoltInspection;

    public TeachingSaveBehavior SaveBehavior
    {
        get
        {
            switch (SelectedPoint?.Position)
            {
                case { Target: TeachingTarget.BoltPickup }:
                    return TeachingSaveBehavior.BoltPickup;
                case { Target: TeachingTarget.ShootingHeadFasteningZ or TeachingTarget.PickupHeadFasteningZ }:
                    return TeachingSaveBehavior.FasteningZ;
                case { Target: TeachingTarget.DataMatrix }:
                    return TeachingSaveBehavior.BarcodeFov;
                case { Target: TeachingTarget.SupplyBufferHandoff }:
                    return TeachingSaveBehavior.SupplyHandoff;
                case { Target: TeachingTarget.PlacementBufferHandoff }:
                    return TeachingSaveBehavior.PlacementHandoff;
                case { Target: TeachingTarget.SupplyCarrierY }:
                    return TeachingSaveBehavior.SupplyCarrierY;
                case { Target: TeachingTarget.NgCarrierPickup }:
                    return TeachingSaveBehavior.NgPickup;
                case { Target: TeachingTarget.BoltPosition }:
                    return TeachingSaveBehavior.BoltPosition;
                case { Target: TeachingTarget.CarrierUpperLeftLocatingPin or TeachingTarget.CarrierLowerRightLocatingPin }:
                    return TeachingSaveBehavior.CameraCenter;
                case { Mode: TeachMode.Image }:
                    return TeachingSaveBehavior.Image;
                case { Storage: TeachingStorage.Buffer }:
                    return TeachingSaveBehavior.Buffer;
                case { Storage: TeachingStorage.Machine }:
                    return TeachingSaveBehavior.Machine;
                default:
                    return TeachingSaveBehavior.Recipe;
            }
        }
    }

    public bool IsInspectionSelected => SelectedTeachingUnit == HardwareArea.InspectionGantry;

    public bool BoltPointEditorVisible => BoltPresetEditorVisible || IsInspectionSelected;

    public bool BoltPresetEditorVisible => SelectedTeachingUnit == HardwareArea.BoltFastening;

    public bool IsBoltSelected => IsInspectionSelected && SelectedPoint?.Position.Bolt is not null;

    public bool HandoffSaveVisible => SelectedTeachingUnit is HardwareArea.PcbSupply or HardwareArea.PcbPlacementHandler;

    partial void OnSelectedPcbChanged(HeatSinkSlot value)
    {
        RefreshTeachingPoints();
        NotifyManualTeachingCommands();
    }

    partial void OnSelectedTeachingUnitChanged(HardwareArea value)
    {
        CancelTeaching();

        if (Inspector.IsLiveView || ToggleLiveViewCommand.IsRunning)
            _ = RequestCameraStopAsync();

        RefreshTeachingPoints();
        ShowRecipeImages();
        OnPropertyChanged(nameof(Motion));
        OnPropertyChanged(nameof(HorizontalZName));
        OnPropertyChanged(nameof(BoltPointEditorVisible));
        OnPropertyChanged(nameof(BoltPresetEditorVisible));
        NotifyManualTeachingCommands();
    }

    private void OnPointTaught(TeachingPoint point)
    {
        if (point.Position.Target == TeachingTarget.CarrierUpperLeftLocatingPin)
        {
            SelectedPoint = FilteredPoints.First(
                candidate => candidate.Position.Target == TeachingTarget.CarrierLowerRightLocatingPin);
        }
    }

    public IRelayCommand AddBoltPointCommand { get; }

    private void AddBoltPoint()
    {
        var draft = SelectedFov?.Metadata is { IsBarcode: false, BoltNumber: null } ? SelectedFov : null;
        var draftRegion = FovRegion;
        var number = Recipes.Current.Pcb.GetBolts(SelectedPcb).Select(bolt => bolt.Number).DefaultIfEmpty().Max() + 1;
        var bolt = new BoltPoint
        {
            Number = number,
            HeatSink = SelectedPcb,
            Head = NewFasteningHead,
            BrightnessThreshold = InspectionRecipe.BrightnessThreshold,
            MinimumBrightRatio = InspectionRecipe.MinimumBrightRatio,
        };
        Recipes.Current.Pcb.BoltPoints.Add(bolt);
        RefreshTeachingPoints();
        SelectedPoint = FilteredPoints.First(
            point => point.BoltNumber == number && point.Position.Target == TeachingTarget.BoltReference);
        if (draft is not null)
        {
            SelectedFov = draft;
            FovRegion = draftRegion;
        }
    }

    private bool IsAddBoltPointAllowed => IsTeachingEditAllowed && IsInspectionSelected;

    public IRelayCommand RemoveBoltPointCommand { get; }

    private void RemoveBoltPoint()
    {
        var number = SelectedPoint!.BoltNumber;
        Recipes.Current.Pcb.BoltPoints.RemoveAll(bolt => bolt.Number == number && bolt.HeatSink == SelectedPcb);
        foreach (var fov in Recipes.Current.CarrierImages.Where(fov =>
            !fov.IsBarcode && fov.BoltNumber == number && fov.HeatSink == SelectedPcb))
        {
            fov.BoltNumber = null;
            fov.Region = null;
        }
        OnSelectedFovChanged(SelectedFov);
        RefreshTeachingPoints();
    }

    private bool IsRemoveBoltPointAllowed
    {
        get
        {
            return IsTeachingEditAllowed
                && IsInspectionSelected
                && SelectedPoint?.Position.Target == TeachingTarget.BoltReference;
        }
    }

    public void Activate()
    {
        CameraError = null;
        RefreshHandoffPoints();
        RecipeEditor.Refresh();
        RefreshTeachingPoints();
        PositionUpdatesActive = true;
        OnPropertyChanged(nameof(Motion));
        ShowRecipeImages();
        NotifyManualTeachingCommands();
    }

    public void Deactivate()
    {
        PositionUpdatesActive = false;
        // Page/application shutdown must still receive an unconfirmed device stop.
        CancelTeaching(reportDeviceFailure: false);
        _recipeImageCancellation.Cancel();
        CarrierImages = [];
        Preview.Clear();

        _ = RequestCameraStopAsync();
    }

    public async Task ShutdownAsync()
    {
        IAsyncRelayCommand[] commands = [
            ToggleLiveViewCommand,
            JogCommand,
            HomeCommand,
            StepCommand,
            MoveToHorizontalZCommand,
            MoveToPointCommand,
            ReturnFromPickupCommand,
            CaptureCarrierImageCommand,
            ApplyRulerResolutionCommand,
            CaptureInspectionCommand,
            ReinspectImageCommand,
            ReadDataMatrixCommand,
            DrawFovRegionCommand,
            TeachFovRegionCommand,
            TeachCurrentPositionCommand,
            SaveHandoffSetupCommand,
            .. OutputCommands,
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

    private void RefreshTeachingPoints()
    {
        var selectedTarget = SelectedPoint?.Position.Target;
        var selectedBolt = SelectedPoint?.BoltNumber;
        TeachingPosition[] positions = SelectedTeachingUnit switch
        {
            HardwareArea.PcbSupply => _supplySettings.GetTeachingPositions(Recipes.Current.PcbSupply)
                .Where(position => position.Storage != TeachingStorage.Buffer).ToArray(),
            HardwareArea.PcbPlacementHandler
                => _placementSettings.GetTeachingPositions(Recipes.Current.PcbPlacement),
            HardwareArea.BoltFastening
                => _fasteningSettings.GetTeachingPositions(
                    Recipes.Current.Pcb,
                    SelectedPcb,
                    _carrierReference),
            HardwareArea.InspectionGantry
                => [
                    .. _inspectionGantrySettings.GetTeachingPositions(_carrierReference),
                    new(
                        TeachingTarget.DataMatrix,
                        MotionGroup.InspectionGantry,
                        TeachMode.Image,
                        () => Inspector.HasBarcodeRegion(SelectedPcb)
                        ? Inspector.GetBarcodeFov(SelectedPcb).Center
                        : new(),
                        apply: null,
                        isDefined: () => Inspector.HasBarcodeRegion(SelectedPcb)),
                    .. Recipes.Current.Pcb.GetBolts(SelectedPcb).Select(bolt =>
                    new TeachingPosition(
                        TeachingTarget.BoltReference,
                        MotionGroup.InspectionGantry,
                        TeachMode.Image,
                        () => Inspector.HasPosition(bolt) ? Inspector.GetFov(bolt).Center : new(),
                        apply: null,
                        isDefined: () => Inspector.HasPosition(bolt))
                    { Bolt = bolt }),
            ],
            HardwareArea.NgCarrierTransfer => _ngTransferSettings.GetTeachingPositions(),
            _ => throw new ArgumentOutOfRangeException(nameof(SelectedTeachingUnit)),
        };
        FilteredPoints = positions.Select(position => new TeachingPoint(position))
            .Concat(_handoffPoints.Where(point => point.Position.MotionGroup == ActiveMotionGroup))
            .OrderBy(point => point.Group)
            .ThenBy(point => point.Position.Target == TeachingTarget.PlacementBufferHandoff ? 0 : 1)
            .ToArray();
        SelectedPoint = FilteredPoints.FirstOrDefault(
            point => point.Position.Target == selectedTarget && point.BoltNumber == selectedBolt)
            ?? NextTeachingPoint
                ?? FilteredPoints.FirstOrDefault();
    }

    private TeachingPoint? NextTeachingPoint
    {
        get
        {
            switch (true)
            {
                case true when !IsInspectionSelected:
                    return null;
                case true when !Inspector.HasBarcodeRegion(SelectedPcb):
                    return FilteredPoints.FirstOrDefault(
                        point => point.Position.Target == TeachingTarget.DataMatrix);
                default:
                    return Recipes.Current.CarrierImages.Count == 0
                        ? null
                        : FilteredPoints.FirstOrDefault(
                            point => point.Position.Target == TeachingTarget.BoltReference && !point.Position.HasPosition);
            }
        }
    }

    partial void OnSelectedPointChanged(TeachingPoint? oldValue, TeachingPoint? newValue)
    {
        CancelTeaching();
        NotifyPointSelectionCommands();
        OnPropertyChanged(nameof(SaveBehavior));
        NotifyManualTeachingCommands();
        CaptureInspectionCommand.Cancel();
        ReinspectImageCommand.Cancel();
        ReadDataMatrixCommand.Cancel();
        DataMatrixResult = null;
        OnPropertyChanged(nameof(SelectedBarcode));
        OnPropertyChanged(nameof(IsDataMatrixSelected));
        OnPropertyChanged(nameof(IsBoltSelected));
        if (!_selectingFovTarget)
            SelectFovForTeachingPoint();
        ReadDataMatrixCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(FovRegion));
        OnPropertyChanged(nameof(FovRoiLabel));
        CaptureInspectionCommand.NotifyCanExecuteChanged();
        RemoveBoltPointCommand.NotifyCanExecuteChanged();
        TeachFovRegionCommand.NotifyCanExecuteChanged();
    }

    partial void OnMillimetersPerPixelChanged(double value)
    {
        Recipes.Current.CarrierImageMillimetersPerPixel = value;
        CaptureCarrierImageCommand.NotifyCanExecuteChanged();
        CaptureInspectionCommand.NotifyCanExecuteChanged();
        TeachFovRegionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(FovRegion));
        OnPropertyChanged(nameof(FovRoiLabel));
    }

    partial void OnCarrierImagesChanged(IReadOnlyList<CarrierImageTileView> value)
    {
        SelectFovForTeachingPoint();
    }

    private void OnRecipeChanged()
    {
        CameraError = null;
        if (Inspector.IsLiveView || ToggleLiveViewCommand.IsRunning)
            _ = RequestCameraStopAsync();
        SelectedPoint = null;
        if (SelectedPcb == HeatSinkSlot.HeatSink1)
            RefreshTeachingPoints();
        else
            SelectedPcb = HeatSinkSlot.HeatSink1;
        OnPropertyChanged(nameof(BoltRecipe));
        OnPropertyChanged(nameof(InspectionRecipe));
        MillimetersPerPixel = Recipes.Current.CarrierImageMillimetersPerPixel;
        ShowRecipeImages();
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
            System.Diagnostics.Trace.TraceError("Teaching recipe image load failed. {0}", exception);
            if (!cancellationToken.IsCancellationRequested)
                CameraError ??= exception.Message;
        }
    }

    private void RefreshPointPositions()
    {
        foreach (var point in FilteredPoints.Where(point => point.Position.Storage != TeachingStorage.Buffer))
            point.Refresh();
        OnPropertyChanged(nameof(FovRegion));
    }

    private void RefreshHandoffPoints()
    {
        TeachingPosition[] positions = [
            .. _supplySettings.GetTeachingPositions(Recipes.Current.PcbSupply)
                .Where(position => position.Storage == TeachingStorage.Buffer),
            _placementSettings.GetBufferTeachingPosition(),
            .. _bufferSettings.GetTeachingPositions(),
        ];
        _handoffPoints = positions.OrderBy(position => position.MotionGroup)
            .Select(position => new TeachingPoint(position)).ToArray();
    }

    public IAsyncRelayCommand SaveHandoffSetupCommand { get; }

    private async Task SaveHandoffSetupAsync(CancellationToken cancellationToken)
    {
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (!(State.SetupEditingEnabled))
                return;
            using var operation = Machine.BeginManualOperation(
                () => State.ManualMode,
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            foreach (var point in _handoffPoints)
                point.Apply();
            await SaveSettingsAsync(operation.Token, _handoffPoints.Select(point => point.Position.Setting!).Distinct().ToArray());
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
}
