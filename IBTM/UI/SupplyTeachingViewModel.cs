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
using IBTM.Storage;

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
    private readonly UnitSettings _units;
    [ObservableProperty]
    private IReadOnlyList<TeachingPoint> _points = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IoGroups))]
    [NotifyPropertyChangedFor(nameof(TeachingOutputs))]
    [NotifyCanExecuteChangedFor(nameof(TeachCurrentPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToPointCommand))]
    private TeachingPoint? _selectedPoint;

    public SupplyTeachingViewModel(
        PcbSupplyHandler supplyHandler,
        PcbPlacementHandler placementHandler,
        BufferStage buffer,
        MachineState state,
        MachineController machine,
        PcbBufferSettings bufferSettings,
        PcbSupplySettings supplySettings,
        PcbPlacementHandlerSettings placementSettings,
        RecipeEditor recipeEditor,
        UnitSettings units,
        OperationCancellation operations,
        MachineStore store,
        IReadOnlyDictionary<MotionGroup, IoStatus[]> ioGroups,
        IReadOnlyDictionary<MotionGroup, IReadOnlyDictionary<OutputIo, TeachingOutput>> teachingOutputs)
        : base(operations, state, machine, store, ioGroups, teachingOutputs)
    {
        _supplyHandler = supplyHandler;
        _placementHandler = placementHandler;
        _buffer = buffer;
        _state = state;
        _bufferSettings = bufferSettings;
        _supplySettings = supplySettings;
        _placementSettings = placementSettings;
        _units = units;
        RecipeEditor = recipeEditor;

        supplyHandler.Feedback.MovingChanged += QueueManualCommandRefresh;
        placementHandler.Feedback.MovingChanged += QueueManualCommandRefresh;
        supplyHandler.Changed += QueueManualCommandRefresh;
        placementHandler.Changed += QueueManualCommandRefresh;
        buffer.StateChanged += QueueManualCommandRefresh;
        state.Changed += QueueManualCommandRefresh;
        recipeEditor.Changed += BuildPoints;

        BuildPoints();
    }

    public RecipeEditor RecipeEditor { get; }
    protected override IReadOnlyList<TeachingPoint> CurrentPoints => Points;
    protected override TeachingPoint? CurrentPoint
    {
        get => SelectedPoint;
        set => SelectedPoint = value;
    }
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

        point.Apply();
        foreach (var currentPoint in Points.Where(candidate => !IsBuffer(candidate)))
            currentPoint.Refresh();
        if (point.Storage == TeachingStorage.Machine)
        {
            using var operation = LinkMotion(CancellationToken.None);
            await SaveSettingsAsync(point.Position.Setting!);
        }

        OnPropertyChanged(nameof(HorizontalZ));
        NotifyManualTeachingCommands();
    }

    private bool CanTeachCurrentPosition() =>
        SelectedPoint is not null && CanUseCurrentHandler();

    [RelayCommand(CanExecute = nameof(CanSaveBufferSetup))]
    private async Task SaveBufferSetupAsync()
    {
        var points = Points.Where(IsBuffer).ToArray();
        foreach (var point in points)
        {
            point.Apply();
        }

        using var operation = LinkMotion(CancellationToken.None);
        await SaveSettingsAsync(points.Select(point => point.Position.Setting!).Distinct().ToArray());
        NotifyManualTeachingCommands();
    }

    private bool CanSaveBufferSetup() => _state.ManualControlsEnabled;

    public void Activate()
    {
        RecipeEditor.Refresh();
        OnPropertyChanged(nameof(SupplyEnabled));
        OnPropertyChanged(nameof(PlacementEnabled));
        BuildPoints();
        ActivatePositionUpdates();
        NotifyManualTeachingCommands();
    }

    public void Deactivate()
    {
        DeactivatePositionUpdates();
        StepCommand.Cancel();
        JogCommand.Cancel();
        MoveToHorizontalZCommand.Cancel();
        MoveToPointCommand.Cancel();
        SetOutputOnCommand.Cancel();
        SetOutputOffCommand.Cancel();
        CancelMotion();
    }

    public Task ShutdownAsync() =>
        CommandShutdown.StopAsync(
            Deactivate,
            StepCommand,
            JogCommand,
            MoveToHorizontalZCommand,
            MoveToPointCommand,
            SetOutputOnCommand,
            SetOutputOffCommand,
            TeachCurrentPositionCommand,
            SaveBufferSetupCommand);

    private void BuildPoints()
    {
        TeachingPosition[] positions =
        [
            .. _supplySettings.GetTeachingPositions(CurrentRecipe.PcbSupply),
            .. _placementSettings.GetTeachingPositions(),
            _placementSettings.GetBufferTeachingPosition(),
            .. _bufferSettings.GetTeachingPositions(),
        ];
        Points = positions
            .Where(point => point.MotionGroup switch
            {
                MotionGroup.PcbSupply => SupplyEnabled,
                MotionGroup.PcbPlacementHandler => PlacementEnabled,
                _ => false,
            })
            .OrderBy(position => position.MotionGroup)
            .Select(position => new TeachingPoint(position))
            .ToArray();
        SelectedPoint = Points.FirstOrDefault();
    }

    private static bool IsBuffer(TeachingPoint point) =>
        point.Storage == TeachingStorage.Buffer;

}
