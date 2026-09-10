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

    private static readonly string[] MachinePropertyNames =
    [
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

    private static readonly string[] PcbSupplyPropertyNames =
    [
        nameof(PcbSupplyPcbDetected),
        nameof(PcbSupplyPcbSecured),
        nameof(PcbSupplyIpmFixed),
        nameof(PcbSupplyGripperClosed),
        nameof(PcbSupplyRotated),
        nameof(Supply),
        nameof(SupplyDisplayState),
    ];

    private static readonly string[] PcbPlacementPropertyNames =
    [
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

    private static readonly string[] ConveyorPropertyNames =
    [
        nameof(ConveyorStatus),
        nameof(InspectionStatus),
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
        nameof(FasteningState),
        nameof(BoltFasteningActiveBolt),
        nameof(BoltTargets),
        nameof(FasteningStateVisible),
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

    private static readonly string[] NgConveyorPropertyNames =
    [
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

    private static readonly string[] ActivationPropertyNames =
    [
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
    private IReadOnlyList<BoltTargetView> _boltTargets = [];
    private IReadOnlyList<BoltTargetView> _inspectionTargets = [];
    private volatile bool _active;
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

        RefreshBoltFasteningDisplay();
        RefreshInspectionDisplay();

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

    public MachineState State => _state;
    public PcbSupplyHandler Supply { get; }
    public PcbPlacementHandler Placement { get; }
    public BoltFasteningGantry Fastening { get; }
    public InspectionGantry InspectionGantry { get; }
    public double PcbSupplyZTop => ZTop(Supply.Motion);
    public double PcbPlacementZTop => ZTop(Placement.Motion);
    public double BoltFasteningZTop => ZTop(Fastening.Motion);

    public double PcbSupplyMapLeft => _map.Supply(Supply.Motion.Position).X;
    public double PcbSupplyMapTop => _map.Supply(Supply.Motion.Position).Y;
    public double PcbPlacementMapLeft => _map.Placement(Placement.Motion.Position).X;
    public double PcbPlacementMapTop => _map.Placement(Placement.Motion.Position).Y;
    public double BoltFasteningMapLeft => _map.Fastening(Fastening.Motion.Position).X;
    public double BoltFasteningMapTop => _map.Fastening(Fastening.Motion.Position).Y;
    public double BoltPickupFeederMapLeft => _map.PickupFeeder().X;
    public double BoltPickupFeederMapTop => _map.PickupFeeder().Y;
    public double InspectionGantryMapLeft => _map.Inspection(InspectionGantry.Motion.Position).X;
    public double InspectionGantryMapTop => _map.Inspection(InspectionGantry.Motion.Position).Y;
    public bool PcbSupplyPcbDetected =>
        Supply.Pcb != PcbSupplyPcbState.None;
    public bool PcbSupplyPcbSecured =>
        Supply.Pcb == PcbSupplyPcbState.Secured;
    public bool PcbPlacementPcbDetected =>
        Placement.Pcb != PlacementPcbState.None;
    public bool PcbPlacementPcbSecured =>
        Placement.PcbSecured;
    public bool PcbPlacementHandlerDown =>
        Placement.Lift == PlacementCylinderState.Down;
    public bool PcbPlacementIpmDown =>
        Placement.IpmLift == PlacementCylinderState.Down;
    public bool PcbSupplyIpmFixed =>
        Supply.IpmFixer == PcbSupplyCylinderState.Forward;
    public bool PcbSupplyGripperClosed =>
        Supply.Gripper == PcbSupplyCylinderState.Forward;
    public bool PcbPlacementIpmGripperClosed =>
        Placement.IpmGripper == PlacementGripperState.Closed;
    public bool PcbPlacementStopperUp =>
        _pcbPlacementWork.Stopper == StationCylinderState.Up;
    public bool PcbPlacementBackupPlateUp =>
        _pcbPlacementWork.BackupPlate == StationCylinderState.Up;
    public bool PcbSupplyRotated =>
        Supply.Rotation == PcbSupplyRotationState.Rotated;
    public bool PcbBufferPcbPresent =>
        _buffer.PcbPresent;
    public bool PcbPlacementHeatSink1Present =>
        _pcbPlacementWork.HeatSinkPresent(HeatSinkSlot.HeatSink1);
    public bool PcbPlacementHeatSink2Present =>
        _pcbPlacementWork.HeatSinkPresent(HeatSinkSlot.HeatSink2);
    public bool PcbPlacementCarrierPresent =>
        _pcbPlacementWork.CarrierPresent;
    public bool MainConveyorEntryCarrierDetected =>
        _conveyor.EntryCarrierDetected;
    public bool MainConveyorExitCarrierDetected =>
        _conveyor.ExitCarrierDetected;
    public bool BoltFasteningHeatSink1Present =>
        _boltFasteningWork.HeatSinkPresent(HeatSinkSlot.HeatSink1);
    public bool BoltFasteningHeatSink2Present =>
        _boltFasteningWork.HeatSinkPresent(HeatSinkSlot.HeatSink2);
    public bool BoltFasteningCarrierPresent =>
        _boltFasteningWork.CarrierPresent;
    public bool BoltFasteningStopperUp =>
        _boltFasteningWork.Stopper == StationCylinderState.Up;
    public bool BoltFasteningBackupPlateUp =>
        _boltFasteningWork.BackupPlate == StationCylinderState.Up;
    public bool InspectionHeatSink1Present =>
        _inspectionWork.HeatSinkPresent(HeatSinkSlot.HeatSink1);
    public bool InspectionHeatSink2Present =>
        _inspectionWork.HeatSinkPresent(HeatSinkSlot.HeatSink2);
    public bool InspectionCarrierPresent =>
        _inspectionWork.CarrierPresent;
    public bool InspectionStopperUp =>
        _inspectionWork.Stopper == StationCylinderState.Up;
    public bool InspectionBackupPlateUp =>
        _inspectionWork.BackupPlate == StationCylinderState.Up;
    public bool PickupHeadDown =>
        Fastening.PickupHeadPosition == BoltCylinderState.Down;
    public bool ShootingHeadDown =>
        Fastening.ShootingHeadPosition == BoltCylinderState.Down;
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
        _ngShuttle.Feedback.CarrierDetected;
    public bool NgConveyorPosition1Occupied =>
        _ngConveyor.Position1Occupied;
    public bool NgConveyorPosition2Occupied =>
        _ngConveyor.Position2Occupied;
    public bool NgAlarmRequired => _ngConveyor.AlarmRequired;
    public bool NgConveyorRunCommandOn => _state.Display.NgConveyorRunning;
    public NgShuttleLiftState NgShuttleLift => _ngShuttle.Feedback.Lift;
    public NgConveyorState NgConveyorState => _state.Display.NgConveyorState;
    public bool ConveyorRunning => _state.Display.ConveyorRunning;
    public MainConveyorState MainConveyorState =>
        _state.Display.ConveyorState;
    public bool CarrierBetweenPlacementAndBolt =>
        !PcbPlacementCarrierPresent
        && !BoltFasteningCarrierPresent
        && MainConveyorState
            == MainConveyorState.MovingPcbPlacementToBoltFastening;
    public bool CarrierBetweenBoltAndInspection =>
        !BoltFasteningCarrierPresent
        && !InspectionCarrierPresent
        && MainConveyorState
            == MainConveyorState.MovingBoltFasteningToInspection;
    public bool MainConveyorEnabled => _units.MainConveyor;
    public bool PcbSupplyEnabled => _units.PcbSupply;
    public bool PcbPlacementEnabled => _units.PcbPlacement;
    public bool PickupFeederEnabled => _units.PickupBoltFeeder;
    public bool BoltFasteningEnabled => _units.BoltFastening;
    public bool InspectionEnabled => _units.Inspection;
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
    public PcbPlacementState PlacementState => _state.Display.PlacementState;
    public HeatSinkSlot? PcbPlacementTargetHeatSink =>
        _state.Display.PlacementTarget;
    public BoltFasteningState FasteningState => _state.Display.FasteningState;
    public InspectionStationState InspectionState =>
        _state.Display.InspectionState;
    public BoltTarget? BoltFasteningActiveBolt =>
        FasteningStateVisible
            ? _state.Display.FasteningBolt
            : null;
    public BoltTarget? InspectionActiveBolt =>
        InspectionStateVisible
            ? _state.Display.InspectionBolt
            : null;
    public HeatSinkSlot? InspectionActivePcb => InspectionStateVisible ? _state.Display.InspectionPcb : null;
    public string? InspectionPcb1Barcode => InspectionBarcode(HeatSinkSlot.HeatSink1);
    public string? InspectionPcb2Barcode => InspectionBarcode(HeatSinkSlot.HeatSink2);

    private string? InspectionBarcode(HeatSinkSlot pcb) =>
        _inspectionWork.CarrierPresent && _inspectionWork.HeatSinkPresent(pcb)
            ? _inspectionWork.Assemblies.FirstOrDefault(assembly => assembly.HeatSink == pcb)?.PcbBarcode
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
    public bool ModeKnown => _state.Display.Available;
    public string ModeText => ModeKnown ? (_state.Display.AutoMode ? "AUTO" : "MANUAL") : "UNKNOWN";
    public bool HasAlarm => _state.Display.Alarm != MachineAlarm.None || _state.Display.ReadError is not null;
    public Enum Alarm => _state.Display.ReadError is null
        ? _state.Display.Alarm : MachineDisplayState.Unavailable;
    public string? AlarmDetail => _state.Display.ReadError?.ToString() ?? _state.Display.AlarmDetail;
    public string? AlarmMessage => _state.Display.ReadError?.Message ?? _state.Display.AlarmMessage;
    public bool SafetyBypass =>
        !_options.UseEmergencyStop
        || !_options.UseDoorInterlock
        || !_options.UseAirPressureInterlock;
    public bool PcbPlacementRecoveryAvailable =>
        _pcbPlacementRecovery.Required;
    public bool BoltFasteningRecoveryAvailable =>
        _boltFasteningRecovery.Required;

    public void Activate()
    {
        _active = true;
        _state.RequestDisplayRefresh();
        RefreshBoltFasteningDisplay();
        RefreshInspectionDisplay();
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

    public void Deactivate() => _active = false;

    public Task ShutdownAsync() => CommandShutdown.StopAsync(
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

    [RelayCommand(CanExecute = nameof(CanOpenPcbPlacementRecovery))]
    private void OpenPcbPlacementRecovery() => _pcbPlacementRecovery.Open(
        Application.Current.MainWindow);

    [RelayCommand(CanExecute = nameof(CanOpenBoltFasteningRecovery))]
    private void OpenBoltFasteningRecovery() => _boltFasteningRecovery.Open(
        Application.Current.MainWindow);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var owner = Application.Current.MainWindow;
        if (!_pcbPlacementRecovery.Prepare(owner)
            || !_boltFasteningRecovery.Prepare(owner))
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
        RaiseCylindersCommand.Cancel();
        _machine.Stop();
    }

    [RelayCommand(CanExecute = nameof(CanRaiseCylinders))]
    private Task RaiseCylindersAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () => _machine.RaiseCylindersAsync(cancellationToken),
            cancellationToken);

    [RelayCommand(CanExecute = nameof(CanHome))]
    private Task HomeAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () => _machine.HomeAsync(cancellationToken),
            cancellationToken);

    [RelayCommand]
    private void StopHome() => HomeCommand.Cancel();

    private bool CanStart() => _state.Display.CanStart;
    private bool CanHome() => _state.Display.CanHome;
    private bool CanRaiseCylinders() => _state.Display.CanRaiseCylinders;
    private bool CanOpenBoltFasteningRecovery() => BoltFasteningRecoveryAvailable;
    private bool CanOpenPcbPlacementRecovery() =>
        PcbPlacementRecoveryAvailable;

    private void NotifyCanExecuteChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        RaiseCylindersCommand.NotifyCanExecuteChanged();
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
        if (!_units.BoltFastening || !_map.FasteningDefined)
        {
            return [];
        }

        var active = BoltFasteningActiveBolt;
        return CreateTargets(
            _recipe.Pcb.GetBolts().Where(bolt =>
                Fastening.HasReference(bolt.Head)),
            BoltFasteningHeatSink1Present,
            BoltFasteningHeatSink2Present,
            _map.FasteningTarget,
            bolt => bolt == active
                ? BoltTargetState.Active
                : FasteningTargetState(bolt));
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
            bolt => bolt == active
                ? BoltTargetState.Active
                : InspectionTargetState(bolt));
    }

    private IReadOnlyList<BoltTargetView> CreateTargets(
        IEnumerable<BoltTarget> bolts,
        bool heatSink1Present,
        bool heatSink2Present,
        Func<BoltTarget, (double X, double Y)> position,
        Func<BoltTarget, BoltTargetState> state)
    {
        return bolts
            .Where(bolt => bolt.X is not null
                           && bolt.Y is not null
                           && ((bolt.HeatSink == HeatSinkSlot.HeatSink1
                                && heatSink1Present)
                               || (bolt.HeatSink == HeatSinkSlot.HeatSink2
                                   && heatSink2Present)))
            .Select(bolt =>
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

        var seating = ResultState(
            assembly.IpmSeatingResults,
            bolt.Number);
        var final = ResultState(assembly.IpmFinalResults, bolt.Number);
        return seating == BoltTargetState.Ng || final == BoltTargetState.Ng
            ? BoltTargetState.Ng
            : final;
    }

    private BoltTargetState InspectionTargetState(BoltTarget bolt)
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
            DisplayRefresh.PcbPlacement);

    private void OnBoltFasteningMotionChanged(
        object? _,
        PropertyChangedEventArgs e) =>
        OnMotionChanged(
            e,
            PositionRefresh.BoltFastening,
            DisplayRefresh.BoltFastening);

    private void OnInspectionGantryMotionChanged(
        object? _,
        PropertyChangedEventArgs e) =>
        OnMotionChanged(
            e,
            PositionRefresh.Inspection,
            DisplayRefresh.Inspection);

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
            QueueDisplayRefresh(displayRefresh);
            return;
        }

        if (e.PropertyName == nameof(MotionStatus.Position))
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

        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
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
        }));
    }

    private void OnMachineDisplayChanged()
    {
        QueueDisplayRefresh(DisplayRefresh.Machine | DisplayRefresh.PcbSupply
            | DisplayRefresh.PcbPlacement | DisplayRefresh.BoltFastening
            | DisplayRefresh.Inspection | DisplayRefresh.NgConveyor);
    }

    private void OnPcbSupplyChanged() =>
        QueueDisplayRefresh(DisplayRefresh.PcbSupply);

    private void OnPcbPlacementChanged()
    {
        if (!_active)
        {
            return;
        }

        QueueDisplayRefresh(DisplayRefresh.PcbPlacement | DisplayRefresh.PcbSupply);
    }

    private void OnMainConveyorChanged() =>
        QueueDisplayRefresh(DisplayRefresh.Conveyor);

    private void OnBoltFasteningChanged() =>
        QueueDisplayRefresh(DisplayRefresh.BoltFastening);

    private void OnBoltFeederChanged() =>
        QueueDisplayRefresh(DisplayRefresh.BoltFastening);


    private void OnNgConveyorChanged() =>
        QueueDisplayRefresh(DisplayRefresh.NgConveyor);

    private void RefreshBoltFasteningDisplay() => _boltTargets = CreateFasteningTargets();

    private void RefreshInspectionDisplay() => _inspectionTargets = CreateInspectionTargets();

    private void QueueDisplayRefresh(DisplayRefresh refresh)
    {
        if (!_active) refresh &= DisplayRefresh.Machine;
        if (refresh == 0)
        {
            return;
        }

        Interlocked.Or(ref _pendingDisplayRefresh, (int)refresh);
        if (Interlocked.Exchange(ref _displayRefreshQueued, 1) != 0)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
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
            RefreshBoltFasteningDisplay();
            NotifyProperties(BoltFasteningPropertyNames);
        }

        if ((refresh & DisplayRefresh.Inspection) != 0)
        {
            RefreshInspectionDisplay();
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
