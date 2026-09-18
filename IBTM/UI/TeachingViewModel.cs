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
    private IReadOnlyList<TeachingPoint> _handoffPoints = [];
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
    private CancellationTokenSource _recipeImageCancellation = new();
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
    private HardwareArea _selectedTeachingUnit = HardwareArea.InspectionGantry;

    [ObservableProperty]
    private FasteningHead _newFasteningHead = FasteningHead.Shooting;

    [ObservableProperty]
    private IReadOnlyList<TeachingPoint> _filteredPoints = [];
    [ObservableProperty]
    private HeatSinkSlot _selectedPcb = HeatSinkSlot.HeatSink1;
    [ObservableProperty]
    private BitmapSource? _liveImage;

    [ObservableProperty]
    private int _selectedCameraTab;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRulerResolutionCommand))]
    private IReadOnlyList<CarrierImageTileView> _carrierImages = [];

    [ObservableProperty]
    private double _millimetersPerPixel;

    private string? _cameraError;

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
        MachineStore store,
        IReadOnlyDictionary<HardwareArea, IoStatus[]> ioGroups,
        IReadOnlyDictionary<HardwareArea, IReadOnlyDictionary<OutputIo, TeachingOutput>> teachingOutputs)
    {
        TeachCurrentPositionCommand = new AsyncRelayCommand(TeachCurrentPositionAsync, CanTeachCurrentPosition);
        MoveToPointCommand = new AsyncRelayCommand(MoveToPointAsync, CanMoveToPoint);
        SelectPreviousPointCommand = new RelayCommand(SelectPreviousPoint, CanSelectPreviousPoint);
        SelectNextPointCommand = new RelayCommand(SelectNextPoint, CanSelectNextPoint);
        JogCommand = new AsyncRelayCommand<TeachingDirection>(JogAsync, CanMoveDirection);
        StepCommand = new AsyncRelayCommand<TeachingDirection>(StepAsync, CanStep);
        JogStopCommand = new RelayCommand(JogStop);
        HomeCommand = new AsyncRelayCommand(HomeAsync, CanHome);
        MoveToHorizontalZCommand = new AsyncRelayCommand(MoveToHorizontalZAsync, CanJogZ);

        State = state;
        Machine = machine;
        Operations = operations;
        _store = store;
        _ioGroups = ioGroups;
        _teachingOutputs = teachingOutputs;

        MeasureImageCommand = new RelayCommand<ImageRuler>(MeasureImage, CanMeasureImage);
        ApplyRulerResolutionCommand = new AsyncRelayCommand(ApplyRulerResolutionAsync, CanApplyRulerResolution);
        ReadDataMatrixCommand = new AsyncRelayCommand(ReadDataMatrixAsync, CanReadDataMatrix);
        DrawFovRegionCommand = new AsyncRelayCommand<Rect>(DrawFovRegionAsync, CanDrawFovRegion);
        TeachFovRegionCommand = new AsyncRelayCommand<Rect>(TeachFovRegionAsync, CanTeachFovRegion);
        ToggleLiveViewCommand = new AsyncRelayCommand(ToggleLiveViewAsync, CanToggleLiveView);
        CaptureCarrierImageCommand = new AsyncRelayCommand(CaptureCarrierImageAsync, CanCaptureCarrierImage);
        CaptureInspectionCommand = new AsyncRelayCommand(CaptureInspectionAsync, CanCaptureInspection);
        ReinspectImageCommand = new AsyncRelayCommand(ReinspectImageAsync, CanReinspectImage);
        AddBoltPointCommand = new RelayCommand(AddBoltPoint, CanAddBoltPoint);
        RemoveBoltPointCommand = new RelayCommand(RemoveBoltPoint, CanRemoveBoltPoint);
        SaveHandoffSetupCommand = new AsyncRelayCommand(SaveHandoffSetupAsync, () => CanEditTeaching);
        ReturnFromPickupCommand = new AsyncRelayCommand(ReturnFromPickupAsync, CanReturnFromPickup);

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
        Preview = new(boltInspector, recipeEditor.Recipe);
        Preview.PropertyChanged += (_, e) =>
        {
            ReinspectImageCommand.NotifyCanExecuteChanged();
        };

        MillimetersPerPixel = RecipeEditor.Recipe.CarrierImageMillimetersPerPixel;

        boltInspector.FrameReady += UpdateLiveImage;
        boltInspector.LiveViewChanged += OnLiveViewChanged;
        CaptureCarrierImageCommand.PropertyChanged += OnInspectionCommandChanged;
        CaptureInspectionCommand.PropertyChanged += OnInspectionCommandChanged;
        state.DisplayChanged += QueueManualCommandRefresh;
        recipeEditor.Changed += OnRecipeChanged;
        recipeEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RecipeEditor.CanSave))
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
        get
        {
            return _cameraError ?? Inspector.LiveViewError?.Message;
        }
        private set
        {
            SetProperty(ref _cameraError, value);
        }
    }

    public BoltInspector Inspector { get; }

    public RecipeEditor RecipeEditor { get; }
    public InspectionPreview Preview { get; }

    public HeatSinkSlot? SelectedBarcode
    {
        get
        {
            return SelectedPoint?.Position.Target == TeachingTarget.DataMatrix ? SelectedPcb : null;
        }
    }

    public bool IsDataMatrixSelected
    {
        get
        {
            return SelectedBarcode is not null;
        }
    }

    public HardwareArea[] TeachingUnits { get; } = [
        HardwareArea.PcbSupply,
        HardwareArea.PcbPlacementHandler,
        HardwareArea.BoltFastening,
        HardwareArea.InspectionGantry,
        HardwareArea.NgCarrierTransfer,
    ];
    public FasteningHead[] FasteningHeads { get; } = Enum.GetValues<FasteningHead>();
    public HeatSinkSlot[] HeatSinkSlots { get; } = Enum.GetValues<HeatSinkSlot>();

    public BoltFasteningRecipe BoltRecipe
    {
        get
        {
            return RecipeEditor.Recipe.BoltFastening;
        }
    }

    public BoltInspectionRecipe InspectionRecipe
    {
        get
        {
            return RecipeEditor.Recipe.BoltInspection;
        }
    }

    public TeachingSaveBehavior SaveBehavior
    {
        get
        {
            return SelectedPoint?.Position switch
            {
                { Target: TeachingTarget.BoltPickup } => TeachingSaveBehavior.BoltPickup,
                { Target: TeachingTarget.ShootingHeadFasteningZ or TeachingTarget.PickupHeadFasteningZ }
                    => TeachingSaveBehavior.FasteningZ,
                { Target: TeachingTarget.DataMatrix } => TeachingSaveBehavior.BarcodeFov,
                { Target: TeachingTarget.SupplyBufferHandoff } => TeachingSaveBehavior.SupplyHandoff,
                { Target: TeachingTarget.PlacementBufferHandoff } => TeachingSaveBehavior.PlacementHandoff,
                { Target: TeachingTarget.SupplyCarrierY } => TeachingSaveBehavior.SupplyCarrierY,
                { Target: TeachingTarget.NgCarrierPickup } => TeachingSaveBehavior.NgPickup,
                { Target: TeachingTarget.BoltPosition } => TeachingSaveBehavior.BoltPosition,
                {
                    Target: TeachingTarget.CarrierUpperLeftLocatingPin
                        or TeachingTarget.CarrierLowerRightLocatingPin
                }
                    => TeachingSaveBehavior.CameraCenter,
                { Mode: TeachMode.Image } => TeachingSaveBehavior.Image,
                { Storage: TeachingStorage.Buffer } => TeachingSaveBehavior.Buffer,
                { Storage: TeachingStorage.Machine } => TeachingSaveBehavior.Machine,
                _ => TeachingSaveBehavior.Recipe,
            };
        }
    }

    public bool IsInspectionSelected
    {
        get
        {
            return SelectedTeachingUnit == HardwareArea.InspectionGantry;
        }
    }

    public bool BoltPointEditorVisible
    {
        get
        {
            return BoltPresetEditorVisible || IsInspectionSelected;
        }
    }

    public bool BoltPresetEditorVisible
    {
        get
        {
            return SelectedTeachingUnit == HardwareArea.BoltFastening;
        }
    }

    public bool IsBoltSelected
    {
        get
        {
            return IsInspectionSelected && SelectedPoint?.Position.Bolt is not null;
        }
    }

    public bool HandoffSaveVisible
    {
        get
        {
            return SelectedTeachingUnit is HardwareArea.PcbSupply or HardwareArea.PcbPlacementHandler;
        }
    }

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
        var number = RecipeEditor.Recipe.Pcb.GetBolts(SelectedPcb).Select(bolt => bolt.Number).DefaultIfEmpty().Max() + 1;
        var bolt = new BoltPoint
        {
            Number = number,
            HeatSink = SelectedPcb,
            Head = NewFasteningHead,
            BrightnessThreshold = InspectionRecipe.BrightnessThreshold,
            MinimumBrightRatio = InspectionRecipe.MinimumBrightRatio,
        };
        RecipeEditor.Recipe.Pcb.BoltPoints.Add(bolt);
        RefreshTeachingPoints();
        SelectedPoint = FilteredPoints.First(
            point => point.BoltNumber == number && point.Position.Target == TeachingTarget.BoltReference);
        if (draft is not null)
        {
            SelectedFov = draft;
            FovRegion = draftRegion;
        }
    }

    private bool CanAddBoltPoint()
    {
        return CanEditTeaching && IsInspectionSelected;
    }

    public IRelayCommand RemoveBoltPointCommand { get; }

    private void RemoveBoltPoint()
    {
        var number = SelectedPoint!.BoltNumber;
        RecipeEditor.Recipe.Pcb.BoltPoints.RemoveAll(bolt => bolt.Number == number && bolt.HeatSink == SelectedPcb);
        foreach (var fov in RecipeEditor.Recipe.CarrierImages.Where(fov =>
            !fov.IsBarcode && fov.BoltNumber == number && fov.HeatSink == SelectedPcb))
        {
            fov.BoltNumber = null;
            fov.Region = null;
        }
        OnSelectedFovChanged(SelectedFov);
        RefreshTeachingPoints();
    }

    private bool CanRemoveBoltPoint()
    {
        return CanEditTeaching
            && IsInspectionSelected
            && SelectedPoint?.Position.Target == TeachingTarget.BoltReference;
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
            HardwareArea.PcbSupply => _supplySettings.GetTeachingPositions(RecipeEditor.Recipe.PcbSupply)
                .Where(position => position.Storage != TeachingStorage.Buffer).ToArray(),
            HardwareArea.PcbPlacementHandler
                => RecipeEditor.Recipe.PcbPlacement.GetTeachingPositions(),
            HardwareArea.BoltFastening
                => _fasteningSettings.GetTeachingPositions(
                    RecipeEditor.Recipe.Pcb,
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
                    .. RecipeEditor.Recipe.Pcb.GetBolts(SelectedPcb).Select(bolt =>
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
            ?? GetNextTeachingPoint()
                ?? FilteredPoints.FirstOrDefault();
    }

    private TeachingPoint? GetNextTeachingPoint()
    {
        if (!IsInspectionSelected)
        {
            return null;
        }

        if (!Inspector.HasBarcodeRegion(SelectedPcb))
        {
            return FilteredPoints.FirstOrDefault(
                point => point.Position.Target == TeachingTarget.DataMatrix);
        }

        return RecipeEditor.Recipe.CarrierImages.Count == 0
            ? null
            : FilteredPoints.FirstOrDefault(
                point => point.Position.Target == TeachingTarget.BoltReference && !point.Position.HasPosition);
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
        RecipeEditor.Recipe.CarrierImageMillimetersPerPixel = value;
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
        MillimetersPerPixel = RecipeEditor.Recipe.CarrierImageMillimetersPerPixel;
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
            .. _supplySettings.GetTeachingPositions(RecipeEditor.Recipe.PcbSupply)
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
