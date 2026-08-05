using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;
using IBTM.Stations.PcbPlacement;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.UI;

public partial class SupplyTeachingViewModel : ObservableObject
{
    private readonly MotionService _supplyMotion;
    private readonly MotionService _placementMotion;
    private readonly IIoService _io;
    private readonly PcbSupplyHandler _supplyHandler;
    private readonly PcbPlacementStation _placementStation;
    private readonly MachineSettings _settings;
    private readonly MachineStore _store;
    private readonly TeachingPointMapper _pointMapper;
    private readonly HashSet<TeachingTarget> _taughtBufferTargets = [];

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
        [FromKeyedServices(MotionGroup.PcbSupply)] MotionService supplyMotion,
        [FromKeyedServices(MotionGroup.PcbPlacement)] MotionService placementMotion,
        IIoService io,
        PcbSupplyHandler supplyHandler,
        PcbPlacementStation placementStation,
        MachineSettings settings,
        MachineStore store,
        TeachingPointMapper pointMapper,
        Recipe recipe)
    {
        _supplyMotion = supplyMotion;
        _placementMotion = placementMotion;
        _io = io;
        _supplyHandler = supplyHandler;
        _placementStation = placementStation;
        _settings = settings;
        _store = store;
        _pointMapper = pointMapper;
        CurrentRecipe = recipe;
        _recipeName = recipe.Name;

        supplyMotion.PositionChanged +=
            (x, y, z) => ApplyPosition(MotionGroup.PcbSupply, x, y, z);
        placementMotion.PositionChanged +=
            (x, y, z) => ApplyPosition(MotionGroup.PcbPlacement, x, y, z);
        io.InputChanged += OnInputChanged;

        BuildPoints();
        RefreshRecipeFiles();
    }

    public ObservableCollection<TeachingPoint> Points { get; } = [];
    public ObservableCollection<string> RecipeFiles { get; } = [];
    public Recipe CurrentRecipe { get; }
    public double[] JogSpeeds { get; } = [1.0, 10.0, 50.0];

    [RelayCommand(CanExecute = nameof(CanTeachCurrentPosition))]
    private async Task TeachCurrentPositionAsync()
    {
        var point = SelectedPoint!;
        var current = CurrentMotion.GetPosition();
        point.Teach(current.X, current.Y, current.Z);

        if (IsBuffer(point))
        {
            _taughtBufferTargets.Add(point.Target);
            SaveBufferPairCommand.NotifyCanExecuteChanged();
            StatusMessage = $"Taught: {point.Name} · save the buffer pair";
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

    [RelayCommand(CanExecute = nameof(CanSaveBufferPair))]
    private async Task SaveBufferPairAsync()
    {
        foreach (var point in Points.Where(IsBuffer))
        {
            _pointMapper.Apply(CurrentRecipe, Points, point);
        }

        await _store.SaveSettingsAsync(_settings);
        _taughtBufferTargets.Clear();
        SaveBufferPairCommand.NotifyCanExecuteChanged();
        StatusMessage = "Buffer pair saved";
    }

    private bool CanSaveBufferPair() =>
        _taughtBufferTargets.Contains(TeachingTarget.SupplyBuffer)
        && _taughtBufferTargets.Contains(
            TeachingTarget.PlacementBuffer);

    public void Activate()
    {
        RecipeName = CurrentRecipe.Name;
        BuildPoints();
        RefreshRecipeFiles();
        RefreshPosition();
        RefreshActuators();
    }

    public void Deactivate()
    {
        MoveToSafeZCommand.Cancel();
        MoveToPointCommand.Cancel();
        ToggleActuatorCommand.Cancel();
        _supplyMotion.Stop();
        _placementMotion.Stop();
    }

    private void BuildPoints()
    {
        _taughtBufferTargets.Clear();
        Points.Clear();
        foreach (var point in _pointMapper.BuildSupply(CurrentRecipe))
        {
            Points.Add(point);
        }

        SelectedPoint = Points.FirstOrDefault();
        SaveBufferPairCommand.NotifyCanExecuteChanged();
    }

    private void RefreshRecipeFiles()
    {
        RecipeFiles.Clear();
        foreach (var fileName in _store.GetRecipeFiles())
        {
            RecipeFiles.Add(fileName);
        }
    }

    private static bool IsBuffer(TeachingPoint point) =>
        point.Target is TeachingTarget.SupplyBuffer
            or TeachingTarget.PlacementBuffer;
}
