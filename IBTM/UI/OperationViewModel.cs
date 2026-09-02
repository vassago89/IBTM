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
    private sealed record MachineDisplaySnapshot(
        MachineDisplayState DisplayState,
        StartBlockReason StartBlock,
        bool IsHoming,
        bool AutomaticRunning,
        bool ConveyorRunning,
        MainConveyorState ConveyorState,
        bool BufferConflict,
        bool EmergencyStopReleased,
        bool DoorClosed,
        bool AirPressureOk,
        bool AutoMode,
        MachineAlarm Alarm,
        bool ServoPowerOn,
        bool Homed,
        bool CanStart,
        bool CanHome);

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

    private static readonly string[] MachinePropertyNames =
    [
        nameof(MachineDisplayState),
        nameof(StartBlocked),
        nameof(StartBlock),
        nameof(IsHoming),
        nameof(ConveyorRunning),
        nameof(MainConveyorState),
        nameof(CarrierBetweenPlacementAndBolt),
        nameof(CarrierBetweenBoltAndInspection),
        nameof(BufferConflict),
        nameof(EmergencyStopReleased),
        nameof(DoorClosed),
        nameof(AirPressureOk),
        nameof(AutoMode),
        nameof(HasAlarm),
        nameof(Alarm),
        nameof(ServoPowerOn),
        nameof(Homed),
        nameof(PcbPlacementRecoveryAvailable),
        nameof(BoltFasteningRecoveryAvailable),
        nameof(SupplyDisplayState),
        nameof(PlacementDisplayState),
        nameof(BoltDisplayState),
        nameof(InspectionDisplayState),
        nameof(BoltProcessStateVisible),
        nameof(InspectionProcessStateVisible),
    ];

    private static readonly string[] PcbSupplyPropertyNames =
    [
        nameof(PcbSupplyPcbSecured),
        nameof(PcbSupplyIpmFixed),
        nameof(PcbSupplyNestForward),
        nameof(PcbSupplyRotated),
        nameof(Supply),
        nameof(SupplyDisplayState),
    ];

    private static readonly string[] PcbPlacementPropertyNames =
    [
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
        nameof(PcbPlacementProcessState),
        nameof(PcbPlacementTargetHeatSink),
        nameof(PlacementDisplayState),
        nameof(PcbPlacementRecoveryAvailable),
    ];

    private static readonly string[] ConveyorPropertyNames =
    [
        nameof(MainConveyorEntryCarrierDetected),
        nameof(MainConveyorExitCarrierDetected),
        nameof(CarrierBetweenPlacementAndBolt),
        nameof(CarrierBetweenBoltAndInspection),
    ];

    private static readonly string[] BoltFasteningPropertyNames =
    [
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
        nameof(BoltFasteningProcessState),
        nameof(BoltFasteningActiveBolt),
        nameof(BoltTargets),
        nameof(BoltProcessStateVisible),
        nameof(BoltFasteningHeatSink1Result),
        nameof(BoltFasteningHeatSink2Result),
        nameof(BoltDisplayState),
        nameof(BoltFasteningRecoveryAvailable),
    ];

    private static readonly string[] InspectionPropertyNames =
    [
        nameof(InspectionHeatSink1Present),
        nameof(InspectionHeatSink2Present),
        nameof(InspectionCarrierPresent),
        nameof(InspectionStopperUp),
        nameof(InspectionBackupPlateUp),
        nameof(InspectionProcessState),
        nameof(InspectionActiveBolt),
        nameof(InspectionTargets),
        nameof(InspectionProcessStateVisible),
        nameof(InspectionHeatSink1Result),
        nameof(InspectionHeatSink2Result),
        nameof(InspectionDisplayState),
    ];

    private static readonly string[] NgConveyorPropertyNames =
    [
        nameof(NgCarrierGripperClosed),
        nameof(NgCarrierPickupDown),
        nameof(NgCarrierDetected),
        nameof(NgShuttleCarrierDetected),
        nameof(NgConveyorPosition1Occupied),
        nameof(NgConveyorPosition2Occupied),
        nameof(NgConveyorPosition3Occupied),
        nameof(NgCarrierCount),
        nameof(NgAlarmCarrierCount),
        nameof(NgAlarmRequired),
        nameof(NgConveyorRunCommandOn),
        nameof(NgShuttleLift),
        nameof(NgConveyorState),
    ];

    private static readonly string[] ActivationPropertyNames =
    [
        nameof(BoltPickupFeederMapLeft),
        nameof(BoltPickupFeederMapTop),
        nameof(NgConveyorMapOffsetLeft),
        nameof(NgConveyorMapOffsetTop),
        nameof(MainConveyorEnabled),
        nameof(PcbSupplyEnabled),
        nameof(PcbPlacementEnabled),
        nameof(PickupFeederEnabled),
        nameof(ShootingFeederEnabled),
        nameof(BoltFasteningEnabled),
        nameof(SafetyBypass),
    ];

    private const double ZSideViewTop = 7;
    private const double ZSideViewTravel = 33;

    private static readonly (double X, double Y) PcbSupplyPcb1MapAnchor =
        (59, 116);
    private static readonly (double X, double Y) PcbSupplyPcb2MapAnchor =
        (153, 116);
    private static readonly (double X, double Y) PcbSupplyBufferMapAnchor =
        (284, 177);
    private static readonly (double X, double Y) PcbPlacementBufferMapAnchor =
        (282, 195);
    private static readonly (double X, double Y) PcbPlacementHeatSink1MapAnchor =
        (220, 399);
    private static readonly (double X, double Y) PcbPlacementHeatSink2MapAnchor =
        (348, 399);
    private static readonly (double X, double Y) BoltGantryFallbackMapPosition =
        (18, 390);
    private static readonly (double X, double Y) BoltShootingUpperLeftMapAnchor =
        (-44.5, 355);
    private static readonly (double X, double Y) BoltShootingLowerRightMapAnchor =
        (187.5, 403);
    private static readonly (double X, double Y) BoltPickupUpperLeftMapAnchor =
        (6.5, 355);
    private static readonly (double X, double Y) BoltPickupToolMapOffset =
        (37.5, 101);
    private static readonly (double X, double Y) BoltShootingToolMapOffset =
        (88.5, 101);
    private static readonly (double X, double Y) BoltTargetMapOrigin =
        (44, 456);
    private static readonly (double X, double Y) BoltPickupFeederMapOffset =
        (-50, -34);
    private static readonly (double X, double Y) InspectionFallbackMapPosition =
        (-40, 390);
    private static readonly (double X, double Y) InspectionUpperLeftMapAnchor =
        (6, 387);
    private static readonly (double X, double Y) InspectionLowerRightMapAnchor =
        (238, 435);
    private static readonly (double X, double Y) InspectionPickupMapAnchor =
        (80, 411);
    private static readonly (double X, double Y) InspectionCameraMapOffset =
        (40, 69);
    private static readonly (double X, double Y) InspectionTargetMapOrigin =
        (46, 456);
    private static readonly (double X, double Y) NgGripperMapOffset =
        (82, 69);
    private static readonly (double X, double Y) NgConveyorPosition3MapAnchor =
        (243, 555);
    private static readonly (double X, double Y) NgConveyorFallbackMapOffset =
        (0, 0);

    private readonly MachineState _state;
    private readonly MachineController _machine;
    private readonly UnitSettings _units;
    private readonly MachineOptions _options;
    private readonly PcbPlacementWork _pcbPlacementWork;
    private readonly BoltFasteningWork _boltFasteningWork;
    private readonly InspectionWork _inspectionWork;
    private readonly PcbPlacementProcess _pcbPlacementProcess;
    private readonly BoltFasteningProcess _boltFasteningProcess;
    private readonly InspectionProcess _inspectionProcess;
    private readonly Recipe _recipe;
    private readonly PcbSupplySettings _pcbSupplySettings;
    private readonly PcbPlacementHandlerSettings _pcbPlacementSettings;
    private readonly BoltFasteningSettings _boltFasteningSettings;
    private readonly CarrierReferenceSettings _carrierReference;
    private readonly InspectionGantrySettings _inspectionGantrySettings;
    private readonly NgConveyorSettings _ngConveyorSettings;
    private readonly MainConveyor _conveyor;
    private readonly BufferStage _buffer;
    private readonly PickupBoltFeeder _pickupFeeder;
    private readonly ShootingBoltFeeder _shootingFeeder;
    private readonly NgConveyorLine _ngConveyor;
    private readonly NgCarrierTransfer _ngTransfer;
    private readonly StartPreparationPlan _startPreparations;
    private MachineDisplaySnapshot _machineDisplay = null!;
    private PcbPlacementState _pcbPlacementProcessState;
    private HeatSinkSlot? _pcbPlacementTargetHeatSink;
    private BoltFasteningProcessState _boltFasteningProcessState;
    private BoltPoint? _boltFasteningActiveBolt;
    private InspectionProcessState _inspectionProcessState;
    private BoltPoint? _inspectionActiveBolt;
    private IReadOnlyList<BoltTargetView> _boltTargets = [];
    private IReadOnlyList<BoltTargetView> _inspectionTargets = [];
    private volatile bool _positionUpdatesActive;
    private int _pendingPositionRefresh;
    private int _positionRefreshQueued;
    private int _pendingDisplayRefresh;
    private int _displayRefreshQueued;

    public OperationViewModel(
        MachineState state,
        MachineController machine,
        UnitSettings units,
        MachineOptions options,
        PcbPlacementWork pcbPlacementWork,
        BoltFasteningWork boltFasteningWork,
        InspectionWork inspectionWork,
        PcbPlacementProcess pcbPlacementProcess,
        BoltFasteningProcess boltFasteningProcess,
        InspectionProcess inspectionProcess,
        Recipe recipe,
        PcbSupplySettings pcbSupplySettings,
        PcbPlacementHandlerSettings pcbPlacementSettings,
        BoltFasteningSettings boltFasteningSettings,
        CarrierReferenceSettings carrierReference,
        InspectionGantrySettings inspectionGantrySettings,
        NgConveyorSettings ngConveyorSettings,
        MainConveyor conveyor,
        BufferStage buffer,
        PickupBoltFeeder pickupFeeder,
        ShootingBoltFeeder shootingFeeder,
        NgConveyorLine ngConveyor,
        NgCarrierTransfer ngTransfer,
        StartPreparationPlan startPreparations,
        PcbSupplyHandler supply,
        PcbPlacementHandler placement,
        BoltFasteningStation fastening,
        InspectionGantry inspectionGantry)
    {
        _state = state;
        _machine = machine;
        _units = units;
        _options = options;
        _pcbPlacementWork = pcbPlacementWork;
        _boltFasteningWork = boltFasteningWork;
        _inspectionWork = inspectionWork;
        _pcbPlacementProcess = pcbPlacementProcess;
        _boltFasteningProcess = boltFasteningProcess;
        _inspectionProcess = inspectionProcess;
        _recipe = recipe;
        _pcbSupplySettings = pcbSupplySettings;
        _pcbPlacementSettings = pcbPlacementSettings;
        _boltFasteningSettings = boltFasteningSettings;
        _carrierReference = carrierReference;
        _inspectionGantrySettings = inspectionGantrySettings;
        _ngConveyorSettings = ngConveyorSettings;
        _ngConveyor = ngConveyor;
        _startPreparations = startPreparations;
        _conveyor = conveyor;
        _buffer = buffer;
        _pickupFeeder = pickupFeeder;
        _shootingFeeder = shootingFeeder;
        _ngTransfer = ngTransfer;
        Supply = supply;
        Placement = placement;
        Fastening = fastening;
        InspectionGantry = inspectionGantry;

        _machineDisplay = CreateMachineDisplaySnapshot();
        RefreshPcbPlacementDisplay();
        RefreshBoltFasteningDisplay();
        RefreshInspectionDisplay();

        supply.Motion.PropertyChanged += OnPcbSupplyMotionChanged;
        supply.Changed += OnPcbSupplyChanged;
        placement.Motion.PropertyChanged += OnPcbPlacementMotionChanged;
        placement.Changed += OnPcbPlacementHardwareChanged;
        fastening.Motion.PropertyChanged += OnBoltFasteningMotionChanged;
        fastening.Changed += OnBoltFasteningHardwareChanged;
        inspectionGantry.Motion.PropertyChanged += OnInspectionGantryMotionChanged;
        buffer.StateChanged += OnPcbBufferChanged;
        conveyor.Changed += OnMainConveyorChanged;
        pickupFeeder.Changed += OnBoltFasteningHardwareChanged;
        shootingFeeder.Changed += OnBoltFasteningHardwareChanged;
        pcbPlacementWork.Changed += OnPcbPlacementWorkChanged;
        boltFasteningWork.Changed += OnBoltFasteningWorkChanged;
        inspectionWork.Changed += OnInspectionWorkChanged;
        ngTransfer.Changed += OnNgTransferChanged;
        ngConveyor.Changed += OnNgConveyorChanged;
        state.Changed += OnMachineStateChanged;
    }

    public PcbSupplyHandler Supply { get; }
    public PcbPlacementHandler Placement { get; }
    public BoltFasteningStation Fastening { get; }
    public InspectionGantry InspectionGantry { get; }
    public double PcbSupplyZTop => ZTop(Supply.Motion);
    public double PcbPlacementZTop => ZTop(Placement.Motion);
    public double BoltFasteningZTop => ZTop(Fastening.Motion);

    public double PcbSupplyMapLeft => MapPcbSupply().X;
    public double PcbSupplyMapTop => MapPcbSupply().Y;
    public double PcbPlacementMapLeft => MapPcbPlacement().X;
    public double PcbPlacementMapTop => MapPcbPlacement().Y;
    public double BoltFasteningMapLeft => MapBoltFastening().X;
    public double BoltFasteningMapTop => MapBoltFastening().Y;
    public double BoltPickupFeederMapLeft =>
        BoltPickupCenter().X + BoltPickupFeederMapOffset.X;
    public double BoltPickupFeederMapTop =>
        BoltPickupCenter().Y + BoltPickupFeederMapOffset.Y;
    public double InspectionGantryMapLeft => MapInspectionGantry().X;
    public double InspectionGantryMapTop => MapInspectionGantry().Y;
    public double NgConveyorMapOffsetLeft => MapNgConveyor().X;
    public double NgConveyorMapOffsetTop => MapNgConveyor().Y;
    private bool PcbSupplyPcbDetected =>
        Supply.Pcb != PcbSupplyPcbState.None;
    public bool PcbSupplyPcbSecured =>
        Supply.Pcb == PcbSupplyPcbState.Secured;
    private bool PcbPlacementPcbDetected =>
        Placement.Pcb != PlacementPcbState.None;
    public bool PcbPlacementPcbSecured =>
        Placement.PcbSecured;
    public bool PcbPlacementHandlerDown =>
        Placement.Handler == PlacementCylinderState.Down;
    public bool PcbPlacementIpmDown =>
        Placement.Ipm == PlacementCylinderState.Down;
    public bool PcbSupplyIpmFixed =>
        Supply.IpmFixer == PcbSupplyCylinderState.Forward;
    public bool PcbSupplyNestForward =>
        Supply.Nest == PcbSupplyCylinderState.Forward;
    public bool PcbPlacementIpmGripperClosed =>
        Placement.Gripper == PlacementGripperState.Closed;
    public bool PcbPlacementStopperUp =>
        _pcbPlacementWork.Stopper == StationCylinderState.Up;
    public bool PcbPlacementBackupPlateUp =>
        _pcbPlacementWork.BackupPlate == StationCylinderState.Up;
    public bool PcbSupplyRotated =>
        Supply.Rotation == PcbSupplyRotationState.Rotated;
    public bool PcbBufferPcbPresent =>
        _buffer.PcbPresent;
    public bool PcbPlacementHeatSink1Present =>
        _pcbPlacementWork.HeatSink1Present;
    public bool PcbPlacementHeatSink2Present =>
        _pcbPlacementWork.HeatSink2Present;
    public bool PcbPlacementCarrierPresent =>
        _pcbPlacementWork.CarrierPresent;
    public bool MainConveyorEntryCarrierDetected =>
        _conveyor.EntryCarrierDetected;
    public bool MainConveyorExitCarrierDetected =>
        _conveyor.ExitCarrierDetected;
    public bool BoltFasteningHeatSink1Present =>
        _boltFasteningWork.HeatSink1Present;
    public bool BoltFasteningHeatSink2Present =>
        _boltFasteningWork.HeatSink2Present;
    public bool BoltFasteningCarrierPresent =>
        _boltFasteningWork.CarrierPresent;
    public bool BoltFasteningStopperUp =>
        _boltFasteningWork.Stopper == StationCylinderState.Up;
    public bool BoltFasteningBackupPlateUp =>
        _boltFasteningWork.BackupPlate == StationCylinderState.Up;
    public bool InspectionHeatSink1Present =>
        _inspectionWork.HeatSink1Present;
    public bool InspectionHeatSink2Present =>
        _inspectionWork.HeatSink2Present;
    public bool InspectionCarrierPresent =>
        _inspectionWork.CarrierPresent;
    public bool InspectionStopperUp =>
        _inspectionWork.Stopper == StationCylinderState.Up;
    public bool InspectionBackupPlateUp =>
        _inspectionWork.BackupPlate == StationCylinderState.Up;
    public bool PickupHeadDown =>
        Fastening.PickupHead == BoltCylinderState.Down;
    public bool ShootingHeadDown =>
        Fastening.ShootingHead == BoltCylinderState.Down;
    public bool PickupFeederBoltDetected =>
        _pickupFeeder.State == BoltFeederState.BoltReady;
    public bool ShootingFeederBoltDetected =>
        _shootingFeeder.State == BoltFeederState.BoltReady;
    public bool NgCarrierGripperClosed =>
        _ngTransfer.Gripper == NgTransferGripperState.Closed;
    public bool NgCarrierPickupDown =>
        _ngTransfer.Lift == NgTransferLiftState.Down;
    public bool NgCarrierDetected =>
        _ngTransfer.CarrierDetected;
    public bool NgShuttleCarrierDetected =>
        _ngConveyor.ShuttleCarrierDetected;
    public bool NgConveyorPosition1Occupied =>
        _ngConveyor.Position1Occupied;
    public bool NgConveyorPosition2Occupied =>
        _ngConveyor.Position2Occupied;
    public bool NgConveyorPosition3Occupied =>
        _ngConveyor.Position3Occupied;
    public int NgCarrierCount => _ngConveyor.CarrierCount;
    public int NgAlarmCarrierCount => _ngConveyor.AlarmCarrierCount;
    public bool NgAlarmRequired => _ngConveyor.AlarmRequired;
    public bool NgConveyorRunCommandOn => _ngConveyor.RunCommandOn;
    public NgShuttleLiftState NgShuttleLift => _ngConveyor.ShuttleLift;
    public NgConveyorState NgConveyorState => _ngConveyor.State;
    public bool IsHoming => _machineDisplay.IsHoming;
    public bool ConveyorRunning => _machineDisplay.ConveyorRunning;
    public MainConveyorState MainConveyorState =>
        _machineDisplay.ConveyorState;
    public bool CarrierBetweenPlacementAndBolt =>
        !PcbPlacementCarrierPresent
        && !BoltFasteningCarrierPresent
        && MainConveyorState is
                MainConveyorState.PcbPlacementCarrierBetweenStations
                or MainConveyorState.MovingPcbPlacementToBoltFastening;
    public bool CarrierBetweenBoltAndInspection =>
        !BoltFasteningCarrierPresent
        && !InspectionCarrierPresent
        && MainConveyorState is
                MainConveyorState.BoltFasteningCarrierBetweenStations
                or MainConveyorState.MovingBoltFasteningToInspection;
    public bool BufferConflict => _machineDisplay.BufferConflict;
    public bool MainConveyorEnabled => _units.MainConveyor;
    public bool PcbSupplyEnabled => _units.PcbSupply;
    public bool PcbPlacementEnabled => _units.PcbPlacement;
    public bool PickupFeederEnabled => _units.PickupBoltFeeder;
    public bool ShootingFeederEnabled => _units.ShootingBoltFeeder;
    public bool BoltFasteningEnabled => _units.BoltFastening;
    public bool PcbPlacementHeatSink1Completed =>
        PcbPlacementEnabled
        && PcbPlacementCarrierPresent
        && PcbPlacementHeatSink1Present
        && HasAssembly(_pcbPlacementWork, HeatSinkSlot.HeatSink1);
    public bool PcbPlacementHeatSink2Completed =>
        PcbPlacementEnabled
        && PcbPlacementCarrierPresent
        && PcbPlacementHeatSink2Present
        && HasAssembly(_pcbPlacementWork, HeatSinkSlot.HeatSink2);
    public PcbPlacementState PcbPlacementProcessState =>
        _pcbPlacementProcessState;
    public HeatSinkSlot? PcbPlacementTargetHeatSink =>
        _pcbPlacementTargetHeatSink;
    public BoltFasteningProcessState BoltFasteningProcessState =>
        _boltFasteningProcessState;
    public InspectionProcessState InspectionProcessState =>
        _inspectionProcessState;
    public BoltPoint? BoltFasteningActiveBolt =>
        BoltProcessStateVisible
            ? _boltFasteningActiveBolt
            : null;
    public BoltPoint? InspectionActiveBolt =>
        InspectionProcessStateVisible
            ? _inspectionActiveBolt
            : null;
    public IReadOnlyList<BoltTargetView> BoltTargets => _boltTargets;
    public IReadOnlyList<BoltTargetView> InspectionTargets => _inspectionTargets;
    public AssemblyResult BoltFasteningHeatSink1Result =>
        Result(_boltFasteningWork, HeatSinkSlot.HeatSink1, inspection: false);
    public AssemblyResult BoltFasteningHeatSink2Result =>
        Result(_boltFasteningWork, HeatSinkSlot.HeatSink2, inspection: false);
    public AssemblyResult InspectionHeatSink1Result =>
        Result(_inspectionWork, HeatSinkSlot.HeatSink1, inspection: true);
    public AssemblyResult InspectionHeatSink2Result =>
        Result(_inspectionWork, HeatSinkSlot.HeatSink2, inspection: true);
    public bool EmergencyStopReleased => _machineDisplay.EmergencyStopReleased;
    public bool DoorClosed => _machineDisplay.DoorClosed;
    public bool AirPressureOk => _machineDisplay.AirPressureOk;
    public bool AutoMode => _machineDisplay.AutoMode;
    public bool HasAlarm => _machineDisplay.Alarm != MachineAlarm.None;
    public MachineAlarm Alarm => _machineDisplay.Alarm;
    public bool ServoPowerOn => _machineDisplay.ServoPowerOn;
    public bool Homed => _machineDisplay.Homed;
    public bool SafetyBypass =>
        !_options.UseEmergencyStop
        || !_options.UseDoorInterlock
        || !_options.UseAirPressureInterlock;
    public bool PcbPlacementRecoveryAvailable =>
        _startPreparations.CanOpen(
            StartPreparationType.PcbPlacementRecovery);
    public bool BoltFasteningRecoveryAvailable =>
        _startPreparations.CanOpen(
            StartPreparationType.BoltFasteningRecovery);

    public void Activate()
    {
        _machineDisplay = CreateMachineDisplaySnapshot();
        RefreshPcbPlacementDisplay();
        RefreshBoltFasteningDisplay();
        RefreshInspectionDisplay();
        _positionUpdatesActive = true;
        QueuePositionRefresh(PositionRefresh.All);
        QueueDisplayRefresh(
            DisplayRefresh.Machine
            | DisplayRefresh.PcbSupply
            | DisplayRefresh.PcbPlacement
            | DisplayRefresh.Conveyor
            | DisplayRefresh.BoltFastening
            | DisplayRefresh.Inspection
            | DisplayRefresh.NgConveyor);
        NotifyProperties(ActivationPropertyNames);
    }

    public void Deactivate() => _positionUpdatesActive = false;

    [RelayCommand(CanExecute = nameof(CanOpenPcbPlacementRecovery))]
    private void OpenPcbPlacementRecovery() => _startPreparations.Open(
        StartPreparationType.PcbPlacementRecovery,
        Application.Current.MainWindow);

    [RelayCommand(CanExecute = nameof(CanOpenBoltFasteningRecovery))]
    private void OpenBoltFasteningRecovery() => _startPreparations.Open(
        StartPreparationType.BoltFasteningRecovery,
        Application.Current.MainWindow);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_startPreparations.Prepare(
                Application.Current.MainWindow))
        {
            return;
        }

        await Task.Run(
            () => _machine.StartAsync(cancellationToken),
            cancellationToken);
    }

    [RelayCommand]
    private void Stop()
    {
        StartCommand.Cancel();
        _machine.Stop();
    }

    [RelayCommand(CanExecute = nameof(CanHome))]
    private Task HomeAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () => _machine.HomeAsync(cancellationToken),
            cancellationToken);

    [RelayCommand(CanExecute = nameof(CanStopHome))]
    private void StopHome() => HomeCommand.Cancel();

    private bool CanStart() => _machineDisplay.CanStart;
    private bool CanHome() => _machineDisplay.CanHome;
    private bool CanStopHome() => _machineDisplay.IsHoming;
    private bool CanOpenBoltFasteningRecovery() => BoltFasteningRecoveryAvailable;
    private bool CanOpenPcbPlacementRecovery() =>
        PcbPlacementRecoveryAvailable;

    private void NotifyCanExecuteChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        StopHomeCommand.NotifyCanExecuteChanged();
        OpenPcbPlacementRecoveryCommand.NotifyCanExecuteChanged();
        OpenBoltFasteningRecoveryCommand.NotifyCanExecuteChanged();
    }

    private static bool HasAssembly(
        StationWork work,
        HeatSinkSlot heatSink) =>
        work.Assemblies.Any(assembly => assembly.HeatSink == heatSink);

    private static AssemblyResult Result(
        StationWork work,
        HeatSinkSlot heatSink,
        bool inspection)
    {
        var assembly = work.Assemblies.FirstOrDefault(
            item => item.HeatSink == heatSink);
        return assembly is null
            ? AssemblyResult.Pending
            : inspection
                ? assembly.InspectionResult
                : assembly.FasteningResult;
    }

    private IReadOnlyList<BoltTargetView> CreateFasteningTargets()
    {
        var active = BoltFasteningActiveBolt;
        return CreateTargets(
            BoltFasteningHeatSink1Present,
            BoltFasteningHeatSink2Present,
            BoltFasteningTargetPosition,
            bolt => ReferenceEquals(bolt, active)
                ? BoltTargetState.Active
                : FasteningTargetState(bolt));
    }

    private IReadOnlyList<BoltTargetView> CreateInspectionTargets()
    {
        var active = InspectionActiveBolt;
        return CreateTargets(
            InspectionHeatSink1Present,
            InspectionHeatSink2Present,
            InspectionTargetPosition,
            bolt => ReferenceEquals(bolt, active)
                ? BoltTargetState.Active
                : InspectionTargetState(bolt));
    }

    private IReadOnlyList<BoltTargetView> CreateTargets(
        bool heatSink1Present,
        bool heatSink2Present,
        Func<BoltPoint, (double X, double Y)> position,
        Func<BoltPoint, BoltTargetState> state)
    {
        var bolts = _recipe.BoltFastening.BoltPoints
            .Where(bolt => bolt.X is not null
                           && bolt.Y is not null
                           && ((bolt.HeatSink == HeatSinkSlot.HeatSink1
                                && heatSink1Present)
                               || (bolt.HeatSink == HeatSinkSlot.HeatSink2
                                   && heatSink2Present)))
            .ToArray();
        if (bolts.Length == 0)
        {
            return [];
        }

        return bolts.Select(bolt =>
            {
                var target = position(bolt);
                return new BoltTargetView(
                    bolt.Number,
                    bolt.Head,
                    target.X,
                    target.Y,
                    state(bolt));
            })
            .ToArray();
    }

    private BoltTargetState FasteningTargetState(BoltPoint bolt)
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

        var seating = ResultState(
            assembly.IpmSeatingResults,
            bolt.Number);
        var final = ResultState(assembly.IpmFinalResults, bolt.Number);
        return seating == BoltTargetState.Ng || final == BoltTargetState.Ng
            ? BoltTargetState.Ng
            : final;
    }

    private BoltTargetState InspectionTargetState(BoltPoint bolt)
    {
        var assembly = _inspectionWork.Assemblies.FirstOrDefault(
            item => item.HeatSink == bolt.HeatSink);
        if (assembly is null
            || !assembly.BoltPresenceResults.TryGetValue(
                bolt.Number,
                out var present))
        {
            return BoltTargetState.Pending;
        }

        return present ? BoltTargetState.Ok : BoltTargetState.Ng;
    }

    private static BoltTargetState ResultState(
        IReadOnlyDictionary<int, BoltResult> results,
        int number) =>
        results.TryGetValue(number, out var result)
            ? result.Success
                ? BoltTargetState.Ok
                : BoltTargetState.Ng
            : BoltTargetState.Pending;

    private (double X, double Y) MapPcbSupply()
    {
        var current = Supply.Motion.Position;
        var buffer = _pcbSupplySettings.BufferHandoffPosition;
        return MapFromThreePoints(
            current.X,
            current.Y,
            (_recipe.PcbSupply.Pcb1PickPosition.X,
                _pcbSupplySettings.CarrierY),
            (_recipe.PcbSupply.Pcb2PickPosition.X,
                _pcbSupplySettings.CarrierY),
            (buffer.X, buffer.Y),
            PcbSupplyPcb1MapAnchor,
            PcbSupplyPcb2MapAnchor,
            PcbSupplyBufferMapAnchor);
    }

    private (double X, double Y) MapPcbPlacement()
    {
        var current = Placement.Motion.Position;
        return MapFromThreePoints(
            current.X,
            current.Y,
            (_pcbPlacementSettings.BufferHandoffPosition.X,
                _pcbPlacementSettings.BufferHandoffPosition.Y),
            (_recipe.PcbPlacement.HeatSink1PcbPlacementPosition.X,
                _recipe.PcbPlacement.HeatSink1PcbPlacementPosition.Y),
            (_recipe.PcbPlacement.HeatSink2PcbPlacementPosition.X,
                _recipe.PcbPlacement.HeatSink2PcbPlacementPosition.Y),
            PcbPlacementBufferMapAnchor,
            PcbPlacementHeatSink1MapAnchor,
            PcbPlacementHeatSink2MapAnchor);
    }

    private (double X, double Y) MapBoltFastening()
    {
        var current = Fastening.Motion.Position;
        return MapBoltGantry(current.X, current.Y);
    }

    private (double X, double Y) MapBoltGantry(double x, double y)
    {
        var shooting = _boltFasteningSettings.ShootingHead;
        var pickup = _boltFasteningSettings.PickupHead;
        if (shooting.UpperLeftLocatingPin is not { } shootingUpperLeft
            || shooting.LowerRightLocatingPin is not { } shootingLowerRight
            || pickup.UpperLeftLocatingPin is not { } pickupUpperLeft)
        {
            return BoltGantryFallbackMapPosition;
        }

        return MapFromThreePoints(
            x,
            y,
            (shootingUpperLeft.X, shootingUpperLeft.Y),
            (shootingLowerRight.X, shootingLowerRight.Y),
            (pickupUpperLeft.X, pickupUpperLeft.Y),
            BoltShootingUpperLeftMapAnchor,
            BoltShootingLowerRightMapAnchor,
            BoltPickupUpperLeftMapAnchor);
    }

    private (double X, double Y) BoltPickupCenter()
    {
        var pickup = _boltFasteningSettings.PickupPosition;
        var root = MapBoltGantry(pickup.X, pickup.Y);
        return (
            root.X + BoltPickupToolMapOffset.X,
            root.Y + BoltPickupToolMapOffset.Y);
    }

    private (double X, double Y) BoltFasteningTargetPosition(
        BoltPoint bolt)
    {
        var target = _boltFasteningSettings.GetBoltPosition(
            bolt,
            _carrierReference);
        var root = MapBoltGantry(target.X, target.Y);
        var toolOffset = bolt.Head == FasteningHead.Pickup
            ? BoltPickupToolMapOffset
            : BoltShootingToolMapOffset;
        return (
            root.X + toolOffset.X - BoltTargetMapOrigin.X,
            root.Y + toolOffset.Y - BoltTargetMapOrigin.Y);
    }

    private (double X, double Y) MapInspectionGantry()
    {
        var current = InspectionGantry.Motion.Position;
        return MapInspectionGantry(current.X, current.Y);
    }

    private (double X, double Y) MapInspectionGantry(double x, double y)
    {
        if (_carrierReference.UpperLeftLocatingPin is not { } upperLeft
            || _carrierReference.LowerRightLocatingPin is not { } lowerRight)
        {
            return InspectionFallbackMapPosition;
        }

        var pickup = _ngConveyorSettings.CarrierPickupPosition;
        return MapFromThreePoints(
            x,
            y,
            (upperLeft.X, upperLeft.Y),
            (lowerRight.X, lowerRight.Y),
            (pickup.X, pickup.Y),
            InspectionUpperLeftMapAnchor,
            InspectionLowerRightMapAnchor,
            InspectionPickupMapAnchor);
    }

    private (double X, double Y) InspectionTargetPosition(BoltPoint bolt)
    {
        var target = _inspectionGantrySettings.GetBoltPosition(
            bolt,
            _carrierReference);
        var root = MapInspectionGantry(target.X, target.Y);
        return (
            root.X + InspectionCameraMapOffset.X
            - InspectionTargetMapOrigin.X,
            root.Y + InspectionCameraMapOffset.Y
            - InspectionTargetMapOrigin.Y);
    }

    private (double X, double Y) MapNgConveyor()
    {
        if (_carrierReference.UpperLeftLocatingPin is null
            || _carrierReference.LowerRightLocatingPin is null)
        {
            return NgConveyorFallbackMapOffset;
        }

        var shuttle = _ngConveyorSettings.ShuttlePlacePosition;
        var root = MapInspectionGantry(shuttle.X, shuttle.Y);
        return (
            root.X + NgGripperMapOffset.X
            - NgConveyorPosition3MapAnchor.X,
            root.Y + NgGripperMapOffset.Y
            - NgConveyorPosition3MapAnchor.Y);
    }

    private static (double X, double Y) MapFromThreePoints(
        double x,
        double y,
        (double X, double Y) source1,
        (double X, double Y) source2,
        (double X, double Y) source3,
        (double X, double Y) target1,
        (double X, double Y) target2,
        (double X, double Y) target3)
    {
        var denominator = ((source2.Y - source3.Y)
                           * (source1.X - source3.X))
                          + ((source3.X - source2.X)
                             * (source1.Y - source3.Y));
        if (denominator == 0)
        {
            return target1;
        }

        var first = (((source2.Y - source3.Y) * (x - source3.X))
                     + ((source3.X - source2.X) * (y - source3.Y)))
                    / denominator;
        var second = (((source3.Y - source1.Y) * (x - source3.X))
                      + ((source1.X - source3.X) * (y - source3.Y)))
                     / denominator;
        var third = 1 - first - second;
        return (
            (first * target1.X)
            + (second * target2.X)
            + (third * target3.X),
            (first * target1.Y)
            + (second * target2.Y)
            + (third * target3.Y));
    }

    private static double ZTop(MotionStatus motion)
        => ZTop(
            motion.Position.Z,
            motion.ZMinimum,
            motion.ZMaximum);

    private static double ZTop(double z, double minimum, double maximum)
    {
        var ratio = Math.Clamp((z - minimum) / (maximum - minimum), 0, 1);
        return ZSideViewTop + (ratio * ZSideViewTravel);
    }

    private void OnPcbSupplyMotionChanged(
        object? _,
        PropertyChangedEventArgs e) =>
        OnMotionChanged(
            e,
            PositionRefresh.PcbSupply,
            DisplayRefresh.PcbSupply);

    private void OnPcbPlacementMotionChanged(
        object? _,
        PropertyChangedEventArgs e) =>
        OnMotionChanged(
            e,
            PositionRefresh.PcbPlacement,
            DisplayRefresh.PcbPlacement,
            RefreshPcbPlacementDisplay);

    private void OnBoltFasteningMotionChanged(
        object? _,
        PropertyChangedEventArgs e) =>
        OnMotionChanged(
            e,
            PositionRefresh.BoltFastening,
            DisplayRefresh.BoltFastening,
            RefreshBoltFasteningDisplay);

    private void OnInspectionGantryMotionChanged(
        object? _,
        PropertyChangedEventArgs e) =>
        OnMotionChanged(
            e,
            PositionRefresh.Inspection,
            DisplayRefresh.Inspection,
            RefreshInspectionDisplay);

    private void OnMotionChanged(
        PropertyChangedEventArgs e,
        PositionRefresh positionRefresh,
        DisplayRefresh displayRefresh,
        Action? refreshDisplay = null)
    {
        if (e.PropertyName == nameof(MotionStatus.IsMoving))
        {
            refreshDisplay?.Invoke();
            QueueDisplayRefresh(displayRefresh);
            return;
        }

        if (_positionUpdatesActive
            && e.PropertyName == nameof(MotionStatus.Position))
        {
            QueuePositionRefresh(positionRefresh);
        }
    }

    private void QueuePositionRefresh(PositionRefresh refresh)
    {
        Interlocked.Or(ref _pendingPositionRefresh, (int)refresh);
        if (Interlocked.Exchange(ref _positionRefreshQueued, 1) != 0)
        {
            return;
        }

        RunOnUi(() =>
        {
            Interlocked.Exchange(ref _positionRefreshQueued, 0);
            var pending = (PositionRefresh)Interlocked.Exchange(
                ref _pendingPositionRefresh,
                0);

            if ((pending & PositionRefresh.PcbSupply) != 0)
            {
                OnPropertyChanged(nameof(PcbSupplyZTop));
                OnPropertyChanged(nameof(PcbSupplyMapLeft));
                OnPropertyChanged(nameof(PcbSupplyMapTop));
            }

            if ((pending & PositionRefresh.PcbPlacement) != 0)
            {
                OnPropertyChanged(nameof(PcbPlacementZTop));
                OnPropertyChanged(nameof(PcbPlacementMapLeft));
                OnPropertyChanged(nameof(PcbPlacementMapTop));
            }

            if ((pending & PositionRefresh.BoltFastening) != 0)
            {
                OnPropertyChanged(nameof(BoltFasteningZTop));
                OnPropertyChanged(nameof(BoltFasteningMapLeft));
                OnPropertyChanged(nameof(BoltFasteningMapTop));
            }

            if ((pending & PositionRefresh.Inspection) != 0)
            {
                OnPropertyChanged(nameof(InspectionGantryMapLeft));
                OnPropertyChanged(nameof(InspectionGantryMapTop));
            }
        }, DispatcherPriority.Background);
    }

    private void OnMachineStateChanged()
    {
        var automaticRunning = _machineDisplay.AutomaticRunning;
        _machineDisplay = CreateMachineDisplaySnapshot();
        var refresh = DisplayRefresh.Machine;
        if (automaticRunning != _machineDisplay.AutomaticRunning)
        {
            RefreshBoltFasteningDisplay();
            RefreshInspectionDisplay();
            refresh |= DisplayRefresh.BoltFastening
                       | DisplayRefresh.Inspection;
        }

        QueueDisplayRefresh(refresh);
    }

    private void OnPcbSupplyChanged() =>
        QueueDisplayRefresh(DisplayRefresh.PcbSupply);

    private void OnPcbPlacementHardwareChanged()
    {
        RefreshPcbPlacementDisplay();
        QueueDisplayRefresh(DisplayRefresh.PcbPlacement);
    }

    private void OnPcbBufferChanged()
    {
        RefreshPcbPlacementDisplay();
        QueueDisplayRefresh(DisplayRefresh.PcbPlacement);
    }

    private void OnMainConveyorChanged() =>
        QueueDisplayRefresh(DisplayRefresh.Conveyor);

    private void OnPcbPlacementWorkChanged()
    {
        RefreshPcbPlacementDisplay();
        QueueDisplayRefresh(DisplayRefresh.PcbPlacement);
    }

    private void OnBoltFasteningWorkChanged()
    {
        RefreshBoltFasteningDisplay();
        QueueDisplayRefresh(DisplayRefresh.BoltFastening);
    }

    private void OnBoltFasteningHardwareChanged()
    {
        RefreshBoltFasteningDisplay();
        QueueDisplayRefresh(DisplayRefresh.BoltFastening);
    }

    private void OnInspectionWorkChanged()
    {
        RefreshInspectionDisplay();
        QueueDisplayRefresh(DisplayRefresh.Inspection);
    }

    private void OnNgConveyorChanged() =>
        QueueDisplayRefresh(DisplayRefresh.NgConveyor);

    private void OnNgTransferChanged() =>
        QueueDisplayRefresh(
            DisplayRefresh.NgConveyor | DisplayRefresh.Inspection);

    private void RefreshPcbPlacementDisplay()
    {
        _pcbPlacementProcessState =
            _pcbPlacementProcess.State(_recipe.PcbPlacement);
        _pcbPlacementTargetHeatSink = _pcbPlacementProcess.TargetHeatSink;
    }

    private void RefreshBoltFasteningDisplay()
    {
        _boltFasteningProcessState =
            _boltFasteningProcess.State(_recipe.BoltFastening);
        _boltFasteningActiveBolt =
            _boltFasteningProcess.ActiveBolt(_recipe.BoltFastening);
        _boltTargets = CreateFasteningTargets();
    }

    private void RefreshInspectionDisplay()
    {
        var bolts = _recipe.BoltFastening.BoltPoints;
        _inspectionProcessState = _inspectionProcess.State(bolts);
        _inspectionActiveBolt = _inspectionProcess.ActiveBolt(bolts);
        _inspectionTargets = CreateInspectionTargets();
    }

    private void QueueDisplayRefresh(DisplayRefresh refresh)
    {
        if (refresh == 0)
        {
            return;
        }

        Interlocked.Or(ref _pendingDisplayRefresh, (int)refresh);
        if (Interlocked.Exchange(ref _displayRefreshQueued, 1) != 0)
        {
            return;
        }

        RunOnUi(() =>
        {
            Interlocked.Exchange(ref _displayRefreshQueued, 0);
            var pending = (DisplayRefresh)Interlocked.Exchange(
                ref _pendingDisplayRefresh,
                0);

            NotifyProperties(pending);
            if ((pending & DisplayRefresh.Machine) != 0)
            {
                NotifyCanExecuteChanged();
            }
        });
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

    private MachineDisplaySnapshot CreateMachineDisplaySnapshot()
    {
        var alarm = _state.Alarm;
        var readiness = _state.MotionReadiness;
        var emergencyStopReleased = _state.EmergencyStopReleased;
        var doorClosed = _state.DoorClosed;
        var airPressureOk = _state.AirPressureOk;
        var servoMainContactorOn = _state.ServoMainContactorOn;
        var autoMode = _state.AutoMode;
        var bufferConflict = _state.BufferConflict;
        var isHoming = _state.IsHoming;
        var automaticRunning = _state.AutomaticRunning;
        var isRunning = _state.IsRunning;
        var conveyorRunning = _state.ConveyorRunning;
        var conveyorState = _state.MainConveyorState;
        var safetyReady =
            (!_options.UseEmergencyStop || emergencyStopReleased)
            && (!_options.UseAirPressureInterlock || airPressureOk);
        var startBlock = StartBlockFor(
            alarm,
            readiness,
            emergencyStopReleased,
            doorClosed,
            airPressureOk,
            servoMainContactorOn,
            autoMode,
            bufferConflict);
        var canStart = !isRunning && startBlock == StartBlockReason.None;
        var canHome = safetyReady
            && (!_options.UseDoorInterlock || doorClosed)
            && readiness.ServosOn
            && !readiness.Homed
            && alarm == MachineAlarm.None
            && !isRunning
            && _machine.CanHome;

        return new(
            DisplayStateFor(
                safetyReady,
                alarm,
                readiness,
                isHoming,
                servoMainContactorOn,
                isRunning),
            startBlock,
            isHoming,
            automaticRunning,
            conveyorRunning,
            conveyorState,
            bufferConflict,
            emergencyStopReleased,
            doorClosed,
            airPressureOk,
            autoMode,
            alarm,
            servoMainContactorOn && readiness.ServosOn,
            readiness.Homed,
            canStart,
            canHome);
    }

    private StartBlockReason StartBlockFor(
        MachineAlarm alarm,
        MotionReadiness readiness,
        bool emergencyStopReleased,
        bool doorClosed,
        bool airPressureOk,
        bool servoMainContactorOn,
        bool autoMode,
        bool bufferConflict)
    {
        if (alarm == MachineAlarm.EmergencyStop)
        {
            return StartBlockReason.EmergencyStop;
        }

        if (alarm == MachineAlarm.DoorOpen)
        {
            return StartBlockReason.DoorOpen;
        }

        if (alarm == MachineAlarm.AirPressureLow)
        {
            return StartBlockReason.AirPressure;
        }

        if (alarm == MachineAlarm.BufferConflict || bufferConflict)
        {
            return StartBlockReason.BufferConflict;
        }

        if (alarm != MachineAlarm.None)
        {
            return StartBlockReason.Alarm;
        }

        if (_options.UseEmergencyStop && !emergencyStopReleased)
        {
            return StartBlockReason.EmergencyStop;
        }

        if (_options.UseAirPressureInterlock && !airPressureOk)
        {
            return StartBlockReason.AirPressure;
        }

        if (readiness.Faulted)
        {
            return StartBlockReason.MotionFault;
        }

        if (!servoMainContactorOn || !readiness.ServosOn)
        {
            return StartBlockReason.ServoOff;
        }

        if (_options.UseDoorInterlock && !doorClosed)
        {
            return StartBlockReason.DoorOpen;
        }

        if (!readiness.Homed)
        {
            return StartBlockReason.HomeRequired;
        }

        if (!autoMode)
        {
            return StartBlockReason.AutoMode;
        }

        if (!_machine.TeachingReady)
        {
            return StartBlockReason.TeachingIncomplete;
        }

        return _units.HasEnabledUnit()
            ? StartBlockReason.None
            : StartBlockReason.NoUnitEnabled;
    }

    private static MachineDisplayState DisplayStateFor(
        bool safetyReady,
        MachineAlarm alarm,
        MotionReadiness readiness,
        bool isHoming,
        bool servoMainContactorOn,
        bool isRunning)
    {
        if (!safetyReady)
        {
            return MachineDisplayState.SafetyStop;
        }

        if (alarm != MachineAlarm.None)
        {
            return MachineDisplayState.Alarm;
        }

        if (readiness.Faulted)
        {
            return MachineDisplayState.MotionFault;
        }

        if (isHoming)
        {
            return MachineDisplayState.Homing;
        }

        if (!servoMainContactorOn || !readiness.ServosOn)
        {
            return MachineDisplayState.ServoOff;
        }

        if (!readiness.Homed)
        {
            return MachineDisplayState.HomeRequired;
        }

        return isRunning
            ? MachineDisplayState.Running
            : MachineDisplayState.Ready;
    }

    private static void RunOnUi(
        Action action,
        DispatcherPriority priority = DispatcherPriority.DataBind) =>
        Application.Current.Dispatcher.BeginInvoke(priority, action);
}
