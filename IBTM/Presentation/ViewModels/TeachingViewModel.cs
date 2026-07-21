using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Configuration;
using IBTM.Core.Machine;
using IBTM.Device;
using IBTM.Infrastructure.Persistence;
using IBTM.Orchestration;
using IBTM.Presentation.Imaging;
using IBTM.Presentation.Mappers;
using IBTM.Presentation.Models;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Presentation.ViewModels;

public partial class TeachingViewModel : ObservableObject, IDisposable
{
    private static readonly double[] JogSpeeds = [1.0, 10.0, 50.0];
    private static readonly int[] LaserChannels =
        [PcbPlacementStation.LaserChannel, BoltFasteningStation.LaserChannel, InspectionStation.LaserChannel];

    private readonly IMotionService[] _motions;
    private readonly IIoService _io;
    private readonly ICameraStreamService _zone2Camera;
    private readonly ICameraStreamService _zone3Camera;
    private readonly RecipeService _recipes;
    private readonly MachineConfig _config;
    private readonly TeachingPointMapper _pointMapper;
    private readonly ProcessOrchestrator _orchestrator;
    private readonly List<TeachingPoint> _allPoints = [];

    [ObservableProperty] private Recipe _currentRecipe = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRecipeCommand))]
    private string _recipeName = "Default";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleLiveViewCommand))]
    [NotifyCanExecuteChangedFor(nameof(CameraClickCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBoltPointCommand))]
    private int _selectedZone = 3;

    [ObservableProperty] private double _currentX;
    [ObservableProperty] private double _currentY;
    [ObservableProperty] private double _currentZ;
    [ObservableProperty] private int _jogSpeedIndex = 1;
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

    public TeachingViewModel(
        [FromKeyedServices(1)] IMotionService zone1,
        [FromKeyedServices(2)] IMotionService zone2,
        [FromKeyedServices(3)] IMotionService zone3,
        IIoService io,
        [FromKeyedServices(2)] ICameraStreamService zone2Camera,
        [FromKeyedServices(3)] ICameraStreamService zone3Camera,
        RecipeService recipes,
        MachineConfig config,
        TeachingPointMapper pointMapper,
        ProcessOrchestrator orchestrator)
    {
        _motions = [zone1, zone2, zone3];
        _io = io;
        _zone2Camera = zone2Camera;
        _zone3Camera = zone3Camera;
        _recipes = recipes;
        _config = config;
        _pointMapper = pointMapper;
        _orchestrator = orchestrator;
        CurrentRecipe = orchestrator.CurrentRecipe;
        RecipeName = CurrentRecipe.Name;

        zone1.PositionChanged += OnZone1PositionChanged;
        zone2.PositionChanged += OnZone2PositionChanged;
        zone3.PositionChanged += OnZone3PositionChanged;
        zone2Camera.FrameReady += OnZone2FrameReady;
        zone3Camera.FrameReady += OnZone3FrameReady;

        BuildTeachingPoints();
        RefreshRecipeFiles();
    }

    public ObservableCollection<TeachingPoint> FilteredPoints { get; } = [];
    public ObservableCollection<string> RecipeFiles { get; } = [];
    public double JogSpeed => JogSpeeds[JogSpeedIndex];

    private IMotionService CurrentMotion => GetMotion(SelectedZone);
    private ICameraStreamService? CurrentCamera => GetCamera(SelectedZone);
    private int LaserChannel => LaserChannels[SelectedZone - 1];

    partial void OnCurrentRecipeChanged(Recipe value) =>
        _orchestrator.CurrentRecipe = value;

    partial void OnSelectedZoneChanged(int oldValue, int newValue)
    {
        if (LaserOn)
        {
            _io.SetOutput(LaserChannels[oldValue - 1], false);
            LaserOn = false;
        }

        GetMotion(oldValue).Stop();

        if (IsCameraLive)
        {
            GetCamera(oldValue)?.StopLiveView();
            if (CurrentCamera is null)
            {
                IsCameraLive = false;
                LiveImage = null;
            }
            else
            {
                CurrentCamera.StartLiveView();
            }
        }

        RefreshFilteredPoints();
        RefreshPosition();
    }

    [RelayCommand] private void JogXPlus() => CurrentMotion.JogX(JogSpeed);
    [RelayCommand] private void JogXMinus() => CurrentMotion.JogX(-JogSpeed);
    [RelayCommand] private void JogYPlus() => CurrentMotion.JogY(JogSpeed);
    [RelayCommand] private void JogYMinus() => CurrentMotion.JogY(-JogSpeed);
    [RelayCommand] private void JogZPlus() => CurrentMotion.JogZ(JogSpeed);
    [RelayCommand] private void JogZMinus() => CurrentMotion.JogZ(-JogSpeed);
    [RelayCommand] private void JogStop() => CurrentMotion.Stop();

    [RelayCommand(CanExecute = nameof(CanToggleLiveView))]
    private void ToggleLiveView()
    {
        if (IsCameraLive)
        {
            CurrentCamera!.StopLiveView();
            LiveImage = null;
        }
        else
        {
            CurrentCamera!.StartLiveView();
        }

        IsCameraLive = !IsCameraLive;
    }

    private bool CanToggleLiveView() => CurrentCamera is not null;

    [RelayCommand(CanExecute = nameof(CanUseCamera))]
    private async Task CameraClickAsync(Point clickPosition)
    {
        var camera = CurrentCamera!;
        var pixelsPerMm = SelectedZone == 2
            ? _config.BoltFastening.PixelsPerMm
            : _config.Inspection.PixelsPerMm;
        var targetX = CurrentX + ((clickPosition.X - (camera.ImageWidth / 2.0)) / pixelsPerMm);
        var targetY = CurrentY + ((clickPosition.Y - (camera.ImageHeight / 2.0)) / pixelsPerMm);

        await CurrentMotion.MoveToXYAsync(
            targetX,
            targetY,
            GetMotionSettings(SelectedZone).SpeedXY);
        StatusMessage = $"Move → X:{targetX:F3} Y:{targetY:F3}";
    }

    private bool CanUseCamera() => IsCameraLive && CurrentCamera is not null;

    [RelayCommand]
    private void ToggleLaser()
    {
        LaserOn = !LaserOn;
        _io.SetOutput(LaserChannel, LaserOn);
    }

    [RelayCommand(CanExecute = nameof(CanTeachCurrentPosition))]
    private void TeachCurrentPosition()
    {
        var current = CurrentMotion.GetPosition();
        SelectedPoint!.Teach(current.X, current.Y, current.Z);
        _pointMapper.Apply(CurrentRecipe, _allPoints, SelectedPoint);
        MoveToPointCommand.NotifyCanExecuteChanged();
        StatusMessage = $"Taught: {SelectedPoint.Name}";
    }

    private bool CanTeachCurrentPosition() => SelectedPoint is not null;

    [RelayCommand(CanExecute = nameof(CanMoveToPoint))]
    private async Task MoveToPointAsync()
    {
        var point = SelectedPoint!;
        var motion = GetMotion(point.Zone);
        var settings = GetMotionSettings(point.Zone);

        await motion.MoveToZAsync(0, settings.SpeedZ);
        await motion.MoveToXYAsync(point.X, point.Y, settings.SpeedXY);
        await motion.MoveToZAsync(point.Z, settings.SpeedZ);
        StatusMessage = $"Moved to: {point.Name}";
    }

    private bool CanMoveToPoint() => SelectedPoint?.IsTaught == true;

    [RelayCommand(CanExecute = nameof(CanAddBoltPoint))]
    private void AddBoltPoint()
    {
        var index = CurrentRecipe.BoltFastening.BoltPoints.Count + 1;
        var bolt = new BoltPoint
        {
            Name = $"B{index}",
            TargetTorqueNm = _config.BoltFastening.DefaultTorqueNm,
        };
        CurrentRecipe.BoltFastening.BoltPoints.Add(bolt);
        BuildTeachingPoints();
        StatusMessage = $"Bolt added: {bolt.Name}";
    }

    private bool CanAddBoltPoint() => SelectedZone is 2 or 3;

    [RelayCommand(CanExecute = nameof(CanRemoveBoltPoint))]
    private void RemoveBoltPoint()
    {
        var name = SelectedPoint!.Name;
        CurrentRecipe.BoltFastening.BoltPoints.RemoveAll(bolt => bolt.Name == name);
        BuildTeachingPoints();
        StatusMessage = $"Bolt removed: {name}";
    }

    private bool CanRemoveBoltPoint() =>
        SelectedPoint?.Kind is TeachingPointKind.Zone2BoltZ
            or TeachingPointKind.Zone3BoltReference;

    [RelayCommand(CanExecute = nameof(CanSaveRecipe))]
    private async Task SaveRecipeAsync()
    {
        try
        {
            CurrentRecipe.Name = RecipeName.Trim();
            RecipeName = CurrentRecipe.Name;
            await _recipes.SaveRecipeAsync(CurrentRecipe);
            RefreshRecipeFiles();
            StatusMessage = $"Recipe saved: {RecipeName}";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            StatusMessage = $"Recipe save failed: {exception.Message}";
        }
    }

    private bool CanSaveRecipe() => !string.IsNullOrWhiteSpace(RecipeName);

    [RelayCommand]
    private async Task LoadRecipeAsync(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }

        try
        {
            CurrentRecipe = await _recipes.LoadRecipeAsync(fileName);
            RecipeName = CurrentRecipe.Name;
            BuildTeachingPoints();
            StatusMessage = $"Recipe loaded: {RecipeName}";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException)
        {
            StatusMessage = $"Recipe load failed: {exception.Message}";
        }
    }

    [RelayCommand]
    private void NewRecipe()
    {
        CurrentRecipe = new Recipe { Name = "New" };
        RecipeName = CurrentRecipe.Name;
        BuildTeachingPoints();
        StatusMessage = "New recipe created";
    }

    public void Activate() => RefreshPosition();

    public void Deactivate()
    {
        foreach (var motion in _motions)
        {
            motion.Stop();
        }

        if (IsCameraLive)
        {
            CurrentCamera?.StopLiveView();
            IsCameraLive = false;
            LiveImage = null;
        }

        if (LaserOn)
        {
            _io.SetOutput(LaserChannel, false);
            LaserOn = false;
        }
    }

    public void Dispose()
    {
        Deactivate();
        _motions[0].PositionChanged -= OnZone1PositionChanged;
        _motions[1].PositionChanged -= OnZone2PositionChanged;
        _motions[2].PositionChanged -= OnZone3PositionChanged;
        _zone2Camera.FrameReady -= OnZone2FrameReady;
        _zone3Camera.FrameReady -= OnZone3FrameReady;
    }

    private IMotionService GetMotion(int zone) => _motions[zone - 1];

    private ICameraStreamService? GetCamera(int zone) => zone switch
    {
        2 => _zone2Camera,
        3 => _zone3Camera,
        _ => null,
    };

    private ZoneMotionParams GetMotionSettings(int zone) => zone switch
    {
        1 => _config.PcbPlacementMotion,
        2 => _config.BoltFastening.Motion,
        3 => _config.Inspection.Motion,
        _ => throw new ArgumentOutOfRangeException(nameof(zone)),
    };

    private void BuildTeachingPoints()
    {
        _allPoints.Clear();
        _allPoints.AddRange(_pointMapper.Build(CurrentRecipe));
        RefreshFilteredPoints();
    }

    private void RefreshFilteredPoints()
    {
        FilteredPoints.Clear();
        foreach (var point in _allPoints.Where(point => point.Zone == SelectedZone))
        {
            FilteredPoints.Add(point);
        }

        SelectedPoint = FilteredPoints.FirstOrDefault();
    }

    private void RefreshRecipeFiles()
    {
        RecipeFiles.Clear();
        foreach (var fileName in _recipes.GetRecipeFiles())
        {
            RecipeFiles.Add(fileName);
        }
    }

    private void RefreshPosition()
    {
        var current = CurrentMotion.GetPosition();
        CurrentX = current.X;
        CurrentY = current.Y;
        CurrentZ = current.Z;
    }

    private void OnZone1PositionChanged(double x, double y, double z) =>
        ApplyPosition(1, x, y, z);

    private void OnZone2PositionChanged(double x, double y, double z) =>
        ApplyPosition(2, x, y, z);

    private void OnZone3PositionChanged(double x, double y, double z) =>
        ApplyPosition(3, x, y, z);

    private void ApplyPosition(int zone, double x, double y, double z) =>
        RunOnUi(() =>
        {
            if (SelectedZone == zone)
            {
                CurrentX = x;
                CurrentY = y;
                CurrentZ = z;
            }
        });

    private void OnZone2FrameReady(ImageFrame frame) => UpdateLiveImage(2, frame);

    private void OnZone3FrameReady(ImageFrame frame) => UpdateLiveImage(3, frame);

    private void UpdateLiveImage(int zone, ImageFrame frame) =>
        RunOnUi(() =>
        {
            if (SelectedZone == zone)
            {
                LiveImage = frame.ToImageSource();
            }
        });

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
