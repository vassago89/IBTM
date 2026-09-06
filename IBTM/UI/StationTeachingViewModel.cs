using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly StationTeachingPoints _teachingPoints;
    private readonly bool _inspectionEnabled;
    private readonly bool _ngCarrierTransferEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInspectionSelected))]
    [NotifyCanExecuteChangedFor(nameof(ToggleLiveViewCommand))]
    [NotifyCanExecuteChangedFor(nameof(CaptureCarrierImagesCommand))]
    [NotifyCanExecuteChangedFor(nameof(TeachImagePointCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBoltPointCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogXPlusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogXMinusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogYPlusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogYMinusCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(TeachImagePointCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveBoltPointCommand))]
    private TeachingPoint? _selectedPoint;

    public StationTeachingViewModel(
        PcbPlacementHandler placementHandler,
        BoltFasteningGantry fasteningGantry,
        InspectionGantry inspectionGantry,
        BoltInspector boltInspector,
        InspectionCameraSettings inspectionCameraSettings,
        BufferStage buffer,
        MachineState state,
        InspectionGantrySettings inspectionGantrySettings,
        CarrierReferenceSettings carrierReference,
        UnitSettings units,
        StationTeachingPoints teachingPoints,
        RecipeEditor recipeEditor,
        OperationCancellation operations) : base(operations)
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
        _inspectionEnabled = units.Inspection;
        _ngCarrierTransferEnabled = units.NgCarrierTransfer;
        _teachingPoints = teachingPoints;
        RecipeEditor = recipeEditor;
        var motionGroups = new List<MotionGroup>();
        if (units.PcbPlacement)
        {
            motionGroups.Add(MotionGroup.PcbPlacementHandler);
        }

        if (units.BoltFastening)
        {
            motionGroups.Add(MotionGroup.BoltFastening);
        }

        if (units.Inspection || units.NgCarrierTransfer)
        {
            motionGroups.Add(MotionGroup.InspectionGantry);
        }

        MotionGroups = motionGroups.ToArray();
        if (MotionGroups.Length > 0)
        {
            SelectedMotionGroup = MotionGroups[0];
        }

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
        boltInspector.FrameReady += UpdateLiveImage;
        boltInspector.LiveViewFailed += OnLiveViewFailed;
        buffer.StateChanged += QueueManualCommandRefresh;
        state.Changed += QueueManualCommandRefresh;
        recipeEditor.Changed += OnRecipeChanged;

        RefreshTeachingPoints();
        ShowRecipeImages();
    }

    public RecipeEditor RecipeEditor { get; }
    public MotionGroup[] MotionGroups { get; }
    public FasteningHead[] FasteningHeads { get; } =
        Enum.GetValues<FasteningHead>();
    public HeatSinkSlot[] HeatSinkSlots { get; } =
        Enum.GetValues<HeatSinkSlot>();
    public BoltFasteningRecipe BoltRecipe => CurrentRecipe.BoltFastening;
    public bool CurrentMotionHasZ => CurrentFeedback.HasZ;
    public bool IsInspectionSelected =>
        _inspectionEnabled
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
            if (!_teachingPoints.CarrierReferenceReady)
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
        OnPropertyChanged(nameof(CurrentMotionHasZ));
        OnPropertyChanged(nameof(BoltPointEditorVisible));
        OnPropertyChanged(nameof(BoltPresetEditorVisible));
        JogZPlusCommand.NotifyCanExecuteChanged();
        JogZMinusCommand.NotifyCanExecuteChanged();
        MoveToHorizontalZCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanTeachCurrentPosition))]
    private async Task TeachCurrentPositionAsync()
    {
        var point = SelectedPoint!;
        var current = CurrentPosition();
        point.Teach(current.X, current.Y, current.Z);
        _teachingPoints.Apply(CurrentRecipe, FilteredPoints, point);
        if (point.Storage == TeachingStorage.Machine)
        {
            await _teachingPoints.SaveAsync(point);
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
        SelectedMotionGroup is MotionGroup.BoltFastening
        || IsInspectionSelected;

    [RelayCommand(CanExecute = nameof(CanRemoveBoltPoint))]
    private void RemoveBoltPoint()
    {
        var number = SelectedPoint!.BoltNumber;
        CurrentRecipe.BoltFastening.BoltPoints.RemoveAll(
            bolt => bolt.Number == number);
        RefreshTeachingPoints();
    }

    private bool CanRemoveBoltPoint() =>
        SelectedPoint?.Target is TeachingTarget.BoltPointZ
            or TeachingTarget.BoltReference;

    public void Activate()
    {
        RecipeEditor.Refresh();
        RefreshTeachingPoints();
        ShowRecipeImages();
        ActivatePositionUpdates();
        NotifyManualTeachingCommands();
    }

    public void Deactivate()
    {
        DeactivatePositionUpdates();
        MoveToHorizontalZCommand.Cancel();
        MoveToPointCommand.Cancel();
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
                MoveToHorizontalZCommand,
                MoveToPointCommand,
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
        FilteredPoints = _teachingPoints.Build(CurrentRecipe)
            .Where(point => point.MotionGroup == SelectedMotionGroup)
            .Where(PointEnabled)
            .ToArray();
        SelectedPoint = FilteredPoints.FirstOrDefault(point =>
                point.Target == selectedTarget
                && point.BoltNumber == selectedBolt)
            ?? NextImageTeachingPoint()
            ?? FilteredPoints.FirstOrDefault();
    }

    private TeachingPoint? NextImageTeachingPoint()
    {
        if (!IsInspectionSelected
            || CurrentRecipe.CarrierImages.Count == 0)
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

        return FilteredPoints.FirstOrDefault(point =>
            point.Target == TeachingTarget.BoltReference
            && !_teachingPoints.HasImagePosition(CurrentRecipe, point));
    }

    partial void OnSelectedPointChanged(TeachingPoint? value) =>
        RefreshImageMarkers();

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
            .Where(point => point.TeachMode == TeachMode.Image)
            .Where(point => _teachingPoints.HasImagePosition(
                CurrentRecipe,
                point))
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

    private bool PointEnabled(TeachingPoint point) => point.Target switch
    {
        TeachingTarget.NgCarrierPickup
            or TeachingTarget.NgShuttlePlace => _ngCarrierTransferEnabled,
        _ when point.MotionGroup == MotionGroup.InspectionGantry =>
            _inspectionEnabled,
        _ => true,
    };
}
