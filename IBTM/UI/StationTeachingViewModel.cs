using System;
using System.Collections.Generic;
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
    private readonly BoltInspector _boltInspector;
    private readonly MachineState _state;
    private readonly InspectionGantrySettings _inspectionGantrySettings;
    private readonly CarrierReferenceSettings _carrierReference;
    private readonly PcbPlacementHandlerSettings _placementSettings;
    private readonly BoltFasteningSettings _fasteningSettings;
    private readonly NgCarrierTransferSettings _ngTransferSettings;
    private readonly InspectionWork _inspectionWork;
    private readonly BoltTrainingStore _trainingStore;
    private CancellationTokenSource _recipeImageCancellation = new();
    private Task _recipeImageUpdate = Task.CompletedTask;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInspectionSelected))]
    [NotifyPropertyChangedFor(nameof(TeachingIoGroups))]
    [NotifyCanExecuteChangedFor(nameof(ToggleLiveViewCommand))]
    [NotifyCanExecuteChangedFor(nameof(CaptureCarrierImagesCommand))]
    [NotifyCanExecuteChangedFor(nameof(TeachImagePointCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBoltPointCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReturnFromPickupCommand))]
    private MotionGroup _selectedMotionGroup = MotionGroup.InspectionGantry;

    [ObservableProperty]
    private FasteningHead _newFasteningHead = FasteningHead.Shooting;

    [ObservableProperty]
    private IReadOnlyList<TeachingPoint> _filteredPoints = [];
    [ObservableProperty]
    private HeatSinkSlot _selectedPcb = HeatSinkSlot.HeatSink1;
    [ObservableProperty]
    private BitmapSource? _liveImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCarrierImages))]
    [NotifyCanExecuteChangedFor(nameof(TeachImagePointCommand))]
    [NotifyCanExecuteChangedFor(nameof(TeachImageRegionCommand))]
    private IReadOnlyList<CarrierImageTileView> _carrierImages = [];

    [ObservableProperty]
    private double _millimetersPerPixel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCarrierImagesCommand))]
    private double _scanOverlap;

    [ObservableProperty]
    private string? _cameraError;

    [ObservableProperty]
    private IReadOnlyList<ImageMarker> _imageMarkers = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCarrierImagesCommand))]
    [NotifyCanExecuteChangedFor(nameof(TeachImagePointCommand))]
    [NotifyCanExecuteChangedFor(nameof(TeachImageRegionCommand))]
    private bool _isCameraLive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TeachCurrentPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToPointCommand))]
    [NotifyCanExecuteChangedFor(nameof(TeachImagePointCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveBoltPointCommand))]
    private TeachingPoint? _selectedPoint;

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
        RecipeEditor recipeEditor,
        InspectionWork inspectionWork,
        BoltTrainingStore trainingStore,
        BoltTrainingSettings trainingSettings,
        MachineStore store,
        IReadOnlyDictionary<MotionGroup, IoStatus[]> ioGroups,
        IReadOnlyDictionary<MotionGroup, IReadOnlyDictionary<OutputIo, TeachingOutput>> teachingOutputs) : base(
            state,
            machine,
            store,
            ioGroups,
            teachingOutputs)
    {
        _placementHandler = placementHandler;
        _fasteningGantry = fasteningGantry;
        _inspectionGantry = inspectionGantry;
        _boltInspector = boltInspector;
        _state = state;
        _inspectionGantrySettings = inspectionGantrySettings;
        _carrierReference = carrierReference;
        _placementSettings = placementSettings;
        _fasteningSettings = fasteningSettings;
        _ngTransferSettings = ngTransferSettings;
        RecipeEditor = recipeEditor;
        _inspectionWork = inspectionWork;
        _trainingStore = trainingStore;
        Preview = new(boltInspector, recipeEditor.Recipe, trainingSettings);
        Preview.PropertyChanged += (_, e) =>
        {
            ReinspectImageCommand.NotifyCanExecuteChanged();
            if (e.PropertyName == nameof(InspectionPreview.Image))
                OnPropertyChanged(nameof(CameraFieldOfView));
            if (e.PropertyName == nameof(InspectionPreview.RegionSize)
                && Preview.IsBolt
                && ReinspectImageCommand.CanExecute(null))
                ReinspectImageCommand.Execute(null);
        };
        inspectionWork.Changed += QueueManualCommandRefresh;
        RefreshMotionGroups();

        MillimetersPerPixel = RecipeEditor.Recipe.CarrierImageMillimetersPerPixel;
        ScanOverlap = RecipeEditor.Recipe.BoltInspection.CarrierScanOverlapMillimeters;

        inspectionGantry.Feedback.PositionChanged += OnInspectionPositionChanged;
        boltInspector.FrameReady += UpdateLiveImage;
        boltInspector.LiveViewFailed += OnLiveViewFailed;
        state.DisplayChanged += QueueManualCommandRefresh;
        recipeEditor.Changed += OnRecipeChanged;
        recipeEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RecipeEditor.CanSave))
                return;
            CaptureCarrierImagesCommand.NotifyCanExecuteChanged();
            TeachImagePointCommand.NotifyCanExecuteChanged();
            TeachImageRegionCommand.NotifyCanExecuteChanged();
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

    public IReadOnlyList<ImageRegion> TeachingRegions
    {
        get
        {
            return !_carrierReference.IsDefined
                ? []
                : Enum.GetValues<HeatSinkSlot>()
                    .SelectMany(
                        pcb =>
                        {
                            var pin = _carrierReference.UpperLeftLocatingPin!;
                            return new[]
                            {
                                (
                                    Region: RecipeEditor.Recipe.Pcb.GetRegion(pcb),
                                    Target: TeachingTarget.PcbRegion,
                                    Label: $"PCB {(int)pcb + 1}"),
                                (
                                    Region: RecipeEditor.Recipe.Pcb.GetDataMatrix(pcb),
                                    Target: TeachingTarget.DataMatrix,
                                    Label: ""),
                            }.Where(item => item.Region is not null)
                                .Select(
                                    item =>
                                        new ImageRegion(
                                            new Rect(
                                                item.Region!.X + pin.X,
                                                item.Region.Y + pin.Y,
                                                item.Region.Width,
                                                item.Region.Height),
                                            pcb == SelectedPcb && SelectedPoint?.Position.Target == item.Target,
                                            item.Label));
                        })
                    .ToArray();
        }
    }

    protected override IReadOnlyList<TeachingPoint> CurrentPoints
    {
        get
        {
            return FilteredPoints;
        }
    }

    protected override TeachingPoint? CurrentPoint
    {
        get
        {
            return SelectedPoint;
        }

        set
        {
            SelectedPoint = value;
        }
    }

    public MotionGroup[] MotionGroups { get; private set; } = [];
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

    public TeachingSaveBehavior SaveBehavior
    {
        get
        {
            return SelectedPoint?.Position switch
            {
                { Target: TeachingTarget.BoltPickup } => TeachingSaveBehavior.BoltPickup,
                { CanTeach: false } => TeachingSaveBehavior.BoltPosition,
                { Target: TeachingTarget.PcbRegion }
                    => SelectedPcb == HeatSinkSlot.HeatSink1
                        ? TeachingSaveBehavior.PcbRegion
                        : TeachingSaveBehavior.PcbOrigin,
                { Target: TeachingTarget.DataMatrix } => TeachingSaveBehavior.ImageRegion,
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
            return SelectedMotionGroup == MotionGroup.InspectionGantry;
        }
    }

    public bool BoltPointEditorVisible
    {
        get
        {
            return SelectedMotionGroup == MotionGroup.BoltFastening || IsInspectionSelected;
        }
    }

    public bool BoltPresetEditorVisible
    {
        get
        {
            return SelectedMotionGroup == MotionGroup.BoltFastening;
        }
    }

    public bool HasCarrierImages
    {
        get
        {
            return CarrierImages.Count > 0;
        }
    }

    public Point? ImageOrigin
    {
        get
        {
            if (!_carrierReference.IsDefined)
            {
                return null;
            }

            var origin = _carrierReference.UpperLeftLocatingPin!;
            var pcb = SelectedPoint?.Position.Target is TeachingTarget.BoltReference or TeachingTarget.DataMatrix
                ? RecipeEditor.Recipe.Pcb.Origins.GetValueOrDefault(SelectedPcb)
                : null;
            return new Point(origin.X + (pcb?.X ?? 0), origin.Y + (pcb?.Y ?? 0));
        }
    }

    public Rect? CameraFieldOfView
    {
        get
        {
            if (!IsInspectionSelected)
            {
                return null;
            }

            var (width, height) = ImageFieldOfView;
            if (width <= 0 || height <= 0)
                return null;
            var position = Motion.Position;
            return new Rect(position.X - (width / 2), position.Y - (height / 2), width, height);
        }
    }

    private void OnInspectionPositionChanged(double x, double y, double z)
    {
        if (PositionUpdatesActive && IsInspectionSelected)
            OnPropertyChanged(nameof(CameraFieldOfView));
    }

    private (double Width, double Height) ImageFieldOfView
    {
        get
        {
            return (Preview.Image ?? LiveImage ?? CarrierImages.FirstOrDefault()?.Image) is { } image
                ? _boltInspector.GetFieldOfView((image.PixelWidth, image.PixelHeight))
                : _boltInspector.FieldOfView;
        }
    }

    partial void OnSelectedPcbChanged(HeatSinkSlot value)
    {
        RefreshTeachingPoints();
        NotifyManualTeachingCommands();
    }

    partial void OnSelectedMotionGroupChanged(MotionGroup value)
    {
        CancelTeaching();

        if (IsCameraLive)
            StopCamera();

        RefreshTeachingPoints();
        ShowRecipeImages();
        OnPropertyChanged(nameof(Motion));
        OnPropertyChanged(nameof(CameraFieldOfView));
        OnPropertyChanged(nameof(BoltPointEditorVisible));
        OnPropertyChanged(nameof(BoltPresetEditorVisible));
        NotifyMotionCommands();
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
        var number = RecipeEditor.Recipe.Pcb.BoltPoints.Select(bolt => bolt.Number).DefaultIfEmpty().Max() + 1;
        var bolt = new BoltPoint
        {
            Number = number,
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
        RecipeEditor.Recipe.Pcb.BoltPoints.RemoveAll(bolt => bolt.Number == number);
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
        RefreshMotionGroups();
        RefreshTeachingPoints();
        ActivatePositionUpdates();
        OnPropertyChanged(nameof(CameraFieldOfView));
        ShowRecipeImages();
        NotifyManualTeachingCommands();
    }

    public override void Deactivate()
    {
        base.Deactivate();
        _recipeImageCancellation.Cancel();
        CarrierImages = [];
        Preview.Clear();

        if (IsCameraLive)
            StopCamera();
    }

    public async Task ShutdownAsync()
    {
        try
        {
            await CommandShutdown.StopAsync(
                Deactivate,
                JogCommand,
                StepCommand,
                MoveToHorizontalZCommand,
                MoveToPointCommand,
                ReturnFromPickupCommand,
                SetOutputOnCommand,
                SetOutputOffCommand,
                CaptureCarrierImagesCommand,
                CaptureInspectionCommand,
                ReinspectImageCommand,
                CollectBoltImagesCommand,
                TeachImageRegionCommand,
                TeachCurrentPositionCommand,
                TeachImagePointCommand);
        }
        finally
        {
            Task imageUpdate;
            lock (_liveImageGate)
            {
                imageUpdate = _liveImageUpdate;
            }

            await imageUpdate;
            await _recipeImageUpdate;
        }
    }

    private void RefreshTeachingPoints()
    {
        var selectedTarget = SelectedPoint?.Position.Target;
        var selectedBolt = SelectedPoint?.BoltNumber;
        TeachingPosition[] positions = SelectedMotionGroup switch
        {
            MotionGroup.PcbPlacementHandler
                => [
                .. _placementSettings.GetTeachingPositions(),
                .. RecipeEditor.Recipe.PcbPlacement.GetTeachingPositions(),
            ],
            MotionGroup.BoltFastening
                => _fasteningSettings.GetTeachingPositions(
                    RecipeEditor.Recipe.Pcb,
                    SelectedPcb,
                    _carrierReference),
            MotionGroup.InspectionGantry
                => [
                .. _inspectionGantrySettings.GetTeachingPositions(_carrierReference),
                .. _inspectionGantrySettings.GetPcbTeachingPositions(
                    RecipeEditor.Recipe.Pcb,
                    SelectedPcb,
                    _carrierReference),
                .. _inspectionGantrySettings.GetBoltTeachingPositions(
                    RecipeEditor.Recipe.Pcb.GetBolts(SelectedPcb),
                    _carrierReference),
                .. _ngTransferSettings.GetTeachingPositions(),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(SelectedMotionGroup)),
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

        if (RecipeEditor.Recipe.Pcb.GetRegion(SelectedPcb) is null)
            return FilteredPoints.FirstOrDefault(point => point.Position.Target == TeachingTarget.PcbRegion);

        return RecipeEditor.Recipe.CarrierImages.Count == 0
            ? null
            : FilteredPoints.FirstOrDefault(
                point => point.Position.Target == TeachingTarget.BoltReference && !point.Position.HasPosition);
    }

    partial void OnSelectedPointChanged(TeachingPoint? value)
    {
        CancelTeaching();
        Preview.Clear(SelectedBarcode);
        OnPropertyChanged(nameof(SelectedBarcode));
        OnPropertyChanged(nameof(ImageOrigin));
        OnPropertyChanged(nameof(TeachingRegions));
        CaptureInspectionCommand.NotifyCanExecuteChanged();
        TeachImageRegionCommand.NotifyCanExecuteChanged();
        NotifyPointSelectionCommands();
        OnPropertyChanged(nameof(SaveBehavior));
        RefreshImageMarkers();
    }

    partial void OnMillimetersPerPixelChanged(double value)
    {
        RecipeEditor.Recipe.CarrierImageMillimetersPerPixel = value;
        OnPropertyChanged(nameof(CameraFieldOfView));
        CaptureCarrierImagesCommand.NotifyCanExecuteChanged();
        CaptureInspectionCommand.NotifyCanExecuteChanged();
        TeachImageRegionCommand.NotifyCanExecuteChanged();
        Preview.RefreshBarcodeRegion();
    }

    partial void OnCarrierImagesChanged(IReadOnlyList<CarrierImageTileView> value)
    {
        OnPropertyChanged(nameof(CameraFieldOfView));
    }

    partial void OnLiveImageChanged(BitmapSource? oldValue, BitmapSource? newValue)
    {
        if (oldValue?.PixelWidth != newValue?.PixelWidth
            || oldValue?.PixelHeight != newValue?.PixelHeight)
            OnPropertyChanged(nameof(CameraFieldOfView));
    }

    partial void OnScanOverlapChanged(double value)
    {
        RecipeEditor.Recipe.BoltInspection.CarrierScanOverlapMillimeters = value;
    }

    private void OnRecipeChanged()
    {
        CameraError = null;
        if (IsCameraLive)
            StopCamera();
        SelectedPoint = null;
        if (SelectedPcb == HeatSinkSlot.HeatSink1)
            RefreshTeachingPoints();
        else
            SelectedPcb = HeatSinkSlot.HeatSink1;
        OnPropertyChanged(nameof(BoltRecipe));
        OnPropertyChanged(nameof(InspectionRecipe));
        ScanOverlap = RecipeEditor.Recipe.BoltInspection.CarrierScanOverlapMillimeters;
        MillimetersPerPixel = RecipeEditor.Recipe.CarrierImageMillimetersPerPixel;
        ShowRecipeImages();
    }

    private void ShowRecipeImages()
    {
        _recipeImageCancellation.Cancel();
        _recipeImageCancellation.Dispose();
        _recipeImageCancellation = new();
        CarrierImages = [];
        RefreshImageMarkers();
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

    private void RefreshImageMarkers()
    {
        if (!IsInspectionSelected)
        {
            ImageMarkers = [];
            return;
        }

        ImageMarkers = FilteredPoints.Where(
            point =>
                point.Position.Target != TeachingTarget.BoltReference
                    && point.Position.Target != TeachingTarget.PcbRegion)
            .Concat(
                _inspectionGantrySettings.GetBoltTeachingPositions(
                    RecipeEditor.Recipe.Pcb.GetBolts(),
                    _carrierReference)
                    .Select(position => new TeachingPoint(position)))
            .Where(
                point =>
                    point.Position.Mode == TeachMode.Image
                        || point.Position.Target is TeachingTarget.CarrierUpperLeftLocatingPin
                            or TeachingTarget.CarrierLowerRightLocatingPin)
            .Where(point => point.Position.HasPosition)
            .Select(
                point =>
                    new ImageMarker(
                        point.X,
                        point.Y,
                        point.Position.Target switch
                        {
                            TeachingTarget.BoltReference
                                => $"{point.Position.Bolt!.HeatSink.GetDescription()} {point.Name}",
                            TeachingTarget.CarrierUpperLeftLocatingPin => "UL",
                            TeachingTarget.CarrierLowerRightLocatingPin => "LR",
                            _ => point.Name,
                        },
                        point.Position.Bolt is { } bolt
                            ? bolt == SelectedPoint?.Position.Bolt
                            : point == SelectedPoint))
            .ToArray();
    }

    protected override void RefreshPointPositions()
    {
        Preview.Clear(SelectedBarcode);
        foreach (var point in FilteredPoints)
            point.Refresh();
        OnPropertyChanged(nameof(ImageOrigin));
        OnPropertyChanged(nameof(TeachingRegions));
        RefreshImageMarkers();
    }

    private void RefreshMotionGroups()
    {
        MotionGroups = [
            MotionGroup.PcbPlacementHandler,
            MotionGroup.BoltFastening,
            MotionGroup.InspectionGantry
        ];
        OnPropertyChanged(nameof(MotionGroups));
        OnPropertyChanged(nameof(IsInspectionSelected));
        OnPropertyChanged(nameof(BoltPointEditorVisible));
        OnPropertyChanged(nameof(BoltPresetEditorVisible));
        if (!MotionGroups.Contains(SelectedMotionGroup))
        {
            SelectedMotionGroup = MotionGroups[0];
        }
    }
}
