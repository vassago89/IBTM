using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Sequence;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.UI;

public partial class SupplyTeachingViewModel : ObservableObject
{
    private readonly MotionService _supplyMotion;
    private readonly MotionService _placementMotion;
    private readonly IIoService _io;
    private readonly MachineSettings _settings;
    private readonly MachineStore _store;
    private readonly TeachingPointMapper _pointMapper;
    private readonly AutoSequence _sequence;
    private readonly HashSet<TeachingTarget> _taughtHandoffTargets = [];

    [ObservableProperty] private Recipe _currentRecipe;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRecipeCommand))]
    private string _recipeName;

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
    [NotifyCanExecuteChangedFor(nameof(TeachCurrentPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToPointCommand))]
    private TeachingPoint? _selectedPoint;

    [ObservableProperty] private string _statusMessage = string.Empty;

    public SupplyTeachingViewModel(
        [FromKeyedServices(EquipmentUnit.PcbSupply)] MotionService supplyMotion,
        [FromKeyedServices(EquipmentUnit.PcbPlacement)] MotionService placementMotion,
        IIoService io,
        MachineSettings settings,
        MachineStore store,
        TeachingPointMapper pointMapper,
        AutoSequence sequence)
    {
        _supplyMotion = supplyMotion;
        _placementMotion = placementMotion;
        _io = io;
        _settings = settings;
        _store = store;
        _pointMapper = pointMapper;
        _sequence = sequence;
        _currentRecipe = sequence.CurrentRecipe;
        _recipeName = _currentRecipe.Name;

        supplyMotion.PositionChanged +=
            (x, y, z) => ApplyPosition(EquipmentUnit.PcbSupply, x, y, z);
        placementMotion.PositionChanged +=
            (x, y, z) => ApplyPosition(EquipmentUnit.PcbPlacement, x, y, z);

        BuildPoints();
        RefreshRecipeFiles();
    }

    public ObservableCollection<TeachingPoint> Points { get; } = [];
    public ObservableCollection<string> RecipeFiles { get; } = [];
    public double[] JogSpeeds { get; } = [1.0, 10.0, 50.0];
    public EquipmentUnit ActiveUnit =>
        SelectedPoint?.Unit ?? EquipmentUnit.PcbSupply;
    public bool HasY => CurrentMotion.HasY;
    public double SafeZ => CurrentSettings.SafeZ;
    public string SupplyRotationLabel =>
        _io.GetOutput(OutputIo.PcbSupplyRotateToHandoff)
            ? "Supply Rotation · HANDOFF"
            : "Supply Rotation · HOME";
    public string SupplyGripperLabel =>
        _io.GetOutput(OutputIo.PcbSupplyGripper)
            ? "Supply Gripper · CLOSED"
            : "Supply Gripper · OPEN";
    public string PlacementGripperLabel =>
        _io.GetOutput(OutputIo.PcbPlacementGripper)
            ? "Placement Gripper · CLOSED"
            : "Placement Gripper · OPEN";
    public string SupplyPcbLabel =>
        _io.GetInput(InputIo.PcbSupplyPcbPresent)
            ? "Supply PCB · DETECTED"
            : "Supply PCB · EMPTY";
    public string PlacementPcbLabel =>
        _io.GetInput(InputIo.PcbPlacementPcbPresent)
            ? "Placement PCB · DETECTED"
            : "Placement PCB · EMPTY";

    private MotionService CurrentMotion => GetMotion(ActiveUnit);
    private StationMotionSettings CurrentSettings => GetSettings(ActiveUnit);

    partial void OnCurrentRecipeChanged(Recipe value) =>
        _sequence.CurrentRecipe = value;

    partial void OnSelectedPointChanged(
        TeachingPoint? oldValue,
        TeachingPoint? newValue)
    {
        if (oldValue is not null)
        {
            GetMotion(oldValue.Unit).Stop();
        }

        OnPropertyChanged(nameof(ActiveUnit));
        OnPropertyChanged(nameof(HasY));
        OnPropertyChanged(nameof(SafeZ));
        JogXPlusCommand.NotifyCanExecuteChanged();
        JogXMinusCommand.NotifyCanExecuteChanged();
        JogYPlusCommand.NotifyCanExecuteChanged();
        JogYMinusCommand.NotifyCanExecuteChanged();
        RefreshPosition();
    }

    [RelayCommand(CanExecute = nameof(CanJogX))]
    private void JogXPlus() => CurrentMotion.JogX(JogSpeed);

    [RelayCommand(CanExecute = nameof(CanJogX))]
    private void JogXMinus() => CurrentMotion.JogX(-JogSpeed);

    [RelayCommand(CanExecute = nameof(CanJogY))]
    private void JogYPlus() => CurrentMotion.JogY(JogSpeed);

    [RelayCommand(CanExecute = nameof(CanJogY))]
    private void JogYMinus() => CurrentMotion.JogY(-JogSpeed);

    [RelayCommand]
    private void JogZPlus() => CurrentMotion.JogZ(JogSpeed);

    [RelayCommand]
    private void JogZMinus() => CurrentMotion.JogZ(-JogSpeed);

    [RelayCommand]
    private void JogStop() => CurrentMotion.Stop();

    private bool CanJogX() => CurrentMotion.IsAtSafeZ;
    private bool CanJogY() => HasY && CurrentMotion.IsAtSafeZ;

    [RelayCommand]
    private Task MoveToSafeZAsync() =>
        CurrentMotion.MoveToSafeZAsync();

    [RelayCommand(CanExecute = nameof(CanTeachCurrentPosition))]
    private async Task TeachCurrentPositionAsync()
    {
        var point = SelectedPoint!;
        var current = CurrentMotion.GetPosition();
        point.Teach(current.X, current.Y, current.Z);

        if (IsHandoff(point))
        {
            _taughtHandoffTargets.Add(point.Target);
            SaveHandoffPairCommand.NotifyCanExecuteChanged();
            StatusMessage = $"Taught: {point.Name} · save the handoff pair";
            return;
        }

        _pointMapper.Apply(CurrentRecipe, Points, point);
        if (point.Storage == TeachingStorage.Machine)
        {
            await _store.SaveSettingsAsync(_settings);
        }

        MoveToPointCommand.NotifyCanExecuteChanged();
        StatusMessage = $"Taught: {point.Name}";
    }

    private bool CanTeachCurrentPosition() => SelectedPoint is not null;

    [RelayCommand(CanExecute = nameof(CanMoveToPoint))]
    private async Task MoveToPointAsync()
    {
        var point = SelectedPoint!;
        var motion = GetMotion(point.Unit);
        var settings = GetSettings(point.Unit);

        switch (point.TeachMode)
        {
            case TeachMode.XOnly:
                await motion.MoveToXAsync(point.X, settings.HorizontalSpeed);
                break;
            case TeachMode.XZOnly:
                await motion.MoveToXZAsync(point.X, point.Z);
                break;
            case TeachMode.Full:
                await motion.MoveToAsync(point.X, point.Y, point.Z);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported handler teach mode: {point.TeachMode}.");
        }

        StatusMessage = $"Moved to: {point.Name}";
    }

    private bool CanMoveToPoint() => SelectedPoint?.IsTaught == true;

    [RelayCommand(CanExecute = nameof(CanSaveHandoffPair))]
    private async Task SaveHandoffPairAsync()
    {
        foreach (var point in Points.Where(IsHandoff))
        {
            _pointMapper.Apply(CurrentRecipe, Points, point);
        }

        await _store.SaveSettingsAsync(_settings);
        _taughtHandoffTargets.Clear();
        SaveHandoffPairCommand.NotifyCanExecuteChanged();
        StatusMessage = "Handoff pair saved";
    }

    private bool CanSaveHandoffPair() =>
        _taughtHandoffTargets.Contains(TeachingTarget.PcbSupplyHandoff)
        && _taughtHandoffTargets.Contains(
            TeachingTarget.PcbPlacementHandoff);

    [RelayCommand]
    private async Task ToggleActuatorAsync(OutputIo output)
    {
        var value = !_io.GetOutput(output);
        try
        {
            await _io.SetOutputAndWaitAsync(output, value);
            StatusMessage = $"{output} {(value ? "on" : "off")}";
        }
        catch (IoFeedbackTimeoutException exception)
        {
            StatusMessage = $"Alarm: {exception.Message}";
        }

        RefreshActuators();
    }

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
            BuildPoints();
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
        BuildPoints();
        StatusMessage = "New recipe created";
    }

    public void Activate()
    {
        CurrentRecipe = _sequence.CurrentRecipe;
        RecipeName = CurrentRecipe.Name;
        BuildPoints();
        RefreshRecipeFiles();
        RefreshPosition();
        RefreshActuators();
    }

    public void Deactivate()
    {
        _supplyMotion.Stop();
        _placementMotion.Stop();
    }

    private void BuildPoints()
    {
        _taughtHandoffTargets.Clear();
        Points.Clear();
        foreach (var point in _pointMapper.BuildSupply(CurrentRecipe))
        {
            Points.Add(point);
        }

        SelectedPoint = Points.FirstOrDefault();
        SaveHandoffPairCommand.NotifyCanExecuteChanged();
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
        var position = CurrentMotion.GetPosition();
        CurrentX = position.X;
        CurrentY = position.Y;
        CurrentZ = position.Z;
    }

    private void RefreshActuators()
    {
        OnPropertyChanged(nameof(SupplyRotationLabel));
        OnPropertyChanged(nameof(SupplyGripperLabel));
        OnPropertyChanged(nameof(PlacementGripperLabel));
        OnPropertyChanged(nameof(SupplyPcbLabel));
        OnPropertyChanged(nameof(PlacementPcbLabel));
    }

    private MotionService GetMotion(EquipmentUnit unit) => unit switch
    {
        EquipmentUnit.PcbSupply => _supplyMotion,
        EquipmentUnit.PcbPlacement => _placementMotion,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    private StationMotionSettings GetSettings(EquipmentUnit unit) => unit switch
    {
        EquipmentUnit.PcbSupply => _settings.PcbSupply.Motion,
        EquipmentUnit.PcbPlacement => _settings.PcbPlacement.Motion,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    private void ApplyPosition(
        EquipmentUnit unit,
        double x,
        double y,
        double z)
    {
        if (unit != ActiveUnit)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            CurrentX = x;
            CurrentY = y;
            CurrentZ = z;
        });
    }

    private static bool IsHandoff(TeachingPoint point) =>
        point.Target is TeachingTarget.PcbSupplyHandoff
            or TeachingTarget.PcbPlacementHandoff;
}
