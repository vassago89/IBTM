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
using IBTM.Inspection.Training;
using IBTM.PcbPlacement;
using IBTM.Storage;

namespace IBTM.UI;

public partial class StationTeachingViewModel : TeachingMotionViewModel
{
    private readonly PcbPlacementHandler _placementHandler;
    private readonly BoltFasteningGantry _fasteningGantry;
    private readonly InspectionGantry _inspectionGantry;
    private readonly MachineState _state;
    private readonly InspectionGantrySettings _inspectionGantrySettings;
    private readonly CarrierReferenceSettings _carrierReference;
    private readonly PcbPlacementHandlerSettings _placementSettings;
    private readonly BoltFasteningSettings _fasteningSettings;
    private readonly NgCarrierTransferSettings _ngTransferSettings;
    private readonly NgCarrierMove _ngCarrierMove;
    private readonly InspectionWork _inspectionWork;
    private readonly BoltTrainingStore _trainingStore;
    private CancellationTokenSource _recipeImageCancellation = new();
    private Task _recipeImageUpdate = Task.CompletedTask;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInspectionSelected))]
    [NotifyPropertyChangedFor(nameof(ActiveMotionGroup))]
    [NotifyPropertyChangedFor(nameof(ActiveTeachingUnit))]
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
    [NotifyPropertyChangedFor(nameof(HasCarrierImages))]
    [NotifyCanExecuteChangedFor(nameof(ClearCarrierImagesCommand))]
    private IReadOnlyList<CarrierImageTileView> _carrierImages = [];

    [ObservableProperty]
    private double _millimetersPerPixel;

    private string? _cameraError;

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

    public StationTeachingViewModel(
        PcbPlacementHandler placementHandler,
        BoltFasteningGantry fasteningGantry,
        InspectionGantry inspectionGantry,
        BoltInspector boltInspector,
        MachineState state,
        MachineController machine,
        InspectionGantrySettings inspectionGantrySettings,
        CarrierReferenceSettings carrierReference,
        PcbPlacementHandlerSettings placementSettings,
        BoltFasteningSettings fasteningSettings,
        NgCarrierTransferSettings ngTransferSettings,
        NgCarrierMove ngCarrierMove,
        RecipeEditor recipeEditor,
        InspectionWork inspectionWork,
        BoltTrainingStore trainingStore,
        BoltTrainingSettings trainingSettings,
        MachineStore store,
        IReadOnlyDictionary<HardwareArea, IoStatus[]> ioGroups,
        IReadOnlyDictionary<HardwareArea, IReadOnlyDictionary<OutputIo, TeachingOutput>> teachingOutputs) : base(
            state,
            machine,
            store,
            ioGroups,
            teachingOutputs)
    {
        _placementHandler = placementHandler;
        _fasteningGantry = fasteningGantry;
        _inspectionGantry = inspectionGantry;
        Inspector = boltInspector;
        _state = state;
        _inspectionGantrySettings = inspectionGantrySettings;
        _carrierReference = carrierReference;
        _placementSettings = placementSettings;
        _fasteningSettings = fasteningSettings;
        _ngTransferSettings = ngTransferSettings;
        _ngCarrierMove = ngCarrierMove;
        RecipeEditor = recipeEditor;
        _inspectionWork = inspectionWork;
        _trainingStore = trainingStore;
        Preview = new(boltInspector, recipeEditor.Recipe, trainingSettings);
        Preview.PropertyChanged += (_, e) =>
        {
            ReinspectImageCommand.NotifyCanExecuteChanged();
        };
        inspectionWork.Changed += QueueManualCommandRefresh;

        MillimetersPerPixel = RecipeEditor.Recipe.CarrierImageMillimetersPerPixel;

        boltInspector.FrameReady += UpdateLiveImage;
        boltInspector.LiveViewChanged += OnLiveViewChanged;
        CaptureCarrierImageCommand.PropertyChanged += OnInspectionCommandChanged;
        CaptureInspectionCommand.PropertyChanged += OnInspectionCommandChanged;
        CollectBoltImagesCommand.PropertyChanged += OnInspectionCommandChanged;
        state.DisplayChanged += QueueManualCommandRefresh;
        recipeEditor.Changed += OnRecipeChanged;
        recipeEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RecipeEditor.CanSave))
                return;
            CaptureCarrierImageCommand.NotifyCanExecuteChanged();
            ClearCarrierImagesCommand.NotifyCanExecuteChanged();
            TeachFovRegionCommand.NotifyCanExecuteChanged();
        };

        RefreshTeachingPoints();
        ShowRecipeImages();
    }

    public RecipeEditor RecipeEditor { get; }
    public InspectionPreview Preview { get; }

    public HeatSinkSlot? SelectedBarcode
    {
        get
        {
            return SelectedPoint?.Position.Target == TeachingTarget.DataMatrix ? SelectedPcb : null;
        }
    }

    protected override IReadOnlyList<TeachingPoint> CurrentPoints
    {
        get
        {
            return FilteredPoints;
        }
    }

    public HardwareArea[] TeachingUnits { get; } = [
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

    public bool CanEditInspectionRecipe
    {
        get
        {
            return CanEditRecipe(live: false);
        }
    }

    private bool CanEditRecipe(bool live = true)
    {
        return live ? _state.SetupEditingEnabled : _state.Display.SetupEditingEnabled;
    }

    public override TeachingSaveBehavior SaveBehavior
    {
        get
        {
            return SelectedPoint?.Position switch
            {
                { Target: TeachingTarget.BoltPickup } => TeachingSaveBehavior.BoltPickup,
                { Target: TeachingTarget.DataMatrix } => TeachingSaveBehavior.BarcodeFov,
                { CanTeach: false } => TeachingSaveBehavior.BoltPosition,
                {
                    Target: TeachingTarget.CarrierUpperLeftLocatingPin
                        or TeachingTarget.CarrierLowerRightLocatingPin
                }
                    => TeachingSaveBehavior.CameraCenter,
                { Mode: TeachMode.Image } => TeachingSaveBehavior.Image,
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
            return SelectedTeachingUnit == HardwareArea.BoltFastening || IsInspectionSelected;
        }
    }

    public bool BoltPresetEditorVisible
    {
        get
        {
            return SelectedTeachingUnit == HardwareArea.BoltFastening;
        }
    }

    public bool HasCarrierImages
    {
        get
        {
            return CarrierImages.Count > 0;
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
        OnPropertyChanged(nameof(BoltPointEditorVisible));
        OnPropertyChanged(nameof(BoltPresetEditorVisible));
        NotifyManualTeachingCommands();
    }

    protected override void OnPointTaught(TeachingPoint point)
    {
        if (point.Position.Target == TeachingTarget.CarrierUpperLeftLocatingPin)
        {
            SelectedPoint = FilteredPoints.First(
                candidate => candidate.Position.Target == TeachingTarget.CarrierLowerRightLocatingPin);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddBoltPoint))]
    private void AddBoltPoint()
    {
        if (!CanEditRecipe())
            return;
        var number = RecipeEditor.Recipe.Pcb.GetBolts(SelectedPcb).Select(bolt => bolt.Number).DefaultIfEmpty().Max() + 1;
        var bolt = new BoltPoint
        {
            Number = number,
            HeatSink = SelectedPcb,
            Head = NewFasteningHead,
        };
        RecipeEditor.Recipe.Pcb.BoltPoints.Add(bolt);
        RefreshTeachingPoints();
        SelectedPoint = FilteredPoints.First(
            point => point.BoltNumber == number && point.Position.Target == TeachingTarget.BoltReference);
    }

    private bool CanAddBoltPoint()
    {
        return CanEditInspectionRecipe && IsInspectionSelected;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveBoltPoint))]
    private void RemoveBoltPoint()
    {
        if (!CanEditRecipe())
            return;
        var number = SelectedPoint!.BoltNumber;
        RecipeEditor.Recipe.Pcb.BoltPoints.RemoveAll(bolt => bolt.Number == number && bolt.HeatSink == SelectedPcb);
        foreach (var fov in RecipeEditor.Recipe.CarrierImages.Where(fov =>
            !fov.IsBarcode && fov.BoltNumber == number && fov.HeatSink == SelectedPcb))
        {
            fov.BoltNumber = null;
            fov.Region = null;
        }
        CarrierImages = CarrierImages.Select(image => !image.IsBarcode && image.BoltNumber == number && image.HeatSink == SelectedPcb
            ? image with { BoltNumber = null, Region = null }
            : image).ToArray();
        RefreshTeachingPoints();
    }

    private bool CanRemoveBoltPoint()
    {
        return CanEditInspectionRecipe
            && IsInspectionSelected
            && SelectedPoint?.Position.Target == TeachingTarget.BoltReference;
    }

    public void Activate()
    {
        CameraError = null;
        RecipeEditor.Refresh();
        RefreshTeachingPoints();
        ActivatePositionUpdates();
        ShowRecipeImages();
        NotifyManualTeachingCommands();
    }

    public override void Deactivate()
    {
        base.Deactivate();
        _recipeImageCancellation.Cancel();
        CarrierImages = [];
        Preview.Clear();

        _ = RequestCameraStopAsync();
    }

    public async Task ShutdownAsync()
    {
        try
        {
            await CommandShutdown.StopAsync(
                Deactivate,
                ToggleLiveViewCommand,
                JogCommand,
                HomeCommand,
                StepCommand,
                MoveToHorizontalZCommand,
                MoveToPointCommand,
                ReturnFromPickupCommand,
                SetOutputOnCommand,
                SetOutputOffCommand,
                CaptureCarrierImageCommand,
                ClearCarrierImagesCommand,
                CaptureInspectionCommand,
                ReinspectImageCommand,
                CollectBoltImagesCommand,
                TeachFovRegionCommand,
                TeachCurrentPositionCommand);
        }
        finally
        {
            Task imageUpdate;
            lock (_liveImageGate)
            {
                imageUpdate = _liveImageUpdate;
            }

            await Task.WhenAll(imageUpdate, _recipeImageUpdate, _cameraStop);
        }
    }

    private void RefreshTeachingPoints()
    {
        var selectedTarget = SelectedPoint?.Position.Target;
        var selectedBolt = SelectedPoint?.BoltNumber;
        TeachingPosition[] positions = SelectedTeachingUnit switch
        {
            HardwareArea.PcbPlacementHandler
                => [
                .. _placementSettings.GetTeachingPositions(),
                .. RecipeEditor.Recipe.PcbPlacement.GetTeachingPositions(),
            ],
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
                .. _inspectionGantrySettings.GetBoltTeachingPositions(
                    RecipeEditor.Recipe.Pcb.GetBolts(SelectedPcb),
                    _carrierReference),
            ],
            HardwareArea.NgCarrierTransfer => _ngTransferSettings.GetTeachingPositions(),
            _ => throw new ArgumentOutOfRangeException(nameof(SelectedTeachingUnit)),
        };
        FilteredPoints = positions.Select(position => new TeachingPoint(position)).ToArray();
        SelectedPoint = FilteredPoints.FirstOrDefault(
            point => point.Position.Target == selectedTarget && point.BoltNumber == selectedBolt)
            ?? NextTeachingPoint()
            ?? FilteredPoints.FirstOrDefault();
    }

    private TeachingPoint? NextTeachingPoint()
    {
        if (!IsInspectionSelected)
        {
            return null;
        }

        if (_carrierReference.UpperLeftLocatingPin is null)
        {
            return FilteredPoints.FirstOrDefault(
                point => point.Position.Target == TeachingTarget.CarrierUpperLeftLocatingPin);
        }

        if (_carrierReference.LowerRightLocatingPin is null)
        {
            return FilteredPoints.FirstOrDefault(
                point => point.Position.Target == TeachingTarget.CarrierLowerRightLocatingPin);
        }

        return RecipeEditor.Recipe.CarrierImages.Count == 0
            ? null
            : FilteredPoints.FirstOrDefault(
                point => point.Position.Target == TeachingTarget.BoltReference && !point.Position.HasPosition);
    }

    protected override void OnTeachingPointChanged(TeachingPoint? oldValue, TeachingPoint? newValue)
    {
        Preview.Clear(SelectedBarcode);
        OnPropertyChanged(nameof(SelectedBarcode));
        OnPropertyChanged(nameof(FovRegion));
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
    }

    partial void OnCarrierImagesChanged(IReadOnlyList<CarrierImageTileView> value)
    {
        SelectedFov = value.FirstOrDefault(image => image.Number == SelectedFov?.Number) ?? value.FirstOrDefault();
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

    protected override void RefreshPointPositions()
    {
        Preview.Clear(SelectedBarcode);
        foreach (var point in FilteredPoints)
            point.Refresh();
        OnPropertyChanged(nameof(FovRegion));
    }

}
