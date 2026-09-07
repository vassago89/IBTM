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

namespace IBTM.UI;

public partial class SupplyTeachingViewModel : TeachingMotionViewModel
{
    private readonly PcbSupplyHandler _supplyHandler;
    private readonly PcbPlacementHandler _placementHandler;
    private readonly BufferStage _buffer;
    private readonly MachineState _state;
    private readonly PcbBufferSettings _bufferSettings;
    private readonly PcbSupplySettings _supplySettings;
    private readonly PcbPlacementHandlerSettings _placementSettings;
    private readonly SupplyTeachingPoints _teachingPoints;
    private readonly UnitSettings _units;
    [ObservableProperty]
    private IReadOnlyList<TeachingPoint> _points = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TeachCurrentPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToPointCommand))]
    private TeachingPoint? _selectedPoint;

    public SupplyTeachingViewModel(
        PcbSupplyHandler supplyHandler,
        PcbPlacementHandler placementHandler,
        BufferStage buffer,
        MachineState state,
        PcbBufferSettings bufferSettings,
        PcbSupplySettings supplySettings,
        PcbPlacementHandlerSettings placementSettings,
        RecipeEditor recipeEditor,
        SupplyTeachingPoints teachingPoints,
        UnitSettings units,
        OperationCancellation operations) : base(operations, state)
    {
        _supplyHandler = supplyHandler;
        _placementHandler = placementHandler;
        _buffer = buffer;
        _state = state;
        _bufferSettings = bufferSettings;
        _supplySettings = supplySettings;
        _placementSettings = placementSettings;
        _teachingPoints = teachingPoints;
        _units = units;
        RecipeEditor = recipeEditor;

        supplyHandler.Feedback.PositionChanged += (x, y, z) =>
            QueuePositionRefresh(MotionGroup.PcbSupply, x, y, z);
        placementHandler.Feedback.PositionChanged += (x, y, z) =>
            QueuePositionRefresh(
                MotionGroup.PcbPlacementHandler,
                x,
                y,
                z);
        supplyHandler.Feedback.MovingChanged += QueueManualCommandRefresh;
        placementHandler.Feedback.MovingChanged += QueueManualCommandRefresh;
        supplyHandler.Changed += OnHandlerChanged;
        placementHandler.Changed += OnHandlerChanged;
        buffer.StateChanged += QueueManualCommandRefresh;
        state.Changed += QueueManualCommandRefresh;
        recipeEditor.Changed += BuildPoints;

        BuildPoints();
    }

    public RecipeEditor RecipeEditor { get; }
    public bool SupplyEnabled => _units.PcbSupply;
    public bool PlacementEnabled => _units.PcbPlacement;
    public TeachingSaveBehavior SaveBehavior => SelectedPoint is { } point && IsBuffer(point)
        ? TeachingSaveBehavior.Buffer
        : SelectedPoint?.Storage == TeachingStorage.Machine
            ? TeachingSaveBehavior.Machine
            : TeachingSaveBehavior.Recipe;
    private Recipe CurrentRecipe => RecipeEditor.Recipe;

    [RelayCommand(CanExecute = nameof(CanTeachCurrentPosition))]
    private async Task TeachCurrentPositionAsync()
    {
        var point = SelectedPoint!;
        var current = CurrentPosition();
        point.Teach(current.X, current.Y, current.Z);

        if (IsBuffer(point))
        {
            return;
        }

        _teachingPoints.Apply(CurrentRecipe.PcbSupply, Points, point);
        if (point.Storage == TeachingStorage.Machine)
        {
            await SaveMachinePositionsAsync();
        }

        OnPropertyChanged(nameof(HorizontalZ));
        NotifyManualTeachingCommands();
    }

    private bool CanTeachCurrentPosition() =>
        SelectedPoint is not null && CanUseCurrentHandler();

    [RelayCommand(CanExecute = nameof(CanSaveBufferSetup))]
    private async Task SaveBufferSetupAsync()
    {
        foreach (var point in Points.Where(IsBuffer))
        {
            _teachingPoints.Apply(CurrentRecipe.PcbSupply, Points, point);
        }

        await SaveMachinePositionsAsync();
        NotifyManualTeachingCommands();
    }

    private bool CanSaveBufferSetup() => _state.ManualControlsEnabled;

    private async Task SaveMachinePositionsAsync()
    {
        using var operation = LinkMotion(CancellationToken.None);
        await Task.WhenAll(
            _supplySettings.SaveAsync(),
            _placementSettings.SaveAsync(),
            _bufferSettings.SaveAsync());
    }

    public void Activate()
    {
        RecipeEditor.Refresh();
        OnPropertyChanged(nameof(SupplyEnabled));
        OnPropertyChanged(nameof(PlacementEnabled));
        BuildPoints();
        ActivatePositionUpdates();
        RefreshActuators();
    }

    public void Deactivate()
    {
        DeactivatePositionUpdates();
        StepCommand.Cancel();
        MoveToHorizontalZCommand.Cancel();
        MoveToPointCommand.Cancel();
        ToggleActuatorCommand.Cancel();
        CancelMotion();
    }

    public Task ShutdownAsync() =>
        CommandShutdown.StopAsync(
            Deactivate,
            StepCommand,
            MoveToHorizontalZCommand,
            MoveToPointCommand,
            ToggleActuatorCommand,
            TeachCurrentPositionCommand,
            SaveBufferSetupCommand);

    private void BuildPoints()
    {
        Points = _teachingPoints.Build(CurrentRecipe.PcbSupply)
            .Where(point => point.MotionGroup switch
            {
                MotionGroup.PcbSupply => SupplyEnabled,
                MotionGroup.PcbPlacementHandler => PlacementEnabled,
                _ => false,
            })
            .ToArray();
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

}
