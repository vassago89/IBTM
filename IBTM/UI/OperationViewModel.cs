using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;

namespace IBTM.UI;

public partial class OperationViewModel : ObservableObject
{
    private readonly MachineController _machine;
    private readonly MachineOptions _options;
    private readonly RecipeManager _recipes;
    private readonly MachineMap _map;
    private readonly PickupBoltFeeder _pickupFeeder;
    private readonly ShootingBoltFeeder _shootingFeeder;
    private volatile bool _active;

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
        BufferStage buffer,
        PickupBoltFeeder pickupFeeder,
        ShootingBoltFeeder shootingFeeder,
        NgCarrierConveyor ngConveyor,
        NgShuttle ngShuttle,
        NgCarrierTransfer ngTransfer,
        PcbSupplyHandler supply,
        PcbPlacementHandler placement,
        BoltFasteningGantry fastening,
        InspectionGantry inspectionGantry)
    {
        StartCommand = new AsyncRelayCommand(StartAsync);
        StopCommand = new AsyncRelayCommand(StopAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        HomeCommand = new AsyncRelayCommand(HomeAsync);

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
        _machine = machine;
        Units = units;
        _options = options;
        PcbPlacementWork = pcbPlacementWork;
        BoltFasteningWork = boltFasteningWork;
        InspectionWork = inspectionWork;
        _recipes = recipes;
        _map = map;
        NgConveyor = ngConveyor;
        NgShuttle = ngShuttle;
        Conveyor = conveyor;
        Buffer = buffer;
        _pickupFeeder = pickupFeeder;
        _shootingFeeder = shootingFeeder;
        NgTransfer = ngTransfer;
        Supply = supply;
        Placement = placement;
        Fastening = fastening;
        InspectionGantry = inspectionGantry;

        supply.Motion.PropertyChanged += OnPcbSupplyMotionChanged;
        supply.Changed += OnPcbSupplyChanged;
        placement.Motion.PropertyChanged += OnPcbPlacementMotionChanged;
        placement.Changed += OnPcbPlacementChanged;
        fastening.Motion.PropertyChanged += OnBoltFasteningMotionChanged;
        fastening.Changed += OnBoltFasteningChanged;
        inspectionGantry.Motion.PropertyChanged += OnInspectionGantryMotionChanged;
        buffer.StateChanged += OnPcbPlacementChanged;
        conveyor.Changed += OnMainConveyorChanged;
        pickupFeeder.Changed += OnBoltFasteningChanged;
        shootingFeeder.Changed += OnBoltFasteningChanged;
        pcbPlacementWork.Changed += OnPcbPlacementChanged;
        boltFasteningWork.Changed += OnBoltFasteningChanged;
        inspectionWork.Changed += OnInspectionChanged;
        ngTransfer.Changed += OnNgConveyorChanged;
        ngConveyor.Changed += OnNgConveyorChanged;
        ngShuttle.Feedback.Changed += OnNgConveyorChanged;
        state.DisplayChanged += OnMachineDisplayChanged;
    }

    public MachineState State { get; }
    public IoSignals Signals { get; }
    public IReadOnlyList<DoorSensorDisplay> DoorSensors { get; }
    public UnitSettings Units { get; }

    public PcbPlacementWork PcbPlacementWork { get; }
    public BoltFasteningWork BoltFasteningWork { get; }
    public InspectionWork InspectionWork { get; }
    public MainConveyor Conveyor { get; }
    public BufferStage Buffer { get; }
    public NgCarrierConveyor NgConveyor { get; }
    public NgShuttle NgShuttle { get; }
    public NgCarrierTransfer NgTransfer { get; }

    public PcbSupplyHandler Supply { get; }
    public PcbPlacementHandler Placement { get; }
    public BoltFasteningGantry Fastening { get; }
    public InspectionGantry InspectionGantry { get; }

    public double? PcbSupplyMapLeft => _map.GetSupplyPosition(Supply.Motion.Position)?.X;

    public double? PcbSupplyMapTop => _map.GetSupplyPosition(Supply.Motion.Position)?.Y;

    public double? PcbPlacementMapLeft => _map.GetPlacementPosition(Placement.Motion.Position)?.X;

    public double? PcbPlacementMapTop => _map.GetPlacementPosition(Placement.Motion.Position)?.Y;

    public double? BoltFasteningMapLeft => _map.GetFasteningPosition(Fastening.Motion.Position)?.X;

    public double? BoltFasteningMapTop => _map.GetFasteningPosition(Fastening.Motion.Position)?.Y;

    public double BoltPickupFeederMapLeft => _map.PickupFeederPosition.X;

    public double BoltPickupFeederMapTop => _map.PickupFeederPosition.Y;

    public double? InspectionGantryMapLeft => _map.GetInspectionPosition(InspectionGantry.Motion.Position)?.X;

    public double? InspectionGantryMapTop => _map.GetInspectionPosition(InspectionGantry.Motion.Position)?.Y;

    public bool PcbSupplyPcbDetected => Supply.Pcb != PcbSupplyPcbState.None;

    public bool PcbSupplyPcbSecured => Supply.Pcb == PcbSupplyPcbState.Secured;

    public bool PcbPlacementPcbDetected => Placement.Pcb != PlacementPcbState.None;

    public bool PcbPlacementIpmDown => Placement.IpmLift == PlacementCylinderState.Down;

    public bool PcbSupplyGripperClosed => Supply.Gripper == PcbSupplyCylinderState.Forward;

    public bool PcbPlacementIpmGripperClosed => Placement.IpmGripper == PlacementGripperState.Closed;

    public bool PickupFeederBoltDetected => _pickupFeeder.State == BoltFeederState.BoltReady;

    public bool ShootingFeederBoltDetected => _shootingFeeder.State == BoltFeederState.BoltReady;

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

    public BoltPoint? BoltFasteningActiveBolt => FasteningStateVisible ? State.Display.FasteningBolt : null;

    public BoltPoint? InspectionActiveBolt => InspectionStateVisible ? State.Display.InspectionBolt : null;

    public HeatSinkSlot? InspectionActivePcb => InspectionStateVisible ? State.Display.InspectionPcb : null;

    public string? InspectionPcb1Barcode => InspectionBarcode(HeatSinkSlot.HeatSink1);

    public string? InspectionPcb2Barcode => InspectionBarcode(HeatSinkSlot.HeatSink2);

    public IReadOnlyList<BoltTargetView> BoltTargets
    {
        get
        {
            if (!Units.BoltFastening || !_map.FasteningDefined)
                return [];

            var active = BoltFasteningActiveBolt;
            var targets = new List<BoltTargetView>();
            foreach (var bolt in _recipes.Current.Pcb.BoltPoints)
            {
                if (bolt.X is null || bolt.Y is null
                    || !Fastening.HasReference(bolt.Head)
                    || !BoltFasteningWork.Station.IsHeatSinkPresent(bolt.HeatSink))
                    continue;

                var position = _map.GetFasteningTargetPosition(bolt);
                var state = bolt == active ? BoltTargetState.Active : GetFasteningTargetState(bolt);
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
            var targets = new List<BoltTargetView>();
            foreach (var bolt in _recipes.Current.Pcb.BoltPoints)
            {
                if (bolt.X is null || bolt.Y is null
                    || !InspectionWork.Station.IsHeatSinkPresent(bolt.HeatSink))
                    continue;

                var position = _map.GetInspectionTargetPosition(bolt);
                var state = bolt == active ? BoltTargetState.Active : GetInspectionTargetState(bolt);
                targets.Add(new(bolt.Number, bolt.Head, position.X, position.Y, state));
            }
            return targets;
        }
    }

    public AssemblyResult BoltFasteningHeatSink1Result => GetAssemblyResult(BoltFasteningWork, HeatSinkSlot.HeatSink1, inspection: false);

    public AssemblyResult BoltFasteningHeatSink2Result => GetAssemblyResult(BoltFasteningWork, HeatSinkSlot.HeatSink2, inspection: false);

    public AssemblyResult InspectionHeatSink1Result => GetAssemblyResult(InspectionWork, HeatSinkSlot.HeatSink1, inspection: true);

    public AssemblyResult InspectionHeatSink2Result => GetAssemblyResult(InspectionWork, HeatSinkSlot.HeatSink2, inspection: true);

    public string ModeText => State.Display.Available ? (State.Display.AutoMode ? "AUTO" : "MANUAL") : "UNKNOWN";

    public bool HasAlarm
    {
        get
        {
            return State.Display.Alarm != MachineAlarm.None
                || State.Display.ReadError is not null;
        }
    }

    public Enum Alarm
    {
        get
        {
            return State.Display.ReadError is null
                ? State.Display.Alarm
                : MachineDisplayState.Unavailable;
        }
    }

    public string? AlarmDetail => State.Display.ReadError?.ToString() ?? State.Display.AlarmDetail;

    public string? AlarmMessage => State.Display.ReadError?.Message ?? State.Display.AlarmMessage;

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
        return InspectionWork.Station.CarrierPresent && InspectionWork.Station.IsHeatSinkPresent(pcb)
            ? InspectionWork.Assemblies.FirstOrDefault(assembly => assembly.HeatSink == pcb)?.PcbBarcode
            : null;
    }

    public void Activate()
    {
        _active = true;
        State.RequestDisplayRefresh();
        OnPropertyChanged(nameof(PcbSupplyMapLeft));
        OnPropertyChanged(nameof(PcbSupplyMapTop));
        OnPropertyChanged(nameof(PcbPlacementMapLeft));
        OnPropertyChanged(nameof(PcbPlacementMapTop));
        OnPropertyChanged(nameof(BoltFasteningMapLeft));
        OnPropertyChanged(nameof(BoltFasteningMapTop));
        OnPropertyChanged(nameof(InspectionGantryMapLeft));
        OnPropertyChanged(nameof(InspectionGantryMapTop));
        OnMachineDisplayChanged();
        OnPropertyChanged(nameof(Conveyor));
        OnPropertyChanged(nameof(Units));
        OnPropertyChanged(nameof(BoltPickupFeederMapLeft));
        OnPropertyChanged(nameof(BoltPickupFeederMapTop));
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
            [StopCommand, StartCommand, HomeCommand]);
    }

    public IAsyncRelayCommand StartCommand { get; }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        await _machine.StartAsync(cancellationToken);
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
                _machine.StopAsync(),
                pending);
        }
        catch (Exception exception)
        {
            if (!State.IsError)
                State.SetError(MachineAlarm.StopFailed, exception);
            else
                System.Diagnostics.Trace.TraceError("Machine STOP also failed. {0}", exception);
        }
    }

    public IAsyncRelayCommand HomeCommand { get; }

    private async Task HomeAsync(CancellationToken cancellationToken)
    {
        await _machine.HomeAsync(cancellationToken);
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

    private BoltTargetState GetFasteningTargetState(BoltPoint bolt)
    {
        var assembly = BoltFasteningWork.Assemblies.FirstOrDefault(
            item => item.HeatSink == bolt.HeatSink);
        switch (true)
        {
            case true when assembly is null:
                return BoltTargetState.Pending;
            case true when bolt.Head == FasteningHead.Shooting:
                return GetResultState(assembly.PcbBoltResults, bolt.Number);
        }

        return GetResultState(assembly.PickupBoltResults, bolt.Number);
    }

    private BoltTargetState GetInspectionTargetState(BoltPoint bolt)
    {
        var assembly = InspectionWork.Assemblies.FirstOrDefault(item => item.HeatSink == bolt.HeatSink);
        if (assembly is null
            || !assembly.BoltPresenceResults.TryGetValue(bolt.Number, out var present))
        {
            return BoltTargetState.Pending;
        }

        return present ? BoltTargetState.Ok : BoltTargetState.Ng;
    }

    private static BoltTargetState GetResultState(IReadOnlyDictionary<int, BoltResult> results, int number)
    {
        if (!results.TryGetValue(number, out var result))
            return BoltTargetState.Pending;
        return result.Success ? BoltTargetState.Ok : BoltTargetState.Ng;
    }

    private void OnPcbSupplyMotionChanged(object? _, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName == nameof(MotionStatus.IsMoving))
            OnPcbSupplyChanged();

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(SupplyPositionKnown));
            OnPropertyChanged(nameof(PcbSupplyMapLeft));
            OnPropertyChanged(nameof(PcbSupplyMapTop));
        }
    }

    private void OnPcbPlacementMotionChanged(object? _, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName == nameof(MotionStatus.IsMoving))
            OnPcbPlacementChanged();

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(PlacementPositionKnown));
            OnPropertyChanged(nameof(PcbPlacementMapLeft));
            OnPropertyChanged(nameof(PcbPlacementMapTop));
        }
    }

    private void OnBoltFasteningMotionChanged(object? _, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName == nameof(MotionStatus.IsMoving))
            OnBoltFasteningChanged();

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(FasteningPositionKnown));
            OnPropertyChanged(nameof(BoltFasteningMapLeft));
            OnPropertyChanged(nameof(BoltFasteningMapTop));
        }
    }

    private void OnInspectionGantryMotionChanged(object? _, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName == nameof(MotionStatus.IsMoving))
            OnInspectionChanged();

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(InspectionPositionKnown));
            OnPropertyChanged(nameof(InspectionGantryMapLeft));
            OnPropertyChanged(nameof(InspectionGantryMapTop));
        }
    }

    private void OnMachineDisplayChanged()
    {
        OnPropertyChanged(nameof(MachineDisplayState));
        OnPropertyChanged(nameof(StartBlocked));
        OnPropertyChanged(nameof(StartBlock));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(HasAlarm));
        OnPropertyChanged(nameof(Alarm));
        OnPropertyChanged(nameof(AlarmDetail));
        OnPropertyChanged(nameof(AlarmMessage));
        OnPropertyChanged(nameof(SupplyPositionKnown));
        OnPropertyChanged(nameof(PlacementPositionKnown));
        OnPropertyChanged(nameof(FasteningPositionKnown));
        OnPropertyChanged(nameof(InspectionPositionKnown));
        OnPropertyChanged(nameof(BoltFeederPositionKnown));
        OnPropertyChanged(nameof(ConveyorStatus));
        OnPcbPlacementChanged();
        OnBoltFasteningChanged();
        OnInspectionChanged();
        OnNgConveyorChanged();
    }

    // Devices expose Changed events; notifying their property also refreshes nested XAML bindings.
    private void OnPcbSupplyChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(PcbSupplyPcbDetected));
        OnPropertyChanged(nameof(PcbSupplyPcbSecured));
        OnPropertyChanged(nameof(PcbSupplyGripperClosed));
        OnPropertyChanged(nameof(Supply));
        OnPropertyChanged(nameof(SupplyDisplayState));
    }

    private void OnPcbPlacementChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(PlacementStatus));
        OnPropertyChanged(nameof(PcbPlacementPcbDetected));
        OnPropertyChanged(nameof(PcbPlacementIpmDown));
        OnPropertyChanged(nameof(Placement));
        OnPropertyChanged(nameof(PcbPlacementIpmGripperClosed));
        OnPropertyChanged(nameof(Buffer));
        OnPropertyChanged(nameof(PcbPlacementWork));
        OnPropertyChanged(nameof(PcbPlacementHeatSink1Completed));
        OnPropertyChanged(nameof(PcbPlacementHeatSink2Completed));
        OnPropertyChanged(nameof(PlacementDisplayState));
        OnPcbSupplyChanged();
    }

    private void OnMainConveyorChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(Conveyor));
        OnPropertyChanged(nameof(ConveyorStatus));
        OnPropertyChanged(nameof(InspectionStatus));
    }

    private void OnBoltFasteningChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(BoltFasteningWork));
        OnPropertyChanged(nameof(Fastening));
        OnPropertyChanged(nameof(PickupFeederBoltDetected));
        OnPropertyChanged(nameof(ShootingFeederBoltDetected));
        OnPropertyChanged(nameof(BoltFasteningActiveBolt));
        OnPropertyChanged(nameof(BoltTargets));
        OnPropertyChanged(nameof(FasteningStateVisible));
        OnPropertyChanged(nameof(BoltFasteningHeatSink1Result));
        OnPropertyChanged(nameof(BoltFasteningHeatSink2Result));
        OnPropertyChanged(nameof(BoltDisplayState));
    }

    private void OnInspectionChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(InspectionWork));
        OnPropertyChanged(nameof(InspectionActiveBolt));
        OnPropertyChanged(nameof(InspectionActivePcb));
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

        OnPropertyChanged(nameof(NgTransfer));
        OnPropertyChanged(nameof(NgShuttle));
        OnPropertyChanged(nameof(NgConveyor));
    }
}
