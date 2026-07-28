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
using IBTM.Core;
using IBTM.Device;
using IBTM.Sequence;
using IBTM.Stations.BoltFastening;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.UI;

public partial class TeachingViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<EquipmentUnit, MotionService> _motions;
    private readonly IIoService _io;
    private readonly ICameraStreamService _alignmentCamera;
    private readonly ICameraStreamService _inspectionCamera;
    private readonly ILightController _light;
    private readonly LightingSettings _lighting;
    private readonly MachineStore _store;
    private readonly MachineSettings _settings;
    private readonly TeachingPointMapper _pointMapper;
    private readonly AutoSequence _sequence;

    [ObservableProperty] private Recipe _currentRecipe = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRecipeCommand))]
    private string _recipeName = "Default";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleLiveViewCommand))]
    [NotifyCanExecuteChangedFor(nameof(CameraClickCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBoltPointCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogXPlusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogXMinusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogYPlusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogYMinusCommand))]
    private EquipmentUnit _selectedUnit = EquipmentUnit.Inspection;

    [ObservableProperty] private double _currentX;
    [ObservableProperty] private double _currentY;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JogXPlusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogXMinusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogYPlusCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogYMinusCommand))]
    private double _currentZ;
    [ObservableProperty] private double _jogSpeed = 10.0;
    [ObservableProperty] private BoltType _newBoltType;
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
        [FromKeyedServices(EquipmentUnit.PcbPlacement)] MotionService pcbPlacementMotion,
        [FromKeyedServices(EquipmentUnit.BoltFastening)] MotionService boltFasteningMotion,
        [FromKeyedServices(EquipmentUnit.Inspection)] MotionService inspectionMotion,
        IIoService io,
        [FromKeyedServices(EquipmentUnit.PcbPlacement)] ICameraStreamService alignmentCamera,
        [FromKeyedServices(EquipmentUnit.Inspection)] ICameraStreamService inspectionCamera,
        ILightController light,
        LightingSettings lighting,
        MachineStore store,
        MachineSettings settings,
        TeachingPointMapper pointMapper,
        AutoSequence sequence)
    {
        _motions = new Dictionary<EquipmentUnit, MotionService>
        {
            [EquipmentUnit.PcbPlacement] = pcbPlacementMotion,
            [EquipmentUnit.BoltFastening] = boltFasteningMotion,
            [EquipmentUnit.Inspection] = inspectionMotion,
        };
        _io = io;
        _alignmentCamera = alignmentCamera;
        _inspectionCamera = inspectionCamera;
        _light = light;
        _lighting = lighting;
        _store = store;
        _settings = settings;
        _pointMapper = pointMapper;
        _sequence = sequence;
        CurrentRecipe = sequence.CurrentRecipe;
        RecipeName = CurrentRecipe.Name;

        pcbPlacementMotion.PositionChanged +=
            (x, y, z) => ApplyPosition(EquipmentUnit.PcbPlacement, x, y, z);
        boltFasteningMotion.PositionChanged +=
            (x, y, z) => ApplyPosition(EquipmentUnit.BoltFastening, x, y, z);
        inspectionMotion.PositionChanged +=
            (x, y, z) => ApplyPosition(EquipmentUnit.Inspection, x, y, z);
        alignmentCamera.FrameReady +=
            frame => UpdateLiveImage(EquipmentUnit.PcbPlacement, frame);
        inspectionCamera.FrameReady +=
            frame => UpdateLiveImage(EquipmentUnit.Inspection, frame);

        RefreshTeachingPoints();
        RefreshRecipeFiles();
    }

    public ObservableCollection<TeachingPoint> FilteredPoints { get; } = [];
    public ObservableCollection<string> RecipeFiles { get; } = [];
    public EquipmentUnit[] Units { get; } =
    [
        EquipmentUnit.PcbPlacement,
        EquipmentUnit.BoltFastening,
        EquipmentUnit.Inspection,
    ];
    public double[] JogSpeeds { get; } = [1.0, 10.0, 50.0];
    public BoltType[] BoltTypes { get; } = Enum.GetValues<BoltType>();
    private MotionService CurrentMotion => GetMotion(SelectedUnit);
    private ICameraStreamService? CurrentCamera => GetCamera(SelectedUnit);
    private OutputIo Laser => GetLaser(SelectedUnit);

    partial void OnCurrentRecipeChanged(Recipe value) =>
        _sequence.CurrentRecipe = value;

    partial void OnSelectedUnitChanged(
        EquipmentUnit oldValue,
        EquipmentUnit newValue)
    {
        if (LaserOn)
        {
            _io.SetOutput(GetLaser(oldValue), false);
            LaserOn = false;
        }

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

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private void JogXPlus() => CurrentMotion.JogX(JogSpeed);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private void JogXMinus() => CurrentMotion.JogX(-JogSpeed);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private void JogYPlus() => CurrentMotion.JogY(JogSpeed);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private void JogYMinus() => CurrentMotion.JogY(-JogSpeed);

    [RelayCommand] private void JogZPlus() => CurrentMotion.JogZ(JogSpeed);
    [RelayCommand] private void JogZMinus() => CurrentMotion.JogZ(-JogSpeed);
    [RelayCommand] private void JogStop() => CurrentMotion.Stop();

    private bool CanJogXY() =>
        CurrentMotion.IsAtSafeZ;

    [RelayCommand]
    private Task MoveToSafeZAsync() =>
        CurrentMotion.MoveToSafeZAsync();

    [RelayCommand(CanExecute = nameof(CanToggleLiveView))]
    private void ToggleLiveView()
    {
        if (IsCameraLive)
        {
            StopCamera(SelectedUnit);
            LiveImage = null;
        }
        else
        {
            StartCamera(SelectedUnit);
        }

        IsCameraLive = !IsCameraLive;
    }

    private bool CanToggleLiveView() => CurrentCamera is not null;

    [RelayCommand(CanExecute = nameof(CanUseCamera))]
    private async Task CameraClickAsync(Point clickPosition)
    {
        var camera = CurrentCamera!;
        var pixelOffsetX = clickPosition.X - (camera.ImageWidth / 2.0);
        var pixelOffsetY = clickPosition.Y - (camera.ImageHeight / 2.0);
        var targetX = SelectedUnit == EquipmentUnit.PcbPlacement
            ? CurrentX
              + (pixelOffsetX * _settings.PcbPlacement.AlignmentXMillimetersPerPixel)
            : CurrentX + (pixelOffsetX / _settings.Inspection.PixelsPerMm);
        var targetY = SelectedUnit == EquipmentUnit.PcbPlacement
            ? CurrentY
              + (pixelOffsetY * _settings.PcbPlacement.AlignmentYMillimetersPerPixel)
            : CurrentY + (pixelOffsetY / _settings.Inspection.PixelsPerMm);

        await CurrentMotion.MoveToAsync(
            targetX,
            targetY,
            CurrentZ);
        StatusMessage = $"Move → X:{targetX:F3} Y:{targetY:F3}";
    }

    private bool CanUseCamera() => IsCameraLive && CurrentCamera is not null;

    [RelayCommand]
    private void ToggleLaser()
    {
        LaserOn = !LaserOn;
        _io.SetOutput(Laser, LaserOn);
    }

    [RelayCommand(CanExecute = nameof(CanTeachCurrentPosition))]
    private void TeachCurrentPosition()
    {
        var point = SelectedPoint!;
        var current = CurrentMotion.GetPosition();
        point.Teach(current.X, current.Y, current.Z);
        _pointMapper.Apply(CurrentRecipe, FilteredPoints, point);

        MoveToPointCommand.NotifyCanExecuteChanged();
        StatusMessage = $"Taught: {point.Name}";
    }

    private bool CanTeachCurrentPosition() => SelectedPoint is not null;

    [RelayCommand(CanExecute = nameof(CanMoveToPoint))]
    private async Task MoveToPointAsync()
    {
        var point = SelectedPoint!;
        var motion = GetMotion(point.Unit);
        switch (point.TeachMode)
        {
            case TeachMode.XOnly:
                await motion.MoveToXAsync(
                    point.X,
                    GetMotionSettings(point.Unit).HorizontalSpeed);
                break;
            case TeachMode.XZOnly:
                await motion.MoveToXZAsync(point.X, point.Z);
                break;
            case TeachMode.XYOnly:
                await motion.MoveToXYAsync(
                    point.X,
                    point.Y,
                    GetMotionSettings(point.Unit).HorizontalSpeed);
                break;
            default:
                await motion.MoveToAsync(point.X, point.Y, point.Z);
                break;
        }

        StatusMessage = $"Moved to: {point.Name}";
    }

    private bool CanMoveToPoint() => SelectedPoint?.IsTaught == true;

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
            BoltType = NewBoltType,
            TargetTorqueNm = _settings.BoltFastening.GetHead(NewBoltType).DefaultTorqueNm,
        };
        CurrentRecipe.BoltFastening.BoltPoints.Add(bolt);
        RefreshTeachingPoints();
        StatusMessage = $"Bolt added: {bolt.Name} · {bolt.BoltType}";
    }

    private bool CanAddBoltPoint() =>
        SelectedUnit is EquipmentUnit.BoltFastening or EquipmentUnit.Inspection;

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
        SelectedPoint?.Target is TeachingTarget.BoltZ
            or TeachingTarget.BoltReference;

    [RelayCommand(CanExecute = nameof(CanSaveRecipe))]
    private async Task SaveRecipeAsync()
    {
        try
        {
            CurrentRecipe.Name = RecipeName.Trim();
            RecipeName = CurrentRecipe.Name;
            await _store.SaveRecipeAsync(CurrentRecipe);
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
            CurrentRecipe = await _store.LoadRecipeAsync(fileName);
            RecipeName = CurrentRecipe.Name;
            RefreshTeachingPoints();
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
        RefreshTeachingPoints();
        StatusMessage = "New recipe created";
    }

    public void Activate()
    {
        CurrentRecipe = _sequence.CurrentRecipe;
        RecipeName = CurrentRecipe.Name;
        RefreshTeachingPoints();
        RefreshRecipeFiles();
        RefreshPosition();
    }

    public void Deactivate()
    {
        foreach (var motion in _motions.Values)
        {
            motion.Stop();
        }

        if (IsCameraLive)
        {
            StopCamera(SelectedUnit);
            IsCameraLive = false;
            LiveImage = null;
        }

        if (LaserOn)
        {
            _io.SetOutput(Laser, false);
            LaserOn = false;
        }
    }

    private MotionService GetMotion(EquipmentUnit unit) => _motions[unit];

    private StationMotionSettings GetMotionSettings(EquipmentUnit unit) =>
        unit switch
        {
            EquipmentUnit.PcbPlacement => _settings.PcbPlacement.Motion,
            EquipmentUnit.BoltFastening => _settings.BoltFastening.Motion,
            EquipmentUnit.Inspection => _settings.Inspection.Motion,
            _ => throw new ArgumentOutOfRangeException(nameof(unit)),
        };

    private ICameraStreamService? GetCamera(EquipmentUnit unit) => unit switch
    {
        EquipmentUnit.PcbPlacement => _alignmentCamera,
        EquipmentUnit.Inspection => _inspectionCamera,
        _ => null,
    };

    private static OutputIo GetLaser(EquipmentUnit unit) => unit switch
    {
        EquipmentUnit.PcbPlacement => OutputIo.PcbPlacementLaser,
        EquipmentUnit.BoltFastening => OutputIo.BoltFasteningLaser,
        EquipmentUnit.Inspection => OutputIo.InspectionLaser,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    private void RefreshTeachingPoints()
    {
        FilteredPoints.Clear();
        foreach (var point in _pointMapper.BuildStations(CurrentRecipe)
                     .Where(point => point.Unit == SelectedUnit))
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

    private void RefreshPosition()
    {
        var current = CurrentMotion.GetPosition();
        CurrentX = current.X;
        CurrentY = current.Y;
        CurrentZ = current.Z;
    }

    private void ApplyPosition(
        EquipmentUnit unit,
        double x,
        double y,
        double z) =>
        RunOnUi(() =>
        {
            if (SelectedUnit == unit)
            {
                CurrentX = x;
                CurrentY = y;
                CurrentZ = z;
            }
        });

    private void StartCamera(EquipmentUnit unit)
    {
        var channel = GetLightChannel(unit);
        _light.SetLevel(channel, GetLightLevel(unit));
        _light.TurnOn(channel);
        GetCamera(unit)!.StartLiveView();
    }

    private void StopCamera(EquipmentUnit unit)
    {
        GetCamera(unit)?.StopLiveView();

        if (unit is EquipmentUnit.PcbPlacement or EquipmentUnit.Inspection)
        {
            _light.TurnOff(GetLightChannel(unit));
        }
    }

    private int GetLightChannel(EquipmentUnit unit) => unit switch
    {
        EquipmentUnit.PcbPlacement => _lighting.AlignmentChannel,
        EquipmentUnit.Inspection => _lighting.InspectionChannel,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    private int GetLightLevel(EquipmentUnit unit) => unit switch
    {
        EquipmentUnit.PcbPlacement => _lighting.AlignmentLevel,
        EquipmentUnit.Inspection => _lighting.InspectionLevel,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    private void UpdateLiveImage(EquipmentUnit unit, ImageFrame frame) =>
        RunOnUi(() =>
        {
            if (SelectedUnit == unit)
            {
                LiveImage = frame.ToImageSource();
            }
        });

    private static void RunOnUi(Action action) =>
        Application.Current.Dispatcher.BeginInvoke(action);
}
