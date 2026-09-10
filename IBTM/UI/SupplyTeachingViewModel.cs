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
    private readonly MachineState _state;
    private readonly PcbBufferSettings _bufferSettings;
    private readonly PcbSupplySettings _supplySettings;
    private readonly PcbPlacementHandlerSettings _placementSettings;
    private readonly UnitSettings _units;
    [ObservableProperty]
    private IReadOnlyList<TeachingPoint> _points = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IoGroups))]
    [NotifyPropertyChangedFor(nameof(TeachingIoGroups))]
    [NotifyPropertyChangedFor(nameof(TeachingOutputs))]
    [NotifyCanExecuteChangedFor(nameof(TeachCurrentPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToPointCommand))]
    private TeachingPoint? _selectedPoint;

    public SupplyTeachingViewModel(
        PcbSupplyHandler supplyHandler,
        PcbPlacementHandler placementHandler,
        MachineState state,
        MachineController machine,
        PcbBufferSettings bufferSettings,
        PcbSupplySettings supplySettings,
        PcbPlacementHandlerSettings placementSettings,
        RecipeEditor recipeEditor,
        UnitSettings units,
        MachineStore store,
        IReadOnlyDictionary<MotionGroup, IoStatus[]> ioGroups,
        IReadOnlyDictionary<MotionGroup, IReadOnlyDictionary<OutputIo, TeachingOutput>> teachingOutputs) : base(
            state,
            machine,
            store,
            ioGroups,
            teachingOutputs)
    {
        _supplyHandler = supplyHandler;
        _placementHandler = placementHandler;
        _state = state;
        _bufferSettings = bufferSettings;
        _supplySettings = supplySettings;
        _placementSettings = placementSettings;
        _units = units;
        RecipeEditor = recipeEditor;

        state.DisplayChanged += QueueManualCommandRefresh;
        recipeEditor.Changed += BuildPoints;

        BuildPoints();
    }

    public RecipeEditor RecipeEditor { get; }

    protected override IReadOnlyList<TeachingPoint> CurrentPoints
    {
        get
        {
            return Points;
        }
    }

    protected override TeachingPoint? CurrentPoint
    {
        get
        {
            return SelectedPoint;
        }

        set
        {
            SelectedPoint = value;
        }
    }

    public bool SupplyEnabled
    {
        get
        {
            return _units.PcbSupply;
        }
    }

    public bool PlacementEnabled
    {
        get
        {
            return _units.PcbPlacement;
        }
    }

    public TeachingSaveBehavior SaveBehavior
    {
        get
        {
            return SelectedPoint is { } point && IsBuffer(point)
                ? TeachingSaveBehavior.Buffer
                : SelectedPoint?.Storage == TeachingStorage.Machine
                    ? TeachingSaveBehavior.Machine
                    : TeachingSaveBehavior.Recipe;
        }
    }

    protected override void RefreshPointPositions()
    {
        foreach (var point in Points.Where(point => !IsBuffer(point)))
            point.Refresh();
        OnPropertyChanged(nameof(HorizontalZ));
    }

    [RelayCommand(CanExecute = nameof(CanSaveBufferSetup))]
    private Task SaveBufferSetupAsync(CancellationToken cancellationToken)
    {
        return Machine.RunTeachingEditAsync(
            async token =>
            {
                var points = Points.Where(IsBuffer).ToArray();
                foreach (var point in points)
                    point.Apply();

                await SaveSettingsAsync(
                    token,
                    points.Select(point => point.Position.Setting!).Distinct().ToArray());
                NotifyManualTeachingCommands();
            },
            cancellationToken,
            ViewCancellation);
    }

    private bool CanSaveBufferSetup()
    {
        return CanEditTeaching;
    }

    public void Activate()
    {
        RecipeEditor.Refresh();
        OnPropertyChanged(nameof(SupplyEnabled));
        OnPropertyChanged(nameof(PlacementEnabled));
        BuildPoints();
        ActivatePositionUpdates();
        NotifyManualTeachingCommands();
    }

    public Task ShutdownAsync()
    {
        return CommandShutdown.StopAsync(
            Deactivate,
            StepCommand,
            JogCommand,
            MoveToHorizontalZCommand,
            MoveToPointCommand,
            SetOutputOnCommand,
            SetOutputOffCommand,
            TeachCurrentPositionCommand,
            SaveBufferSetupCommand);
    }

    private void BuildPoints()
    {
        TeachingPosition[] positions = [
            .. _supplySettings.GetTeachingPositions(RecipeEditor.Recipe.PcbSupply),
            .. _placementSettings.GetTeachingPositions(),
            _placementSettings.GetBufferTeachingPosition(),
            .. _bufferSettings.GetTeachingPositions(),
        ];
        Points = positions.OrderBy(position => position.MotionGroup)
            .Select(position => new TeachingPoint(position))
            .ToArray();
        SelectedPoint = Points.FirstOrDefault();
    }

    private static bool IsBuffer(TeachingPoint point)
    {
        return point.Storage == TeachingStorage.Buffer;
    }

}
