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
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;

namespace IBTM.UI;

public partial class StationTeachingViewModel : TeachingMotionViewModel
{
    private readonly PcbPlacementHandler _placementHandler;
    private readonly BoltFasteningGantry _fasteningGantry;
    private readonly InspectionGantry _inspectionGantry;
    private readonly BoltInspector _boltInspector;
    private readonly InspectionCameraSettings _inspectionCameraSettings;
    private readonly BufferStage _buffer;
    private readonly MachineState _state;
    private readonly InspectionGantrySettings _inspectionGantrySettings;
    private readonly CarrierReferenceSettings _carrierReference;
    private readonly PcbPlacementHandlerSettings _placementSettings;
    private readonly BoltFasteningSettings _fasteningSettings;
    private readonly NgCarrierTransferSettings _ngTransferSettings;
    private readonly UnitSettings _units;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInspectionSelected))]
    [NotifyPropertyChangedFor(nameof(IoGroups))]
    [NotifyPropertyChangedFor(nameof(TeachingOutputs))]
    [NotifyCanExecuteChangedFor(nameof(ToggleLiveViewCommand))]
    [NotifyCanExecuteChangedFor(nameof(CaptureCarrierImagesCommand))]
    [NotifyCanExecuteChangedFor(nameof(TeachImagePointCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBoltPointCommand))]
    private MotionGroup _selectedMotionGroup = MotionGroup.InspectionGantry;

    [ObservableProperty]
    private FasteningHead _newFasteningHead = FasteningHead.Shooting;

    [ObservableProperty]
    private IReadOnlyList<TeachingPoint> _filteredPoints = [];
    [ObservableProperty]
    private HeatSinkSlot _newHeatSinkSlot = HeatSinkSlot.HeatSink1;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TeachImagePointCommand))]
    private BitmapSource? _liveImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCarrierImages))]
    [NotifyCanExecuteChangedFor(nameof(TeachImagePointCommand))]
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
    private bool _isCameraLive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TeachCurrentPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToPointCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToXYCommand))]
    [NotifyCanExecuteChangedFor(nameof(TeachImagePointCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveBoltPointCommand))]
    private TeachingPoint? _selectedPoint;

    public StationTeachingViewModel(
        PcbPlacementHandler placementHandler,
        BoltFasteningGantry fasteningGantry,
        InspectionGantry inspectionGantry,
        NgCarrierTransfer ngCarrierTransfer,
        BoltInspector boltInspector,
        InspectionCameraSettings inspectionCameraSettings,
        BufferStage buffer,
        MachineState state,
        InspectionGantrySettings inspectionGantrySettings,
        CarrierReferenceSettings carrierReference,
        UnitSettings units,
        PcbPlacementHandlerSettings placementSettings,
        BoltFasteningSettings fasteningSettings,
        NgCarrierTransferSettings ngTransferSettings,
        RecipeEditor recipeEditor,
        OperationCancellation operations,
        IReadOnlyDictionary<MotionGroup, IoStatus[]> ioGroups,
        IReadOnlyDictionary<MotionGroup, IReadOnlyDictionary<OutputIo, TeachingOutput>> teachingOutputs)
        : base(operations, state, ioGroups, teachingOutputs)
    {
        _placementHandler = placementHandler;
        _fasteningGantry = fasteningGantry;
        _inspectionGantry = inspectionGantry;
        _boltInspector = boltInspector;
        _inspectionCameraSettings = inspectionCameraSettings;
        _buffer = buffer;
        _state = state;
        _inspectionGantrySettings = inspectionGantrySettings;
        _carrierReference = carrierReference;
        _units = units;
        _placementSettings = placementSettings;
        _fasteningSettings = fasteningSettings;
        _ngTransferSettings = ngTransferSettings;
        RecipeEditor = recipeEditor;
        RefreshMotionGroups();

        MillimetersPerPixel = CurrentRecipe.CarrierImageMillimetersPerPixel;
        ScanOverlap =
            inspectionGantrySettings.CarrierScanOverlapMillimeters;

        placementHandler.Feedback.PositionChanged += (x, y, z) =>
            QueuePositionRefresh(
                MotionGroup.PcbPlacementHandler,
                x,
                y,
                z);
        fasteningGantry.Feedback.PositionChanged += (x, y, z) =>
            QueuePositionRefresh(MotionGroup.BoltFastening, x, y, z);
        inspectionGantry.Feedback.PositionChanged += (x, y, z) =>
            QueuePositionRefresh(MotionGroup.InspectionGantry, x, y, z);
        placementHandler.Feedback.MovingChanged += QueueManualCommandRefresh;
        fasteningGantry.Feedback.MovingChanged += QueueManualCommandRefresh;
        inspectionGantry.Feedback.MovingChanged += QueueManualCommandRefresh;
        placementHandler.Changed += QueueManualCommandRefresh;
        fasteningGantry.Changed += QueueManualCommandRefresh;
        ngCarrierTransfer.Changed += QueueManualCommandRefresh;
        boltInspector.FrameReady += UpdateLiveImage;
        boltInspector.LiveViewFailed += OnLiveViewFailed;
        buffer.StateChanged += QueueManualCommandRefresh;
        state.Changed += QueueManualCommandRefresh;
        recipeEditor.Changed += OnRecipeChanged;

        RefreshTeachingPoints();
        ShowRecipeImages();
    }

    public RecipeEditor RecipeEditor { get; }
    protected override IReadOnlyList<TeachingPoint> CurrentPoints => FilteredPoints;
    protected override TeachingPoint? CurrentPoint
    {
        get => SelectedPoint;
        set => SelectedPoint = value;
    }
    public MotionGroup[] MotionGroups { get; private set; } = [];
    public FasteningHead[] FasteningHeads { get; } =
        Enum.GetValues<FasteningHead>();
    public HeatSinkSlot[] HeatSinkSlots { get; } =
        Enum.GetValues<HeatSinkSlot>();
    public BoltFasteningRecipe BoltRecipe => CurrentRecipe.BoltFastening;
    public TeachingSaveBehavior SaveBehavior => SelectedPoint?.Target is
        TeachingTarget.CarrierUpperLeftLocatingPin or TeachingTarget.CarrierLowerRightLocatingPin
        ? TeachingSaveBehavior.CameraCenter
        : SelectedPoint?.TeachMode == TeachMode.Image
            ? TeachingSaveBehavior.Image
            : SelectedPoint?.Storage == TeachingStorage.Machine
                ? TeachingSaveBehavior.Machine
                : TeachingSaveBehavior.Recipe;
    public bool IsInspectionSelected =>
        _units.Inspection
        && SelectedMotionGroup == MotionGroup.InspectionGantry;
    public bool BoltPointEditorVisible =>
        SelectedMotionGroup == MotionGroup.BoltFastening
        || IsInspectionSelected;
    public bool BoltPresetEditorVisible =>
        SelectedMotionGroup == MotionGroup.BoltFastening;
    public bool HasCarrierImages => CarrierImages.Count > 0;
    public Point? CarrierOrigin
    {
        get
        {
            if (!_carrierReference.IsDefined)
            {
                return null;
            }

            var origin = _carrierReference.UpperLeftLocatingPin!;
            return new Point(origin.X, origin.Y);
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

            var width = _inspectionCameraSettings
                .FieldOfViewWidthMillimeters;
            var height = _inspectionCameraSettings
                .FieldOfViewHeightMillimeters;
            return new Rect(
                CurrentX - (width / 2),
                CurrentY - (height / 2),
                width,
                height);
        }
    }
    private Recipe CurrentRecipe => RecipeEditor.Recipe;

    partial void OnSelectedMotionGroupChanged(MotionGroup value)
    {
        CancelMotion();

        if (IsCameraLive)
        {
            StopCamera();
            LiveImage = null;
        }

        RefreshTeachingPoints();
        ShowRecipeImages();
        RefreshPosition();
        OnPropertyChanged(nameof(HasY));
        OnPropertyChanged(nameof(HasZ));
        OnPropertyChanged(nameof(BoltPointEditorVisible));
        OnPropertyChanged(nameof(BoltPresetEditorVisible));
        NotifyMotionCommands();
    }

    [RelayCommand(CanExecute = nameof(CanTeachCurrentPosition))]
    private async Task TeachCurrentPositionAsync()
    {
        var point = SelectedPoint!;
        var current = CurrentPosition();
        point.Teach(current.X, current.Y, current.Z);
        point.Apply();
        RefreshPointPositions();
        if (point.Storage == TeachingStorage.Machine)
        {
            using var operation = LinkMotion(CancellationToken.None);
            await point.Position.Setting!.SaveAsync();
        }

        if (point.Target == TeachingTarget.CarrierUpperLeftLocatingPin)
        {
            SelectedPoint = FilteredPoints.First(candidate =>
                candidate.Target == TeachingTarget.CarrierLowerRightLocatingPin);
        }

        NotifyManualTeachingCommands();
    }

    private bool CanTeachCurrentPosition() =>
        SelectedPoint is { TeachMode: not TeachMode.Image }
        && CanUseCurrentHandler();

    [RelayCommand(CanExecute = nameof(CanAddBoltPoint))]
    private void AddBoltPoint()
    {
        var number = CurrentRecipe.BoltFastening.BoltPoints
            .Select(bolt => bolt.Number)
            .DefaultIfEmpty()
            .Max() + 1;
        var bolt = new BoltPoint
        {
            Number = number,
            HeatSink = NewHeatSinkSlot,
            Head = NewFasteningHead,
        };
        CurrentRecipe.BoltFastening.BoltPoints.Add(bolt);
        RefreshTeachingPoints();
        SelectedPoint = FilteredPoints.First(point =>
            point.BoltNumber == number
            && point.Target == (IsInspectionSelected
                ? TeachingTarget.BoltReference
                : TeachingTarget.BoltPointZ));
    }

    private bool CanAddBoltPoint() =>
        CanEditTeaching
        && (SelectedMotionGroup is MotionGroup.BoltFastening || IsInspectionSelected);

    [RelayCommand(CanExecute = nameof(CanRemoveBoltPoint))]
    private void RemoveBoltPoint()
    {
        var number = SelectedPoint!.BoltNumber;
        CurrentRecipe.BoltFastening.BoltPoints.RemoveAll(
            bolt => bolt.Number == number);
        RefreshTeachingPoints();
    }

    private bool CanRemoveBoltPoint() =>
        CanEditTeaching
        && SelectedPoint?.Target is TeachingTarget.BoltPointZ or TeachingTarget.BoltReference;

    public void Activate()
    {
        RecipeEditor.Refresh();
        RefreshMotionGroups();
        RefreshTeachingPoints();
        ShowRecipeImages();
        ActivatePositionUpdates();
        NotifyManualTeachingCommands();
    }

    public void Deactivate()
    {
        DeactivatePositionUpdates();
        JogCommand.Cancel();
        StepCommand.Cancel();
        MoveToHorizontalZCommand.Cancel();
        MoveToPointCommand.Cancel();
        MoveToXYCommand.Cancel();
        SetOutputOnCommand.Cancel();
        SetOutputOffCommand.Cancel();
        CaptureCarrierImagesCommand.Cancel();
        CancelMotion();

        if (IsCameraLive)
        {
            StopCamera();
            LiveImage = null;
        }
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
                MoveToXYCommand,
                SetOutputOnCommand,
                SetOutputOffCommand,
                CaptureCarrierImagesCommand,
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
        }
    }

    private void RefreshTeachingPoints()
    {
        var selectedTarget = SelectedPoint?.Target;
        var selectedBolt = SelectedPoint?.BoltNumber;
        TeachingPosition[] positions =
        [
            .. _placementSettings.GetTeachingPositions(),
            .. CurrentRecipe.PcbPlacement.GetTeachingPositions(),
            .. _fasteningSettings.GetTeachingPositions(CurrentRecipe.BoltFastening, _carrierReference),
            .. _inspectionGantrySettings.GetTeachingPositions(_carrierReference),
            .. _inspectionGantrySettings.GetBoltTeachingPositions(CurrentRecipe.BoltFastening.BoltPoints, _carrierReference),
            .. _ngTransferSettings.GetTeachingPositions(),
        ];
        FilteredPoints = positions
            .Where(point => point.MotionGroup == SelectedMotionGroup)
            .Select(position => new TeachingPoint(position))
            .Where(PointEnabled)
            .ToArray();
        SelectedPoint = FilteredPoints.FirstOrDefault(point =>
                point.Target == selectedTarget
                && point.BoltNumber == selectedBolt)
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
            return FilteredPoints.FirstOrDefault(point =>
                point.Target == TeachingTarget.CarrierUpperLeftLocatingPin);
        }

        if (_carrierReference.LowerRightLocatingPin is null)
        {
            return FilteredPoints.FirstOrDefault(point =>
                point.Target == TeachingTarget.CarrierLowerRightLocatingPin);
        }

        return CurrentRecipe.CarrierImages.Count == 0
            ? null
            : FilteredPoints.FirstOrDefault(point =>
                point.Target == TeachingTarget.BoltReference
                && !point.Position.HasPosition);
    }

    partial void OnSelectedPointChanged(TeachingPoint? value)
    {
        CancelMotion();
        NotifyPointSelectionCommands();
        OnPropertyChanged(nameof(SaveBehavior));
        RefreshImageMarkers();
    }

    partial void OnMillimetersPerPixelChanged(double value)
    {
        CurrentRecipe.CarrierImageMillimetersPerPixel = value;
        CaptureCarrierImagesCommand.NotifyCanExecuteChanged();
    }

    partial void OnScanOverlapChanged(double value) =>
        _inspectionGantrySettings.CarrierScanOverlapMillimeters = value;

    private void OnRecipeChanged()
    {
        SelectedPoint = null;
        RefreshTeachingPoints();
        OnPropertyChanged(nameof(BoltRecipe));
        MillimetersPerPixel = CurrentRecipe.CarrierImageMillimetersPerPixel;
        ShowRecipeImages();
    }

    private void ShowRecipeImages()
    {
        if (!IsCameraLive)
        {
            LiveImage = null;
        }

        CarrierImages = IsInspectionSelected
            ? RecipeEditor.LoadCarrierImages()
            : [];

        RefreshImageMarkers();
    }

    private void RefreshImageMarkers()
    {
        if (!IsInspectionSelected)
        {
            ImageMarkers = [];
            return;
        }

        ImageMarkers = FilteredPoints
            .Where(point => point.TeachMode == TeachMode.Image
                || point.Target is TeachingTarget.CarrierUpperLeftLocatingPin
                    or TeachingTarget.CarrierLowerRightLocatingPin)
            .Where(point => point.Position.HasPosition)
            .Select(point => new ImageMarker(
                point.X,
                point.Y,
                point.Target == TeachingTarget.BoltReference
                    ? $"{point.HeatSink!.Value.GetDescription()} {point.Name}"
                    : point.Target == TeachingTarget.CarrierUpperLeftLocatingPin
                        ? "UL"
                        : "LR",
                point == SelectedPoint))
            .ToArray();
    }

    private void RefreshPointPositions()
    {
        foreach (var point in FilteredPoints) point.Refresh();
        OnPropertyChanged(nameof(CarrierOrigin));
        RefreshImageMarkers();
    }

    private bool PointEnabled(TeachingPoint point) => point.Target switch
    {
        TeachingTarget.NgCarrierPickup
            or TeachingTarget.NgShuttlePlace => _units.NgCarrierTransfer,
        _ when point.MotionGroup == MotionGroup.InspectionGantry =>
            _units.Inspection,
        _ => true,
    };

    private void RefreshMotionGroups()
    {
        var groups = new List<MotionGroup>();
        if (_units.PcbPlacement)
        {
            groups.Add(MotionGroup.PcbPlacementHandler);
        }

        if (_units.BoltFastening)
        {
            groups.Add(MotionGroup.BoltFastening);
        }

        if (_units.Inspection || _units.NgCarrierTransfer)
        {
            groups.Add(MotionGroup.InspectionGantry);
        }

        MotionGroups = [.. groups];
        OnPropertyChanged(nameof(MotionGroups));
        OnPropertyChanged(nameof(IsInspectionSelected));
        OnPropertyChanged(nameof(BoltPointEditorVisible));
        OnPropertyChanged(nameof(BoltPresetEditorVisible));
        if (groups.Count > 0 && !groups.Contains(SelectedMotionGroup))
        {
            SelectedMotionGroup = groups[0];
        }
    }
}
