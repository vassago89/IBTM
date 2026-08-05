using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.UI;

public partial class StationTeachingViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<MotionGroup, MotionService> _motions;
    private readonly IIoService _io;
    private readonly ICamera _alignmentCamera;
    private readonly ICamera _inspectionCamera;
    private readonly ILightController _light;
    private readonly LightingSettings _lighting;
    private readonly PcbPlacementStation _pcbPlacement;
    private readonly InspectionStation _inspection;
    private readonly MachineStore _store;
    private readonly MachineSettings _settings;
    private readonly TeachingPointMapper _pointMapper;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRecipeCommand))]
    private string _recipeName = "Default";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleLiveViewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleLaserCommand))]
    [NotifyCanExecuteChangedFor(nameof(CameraClickCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBoltPointCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogXPlusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogXMinusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogYPlusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogYMinusCommand))]
    private MotionGroup _selectedMotionGroup = MotionGroup.Inspection;

    [ObservableProperty] private double _currentX;
    [ObservableProperty] private double _currentY;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JogXPlusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogXMinusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogYPlusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogYMinusCommand))]
    private double _currentZ;

    [ObservableProperty] private double _jogSpeed = 10.0;
    [ObservableProperty]
    private FasteningHead _newFasteningHead = FasteningHead.Standard;
    [ObservableProperty] private ImageSource? _liveImage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CameraClickCommand))]
    private bool _isCameraLive;

    [ObservableProperty] private bool _laserOn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TeachCurrentPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToPointCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveBoltPointCommand))]
    private TeachingPoint? _selectedPoint;

    [ObservableProperty] private string _statusMessage = string.Empty;

    public StationTeachingViewModel(
        [FromKeyedServices(MotionGroup.PcbPlacement)] MotionService pcbPlacementMotion,
        [FromKeyedServices(MotionGroup.BoltFastening)] MotionService boltFasteningMotion,
        [FromKeyedServices(MotionGroup.Inspection)] MotionService inspectionMotion,
        IIoService io,
        [FromKeyedServices(CameraRole.Alignment)] ICamera alignmentCamera,
        [FromKeyedServices(CameraRole.Inspection)] ICamera inspectionCamera,
        ILightController light,
        LightingSettings lighting,
        PcbPlacementStation pcbPlacement,
        InspectionStation inspection,
        MachineStore store,
        MachineSettings settings,
        TeachingPointMapper pointMapper,
        Recipe recipe)
    {
        _motions = new Dictionary<MotionGroup, MotionService>
        {
            [MotionGroup.PcbPlacement] = pcbPlacementMotion,
            [MotionGroup.BoltFastening] = boltFasteningMotion,
            [MotionGroup.Inspection] = inspectionMotion,
        };
        _io = io;
        _alignmentCamera = alignmentCamera;
        _inspectionCamera = inspectionCamera;
        _light = light;
        _lighting = lighting;
        _pcbPlacement = pcbPlacement;
        _inspection = inspection;
        _store = store;
        _settings = settings;
        _pointMapper = pointMapper;
        CurrentRecipe = recipe;
        RecipeName = recipe.Name;

        pcbPlacementMotion.PositionChanged +=
            (x, y, z) => ApplyPosition(MotionGroup.PcbPlacement, x, y, z);
        boltFasteningMotion.PositionChanged +=
            (x, y, z) => ApplyPosition(MotionGroup.BoltFastening, x, y, z);
        inspectionMotion.PositionChanged +=
            (x, y, z) => ApplyPosition(MotionGroup.Inspection, x, y, z);
        alignmentCamera.FrameReady +=
            frame => UpdateLiveImage(MotionGroup.PcbPlacement, frame);
        inspectionCamera.FrameReady +=
            frame => UpdateLiveImage(MotionGroup.Inspection, frame);
        io.OutputChanged += OnOutputChanged;

        RefreshTeachingPoints();
        RefreshRecipeFiles();
    }

    public ObservableCollection<TeachingPoint> FilteredPoints { get; } = [];
    public ObservableCollection<string> RecipeFiles { get; } = [];
    public Recipe CurrentRecipe { get; }
    public MotionGroup[] MotionGroups { get; } =
    [
        MotionGroup.PcbPlacement,
        MotionGroup.BoltFastening,
        MotionGroup.Inspection,
    ];
    public double[] JogSpeeds { get; } = [1.0, 10.0, 50.0];
    public FasteningHead[] FasteningHeads { get; } =
        Enum.GetValues<FasteningHead>();
    public bool HasLaser => CurrentLaser is not null;

    partial void OnSelectedMotionGroupChanged(
        MotionGroup oldValue,
        MotionGroup newValue)
    {
        SetLaser(oldValue, false);
        LaserOn = GetLaser(newValue) is { } laser && _io.GetOutput(laser);
        OnPropertyChanged(nameof(HasLaser));
        GetMotion(oldValue).Stop();

        if (IsCameraLive)
        {
            StopCamera(oldValue);
            if (GetCamera(newValue) is null)
            {
                IsCameraLive = false;
                LiveImage = null;
            }
            else
            {
                StartCamera(newValue);
            }
        }

        RefreshTeachingPoints();
        RefreshPosition();
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
            await _store.SaveSettingsAsync(_settings);
        }

        MoveToPointCommand.NotifyCanExecuteChanged();
        StatusMessage = $"Taught: {point.Name}";
    }

    private bool CanTeachCurrentPosition() => SelectedPoint is not null;

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
            Head = NewFasteningHead,
            TargetTorqueNm = _settings.BoltFastening
                .GetHead(NewFasteningHead)
                .DefaultTorqueNm,
        };
        CurrentRecipe.BoltFastening.BoltPoints.Add(bolt);
        RefreshTeachingPoints();
        StatusMessage =
            $"Bolt added: {bolt.Name} · {bolt.Head.GetDescription()}";
    }

    private bool CanAddBoltPoint() =>
        SelectedMotionGroup is MotionGroup.BoltFastening
            or MotionGroup.Inspection;

    [RelayCommand(CanExecute = nameof(CanRemoveBoltPoint))]
    private void RemoveBoltPoint()
    {
        var number = SelectedPoint!.BoltNumber;
        CurrentRecipe.BoltFastening.BoltPoints.RemoveAll(
            bolt => bolt.Number == number);
        RefreshTeachingPoints();
        StatusMessage = $"Bolt removed: B{number}";
    }

    private bool CanRemoveBoltPoint() =>
        SelectedPoint?.Target is TeachingTarget.BoltWorkZ
            or TeachingTarget.BoltReference;

    public void Activate()
    {
        RecipeName = CurrentRecipe.Name;
        RefreshTeachingPoints();
        RefreshRecipeFiles();
        RefreshPosition();
        LaserOn = CurrentLaser is { } laser && _io.GetOutput(laser);
    }

    public void Deactivate()
    {
        MoveToSafeZCommand.Cancel();
        CameraClickCommand.Cancel();
        MoveToPointCommand.Cancel();
        foreach (var motion in _motions.Values)
        {
            motion.Stop();
        }

        if (IsCameraLive)
        {
            StopCamera(SelectedMotionGroup);
            IsCameraLive = false;
            LiveImage = null;
        }

        SetLaser(SelectedMotionGroup, false);
        LaserOn = false;
    }

    private void RefreshTeachingPoints()
    {
        FilteredPoints.Clear();
        foreach (var point in _pointMapper.BuildStations(CurrentRecipe)
                     .Where(point =>
                         point.MotionGroup == SelectedMotionGroup))
        {
            FilteredPoints.Add(point);
        }

        SelectedPoint = FilteredPoints.FirstOrDefault();
    }

    private void RefreshRecipeFiles()
    {
        RecipeFiles.Clear();
        foreach (var fileName in _store.GetRecipeFiles())
        {
            RecipeFiles.Add(fileName);
        }
    }

    private static void RunOnUi(Action action) =>
        Application.Current.Dispatcher.BeginInvoke(action);
}
