using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
    [Flags]
    private enum PositionRefresh
    {
        PcbSupply = 1,
        PcbPlacement = 2,
        BoltFastening = 4,
        Inspection = 8,
        All = PcbSupply | PcbPlacement | BoltFastening | Inspection,
    }

    [Flags]
    private enum DisplayRefresh
    {
        Machine = 1,
        PcbSupply = 2,
        PcbPlacement = 4,
        Conveyor = 8,
        BoltFastening = 16,
        Inspection = 32,
        NgConveyor = 64,
    }

    private static readonly string[] MachinePropertyNames = [
        nameof(MachineDisplayState),
        nameof(StartBlocked),
        nameof(StartBlock),
        nameof(HomeBlock),
        nameof(ConveyorRunning),
        nameof(MainConveyorState),
        nameof(CarrierBetweenPlacementAndBolt),
        nameof(CarrierBetweenBoltAndInspection),
        nameof(ModeKnown),
        nameof(ModeText),
        nameof(HasAlarm),
        nameof(Alarm),
        nameof(AlarmDetail),
        nameof(AlarmMessage),
        nameof(SupplyPositionKnown),
        nameof(PlacementPositionKnown),
        nameof(FasteningPositionKnown),
        nameof(InspectionPositionKnown),
        nameof(BoltFeederPositionKnown),
        nameof(PlacementStatus),
        nameof(ConveyorStatus),
        nameof(PcbPlacementRecoveryAvailable),
        nameof(BoltFasteningRecoveryAvailable),
        nameof(SupplyDisplayState),
        nameof(PlacementDisplayState),
        nameof(BoltDisplayState),
        nameof(InspectionDisplayState),
        nameof(InspectionStatus),
        nameof(FasteningStateVisible),
        nameof(InspectionStateVisible),
    ];

    private static readonly string[] PcbSupplyPropertyNames = [
        nameof(PcbSupplyPcbDetected),
        nameof(PcbSupplyPcbSecured),
        nameof(PcbSupplyIpmFixed),
        nameof(PcbSupplyGripperClosed),
        nameof(PcbSupplyRotated),
        nameof(Supply),
        nameof(SupplyDisplayState),
    ];

    private static readonly string[] PcbPlacementPropertyNames = [
        nameof(PlacementStatus),
        nameof(PcbPlacementPcbDetected),
        nameof(PcbPlacementPcbSecured),
        nameof(PcbPlacementHandlerDown),
        nameof(PcbPlacementIpmDown),
        nameof(Placement),
        nameof(PcbPlacementIpmGripperClosed),
        nameof(PcbPlacementStopperUp),
        nameof(PcbPlacementBackupPlateUp),
        nameof(PcbBufferPcbPresent),
        nameof(PcbPlacementHeatSink1Present),
        nameof(PcbPlacementHeatSink2Present),
        nameof(PcbPlacementCarrierPresent),
        nameof(PcbPlacementHeatSink1Completed),
        nameof(PcbPlacementHeatSink2Completed),
        nameof(PlacementState),
        nameof(PcbPlacementTargetHeatSink),
        nameof(PlacementDisplayState),
        nameof(PcbPlacementRecoveryAvailable),
    ];

    private static readonly string[] ConveyorPropertyNames = [
        nameof(ConveyorStatus),
        nameof(InspectionStatus),
        nameof(MainConveyorEntryCarrierDetected),
        nameof(MainConveyorExitCarrierDetected),
        nameof(CarrierBetweenPlacementAndBolt),
        nameof(CarrierBetweenBoltAndInspection),
    ];

    private static readonly string[] BoltFasteningPropertyNames = [
        nameof(BoltFasteningHeatSink1Present),
        nameof(BoltFasteningHeatSink2Present),
        nameof(BoltFasteningCarrierPresent),
        nameof(BoltFasteningStopperUp),
        nameof(BoltFasteningBackupPlateUp),
        nameof(PickupHeadDown),
        nameof(ShootingHeadDown),
        nameof(Fastening),
        nameof(PickupFeederBoltDetected),
        nameof(ShootingFeederBoltDetected),
        nameof(FasteningState),
        nameof(BoltFasteningActiveBolt),
        nameof(BoltTargets),
        nameof(FasteningStateVisible),
        nameof(BoltFasteningHeatSink1Result),
        nameof(BoltFasteningHeatSink2Result),
        nameof(BoltDisplayState),
        nameof(BoltFasteningRecoveryAvailable),
    ];

    private static readonly string[] InspectionPropertyNames = [
        nameof(InspectionHeatSink1Present),
        nameof(InspectionHeatSink2Present),
        nameof(InspectionCarrierPresent),
        nameof(InspectionStopperUp),
        nameof(InspectionBackupPlateUp),
        nameof(InspectionState),
        nameof(InspectionActiveBolt),
        nameof(InspectionActivePcb),
        nameof(InspectionPcb1Barcode),
        nameof(InspectionPcb2Barcode),
        nameof(InspectionTargets),
        nameof(InspectionStateVisible),
        nameof(InspectionHeatSink1Result),
        nameof(InspectionHeatSink2Result),
        nameof(InspectionDisplayState),
        nameof(InspectionStatus),
    ];

    private static readonly string[] NgConveyorPropertyNames = [
        nameof(NgCarrierGripperClosed),
        nameof(NgCarrierPickupDown),
        nameof(NgCarrierDetected),
        nameof(NgShuttleCarrierDetected),
        nameof(NgConveyorPosition1Occupied),
        nameof(NgConveyorPosition2Occupied),
        nameof(NgAlarmRequired),
        nameof(NgConveyorRunCommandOn),
        nameof(NgShuttleLift),
        nameof(NgConveyorState),
    ];

    private static readonly string[] ActivationPropertyNames = [
        nameof(InspectionEnabled),
        nameof(ConveyorStatus),
        nameof(SupplyPositionKnown),
        nameof(PlacementPositionKnown),
        nameof(FasteningPositionKnown),
        nameof(InspectionPositionKnown),
        nameof(BoltFeederPositionKnown),
        nameof(BoltPickupFeederMapLeft),
        nameof(BoltPickupFeederMapTop),
        nameof(MainConveyorEnabled),
        nameof(PcbSupplyEnabled),
        nameof(PcbPlacementEnabled),
        nameof(PickupFeederEnabled),
        nameof(BoltFasteningEnabled),
        nameof(SafetyBypass),
    ];

    private const double ZSideViewTop = 7;
    private const double ZSideViewTravel = 33;

    private readonly MachineState _state;
    private readonly MachineController _machine;
    private readonly UnitSettings _units;
    private readonly MachineOptions _options;
    private readonly PcbPlacementWork _pcbPlacementWork;
    private readonly BoltFasteningWork _boltFasteningWork;
    private readonly InspectionWork _inspectionWork;
    private readonly Recipe _recipe;
    private readonly MachineMap _map;
    private readonly MainConveyor _conveyor;
    private readonly BufferStage _buffer;
    private readonly PickupBoltFeeder _pickupFeeder;
    private readonly ShootingBoltFeeder _shootingFeeder;
    private readonly NgCarrierConveyor _ngConveyor;
    private readonly NgShuttle _ngShuttle;
    private readonly NgCarrierTransfer _ngTransfer;
    private readonly PcbPlacementRecoveryPreparation _pcbPlacementRecovery;
    private readonly BoltFasteningRecoveryPreparation _boltFasteningRecovery;
    private volatile bool _active;
    private int _commandRefreshQueued;

    public OperationViewModel(
        MachineState state,
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
        _state = state;
        _machine = machine;
        _units = units;
        _options = options;
        _pcbPlacementWork = pcbPlacementWork;
        _boltFasteningWork = boltFasteningWork;
        _inspectionWork = inspectionWork;
        _recipe = recipe;
        _map = map;
        _ngConveyor = ngConveyor;
        _ngShuttle = ngShuttle;
        _pcbPlacementRecovery = pcbPlacementRecovery;
        _boltFasteningRecovery = boltFasteningRecovery;
        _conveyor = conveyor;
        _buffer = buffer;
        _pickupFeeder = pickupFeeder;
        _shootingFeeder = shootingFeeder;
        _ngTransfer = ngTransfer;
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
        pickupFeeder.Changed += OnBoltFeederChanged;
        shootingFeeder.Changed += OnBoltFeederChanged;
        pcbPlacementWork.Changed += OnPcbPlacementChanged;
        boltFasteningWork.Changed += OnBoltFasteningChanged;
        ngConveyor.Changed += OnNgConveyorChanged;
        ngShuttle.Feedback.Changed += OnNgConveyorChanged;
        state.DisplayChanged += OnMachineDisplayChanged;
    }

    public MachineState State
    {
        get
        {
            return _state;
        }
    }

    public PcbSupplyHandler Supply { get; }
    public PcbPlacementHandler Placement { get; }
    public BoltFasteningGantry Fastening { get; }
    public InspectionGantry InspectionGantry { get; }

    public double PcbSupplyZTop
    {
        get
        {
            return ZTop(Supply.Motion);
        }
    }

    public double PcbPlacementZTop
    {
        get
        {
            return ZTop(Placement.Motion);
        }
    }

    public double BoltFasteningZTop
    {
        get
        {
            return ZTop(Fastening.Motion);
        }
    }

    public double PcbSupplyMapLeft
    {
        get
        {
            return _map.Supply(Supply.Motion.Position).X;
        }
    }

    public double PcbSupplyMapTop
    {
        get
        {
            return _map.Supply(Supply.Motion.Position).Y;
        }
    }

    public double PcbPlacementMapLeft
    {
        get
        {
            return _map.Placement(Placement.Motion.Position).X;
        }
    }

    public double PcbPlacementMapTop
    {
        get
        {
            return _map.Placement(Placement.Motion.Position).Y;
        }
    }

    public double BoltFasteningMapLeft
    {
        get
        {
            return _map.Fastening(Fastening.Motion.Position).X;
        }
    }

    public double BoltFasteningMapTop
    {
        get
        {
            return _map.Fastening(Fastening.Motion.Position).Y;
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

    public double InspectionGantryMapLeft
    {
        get
        {
            return _map.Inspection(InspectionGantry.Motion.Position).X;
        }
    }

    public double InspectionGantryMapTop
    {
        get
        {
            return _map.Inspection(InspectionGantry.Motion.Position).Y;
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

    public bool PcbPlacementPcbSecured
    {
        get
        {
            return Placement.PcbSecured;
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
            return _pcbPlacementWork.Stopper == StationCylinderState.Up;
        }
    }

    public bool PcbPlacementBackupPlateUp
    {
        get
        {
            return _pcbPlacementWork.BackupPlate == StationCylinderState.Up;
        }
    }

    public bool PcbSupplyRotated
    {
        get
        {
            return Supply.Rotation == PcbSupplyRotationState.Rotated;
        }
    }

    public bool PcbBufferPcbPresent
    {
        get
        {
            return _buffer.PcbPresent;
        }
    }

    public bool PcbPlacementHeatSink1Present
    {
        get
        {
            return _pcbPlacementWork.HeatSinkPresent(HeatSinkSlot.HeatSink1);
        }
    }

    public bool PcbPlacementHeatSink2Present
    {
        get
        {
            return _pcbPlacementWork.HeatSinkPresent(HeatSinkSlot.HeatSink2);
        }
    }

    public bool PcbPlacementCarrierPresent
    {
        get
        {
            return _pcbPlacementWork.CarrierPresent;
        }
    }

    public bool MainConveyorEntryCarrierDetected
    {
        get
        {
            return _conveyor.EntryCarrierDetected;
        }
    }

    public bool MainConveyorExitCarrierDetected
    {
        get
        {
            return _conveyor.ExitCarrierDetected;
        }
    }

    public bool BoltFasteningHeatSink1Present
    {
        get
        {
            return _boltFasteningWork.HeatSinkPresent(HeatSinkSlot.HeatSink1);
        }
    }

    public bool BoltFasteningHeatSink2Present
    {
        get
        {
            return _boltFasteningWork.HeatSinkPresent(HeatSinkSlot.HeatSink2);
        }
    }

    public bool BoltFasteningCarrierPresent
    {
        get
        {
            return _boltFasteningWork.CarrierPresent;
        }
    }

    public bool BoltFasteningStopperUp
    {
        get
        {
            return _boltFasteningWork.Stopper == StationCylinderState.Up;
        }
    }

    public bool BoltFasteningBackupPlateUp
    {
        get
        {
            return _boltFasteningWork.BackupPlate == StationCylinderState.Up;
        }
    }

    public bool InspectionHeatSink1Present
    {
        get
        {
            return _inspectionWork.HeatSinkPresent(HeatSinkSlot.HeatSink1);
        }
    }

    public bool InspectionHeatSink2Present
    {
        get
        {
            return _inspectionWork.HeatSinkPresent(HeatSinkSlot.HeatSink2);
        }
    }

    public bool InspectionCarrierPresent
    {
        get
        {
            return _inspectionWork.CarrierPresent;
        }
    }

    public bool InspectionStopperUp
    {
        get
        {
            return _inspectionWork.Stopper == StationCylinderState.Up;
        }
    }

    public bool InspectionBackupPlateUp
    {
        get
        {
            return _inspectionWork.BackupPlate == StationCylinderState.Up;
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
            return _ngTransfer.Gripper == NgTransferGripperState.Closed;
        }
    }

    public bool NgCarrierPickupDown
    {
        get
        {
            return _ngTransfer.Lift == NgTransferLiftState.Down;
        }
    }

    public bool NgCarrierDetected
    {
        get
        {
            return _ngTransfer.CarrierDetected;
        }
    }

    public bool NgShuttleCarrierDetected
    {
        get
        {
            return _ngShuttle.Feedback.CarrierDetected;
        }
    }

    public bool NgConveyorPosition1Occupied
    {
        get
        {
            return _ngConveyor.Position1Occupied;
        }
    }

    public bool NgConveyorPosition2Occupied
    {
        get
        {
            return _ngConveyor.Position2Occupied;
        }
    }

    public bool NgAlarmRequired
    {
        get
        {
            return _ngConveyor.AlarmRequired;
        }
    }

    public bool NgConveyorRunCommandOn
    {
        get
        {
            return _state.Display.NgConveyorRunning;
        }
    }

    public NgShuttleLiftState NgShuttleLift
    {
        get
        {
            return _ngShuttle.Feedback.Lift;
        }
    }

    public NgConveyorState NgConveyorState
    {
        get
        {
            return _state.Display.NgConveyorState;
        }
    }

    public bool ConveyorRunning
    {
        get
        {
            return _state.Display.ConveyorRunning;
        }
    }

    public MainConveyorState MainConveyorState
    {
        get
        {
            return _state.Display.ConveyorState;
        }
    }

    public bool CarrierBetweenPlacementAndBolt
    {
        get
        {
            return !PcbPlacementCarrierPresent
                && !BoltFasteningCarrierPresent
                && MainConveyorState == MainConveyorState.MovingPcbPlacementToBoltFastening;
        }
    }

    public bool CarrierBetweenBoltAndInspection
    {
        get
        {
            return !BoltFasteningCarrierPresent
                && !InspectionCarrierPresent
                && MainConveyorState == MainConveyorState.MovingBoltFasteningToInspection;
        }
    }

    public bool MainConveyorEnabled
    {
        get
        {
            return _units.MainConveyor;
        }
    }

    public bool PcbSupplyEnabled
    {
        get
        {
            return _units.PcbSupply;
        }
    }

    public bool PcbPlacementEnabled
    {
        get
        {
            return _units.PcbPlacement;
        }
    }

    public bool PickupFeederEnabled
    {
        get
        {
            return _units.PickupBoltFeeder;
        }
    }

    public bool BoltFasteningEnabled
    {
        get
        {
            return _units.BoltFastening;
        }
    }

    public bool InspectionEnabled
    {
        get
        {
            return _units.Inspection;
        }
    }

    public bool PcbPlacementHeatSink1Completed
    {
        get
        {
            return PcbPlacementEnabled
                && PcbPlacementCarrierPresent
                && PcbPlacementHeatSink1Present
                && HasAssembly(_pcbPlacementWork, HeatSinkSlot.HeatSink1);
        }
    }

    public bool PcbPlacementHeatSink2Completed
    {
        get
        {
            return PcbPlacementEnabled
                && PcbPlacementCarrierPresent
                && PcbPlacementHeatSink2Present
                && HasAssembly(_pcbPlacementWork, HeatSinkSlot.HeatSink2);
        }
    }

    public PcbPlacementState PlacementState
    {
        get
        {
            return _state.Display.PlacementState;
        }
    }

    public HeatSinkSlot? PcbPlacementTargetHeatSink
    {
        get
        {
            return _state.Display.PlacementTarget;
        }
    }

    public BoltFasteningState FasteningState
    {
        get
        {
            return _state.Display.FasteningState;
        }
    }

    public InspectionStationState InspectionState
    {
        get
        {
            return _state.Display.InspectionState;
        }
    }

    public BoltTarget? BoltFasteningActiveBolt
    {
        get
        {
            return FasteningStateVisible ? _state.Display.FasteningBolt : null;
        }
    }

    public BoltTarget? InspectionActiveBolt
    {
        get
        {
            return InspectionStateVisible ? _state.Display.InspectionBolt : null;
        }
    }

    public HeatSinkSlot? InspectionActivePcb
    {
        get
        {
            return InspectionStateVisible ? _state.Display.InspectionPcb : null;
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
        return _inspectionWork.CarrierPresent && _inspectionWork.HeatSinkPresent(pcb)
            ? _inspectionWork.Assemblies.FirstOrDefault(assembly => assembly.HeatSink == pcb)?.PcbBarcode
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
            return Result(_boltFasteningWork, HeatSinkSlot.HeatSink1, inspection: false);
        }
    }

    public AssemblyResult BoltFasteningHeatSink2Result
    {
        get
        {
            return Result(_boltFasteningWork, HeatSinkSlot.HeatSink2, inspection: false);
        }
    }

    public AssemblyResult InspectionHeatSink1Result
    {
        get
        {
            return Result(_inspectionWork, HeatSinkSlot.HeatSink1, inspection: true);
        }
    }

    public AssemblyResult InspectionHeatSink2Result
    {
        get
        {
            return Result(_inspectionWork, HeatSinkSlot.HeatSink2, inspection: true);
        }
    }

    public bool ModeKnown
    {
        get
        {
            return _state.Display.Available;
        }
    }

    public string ModeText
    {
        get
        {
            return ModeKnown ? (_state.Display.AutoMode ? "AUTO" : "MANUAL") : "UNKNOWN";
        }
    }

    public bool HasAlarm
    {
        get
        {
            return _state.Display.Alarm != MachineAlarm.None
                || _state.Display.ReadError is not null;
        }
    }

    public Enum Alarm
    {
        get
        {
            return _state.Display.ReadError is null
                ? _state.Display.Alarm
                : MachineDisplayState.Unavailable;
        }
    }

    public string? AlarmDetail
    {
        get
        {
            return _state.Display.ReadError?.ToString() ?? _state.Display.AlarmDetail;
        }
    }

    public string? AlarmMessage
    {
        get
        {
            return _state.Display.ReadError?.Message ?? _state.Display.AlarmMessage;
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
        _state.RequestDisplayRefresh();
        NotifyPositionProperties(PositionRefresh.All);
        NotifyDisplayProperties(
            DisplayRefresh.Machine
                | DisplayRefresh.PcbSupply
                | DisplayRefresh.PcbPlacement
                | DisplayRefresh.Conveyor
                | DisplayRefresh.BoltFastening
                | DisplayRefresh.Inspection
                | DisplayRefresh.NgConveyor);
        NotifyProperties(ActivationPropertyNames);
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

    [RelayCommand(CanExecute = nameof(CanOpenPcbPlacementRecovery))]
    private void OpenPcbPlacementRecovery()
    {
        _pcbPlacementRecovery.Open(Application.Current.MainWindow);
    }

    [RelayCommand(CanExecute = nameof(CanOpenBoltFasteningRecovery))]
    private void OpenBoltFasteningRecovery()
    {
        _boltFasteningRecovery.Open(Application.Current.MainWindow);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
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

    [RelayCommand(CanExecute = nameof(CanRaiseCylinders))]
    private Task RaiseCylindersAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => _machine.RaiseCylindersAsync(cancellationToken), cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanHome))]
    private Task HomeAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => _machine.HomeAsync(cancellationToken), cancellationToken);
    }

    [RelayCommand]
    private void StopHome()
    {
        HomeCommand.Cancel();
    }

    private bool CanStart()
    {
        return _state.Display.CanStart;
    }

    private bool CanHome()
    {
        return _state.Display.CanHome;
    }

    private bool CanRaiseCylinders()
    {
        return _state.Display.CanRaiseCylinders;
    }

    private bool CanOpenBoltFasteningRecovery()
    {
        return BoltFasteningRecoveryAvailable;
    }

    private bool CanOpenPcbPlacementRecovery()
    {
        return PcbPlacementRecoveryAvailable;
    }

    private void NotifyCanExecuteChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        RaiseCylindersCommand.NotifyCanExecuteChanged();
        OpenPcbPlacementRecoveryCommand.NotifyCanExecuteChanged();
        OpenBoltFasteningRecoveryCommand.NotifyCanExecuteChanged();
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
        if (!_units.BoltFastening || !_map.FasteningDefined)
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
        if (!_units.Inspection || !_map.InspectionDefined)
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
        var assembly = _boltFasteningWork.Assemblies.FirstOrDefault(
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
        var assembly = _inspectionWork.Assemblies.FirstOrDefault(item => item.HeatSink == bolt.HeatSink);
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

    private static double ZTop(MotionStatus motion)
    {
        return ZTop(motion.Position.Z, motion.ZMinimum, motion.ZMaximum);
    }

    private static double ZTop(double z, double minimum, double maximum)
    {
        var ratio = Math.Clamp((z - minimum) / (maximum - minimum), 0, 1);
        return ZSideViewTop + (ratio * ZSideViewTravel);
    }

    private void OnPcbSupplyMotionChanged(object? _, PropertyChangedEventArgs e)
    {
        OnMotionChanged(e, PositionRefresh.PcbSupply, DisplayRefresh.PcbSupply);
    }

    private void OnPcbPlacementMotionChanged(object? _, PropertyChangedEventArgs e)
    {
        OnMotionChanged(e, PositionRefresh.PcbPlacement, DisplayRefresh.PcbPlacement);
    }

    private void OnBoltFasteningMotionChanged(object? _, PropertyChangedEventArgs e)
    {
        OnMotionChanged(e, PositionRefresh.BoltFastening, DisplayRefresh.BoltFastening);
    }

    private void OnInspectionGantryMotionChanged(object? _, PropertyChangedEventArgs e)
    {
        OnMotionChanged(e, PositionRefresh.Inspection, DisplayRefresh.Inspection);
    }

    private void OnMotionChanged(
        PropertyChangedEventArgs e,
        PositionRefresh positionRefresh,
        DisplayRefresh displayRefresh)
    {
        if (!_active)
        {
            return;
        }

        if (e.PropertyName == nameof(MotionStatus.IsMoving))
        {
            NotifyDisplayProperties(displayRefresh);
            return;
        }

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            NotifyPositionProperties(positionRefresh);
        }
    }

    private void NotifyPositionProperties(PositionRefresh refresh)
    {
        if ((refresh & PositionRefresh.PcbSupply) != 0)
        {
            OnPropertyChanged(nameof(PcbSupplyZTop));
            OnPropertyChanged(nameof(PcbSupplyMapLeft));
            OnPropertyChanged(nameof(PcbSupplyMapTop));
        }

        if ((refresh & PositionRefresh.PcbPlacement) != 0)
        {
            OnPropertyChanged(nameof(PcbPlacementZTop));
            OnPropertyChanged(nameof(PcbPlacementMapLeft));
            OnPropertyChanged(nameof(PcbPlacementMapTop));
        }

        if ((refresh & PositionRefresh.BoltFastening) != 0)
        {
            OnPropertyChanged(nameof(BoltFasteningZTop));
            OnPropertyChanged(nameof(BoltFasteningMapLeft));
            OnPropertyChanged(nameof(BoltFasteningMapTop));
        }

        if ((refresh & PositionRefresh.Inspection) != 0)
        {
            OnPropertyChanged(nameof(InspectionGantryMapLeft));
            OnPropertyChanged(nameof(InspectionGantryMapTop));
        }
    }

    private void OnMachineDisplayChanged()
    {
        NotifyDisplayProperties(
            DisplayRefresh.Machine
                | DisplayRefresh.PcbSupply
                | DisplayRefresh.PcbPlacement
                | DisplayRefresh.BoltFastening
                | DisplayRefresh.Inspection
                | DisplayRefresh.NgConveyor);
    }

    private void OnPcbSupplyChanged()
    {
        NotifyDisplayProperties(DisplayRefresh.PcbSupply);
    }

    private void OnPcbPlacementChanged()
    {
        if (!_active)
        {
            return;
        }

        NotifyDisplayProperties(DisplayRefresh.PcbPlacement | DisplayRefresh.PcbSupply);
    }

    private void OnMainConveyorChanged()
    {
        NotifyDisplayProperties(DisplayRefresh.Conveyor);
    }

    private void OnBoltFasteningChanged()
    {
        NotifyDisplayProperties(DisplayRefresh.BoltFastening);
    }

    private void OnBoltFeederChanged()
    {
        NotifyDisplayProperties(DisplayRefresh.BoltFastening);
    }

    private void OnNgConveyorChanged()
    {
        NotifyDisplayProperties(DisplayRefresh.NgConveyor);
    }

    private void NotifyDisplayProperties(DisplayRefresh refresh)
    {
        if (!_active)
            refresh &= DisplayRefresh.Machine;

        NotifyProperties(refresh);
        if ((refresh & DisplayRefresh.Machine) != 0)
            QueueCommandRefresh();
    }

    private void QueueCommandRefresh()
    {
        // CanExecuteChanged reaches WPF command subscribers directly, unlike bindings.
        if (Interlocked.Exchange(ref _commandRefreshQueued, 1) != 0)
            return;

        Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(
                () =>
                {
                    Interlocked.Exchange(ref _commandRefreshQueued, 0);
                    NotifyCanExecuteChanged();
                }));
    }

    private void NotifyProperties(DisplayRefresh refresh)
    {
        if ((refresh & DisplayRefresh.Machine) != 0)
        {
            NotifyProperties(MachinePropertyNames);
        }

        if ((refresh & DisplayRefresh.PcbSupply) != 0)
        {
            NotifyProperties(PcbSupplyPropertyNames);
        }

        if ((refresh & DisplayRefresh.PcbPlacement) != 0)
        {
            NotifyProperties(PcbPlacementPropertyNames);
        }

        if ((refresh & DisplayRefresh.Conveyor) != 0)
        {
            NotifyProperties(ConveyorPropertyNames);
        }

        if ((refresh & DisplayRefresh.BoltFastening) != 0)
        {
            NotifyProperties(BoltFasteningPropertyNames);
        }

        if ((refresh & DisplayRefresh.Inspection) != 0)
        {
            NotifyProperties(InspectionPropertyNames);
        }

        if ((refresh & DisplayRefresh.NgConveyor) != 0)
        {
            NotifyProperties(NgConveyorPropertyNames);
        }
    }

    private void NotifyProperties(IEnumerable<string> properties)
    {
        foreach (var property in properties)
        {
            OnPropertyChanged(property);
        }
    }
}
