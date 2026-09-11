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

namespace IBTM.UI;

public partial class OperationViewModel : ObservableObject
{

    private readonly MachineController _machine;
    private readonly MachineOptions _options;
    private readonly Recipe _recipe;
    private readonly MachineMap _map;
    private readonly PickupBoltFeeder _pickupFeeder;
    private readonly ShootingBoltFeeder _shootingFeeder;
    private readonly PcbPlacementRecoveryPreparation _pcbPlacementRecovery;
    private readonly BoltFasteningRecoveryPreparation _boltFasteningRecovery;
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
        Recipe recipe,
        MachineMap map,
        MainConveyor conveyor,
        BufferStage buffer,
        PickupBoltFeeder pickupFeeder,
        ShootingBoltFeeder shootingFeeder,
        NgCarrierConveyor ngConveyor,
        NgShuttle ngShuttle,
        NgCarrierTransfer ngTransfer,
        PcbPlacementRecoveryPreparation pcbPlacementRecovery,
        BoltFasteningRecoveryPreparation boltFasteningRecovery,
        PcbSupplyHandler supply,
        PcbPlacementHandler placement,
        BoltFasteningGantry fastening,
        InspectionGantry inspectionGantry)
    {
        State = state;
        Signals = signals;
        MainConveyorRun = signals.Outputs[OutputIo.MainConveyorRun];
        NgConveyorRun = signals.Outputs[OutputIo.NgConveyorRun];
        _machine = machine;
        Units = units;
        _options = options;
        PcbPlacementWork = pcbPlacementWork;
        BoltFasteningWork = boltFasteningWork;
        InspectionWork = inspectionWork;
        _recipe = recipe;
        _map = map;
        NgConveyor = ngConveyor;
        NgShuttle = ngShuttle;
        _pcbPlacementRecovery = pcbPlacementRecovery;
        _boltFasteningRecovery = boltFasteningRecovery;
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
    public IoOutputStatus MainConveyorRun { get; }
    public IoOutputStatus NgConveyorRun { get; }
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

    public double? PcbSupplyMapLeft
    {
        get
        {
            return _map.Supply(Supply.Motion.Position)?.X;
        }
    }

    public double? PcbSupplyMapTop
    {
        get
        {
            return _map.Supply(Supply.Motion.Position)?.Y;
        }
    }

    public double? PcbPlacementMapLeft
    {
        get
        {
            return _map.Placement(Placement.Motion.Position)?.X;
        }
    }

    public double? PcbPlacementMapTop
    {
        get
        {
            return _map.Placement(Placement.Motion.Position)?.Y;
        }
    }

    public double? BoltFasteningMapLeft
    {
        get
        {
            return _map.Fastening(Fastening.Motion.Position)?.X;
        }
    }

    public double? BoltFasteningMapTop
    {
        get
        {
            return _map.Fastening(Fastening.Motion.Position)?.Y;
        }
    }

    public double BoltPickupFeederMapLeft
    {
        get
        {
            return _map.PickupFeeder().X;
        }
    }

    public double BoltPickupFeederMapTop
    {
        get
        {
            return _map.PickupFeeder().Y;
        }
    }

    public double? InspectionGantryMapLeft
    {
        get
        {
            return _map.Inspection(InspectionGantry.Motion.Position)?.X;
        }
    }

    public double? InspectionGantryMapTop
    {
        get
        {
            return _map.Inspection(InspectionGantry.Motion.Position)?.Y;
        }
    }

    public bool PcbSupplyPcbDetected
    {
        get
        {
            return Supply.Pcb != PcbSupplyPcbState.None;
        }
    }

    public bool PcbSupplyPcbSecured
    {
        get
        {
            return Supply.Pcb == PcbSupplyPcbState.Secured;
        }
    }

    public bool PcbPlacementPcbDetected
    {
        get
        {
            return Placement.Pcb != PlacementPcbState.None;
        }
    }

    public bool PcbPlacementHandlerDown
    {
        get
        {
            return Placement.Lift == PlacementCylinderState.Down;
        }
    }

    public bool PcbPlacementIpmDown
    {
        get
        {
            return Placement.IpmLift == PlacementCylinderState.Down;
        }
    }

    public bool PcbSupplyIpmFixed
    {
        get
        {
            return Supply.IpmFixer == PcbSupplyCylinderState.Forward;
        }
    }

    public bool PcbSupplyGripperClosed
    {
        get
        {
            return Supply.Gripper == PcbSupplyCylinderState.Forward;
        }
    }

    public bool PcbPlacementIpmGripperClosed
    {
        get
        {
            return Placement.IpmGripper == PlacementGripperState.Closed;
        }
    }

    public bool PcbPlacementStopperUp
    {
        get
        {
            return PcbPlacementWork.Stopper == StationCylinderState.Up;
        }
    }

    public bool PcbPlacementBackupPlateUp
    {
        get
        {
            return PcbPlacementWork.BackupPlate == StationCylinderState.Up;
        }
    }

    public bool PcbSupplyRotated
    {
        get
        {
            return Supply.Rotation == PcbSupplyRotationState.Rotated;
        }
    }

    public bool PcbPlacementHeatSink1Present
    {
        get
        {
            return PcbPlacementWork.HeatSinkPresent(HeatSinkSlot.HeatSink1);
        }
    }

    public bool PcbPlacementHeatSink2Present
    {
        get
        {
            return PcbPlacementWork.HeatSinkPresent(HeatSinkSlot.HeatSink2);
        }
    }

    public bool BoltFasteningHeatSink1Present
    {
        get
        {
            return BoltFasteningWork.HeatSinkPresent(HeatSinkSlot.HeatSink1);
        }
    }

    public bool BoltFasteningHeatSink2Present
    {
        get
        {
            return BoltFasteningWork.HeatSinkPresent(HeatSinkSlot.HeatSink2);
        }
    }

    public bool BoltFasteningStopperUp
    {
        get
        {
            return BoltFasteningWork.Stopper == StationCylinderState.Up;
        }
    }

    public bool BoltFasteningBackupPlateUp
    {
        get
        {
            return BoltFasteningWork.BackupPlate == StationCylinderState.Up;
        }
    }

    public bool InspectionHeatSink1Present
    {
        get
        {
            return InspectionWork.HeatSinkPresent(HeatSinkSlot.HeatSink1);
        }
    }

    public bool InspectionHeatSink2Present
    {
        get
        {
            return InspectionWork.HeatSinkPresent(HeatSinkSlot.HeatSink2);
        }
    }

    public bool InspectionStopperUp
    {
        get
        {
            return InspectionWork.Stopper == StationCylinderState.Up;
        }
    }

    public bool InspectionBackupPlateUp
    {
        get
        {
            return InspectionWork.BackupPlate == StationCylinderState.Up;
        }
    }

    public bool PickupHeadDown
    {
        get
        {
            return Fastening.PickupHeadPosition == BoltCylinderState.Down;
        }
    }

    public bool ShootingHeadDown
    {
        get
        {
            return Fastening.ShootingHeadPosition == BoltCylinderState.Down;
        }
    }

    public bool PickupFeederBoltDetected
    {
        get
        {
            return _pickupFeeder.State == BoltFeederState.BoltReady;
        }
    }

    public bool ShootingFeederBoltDetected
    {
        get
        {
            return _shootingFeeder.State == BoltFeederState.BoltReady;
        }
    }

    public bool NgCarrierGripperClosed
    {
        get
        {
            return NgTransfer.Gripper == NgTransferGripperState.Closed;
        }
    }

    public bool NgCarrierPickupDown
    {
        get
        {
            return NgTransfer.Lift == NgTransferLiftState.Down;
        }
    }

    public bool PcbPlacementHeatSink1Completed
    {
        get
        {
            return Units.PcbPlacement
                && PcbPlacementWork.CarrierPresent
                && PcbPlacementHeatSink1Present
                && HasAssembly(PcbPlacementWork, HeatSinkSlot.HeatSink1);
        }
    }

    public bool PcbPlacementHeatSink2Completed
    {
        get
        {
            return Units.PcbPlacement
                && PcbPlacementWork.CarrierPresent
                && PcbPlacementHeatSink2Present
                && HasAssembly(PcbPlacementWork, HeatSinkSlot.HeatSink2);
        }
    }

    public BoltTarget? BoltFasteningActiveBolt
    {
        get
        {
            return FasteningStateVisible ? State.Display.FasteningBolt : null;
        }
    }

    public BoltTarget? InspectionActiveBolt
    {
        get
        {
            return InspectionStateVisible ? State.Display.InspectionBolt : null;
        }
    }

    public HeatSinkSlot? InspectionActivePcb
    {
        get
        {
            return InspectionStateVisible ? State.Display.InspectionPcb : null;
        }
    }

    public string? InspectionPcb1Barcode
    {
        get
        {
            return InspectionBarcode(HeatSinkSlot.HeatSink1);
        }
    }

    public string? InspectionPcb2Barcode
    {
        get
        {
            return InspectionBarcode(HeatSinkSlot.HeatSink2);
        }
    }

    private string? InspectionBarcode(HeatSinkSlot pcb)
    {
        return InspectionWork.CarrierPresent && InspectionWork.HeatSinkPresent(pcb)
            ? InspectionWork.Assemblies.FirstOrDefault(assembly => assembly.HeatSink == pcb)?.PcbBarcode
            : null;
    }

    public IReadOnlyList<BoltTargetView> BoltTargets
    {
        get
        {
            return CreateFasteningTargets();
        }
    }

    public IReadOnlyList<BoltTargetView> InspectionTargets
    {
        get
        {
            return CreateInspectionTargets();
        }
    }

    public AssemblyResult BoltFasteningHeatSink1Result
    {
        get
        {
            return Result(BoltFasteningWork, HeatSinkSlot.HeatSink1, inspection: false);
        }
    }

    public AssemblyResult BoltFasteningHeatSink2Result
    {
        get
        {
            return Result(BoltFasteningWork, HeatSinkSlot.HeatSink2, inspection: false);
        }
    }

    public AssemblyResult InspectionHeatSink1Result
    {
        get
        {
            return Result(InspectionWork, HeatSinkSlot.HeatSink1, inspection: true);
        }
    }

    public AssemblyResult InspectionHeatSink2Result
    {
        get
        {
            return Result(InspectionWork, HeatSinkSlot.HeatSink2, inspection: true);
        }
    }

    public string ModeText
    {
        get
        {
            return State.Display.Available ? (State.Display.AutoMode ? "AUTO" : "MANUAL") : "UNKNOWN";
        }
    }

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

    public string? AlarmDetail
    {
        get
        {
            return State.Display.ReadError?.ToString() ?? State.Display.AlarmDetail;
        }
    }

    public string? AlarmMessage
    {
        get
        {
            return State.Display.ReadError?.Message ?? State.Display.AlarmMessage;
        }
    }

    public bool SafetyBypass
    {
        get
        {
            return !_options.UseEmergencyStop
                || !_options.UseDoorInterlock
                || !_options.UseAirPressureInterlock;
        }
    }

    public bool PcbPlacementRecoveryAvailable
    {
        get
        {
            return _pcbPlacementRecovery.Required;
        }
    }

    public bool BoltFasteningRecoveryAvailable
    {
        get
        {
            return _boltFasteningRecovery.Required;
        }
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
        OnMainConveyorChanged();
        OnPropertyChanged(nameof(Units));
        OnPropertyChanged(nameof(ConveyorStatus));
        OnPropertyChanged(nameof(SupplyPositionKnown));
        OnPropertyChanged(nameof(PlacementPositionKnown));
        OnPropertyChanged(nameof(FasteningPositionKnown));
        OnPropertyChanged(nameof(InspectionPositionKnown));
        OnPropertyChanged(nameof(BoltFeederPositionKnown));
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
        return CommandShutdown.StopAsync(
            () =>
            {
                Deactivate();
                StartCommand.Cancel();
                HomeCommand.Cancel();
                RaiseCylindersCommand.Cancel();
            },
            StartCommand,
            HomeCommand,
            RaiseCylindersCommand);
    }

    [RelayCommand]
    private void OpenPcbPlacementRecovery()
    {
        _pcbPlacementRecovery.Open(Application.Current.MainWindow);
    }

    [RelayCommand]
    private void OpenBoltFasteningRecovery()
    {
        _boltFasteningRecovery.Open(Application.Current.MainWindow);
    }

    [RelayCommand]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var owner = Application.Current.MainWindow;
        if (!_pcbPlacementRecovery.Prepare(owner) || !_boltFasteningRecovery.Prepare(owner))
        {
            return;
        }

        await Task.Run(() => _machine.StartAsync(cancellationToken), cancellationToken);
    }

    [RelayCommand]
    private void Stop()
    {
        StartCommand.Cancel();
        RaiseCylindersCommand.Cancel();
        _machine.Stop();
    }

    [RelayCommand]
    private Task RaiseCylindersAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => _machine.RaiseCylindersAsync(cancellationToken), cancellationToken);
    }

    [RelayCommand]
    private Task HomeAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => _machine.HomeAsync(cancellationToken), cancellationToken);
    }

    [RelayCommand]
    private void StopHome()
    {
        HomeCommand.Cancel();
    }

    private static bool HasAssembly(StationWork work, HeatSinkSlot heatSink)
    {
        return work.Assemblies.Any(assembly => assembly.HeatSink == heatSink);
    }

    private static AssemblyResult Result(StationWork work, HeatSinkSlot heatSink, bool inspection)
    {
        var assembly = work.Assemblies.FirstOrDefault(item => item.HeatSink == heatSink);
        return assembly is null
            ? AssemblyResult.Pending
            : inspection ? assembly.InspectionResult : assembly.FasteningResult;
    }

    private IReadOnlyList<BoltTargetView> CreateFasteningTargets()
    {
        if (!Units.BoltFastening || !_map.FasteningDefined)
        {
            return [];
        }

        var active = BoltFasteningActiveBolt;
        return CreateTargets(
            _recipe.Pcb.GetBolts().Where(bolt => Fastening.HasReference(bolt.Head)),
            BoltFasteningHeatSink1Present,
            BoltFasteningHeatSink2Present,
            _map.FasteningTarget,
            bolt => bolt == active ? BoltTargetState.Active : FasteningTargetState(bolt));
    }

    private IReadOnlyList<BoltTargetView> CreateInspectionTargets()
    {
        if (!Units.Inspection || !_map.InspectionDefined)
        {
            return [];
        }

        var active = InspectionActiveBolt;
        return CreateTargets(
            _recipe.Pcb.GetBolts().ToArray(),
            InspectionHeatSink1Present,
            InspectionHeatSink2Present,
            _map.InspectionTarget,
            bolt => bolt == active ? BoltTargetState.Active : InspectionTargetState(bolt));
    }

    private IReadOnlyList<BoltTargetView> CreateTargets(
        IEnumerable<BoltTarget> bolts,
        bool heatSink1Present,
        bool heatSink2Present,
        Func<BoltTarget, (double X, double Y)> position,
        Func<BoltTarget, BoltTargetState> state)
    {
        return bolts.Where(
            bolt =>
                bolt.X is not null
                    && bolt.Y is not null
                    && ((bolt.HeatSink == HeatSinkSlot.HeatSink1 && heatSink1Present)
                        || (bolt.HeatSink == HeatSinkSlot.HeatSink2 && heatSink2Present)))
            .Select(
                bolt =>
                {
                    var target = position(bolt);
                    return new BoltTargetView(bolt.Number, bolt.Head, target.X, target.Y, state(bolt));
                })
            .ToArray();
    }

    private BoltTargetState FasteningTargetState(BoltTarget bolt)
    {
        var assembly = BoltFasteningWork.Assemblies.FirstOrDefault(
            item => item.HeatSink == bolt.HeatSink);
        if (assembly is null)
        {
            return BoltTargetState.Pending;
        }

        if (bolt.Head == FasteningHead.Shooting)
        {
            return ResultState(assembly.PcbBoltResults, bolt.Number);
        }

        var seating = ResultState(assembly.IpmSeatingResults, bolt.Number);
        var final = ResultState(assembly.IpmFinalResults, bolt.Number);
        return seating == BoltTargetState.Ng || final == BoltTargetState.Ng ? BoltTargetState.Ng : final;
    }

    private BoltTargetState InspectionTargetState(BoltTarget bolt)
    {
        var assembly = InspectionWork.Assemblies.FirstOrDefault(item => item.HeatSink == bolt.HeatSink);
        if (assembly is null
            || !assembly.BoltPresenceResults.TryGetValue(bolt.Number, out var present))
        {
            return BoltTargetState.Pending;
        }

        return present ? BoltTargetState.Ok : BoltTargetState.Ng;
    }

    private static BoltTargetState ResultState(IReadOnlyDictionary<int, BoltResult> results, int number)
    {
        return results.TryGetValue(number, out var result)
            ? result.Success ? BoltTargetState.Ok : BoltTargetState.Ng
            : BoltTargetState.Pending;
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
        OnPropertyChanged(nameof(PlacementStatus));
        OnPropertyChanged(nameof(ConveyorStatus));
        OnPropertyChanged(nameof(PcbPlacementRecoveryAvailable));
        OnPropertyChanged(nameof(BoltFasteningRecoveryAvailable));
        OnPropertyChanged(nameof(SupplyDisplayState));
        OnPropertyChanged(nameof(PlacementDisplayState));
        OnPropertyChanged(nameof(BoltDisplayState));
        OnPropertyChanged(nameof(InspectionDisplayState));
        OnPropertyChanged(nameof(InspectionStatus));
        OnPropertyChanged(nameof(FasteningStateVisible));
        OnPropertyChanged(nameof(InspectionStateVisible));
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
        OnPropertyChanged(nameof(PcbSupplyIpmFixed));
        OnPropertyChanged(nameof(PcbSupplyGripperClosed));
        OnPropertyChanged(nameof(PcbSupplyRotated));
        OnPropertyChanged(nameof(Supply));
        OnPropertyChanged(nameof(SupplyDisplayState));
    }

    private void OnPcbPlacementChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(PlacementStatus));
        OnPropertyChanged(nameof(PcbPlacementPcbDetected));
        OnPropertyChanged(nameof(PcbPlacementHandlerDown));
        OnPropertyChanged(nameof(PcbPlacementIpmDown));
        OnPropertyChanged(nameof(Placement));
        OnPropertyChanged(nameof(PcbPlacementIpmGripperClosed));
        OnPropertyChanged(nameof(PcbPlacementStopperUp));
        OnPropertyChanged(nameof(PcbPlacementBackupPlateUp));
        OnPropertyChanged(nameof(Buffer));
        OnPropertyChanged(nameof(PcbPlacementWork));
        OnPropertyChanged(nameof(PcbPlacementHeatSink1Present));
        OnPropertyChanged(nameof(PcbPlacementHeatSink2Present));
        OnPropertyChanged(nameof(PcbPlacementHeatSink1Completed));
        OnPropertyChanged(nameof(PcbPlacementHeatSink2Completed));
        OnPropertyChanged(nameof(PlacementDisplayState));
        OnPropertyChanged(nameof(PcbPlacementRecoveryAvailable));
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
        OnPropertyChanged(nameof(BoltFasteningHeatSink1Present));
        OnPropertyChanged(nameof(BoltFasteningHeatSink2Present));
        OnPropertyChanged(nameof(BoltFasteningStopperUp));
        OnPropertyChanged(nameof(BoltFasteningBackupPlateUp));
        OnPropertyChanged(nameof(PickupHeadDown));
        OnPropertyChanged(nameof(ShootingHeadDown));
        OnPropertyChanged(nameof(Fastening));
        OnPropertyChanged(nameof(PickupFeederBoltDetected));
        OnPropertyChanged(nameof(ShootingFeederBoltDetected));
        OnPropertyChanged(nameof(BoltFasteningActiveBolt));
        OnPropertyChanged(nameof(BoltTargets));
        OnPropertyChanged(nameof(FasteningStateVisible));
        OnPropertyChanged(nameof(BoltFasteningHeatSink1Result));
        OnPropertyChanged(nameof(BoltFasteningHeatSink2Result));
        OnPropertyChanged(nameof(BoltDisplayState));
        OnPropertyChanged(nameof(BoltFasteningRecoveryAvailable));
    }

    private void OnInspectionChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(InspectionWork));
        OnPropertyChanged(nameof(InspectionHeatSink1Present));
        OnPropertyChanged(nameof(InspectionHeatSink2Present));
        OnPropertyChanged(nameof(InspectionStopperUp));
        OnPropertyChanged(nameof(InspectionBackupPlateUp));
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
        OnPropertyChanged(nameof(NgCarrierGripperClosed));
        OnPropertyChanged(nameof(NgCarrierPickupDown));
    }
}
