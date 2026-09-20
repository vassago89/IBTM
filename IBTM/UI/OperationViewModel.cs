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
using IBTM.BoltFeeder;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;

namespace IBTM.UI;

public partial class OperationViewModel : ObservableObject
{
    public MachineController Machine { get; }
    private readonly PcbPlacer _placer;
    private readonly BoltFasteningStation _fasteningStation;
    private readonly InspectionStation _inspectionStation;
    private readonly MachineOptions _options;
    private readonly RecipeManager _recipes;
    private readonly MachineMap _map;
    private readonly PickupBoltFeeder _pickupFeeder;
    private readonly ShootingBoltFeeder _shootingFeeder;
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
        PickupBoltFeeder pickupFeeder,
        ShootingBoltFeeder shootingFeeder,
        NgCarrierConveyor ngConveyor,
        NgShuttle ngShuttle,
        NgCarrierTransfer ngTransfer,
        PcbSupplyHandler supply,
        PcbPlacementHandler placement,
        BoltFasteningGantry fastening,
        InspectionGantry inspectionGantry,
        BoltInspector inspector,
        PcbPlacer placer,
        BoltFasteningStation fasteningStation,
        InspectionStation inspectionStation)
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
        Machine = machine;
        _placer = placer;
        _fasteningStation = fasteningStation;
        _inspectionStation = inspectionStation;
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
        conveyor.Changed += OnMainConveyorChanged;
        pickupFeeder.Changed += OnBoltFasteningChanged;
        shootingFeeder.Changed += OnBoltFasteningChanged;
        pcbPlacementWork.Changed += OnPcbPlacementChanged;
        boltFasteningWork.Changed += OnBoltFasteningChanged;
        inspectionWork.Changed += OnInspectionChanged;
        inspector.InspectionCaptured += OnInspectionCaptured;
        ngTransfer.Changed += OnNgConveyorChanged;
        ngConveyor.Changed += OnNgConveyorChanged;
        ngShuttle.Feedback.Changed += OnNgConveyorChanged;
        placer.Changed += OnPcbPlacementChanged;
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

    public BoltPoint? BoltFasteningActiveBolt
    {
        get
        {
            return State.AutomaticRunning && FasteningState is { } state
                ? _fasteningStation.GetActiveBolt(state) : null;
        }
    }

    public BoltPoint? InspectionActiveBolt
    {
        get
        {
            return InspectionStateVisible && Signals.Outputs[OutputIo.MainConveyorRun].IsOn is { } running
                ? _inspectionStation.GetActiveBolt(_recipes.Current.Pcb.BoltPoints, running) : null;
        }
    }

    public HeatSinkSlot? InspectionActivePcb
    {
        get
        {
            return InspectionStateVisible && Signals.Outputs[OutputIo.MainConveyorRun].IsOn is { } running
                ? _inspectionStation.GetActivePcb(_recipes.Current.Pcb.BoltPoints, running) : null;
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
        return InspectionWork.Station.CarrierPresent && InspectionWork.Station.IsHeatSinkPresent(pcb)
            ? InspectionWork.Assemblies.FirstOrDefault(assembly => assembly.HeatSink == pcb)?.PcbBarcode
            : null;
    }

    public void Activate()
    {
        _active = true;
        OnPropertyChanged(nameof(PcbSupplyMapLeft));
        OnPropertyChanged(nameof(PcbSupplyMapTop));
        OnPropertyChanged(nameof(PcbPlacementMapLeft));
        OnPropertyChanged(nameof(PcbPlacementMapTop));
        OnPropertyChanged(nameof(BoltFasteningMapLeft));
        OnPropertyChanged(nameof(BoltFasteningMapTop));
        OnPropertyChanged(nameof(InspectionGantryMapLeft));
        OnPropertyChanged(nameof(InspectionGantryMapTop));
        OnMachineStateChanged(this, new(null));
        OnRecipeChanged();
        OnPropertyChanged(nameof(ConveyorState));
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
                System.Diagnostics.Trace.TraceError("Machine STOP also failed. {0}", exception);
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

    private void OnPcbSupplyMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(MotionStatus.Position) or nameof(MotionStatus.XyHomed))
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

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(MotionStatus.Position) or nameof(MotionStatus.XyHomed))
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

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(MotionStatus.Position) or nameof(MotionStatus.XyHomed))
            OnBoltFasteningChanged();

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(FasteningPositionKnown));
            OnPropertyChanged(nameof(BoltFasteningMapLeft));
            OnPropertyChanged(nameof(BoltFasteningMapTop));
        }
    }

    private void OnInspectionGantryMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(MotionStatus.Position) or nameof(MotionStatus.XyHomed))
        {
            OnInspectionChanged();
            OnMainConveyorChanged();
        }

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(InspectionPositionKnown));
            OnPropertyChanged(nameof(InspectionGantryMapLeft));
            OnPropertyChanged(nameof(InspectionGantryMapTop));
        }
    }

    private void OnMachineStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(MachineDisplayState));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(HasAlarm));
        OnPropertyChanged(nameof(Alarm));
        OnPropertyChanged(nameof(AlarmDetail));
        OnPropertyChanged(nameof(AlarmMessage));
        if (e.PropertyName is null or nameof(MachineState.AutomaticRunning)
            or nameof(MachineState.Alarm) or nameof(MachineState.ReadError))
        {
            OnPcbSupplyChanged();
            OnPcbPlacementChanged();
            OnBoltFasteningChanged();
            OnInspectionChanged();
            OnMainConveyorChanged();
            OnNgConveyorChanged();
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
        OnPropertyChanged(nameof(PcbPlacementIpmGripperClosed));
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
        OnPropertyChanged(nameof(PickupFeederBoltDetected));
        OnPropertyChanged(nameof(ShootingFeederBoltDetected));
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
        OnPropertyChanged(nameof(NgTransfer));
        OnPropertyChanged(nameof(NgShuttle));
        OnPropertyChanged(nameof(NgConveyor));
    }
}
