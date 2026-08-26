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
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.UI;

public partial class StationTeachingViewModel : ObservableObject
{
    private readonly Dictionary<MotionGroup, IXyMotion> _motions;
    private readonly ICamera _inspectionCamera;
    private readonly ILightController _light;
    private readonly BufferStage _buffer;
    private readonly MachineState _state;
    private readonly LightingSettings _lighting;
    private readonly PcbPlacementHandlerSettings _placementSettings;
    private readonly BoltFasteningSettings _fasteningSettings;
    private readonly InspectionGantrySettings _inspectionGantrySettings;
    private readonly TeachingPointMapper _pointMapper;
    private CancellationTokenSource _motionCancellation = new();

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

    [ObservableProperty] private double _jogSpeed = 10.0;
    [ObservableProperty]
    private FasteningHead _newFasteningHead = FasteningHead.Shooting;

    [ObservableProperty]
    private IReadOnlyList<TeachingPoint> _filteredPoints = [];
    [ObservableProperty]
    private HousingSlot _newHousingSlot = HousingSlot.Housing1;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TeachImagePointCommand))]
    private BitmapSource? _liveImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCarrierImages))]
    [NotifyCanExecuteChangedFor(nameof(TeachImagePointCommand))]
    private IReadOnlyList<CarrierImageTileView> _carrierImages = [];

    [ObservableProperty]
    private double _millimetersPerPixel = 0.05;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCarrierImagesCommand))]
    private double _scanPitchX = 15.0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCarrierImagesCommand))]
    private double _scanPitchY = 11.0;

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
        [FromKeyedServices(MotionGroup.PcbPlacementHandler)] IXyMotion pcbPlacementMotion,
        [FromKeyedServices(MotionGroup.BoltFastening)] IXyMotion boltFasteningMotion,
        [FromKeyedServices(MotionGroup.InspectionGantry)] IXyMotion inspectionGantryMotion,
        ICamera inspectionCamera,
        ILightController light,
        BufferStage buffer,
        MachineState state,
        LightingSettings lighting,
        PcbPlacementHandlerSettings placementSettings,
        BoltFasteningSettings fasteningSettings,
        InspectionGantrySettings inspectionGantrySettings,
        TeachingPointMapper pointMapper,
        RecipeEditor recipeEditor)
    {
        _motions = new Dictionary<MotionGroup, IXyMotion>
        {
            [MotionGroup.PcbPlacementHandler] = pcbPlacementMotion,
            [MotionGroup.BoltFastening] = boltFasteningMotion,
            [MotionGroup.InspectionGantry] = inspectionGantryMotion,
        };
        _inspectionCamera = inspectionCamera;
        _light = light;
        _buffer = buffer;
        _state = state;
        _lighting = lighting;
        _placementSettings = placementSettings;
        _fasteningSettings = fasteningSettings;
        _inspectionGantrySettings = inspectionGantrySettings;
        _pointMapper = pointMapper;
        RecipeEditor = recipeEditor;
        MillimetersPerPixel = CurrentRecipe.CarrierImageMillimetersPerPixel;
        ScanPitchX = inspectionGantrySettings.CarrierScanPitchX;
        ScanPitchY = inspectionGantrySettings.CarrierScanPitchY;

        pcbPlacementMotion.PositionChanged +=
            (_, _, _) => ApplyPosition(MotionGroup.PcbPlacementHandler);
        boltFasteningMotion.PositionChanged +=
            (_, _, _) => ApplyPosition(MotionGroup.BoltFastening);
        inspectionGantryMotion.PositionChanged +=
            (_, _, _) => ApplyPosition(MotionGroup.InspectionGantry);
        pcbPlacementMotion.MovingChanged += OnMotionChanged;
        boltFasteningMotion.MovingChanged += OnMotionChanged;
        inspectionGantryMotion.MovingChanged += OnMotionChanged;
        inspectionCamera.FrameReady += UpdateLiveImage;
        buffer.StateChanged += OnBufferChanged;
        recipeEditor.Changed += OnRecipeChanged;

        RefreshTeachingPoints();
        ShowRecipeImages();
    }

    public RecipeEditor RecipeEditor { get; }
    public MotionGroup[] MotionGroups { get; } =
    [
        MotionGroup.PcbPlacementHandler,
        MotionGroup.BoltFastening,
        MotionGroup.InspectionGantry,
    ];
    public double[] JogSpeeds { get; } = [1.0, 10.0, 50.0];
    public FasteningHead[] FasteningHeads { get; } =
        Enum.GetValues<FasteningHead>();
    public HousingSlot[] HousingSlots { get; } =
        Enum.GetValues<HousingSlot>();
    public BoltFasteningRecipe BoltRecipe => CurrentRecipe.BoltFastening;
    public double CurrentX => CurrentMotion.GetPosition().X;
    public double CurrentY => CurrentMotion.GetPosition().Y;
    public double CurrentZ => CurrentMotion.GetPosition().Z;
    public bool CurrentMotionHasZ => CurrentMotion.HasZ;
    public bool IsInspectionSelected =>
        SelectedMotionGroup == MotionGroup.InspectionGantry;
    public bool HasCarrierImages => CarrierImages.Count > 0;
    private Recipe CurrentRecipe => RecipeEditor.Recipe;

    partial void OnSelectedMotionGroupChanged(
        MotionGroup oldValue,
        MotionGroup newValue)
    {
        CancelMotion();

        if (IsCameraLive)
        {
            StopCamera();
            if (newValue != MotionGroup.InspectionGantry)
            {
                IsCameraLive = false;
                LiveImage = null;
            }
            else
            {
                StartCamera();
            }
        }

        RefreshTeachingPoints();
        ShowRecipeImages();
        RefreshPosition();
        OnPropertyChanged(nameof(CurrentMotionHasZ));
        JogZPlusCommand.NotifyCanExecuteChanged();
        JogZMinusCommand.NotifyCanExecuteChanged();
        MoveToHorizontalZCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanTeachCurrentPosition))]
    private async Task TeachCurrentPositionAsync()
    {
        var point = SelectedPoint!;
        var current = CurrentMotion.GetPosition();
        point.Teach(current.X, current.Y, current.Z);
        _pointMapper.Apply(CurrentRecipe, FilteredPoints, point);
        if (point.Storage == TeachingStorage.Machine)
        {
            await SaveMachinePositionAsync(point.MotionGroup);
        }

        NotifyManualTeachingCommands();
    }

    private bool CanTeachCurrentPosition() =>
        SelectedPoint?.TeachMode != TeachMode.Image
        && CanUseCurrentHandler();

    private Task SaveMachinePositionAsync(MotionGroup motionGroup) =>
        motionGroup switch
        {
            MotionGroup.PcbPlacementHandler => _placementSettings.SaveAsync(),
            MotionGroup.BoltFastening => _fasteningSettings.SaveAsync(),
            MotionGroup.InspectionGantry => _inspectionGantrySettings.SaveAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(motionGroup)),
        };

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
            Housing = NewHousingSlot,
            Head = NewFasteningHead,
        };
        CurrentRecipe.BoltFastening.BoltPoints.Add(bolt);
        RefreshTeachingPoints();
    }

    private bool CanAddBoltPoint() =>
        SelectedMotionGroup is MotionGroup.BoltFastening
            or MotionGroup.InspectionGantry;

    [RelayCommand(CanExecute = nameof(CanRemoveBoltPoint))]
    private void RemoveBoltPoint()
    {
        var number = SelectedPoint!.BoltNumber;
        CurrentRecipe.BoltFastening.BoltPoints.RemoveAll(
            bolt => bolt.Number == number);
        RefreshTeachingPoints();
    }

    private bool CanRemoveBoltPoint() =>
        SelectedPoint?.Target is TeachingTarget.BoltWorkZ
            or TeachingTarget.BoltReference;

    public void Activate()
    {
        RecipeEditor.Refresh();
        RefreshTeachingPoints();
        ShowRecipeImages();
        RefreshPosition();
    }

    public void Deactivate()
    {
        MoveToHorizontalZCommand.Cancel();
        MoveToPointCommand.Cancel();
        CancelMotion();

        if (IsCameraLive)
        {
            StopCamera();
            IsCameraLive = false;
            LiveImage = null;
        }
    }

    private void RefreshTeachingPoints()
    {
        FilteredPoints = _pointMapper.BuildStations(CurrentRecipe)
            .Where(point => point.MotionGroup == SelectedMotionGroup)
            .ToArray();
        SelectedPoint = FilteredPoints.FirstOrDefault();
    }

    partial void OnSelectedPointChanged(TeachingPoint? value) =>
        RefreshImageMarkers();

    partial void OnMillimetersPerPixelChanged(double value)
    {
        CurrentRecipe.CarrierImageMillimetersPerPixel = value;
        CaptureCarrierImagesCommand.NotifyCanExecuteChanged();
    }

    partial void OnScanPitchXChanged(double value) =>
        _inspectionGantrySettings.CarrierScanPitchX = value;

    partial void OnScanPitchYChanged(double value) =>
        _inspectionGantrySettings.CarrierScanPitchY = value;

    private void OnRecipeChanged()
    {
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

        CarrierImages = SelectedMotionGroup == MotionGroup.InspectionGantry
            ? RecipeEditor.LoadCarrierImages()
            : [];

        RefreshImageMarkers();
    }

    private void RefreshImageMarkers()
    {
        if (SelectedMotionGroup != MotionGroup.InspectionGantry)
        {
            ImageMarkers = [];
            return;
        }

        ImageMarkers = FilteredPoints
            .Where(point => point.TeachMode == TeachMode.Image)
            .Where(point => _pointMapper.HasImagePosition(
                CurrentRecipe,
                point))
            .Select(point => new ImageMarker(
                point.X,
                point.Y,
                point.Target == TeachingTarget.BoltReference
                    ? $"{point.Housing!.Value.GetDescription()} {point.Name}"
                    : point.Target == TeachingTarget.InspectionUpperLeftLocatingPin
                        ? "UL"
                        : "LR",
                point == SelectedPoint))
            .ToArray();
    }

    private static void RunOnUi(Action action) =>
        Application.Current.Dispatcher.BeginInvoke(action);

    private void OnBufferChanged() =>
        RunOnUi(NotifyManualTeachingCommands);

    private void OnMotionChanged(bool _) =>
        RunOnUi(NotifyManualTeachingCommands);
}
