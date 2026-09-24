using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public partial class OperationViewModel : ObservableObject
{
    public MachineController Machine { get; }
    private readonly MachineOptions _options;
    private readonly RecipeManager _recipes;
    private readonly MachineMap _map;
    private volatile bool _active;

    [ObservableProperty]
    public partial BitmapSource? InspectionImage { get; private set; }

    [ObservableProperty]
    public partial string? InspectionImageCaption { get; private set; }

    public OperationViewModel(
        MachineState state,
        IoSignals signals,
        MachineController machine,
        UnitSettings units,
        MachineOptions options,
        PcbPlacementWork pcbPlacementWork,
        BoltFasteningWork boltFasteningWork,
        InspectionWork inspectionWork,
        RecipeManager recipes,
        MachineMap map,
        MainConveyor conveyor,
        NgCarrierConveyor ngConveyor,
        PcbSupplier supply,
        PcbPlacer placement,
        BoltFasteningStation fastening,
        InspectionStation inspectionStation,
        MachineStore store,
        PcbHistorySettings historySettings,
        PcbDetailsViewModel pcbDetails,
        ILogger<OperationViewModel> log)
    {
        StartCommand = new AsyncRelayCommand(StartAsync);
        StopCommand = new AsyncRelayCommand(StopAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        HomeCommand = new AsyncRelayCommand(HomeAsync);
        LoadOlderPcbsCommand = new AsyncRelayCommand(LoadOlderPcbsAsync, () => HasOlderPcbs);
        ClosePcbDetailsCommand = new RelayCommand(ClosePcbDetails);
        PcbRecords = new();
        _pcbHistoryLimit = PcbHistoryPageSize;
        _store = store;
        _historySettings = historySettings;
        _pcbHistoryDirectory = historySettings.Directory;
        _log = log;
        PcbDetails = pcbDetails;
        HasOlderPcbs = true;

        State = state;
        Signals = signals;
        DoorSensors = [
            new("DOOR 1", signals.Inputs[InputIo.Door1Open]),
            new("DOOR 2", signals.Inputs[InputIo.Door2Open]),
            new("DOOR 3", signals.Inputs[InputIo.Door3Open]),
            new("DOOR 4", signals.Inputs[InputIo.Door4Open]),
            new("DOOR 5", signals.Inputs[InputIo.Door5Open]),
            new("DOOR 6", signals.Inputs[InputIo.Door6Open]),
        ];
        Machine = machine;
        Inspection = inspectionStation;
        Units = units;
        _options = options;
        PcbPlacementWork = pcbPlacementWork;
        BoltFasteningWork = boltFasteningWork;
        InspectionWork = inspectionWork;
        _recipes = recipes;
        _map = map;
        NgConveyor = ngConveyor;
        Conveyor = conveyor;
        Supply = supply;
        Placement = placement;
        Fastening = fastening;
        machine.PcbHistory.Saved += OnPcbSaved;
        machine.PcbHistory.ImageSaved += OnPcbImageSaved;

        supply.Motion.PropertyChanged += OnPcbSupplyMotionChanged;
        foreach (var axis in supply.Motion.Axes.Values)
            axis.PropertyChanged += OnPcbSupplyMotionChanged;
        supply.Changed += OnPcbSupplyChanged;
        placement.Motion.PropertyChanged += OnPcbPlacementMotionChanged;
        foreach (var axis in placement.Motion.Axes.Values)
            axis.PropertyChanged += OnPcbPlacementMotionChanged;
        placement.Changed += OnPcbPlacementChanged;
        fastening.Motion.PropertyChanged += OnBoltFasteningMotionChanged;
        foreach (var axis in fastening.Motion.Axes.Values)
            axis.PropertyChanged += OnBoltFasteningMotionChanged;
        fastening.Changed += OnBoltFasteningChanged;
        inspectionStation.Motion.PropertyChanged += OnInspectionGantryMotionChanged;
        foreach (var axis in inspectionStation.Motion.Axes.Values)
            axis.PropertyChanged += OnInspectionGantryMotionChanged;
        conveyor.Changed += OnMainConveyorChanged;
        inspectionStation.InspectionCaptured += OnInspectionCaptured;
        ngConveyor.Changed += OnNgConveyorChanged;
        inspectionStation.Changed += OnInspectionChanged;
        state.PropertyChanged += OnMachineStateChanged;
        machine.PropertyChanged += OnMachinePropertyChanged;
        recipes.Changed += OnRecipeChanged;
        signals.Outputs[OutputIo.MainConveyorRun].PropertyChanged += OnConveyorOutputChanged;
        signals.Outputs[OutputIo.NgConveyorRun].PropertyChanged += OnConveyorOutputChanged;
    }

    public MachineState State { get; }
    public IoSignals Signals { get; }
    public IReadOnlyList<DoorSensorDisplay> DoorSensors { get; }
    public UnitSettings Units { get; }

    public PcbPlacementWork PcbPlacementWork { get; }
    public BoltFasteningWork BoltFasteningWork { get; }
    public InspectionWork InspectionWork { get; }
    public MainConveyor Conveyor { get; }
    public NgCarrierConveyor NgConveyor { get; }
    public InspectionStation Inspection { get; }

    public PcbSupplier Supply { get; }
    public PcbPlacer Placement { get; }
    public BoltFasteningStation Fastening { get; }

    public double? PcbSupplyMapLeft => _map.GetSupplyPosition(Supply.Motion.Position)?.X;

    public double? PcbSupplyMapTop => _map.GetSupplyPosition(Supply.Motion.Position)?.Y;

    public double? PcbPlacementMapLeft => _map.GetPlacementPosition(Placement.Motion.Position)?.X;

    public double? PcbPlacementMapTop => _map.GetPlacementPosition(Placement.Motion.Position)?.Y;

    public double? ShootingHeadMapLeft => _map.GetFasteningPosition(Fastening.Motion.Position, FasteningHead.Shooting)?.X;

    public double? ShootingHeadMapTop => _map.GetFasteningPosition(Fastening.Motion.Position, FasteningHead.Shooting)?.Y;

    public double? PickupHeadMapLeft => _map.GetFasteningPosition(Fastening.Motion.Position, FasteningHead.Pickup)?.X;

    public double? PickupHeadMapTop => _map.GetFasteningPosition(Fastening.Motion.Position, FasteningHead.Pickup)?.Y;

    public double? BoltPickupFeederMapLeft => _map.PickupFeederPosition?.X;

    public double? BoltPickupFeederMapTop => _map.PickupFeederPosition?.Y;

    public double? InspectionGantryMapLeft => _map.GetInspectionPosition(Inspection.Motion.Position)?.X;

    public double? InspectionGantryMapTop => _map.GetInspectionPosition(Inspection.Motion.Position)?.Y;

    public double? NgPickupMapLeft => _map.GetNgPickupPosition(Inspection.Motion.Position)?.X;

    public double? NgPickupMapTop => _map.GetNgPickupPosition(Inspection.Motion.Position)?.Y;

    public bool PcbSupplyPcbDetected => Supply.Pcb != PcbSupplyPcbState.None;

    public bool PcbSupplyPcbSecured => Supply.Pcb == PcbSupplyPcbState.Secured;

    public bool PcbPlacementPcbDetected => Placement.Pcb != PlacementPcbState.None;

    public bool PcbPlacementIpmDown => Placement.IpmLift == PlacementCylinderState.Down;

    public bool PcbSupplyGripperClosed => Supply.Gripper == PcbSupplyCylinderState.Forward;


    public bool PcbPlacementHeatSink1Completed
    {
        get
        {
            return Units.PcbPlacement
                && PcbPlacementWork.Station.CarrierPresent
                && PcbPlacementWork.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink1)
                && HasAssembly(PcbPlacementWork, HeatSinkSlot.HeatSink1);
        }
    }

    public bool PcbPlacementHeatSink2Completed
    {
        get
        {
            return Units.PcbPlacement
                && PcbPlacementWork.Station.CarrierPresent
                && PcbPlacementWork.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink2)
                && HasAssembly(PcbPlacementWork, HeatSinkSlot.HeatSink2);
        }
    }

    public BoltPoint? BoltFasteningActiveBolt
    {
        get
        {
            return State.AutomaticRunning && FasteningState is { } state
                ? Fastening.GetActiveBolt(state) : null;
        }
    }

    public BoltPoint? InspectionActiveBolt
    {
        get
        {
            return InspectionStateVisible && Signals.Outputs[OutputIo.MainConveyorRun].IsOn is { } running
                ? Inspection.GetActiveBolt(running) : null;
        }
    }

    public HeatSinkSlot? InspectionActivePcb
    {
        get
        {
            return InspectionStateVisible && Signals.Outputs[OutputIo.MainConveyorRun].IsOn is { } running
                ? Inspection.GetActivePcb(running) : null;
        }
    }

    public string? InspectionActiveBarcode => InspectionActivePcb is { } pcb ? InspectionBarcode(pcb) : null;

    public string? InspectionPcb1Barcode => InspectionBarcode(HeatSinkSlot.HeatSink1);

    public string? InspectionPcb2Barcode => InspectionBarcode(HeatSinkSlot.HeatSink2);

    public IReadOnlyList<BoltTargetView> BoltTargets
    {
        get
        {
            if (!Units.BoltFastening || !_map.FasteningDefined)
                return [];

            var active = BoltFasteningActiveBolt;
            var assemblies = BoltFasteningWork.Assemblies;
            var targets = new List<BoltTargetView>();
            foreach (var bolt in _recipes.Current.Pcb.BoltPoints)
            {
                if (!BoltFasteningWork.Station.IsHeatSinkPresent(bolt.HeatSink)
                    || _map.GetFasteningTargetPosition(bolt) is not { } position)
                    continue;

                var assembly = assemblies.FirstOrDefault(item => item.HeatSink == bolt.HeatSink);
                var results = bolt.Head == FasteningHead.Shooting ? assembly?.PcbBoltResults : assembly?.PickupBoltResults;
                var state = BoltTargetState.Pending;
                if (bolt == active)
                    state = BoltTargetState.Active;
                else if (results is not null && results.TryGetValue(bolt.Number, out var result))
                    state = result.Success ? BoltTargetState.Ok : BoltTargetState.Ng;
                targets.Add(new(bolt.Number, bolt.Head, position.X, position.Y, state));
            }
            return targets;
        }
    }

    public IReadOnlyList<BoltTargetView> InspectionTargets
    {
        get
        {
            if (!Units.Inspection || !_map.InspectionDefined)
                return [];

            var active = InspectionActiveBolt;
            var assemblies = InspectionWork.Assemblies;
            var targets = new List<BoltTargetView>();
            foreach (var bolt in _recipes.Current.Pcb.BoltPoints)
            {
                if (!InspectionWork.Station.IsHeatSinkPresent(bolt.HeatSink)
                    || _map.GetInspectionTargetPosition(bolt) is not { } position)
                    continue;

                var assembly = assemblies.FirstOrDefault(item => item.HeatSink == bolt.HeatSink);
                var state = BoltTargetState.Pending;
                if (bolt == active)
                    state = BoltTargetState.Active;
                else if (assembly is not null && assembly.BoltPresenceResults.TryGetValue(bolt.Number, out var present))
                    state = present ? BoltTargetState.Ok : BoltTargetState.Ng;
                targets.Add(new(bolt.Number, bolt.Head, position.X, position.Y, state));
            }
            return targets;
        }
    }

    public AssemblyResult BoltFasteningHeatSink1Result => GetAssemblyResult(BoltFasteningWork, HeatSinkSlot.HeatSink1, inspection: false);

    public AssemblyResult BoltFasteningHeatSink2Result => GetAssemblyResult(BoltFasteningWork, HeatSinkSlot.HeatSink2, inspection: false);

    public AssemblyResult InspectionHeatSink1Result => GetAssemblyResult(InspectionWork, HeatSinkSlot.HeatSink1, inspection: true);

    public AssemblyResult InspectionHeatSink2Result => GetAssemblyResult(InspectionWork, HeatSinkSlot.HeatSink2, inspection: true);

    public string ModeText => State.Available ? (State.AutoMode ? "AUTO" : "MANUAL") : "UNKNOWN";

    public bool HasAlarm
    {
        get
        {
            return State.Alarm != MachineAlarm.None
                || State.ReadError is not null;
        }
    }

    public Enum Alarm
    {
        get
        {
            return State.ReadError is null
                ? State.Alarm
                : MachineDisplayState.Unavailable;
        }
    }

    public string? AlarmDetail => State.ReadError?.ToString() ?? State.AlarmDetail;

    public string? AlarmMessage => State.ReadError?.Message ?? State.AlarmMessage;

    public bool SafetyBypass
    {
        get
        {
            return !_options.UseEmergencyStop
                || !_options.UseDoorInterlock
                || !_options.UseAirPressureInterlock;
        }
    }

    private string? InspectionBarcode(HeatSinkSlot pcb)
    {
        if (!InspectionWork.Station.CarrierPresent || !InspectionWork.Station.IsHeatSinkPresent(pcb))
            return null;
        var assembly = InspectionWork.Assemblies.FirstOrDefault(assembly => assembly.HeatSink == pcb);
        return assembly?.PcbBarcodeResult == AssemblyResult.Ng ? "NG · Not Read" : assembly?.PcbBarcode;
    }

    public void Activate()
    {
        _active = true;
        if (_pcbHistoryDirectory != _historySettings.Directory)
        {
            _pcbHistoryDirectory = _historySettings.Directory;
            PcbRecords.Clear();
            SelectedPcb = null;
            HasOlderPcbs = true;
            _pcbHistoryLimit = PcbHistoryPageSize;
            _pcbHistoryLoaded = false;
        }
        if (!_pcbHistoryLoaded && LoadOlderPcbsCommand.CanExecute(null))
            LoadOlderPcbsCommand.Execute(null);
        OnMachineStateChanged(this, new(null));
        OnRecipeChanged();
        OnPropertyChanged(nameof(ConveyorState));
        OnPropertyChanged(nameof(Conveyor));
        OnPropertyChanged(nameof(Units));
        OnPropertyChanged(nameof(SafetyBypass));
    }

    public void Deactivate()
    {
        _active = false;
    }

    public Task ShutdownAsync()
    {
        Deactivate();
        return CommandShutdown.CancelAndWaitAsync(
            [StopCommand, StartCommand, HomeCommand, LoadOlderPcbsCommand]);
    }

    public IAsyncRelayCommand StartCommand { get; }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        await Machine.StartAsync(cancellationToken);
    }

    public IAsyncRelayCommand StopCommand { get; }

    private async Task StopAsync()
    {
        try
        {
            IAsyncRelayCommand[] commands = [StartCommand, HomeCommand];
            var pending = CommandShutdown.Capture(commands);
            await CommandShutdown.CancelAndWaitAsync(
                commands,
                Machine.StopAsync(),
                pending);
        }
        catch (Exception exception)
        {
            if (!State.IsError)
                State.SetError(MachineAlarm.StopFailed, exception);
            else
                _log.LogError(exception, "Machine STOP also failed.");
        }
    }

    public IAsyncRelayCommand HomeCommand { get; }

    private async Task HomeAsync(CancellationToken cancellationToken)
    {
        await Machine.HomeAsync(cancellationToken);
    }

    private static bool HasAssembly(StationWork work, HeatSinkSlot heatSink)
    {
        return work.Assemblies.Any(assembly => assembly.HeatSink == heatSink);
    }

    private static AssemblyResult GetAssemblyResult(StationWork work, HeatSinkSlot heatSink, bool inspection)
    {
        var assembly = work.Assemblies.FirstOrDefault(item => item.HeatSink == heatSink);
        switch (true)
        {
            case true when assembly is null:
                return AssemblyResult.Pending;
            case true when inspection:
                return assembly.InspectionResult;
            default:
                return assembly.FasteningResult;
        }
    }

    private void OnPcbSupplyMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(MotionStatus.Position) or nameof(AxisStatus.State))
            OnPcbSupplyChanged();

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(SupplyPositionKnown));
            OnPropertyChanged(nameof(PcbSupplyMapLeft));
            OnPropertyChanged(nameof(PcbSupplyMapTop));
        }
    }

    private void OnPcbPlacementMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(MotionStatus.Position) or nameof(AxisStatus.State))
            OnPcbPlacementChanged();

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(PlacementPositionKnown));
            OnPropertyChanged(nameof(PcbPlacementMapLeft));
            OnPropertyChanged(nameof(PcbPlacementMapTop));
        }
    }

    private void OnBoltFasteningMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(MotionStatus.Position) or nameof(AxisStatus.State))
            OnBoltFasteningChanged();

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(FasteningPositionKnown));
            OnPropertyChanged(nameof(ShootingHeadMapLeft));
            OnPropertyChanged(nameof(ShootingHeadMapTop));
            OnPropertyChanged(nameof(PickupHeadMapLeft));
            OnPropertyChanged(nameof(PickupHeadMapTop));
        }
    }

    private void OnInspectionGantryMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(MotionStatus.Position) or nameof(AxisStatus.State))
        {
            OnInspectionChanged();
            OnMainConveyorChanged();
        }

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(InspectionPositionKnown));
            OnPropertyChanged(nameof(InspectionGantryMapLeft));
            OnPropertyChanged(nameof(InspectionGantryMapTop));
            OnPropertyChanged(nameof(NgPickupMapLeft));
            OnPropertyChanged(nameof(NgPickupMapTop));
        }
    }

    private void OnMachineStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MachineState.Available) or nameof(MachineState.SafetyReady)
            or nameof(MachineState.Alarm) or nameof(MachineState.Faulted) or nameof(MachineState.IsHoming)
            or nameof(MachineState.ServoPowerOn) or nameof(MachineState.Homed) or nameof(MachineState.IsRunning))
            OnPropertyChanged(nameof(MachineDisplayState));
        if (e.PropertyName is null or nameof(MachineState.AutoMode) or nameof(MachineState.Available))
            OnPropertyChanged(nameof(ModeText));
        if (e.PropertyName is null or nameof(MachineState.Alarm) or nameof(MachineState.ReadError))
        {
            OnPropertyChanged(nameof(HasAlarm));
            OnPropertyChanged(nameof(Alarm));
        }
        if (e.PropertyName is null or nameof(MachineState.AlarmDetail) or nameof(MachineState.ReadError))
            OnPropertyChanged(nameof(AlarmDetail));
        if (e.PropertyName is null or nameof(MachineState.AlarmMessage) or nameof(MachineState.ReadError))
            OnPropertyChanged(nameof(AlarmMessage));
        if (e.PropertyName is null or nameof(MachineState.AutomaticRunning)
            or nameof(MachineState.Alarm) or nameof(MachineState.Available))
        {
            OnPcbSupplyChanged();
            OnPcbPlacementChanged();
            OnBoltFasteningChanged();
            OnInspectionChanged();
            OnMainConveyorChanged();
            OnNgConveyorChanged();
        }
        if (e.PropertyName == nameof(MachineState.BoltTestRunning))
            OnPropertyChanged(nameof(BoltDisplayState));
        if (e.PropertyName == nameof(MachineState.RepeatEnabled))
        {
            OnInspectionChanged();
            OnMainConveyorChanged();
        }
    }

    private void OnMachinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MachineController.StartBlock)
            or nameof(MachineController.HomeBlock) or nameof(MachineController.IsStartAllowed))
        {
            OnPropertyChanged(nameof(StartBlocked));
            OnPropertyChanged(nameof(StartBlock));
        }
    }

    private void OnConveyorOutputChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IoOutputStatus.IsOn))
            return;
        if (sender == Signals.Outputs[OutputIo.MainConveyorRun])
        {
            OnMainConveyorChanged();
            OnInspectionChanged();
        }
        else
        {
            OnNgConveyorChanged();
            OnInspectionChanged();
        }
    }

    private void OnRecipeChanged()
    {
        OnPropertyChanged(nameof(PcbSupplyMapLeft));
        OnPropertyChanged(nameof(PcbSupplyMapTop));
        OnPropertyChanged(nameof(PcbPlacementMapLeft));
        OnPropertyChanged(nameof(PcbPlacementMapTop));
        OnPropertyChanged(nameof(ShootingHeadMapLeft));
        OnPropertyChanged(nameof(ShootingHeadMapTop));
        OnPropertyChanged(nameof(PickupHeadMapLeft));
        OnPropertyChanged(nameof(PickupHeadMapTop));
        OnPropertyChanged(nameof(InspectionGantryMapLeft));
        OnPropertyChanged(nameof(InspectionGantryMapTop));
        OnPropertyChanged(nameof(NgPickupMapLeft));
        OnPropertyChanged(nameof(NgPickupMapTop));
        OnPropertyChanged(nameof(BoltPickupFeederMapLeft));
        OnPropertyChanged(nameof(BoltPickupFeederMapTop));
        OnPcbSupplyChanged();
        OnPcbPlacementChanged();
        OnBoltFasteningChanged();
        OnInspectionChanged();
        OnPropertyChanged(nameof(BoltFeederPositionKnown));
    }

    // Devices expose Changed events; notifying their property also refreshes nested XAML bindings.
    private void OnPcbSupplyChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(PcbSupplyPcbDetected));
        OnPropertyChanged(nameof(PcbSupplyPcbSecured));
        OnPropertyChanged(nameof(PcbSupplyGripperClosed));
        OnPropertyChanged(nameof(SupplyPositionKnown));
        OnPropertyChanged(nameof(Supply));
        OnPropertyChanged(nameof(SupplyDisplayState));
        OnPropertyChanged(nameof(SupplyStatus));
    }

    private void OnPcbPlacementChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(PlacementState));
        OnPropertyChanged(nameof(PlacementTarget));
        OnPropertyChanged(nameof(PlacementPositionKnown));
        OnPropertyChanged(nameof(PlacementStatus));
        OnPropertyChanged(nameof(PcbPlacementPcbDetected));
        OnPropertyChanged(nameof(PcbPlacementIpmDown));
        OnPropertyChanged(nameof(Placement));
        OnPropertyChanged(nameof(PcbPlacementWork));
        OnPropertyChanged(nameof(PcbPlacementHeatSink1Completed));
        OnPropertyChanged(nameof(PcbPlacementHeatSink2Completed));
        OnPropertyChanged(nameof(PlacementDisplayState));
    }

    private void OnMainConveyorChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(ConveyorState));
        OnPropertyChanged(nameof(Conveyor));
        OnPropertyChanged(nameof(ConveyorStatus));
        OnPropertyChanged(nameof(InspectionStatus));
    }

    private void OnBoltFasteningChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(FasteningState));
        OnPropertyChanged(nameof(FasteningPositionKnown));
        OnPropertyChanged(nameof(BoltFasteningWork));
        OnPropertyChanged(nameof(Fastening));
        OnPropertyChanged(nameof(BoltFasteningActiveBolt));
        OnPropertyChanged(nameof(BoltTargets));
        OnPropertyChanged(nameof(FasteningStateVisible));
        OnPropertyChanged(nameof(BoltFasteningHeatSink1Result));
        OnPropertyChanged(nameof(BoltFasteningHeatSink2Result));
        OnPropertyChanged(nameof(BoltDisplayState));
    }

    private void OnInspectionCaptured(ImageFrame frame, HeatSinkSlot pcb, int? boltNumber)
    {
        InspectionImageCaption = boltNumber is { } number
            ? $"{pcb.GetDescription()} · Bolt {number}"
            : $"{pcb.GetDescription()} · Data Matrix";
        InspectionImage = InspectionPreview.CreateBitmap(frame);
    }

    private void OnInspectionChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(Inspection));

        OnPropertyChanged(nameof(InspectionState));
        OnPropertyChanged(nameof(InspectionPositionKnown));
        OnPropertyChanged(nameof(InspectionWork));
        OnPropertyChanged(nameof(InspectionActiveBolt));
        OnPropertyChanged(nameof(InspectionActivePcb));
        OnPropertyChanged(nameof(InspectionActiveBarcode));
        OnPropertyChanged(nameof(InspectionPcb1Barcode));
        OnPropertyChanged(nameof(InspectionPcb2Barcode));
        OnPropertyChanged(nameof(InspectionTargets));
        OnPropertyChanged(nameof(InspectionStateVisible));
        OnPropertyChanged(nameof(InspectionHeatSink1Result));
        OnPropertyChanged(nameof(InspectionHeatSink2Result));
        OnPropertyChanged(nameof(InspectionDisplayState));
        OnPropertyChanged(nameof(InspectionStatus));
    }

    private void OnNgConveyorChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(NgConveyorState));
        OnPropertyChanged(nameof(NgConveyor));
    }
}
