using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.UI;

public partial class SupplyTeachingViewModel : ObservableObject
{
    private readonly IAxisMotion _supplyMotion;
    private readonly IXyMotion _placementMotion;
    private readonly PcbSupplyHandler _supplyHandler;
    private readonly PcbPlacementHandler _placementHandler;
    private readonly BufferStage _buffer;
    private readonly MachineState _state;
    private readonly PcbBufferSettings _bufferSettings;
    private readonly PcbSupplySettings _supplySettings;
    private readonly PcbPlacementHandlerSettings _placementSettings;
    private readonly TeachingPointMapper _pointMapper;
    private CancellationTokenSource _motionCancellation = new();

    [ObservableProperty] private double _jogSpeed = 10.0;

    [ObservableProperty]
    private IReadOnlyList<TeachingPoint> _points = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TeachCurrentPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToPointCommand))]
    private TeachingPoint? _selectedPoint;

    public SupplyTeachingViewModel(
        [FromKeyedServices(MotionGroup.PcbSupply)] IAxisMotion supplyMotion,
        [FromKeyedServices(MotionGroup.PcbPlacementHandler)] IXyMotion placementMotion,
        PcbSupplyHandler supplyHandler,
        PcbPlacementHandler placementHandler,
        BufferStage buffer,
        MachineState state,
        PcbBufferSettings bufferSettings,
        PcbSupplySettings supplySettings,
        PcbPlacementHandlerSettings placementSettings,
        RecipeEditor recipeEditor,
        TeachingPointMapper pointMapper)
    {
        _supplyMotion = supplyMotion;
        _placementMotion = placementMotion;
        _supplyHandler = supplyHandler;
        _placementHandler = placementHandler;
        _buffer = buffer;
        _state = state;
        _bufferSettings = bufferSettings;
        _supplySettings = supplySettings;
        _placementSettings = placementSettings;
        _pointMapper = pointMapper;
        RecipeEditor = recipeEditor;

        supplyMotion.PositionChanged +=
            (_, _, _) => ApplyPosition(MotionGroup.PcbSupply);
        placementMotion.PositionChanged +=
            (_, _, _) => ApplyPosition(MotionGroup.PcbPlacementHandler);
        supplyMotion.MovingChanged += OnMotionChanged;
        placementMotion.MovingChanged += OnMotionChanged;
        supplyHandler.Changed += OnHandlerChanged;
        placementHandler.Changed += OnHandlerChanged;
        buffer.StateChanged += OnBufferChanged;
        recipeEditor.Changed += BuildPoints;

        BuildPoints();
    }

    public RecipeEditor RecipeEditor { get; }
    public double[] JogSpeeds { get; } = [1.0, 10.0, 50.0];
    public double CurrentX => CurrentMotion.GetPosition().X;
    public double CurrentY => CurrentMotion.GetPosition().Y;
    public double CurrentZ => CurrentMotion.GetPosition().Z;
    private Recipe CurrentRecipe => RecipeEditor.Recipe;

    [RelayCommand(CanExecute = nameof(CanTeachCurrentPosition))]
    private async Task TeachCurrentPositionAsync()
    {
        var point = SelectedPoint!;
        var current = CurrentMotion.GetPosition();
        point.Teach(current.X, current.Y, current.Z);

        if (IsBuffer(point))
        {
            return;
        }

        _pointMapper.Apply(CurrentRecipe, Points, point);
        if (point.Storage == TeachingStorage.Machine)
        {
            await SaveMachinePositionsAsync();
        }

        OnPropertyChanged(nameof(SafeZ));
        NotifyManualTeachingCommands();
    }

    private bool CanTeachCurrentPosition() => CanUseCurrentHandler();

    [RelayCommand]
    private async Task SaveBufferSetupAsync()
    {
        foreach (var point in Points.Where(IsBuffer))
        {
            _pointMapper.Apply(CurrentRecipe, Points, point);
        }

        await SaveMachinePositionsAsync();
        NotifyManualTeachingCommands();
    }

    private Task SaveMachinePositionsAsync() => Task.WhenAll(
        _supplySettings.SaveAsync(),
        _placementSettings.SaveAsync(),
        _bufferSettings.SaveAsync());

    public void Activate()
    {
        RecipeEditor.Refresh();
        BuildPoints();
        RefreshPosition();
        RefreshActuators();
    }

    public void Deactivate()
    {
        MoveToSafeZCommand.Cancel();
        MoveToPointCommand.Cancel();
        ToggleActuatorCommand.Cancel();
        CancelMotion();
    }

    private void BuildPoints()
    {
        Points = _pointMapper.BuildSupply(CurrentRecipe);
        SelectedPoint = Points.FirstOrDefault();
    }

    private static bool IsBuffer(TeachingPoint point) =>
        point.Target is TeachingTarget.SupplyBufferHandoff
            or TeachingTarget.SupplyBufferClearZ
            or TeachingTarget.PlacementBufferHandoff
            or TeachingTarget.SupplyBufferBoundary1
            or TeachingTarget.SupplyBufferBoundary2
            or TeachingTarget.PlacementBufferBoundary1
            or TeachingTarget.PlacementBufferBoundary2;

    private void OnBufferChanged() =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            NotifyManualTeachingCommands);

    private void OnMotionChanged(bool moving)
    {
        if (!moving)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(
                NotifyManualTeachingCommands);
        }
    }
}
