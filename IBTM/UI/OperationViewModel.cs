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
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.UI;

public partial class OperationViewModel : ObservableObject
{
    private const double PickupToolX = 37.5;
    private const double ShootingToolX = 88.5;
    private const double BoltToolY = 101;
    private const double BoltTargetOriginX = 44;
    private const double BoltTargetOriginY = 456;
    private const double InspectionCameraX = 40;
    private const double InspectionToolY = 69;
    private const double InspectionTargetOriginX = 46;
    private const double InspectionTargetOriginY = 456;
    private const double NgGripperX = 82;
    private const double NgConveyorPosition3X = 243;
    private const double NgConveyorPosition3Y = 555;

    private readonly MachineState _state;
    private readonly MachineController _machine;
    private readonly IIoService _io;
    private readonly UnitSettings _units;
    private readonly MachineOptions _options;
    private readonly IAxisMotion _pcbSupplyMotion;
    private readonly IXyMotion _pcbPlacementMotion;
    private readonly IXyMotion _boltFasteningMotion;
    private readonly IXyMotion _inspectionGantryMotion;
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
    private readonly NgConveyorLine _ngConveyor;
    private int _positionRefreshQueued;

    public OperationViewModel(
        MachineState state,
        MachineController machine,
        IIoService io,
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
        NgConveyorLine ngConveyor,
        StartPreparationPlan startPreparations,
        [FromKeyedServices(MotionGroup.PcbSupply)] IAxisMotion pcbSupplyMotion,
        [FromKeyedServices(MotionGroup.PcbPlacementHandler)] IXyMotion pcbPlacementMotion,
        [FromKeyedServices(MotionGroup.BoltFastening)] IXyMotion boltFasteningMotion,
        [FromKeyedServices(MotionGroup.InspectionGantry)] IXyMotion inspectionGantryMotion)
    {
        _state = state;
        _machine = machine;
        _io = io;
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
        _pcbSupplyMotion = pcbSupplyMotion;
        _pcbPlacementMotion = pcbPlacementMotion;
        _boltFasteningMotion = boltFasteningMotion;
        _inspectionGantryMotion = inspectionGantryMotion;
        pcbSupplyMotion.PositionChanged += OnPositionChanged;
        pcbPlacementMotion.PositionChanged += OnPositionChanged;
        boltFasteningMotion.PositionChanged += OnPositionChanged;
        inspectionGantryMotion.PositionChanged += OnPositionChanged;
        pcbPlacementWork.Changed += OnMachineStateChanged;
        boltFasteningWork.Changed += OnMachineStateChanged;
        inspectionWork.Changed += OnMachineStateChanged;
        state.Changed += OnMachineStateChanged;
    }

    public string PcbSupplyPosition
        => FormatPosition(_pcbSupplyMotion.GetPosition());

    public string PcbPlacementPosition =>
        FormatPosition(_pcbPlacementMotion.GetPosition());
    public string BoltFasteningPosition =>
        FormatPosition(_boltFasteningMotion.GetPosition());
    public string InspectionGantryPosition =>
        FormatXyPosition(_inspectionGantryMotion.GetPosition());

    public double PcbSupplyMapLeft => MapPcbSupply().X;
    public double PcbSupplyMapTop => MapPcbSupply().Y;
    public double PcbPlacementMapLeft => MapPcbPlacement().X;
    public double PcbPlacementMapTop => MapPcbPlacement().Y;
    public double BoltFasteningMapLeft => MapBoltFastening().X;
    public double BoltFasteningMapTop => MapBoltFastening().Y;
    public double BoltPickupFeederMapLeft => BoltPickupCenter().X - 50;
    public double BoltPickupFeederMapTop => BoltPickupCenter().Y - 34;
    public double InspectionGantryMapLeft => MapInspectionGantry().X;
    public double InspectionGantryMapTop => MapInspectionGantry().Y;
    public double NgConveyorMapOffsetLeft => MapNgConveyor().X;
    public double NgConveyorMapOffsetTop => MapNgConveyor().Y;
    public bool PcbSupplyPcbDetected =>
        Input(InputIo.PcbSupplyPcbDetected);
    public bool PcbPlacementPcbDetected =>
        Input(InputIo.PcbPlacementPcbDetected);
    public bool PcbPlacementHandlerDown =>
        Input(InputIo.PcbPlacementHandlerDown);
    public bool PcbPlacementIpmDown =>
        Input(InputIo.PcbPlacementIpmDown);
    public bool PcbPlacementVacuumDetected =>
        Input(InputIo.PcbPlacementVacuumDetected);
    public bool PcbSupplyIpmFixed =>
        Input(InputIo.PcbSupplyIpmFixerForward);
    public bool PcbSupplyNestForward =>
        Input(InputIo.PcbSupplyNestForward);
    public bool PcbPlacementIpmGripperClosed =>
        Input(InputIo.PcbPlacementIpmGripperClosed);
    public bool PcbPlacementStopperUp =>
        Input(InputIo.PcbPlacementStopperUp);
    public bool PcbPlacementBackupPlateUp =>
        Input(InputIo.PcbPlacementBackupPlateUp);
    public bool PcbSupplyRotated =>
        Input(InputIo.PcbSupplyRotated);
    public bool PcbSupplyAvailableFromFront1 =>
        Input(InputIo.PcbSupplyAvailableFromFront1);
    public bool PcbBufferPcbPresent =>
        Input(InputIo.PcbBufferPcbPresent);
    public bool PcbPlacementHeatSink1Present =>
        Input(InputIo.PcbPlacementHeatSink1Present);
    public bool PcbPlacementHeatSink2Present =>
        Input(InputIo.PcbPlacementHeatSink2Present);
    public bool PcbPlacementCarrierPresent =>
        Input(InputIo.PcbPlacementCarrierPresent);
    public bool MainConveyorEntryCarrierDetected =>
        Input(InputIo.MainConveyorEntryCarrierDetected);
    public bool MainConveyorExitCarrierDetected =>
        Input(InputIo.MainConveyorExitCarrierDetected);
    public bool BoltFasteningHeatSink1Present =>
        Input(InputIo.BoltFasteningHeatSink1Present);
    public bool BoltFasteningHeatSink2Present =>
        Input(InputIo.BoltFasteningHeatSink2Present);
    public bool BoltFasteningCarrierPresent =>
        Input(InputIo.BoltFasteningCarrierPresent);
    public bool BoltFasteningStopperUp =>
        Input(InputIo.BoltFasteningStopperUp);
    public bool BoltFasteningBackupPlateUp =>
        Input(InputIo.BoltFasteningBackupPlateUp);
    public bool InspectionHeatSink1Present =>
        Input(InputIo.InspectionHeatSink1Present);
    public bool InspectionHeatSink2Present =>
        Input(InputIo.InspectionHeatSink2Present);
    public bool InspectionCarrierPresent =>
        Input(InputIo.InspectionCarrierPresent);
    public bool InspectionStopperUp =>
        Input(InputIo.InspectionStopperUp);
    public bool InspectionBackupPlateUp =>
        Input(InputIo.InspectionBackupPlateUp);
    public bool BoltHead1Down =>
        Input(InputIo.BoltHead1Down);
    public bool BoltHead2Down =>
        Input(InputIo.BoltHead2Down);
    public bool BoltHead1Loaded =>
        Input(InputIo.BoltHead1VacuumDetected);
    public bool BoltHead2Loaded =>
        Input(InputIo.BoltHead2VacuumDetected);
    public bool PickupFeederReady =>
        Input(InputIo.PickupFeederBoltDetected);
    public bool LinearFeederReady =>
        Input(InputIo.LinearFeederBoltDetected);
    public bool NgCarrierGripperClosed =>
        Input(InputIo.NgCarrierGripperClosed);
    public bool NgCarrierPickupDown =>
        Input(InputIo.NgCarrierPickupDown);
    public bool NgCarrierDetected =>
        Input(InputIo.NgCarrierDetected);
    public bool NgShuttleCarrierDetected =>
        Input(InputIo.NgShuttleCarrierDetected);
    public bool NgConveyorPosition1Occupied =>
        Input(InputIo.NgConveyorPosition1Occupied);
    public bool NgConveyorPosition2Occupied =>
        Input(InputIo.NgConveyorPosition2Occupied);
    public bool NgConveyorPosition3Occupied =>
        Input(InputIo.NgConveyorPosition3Occupied);
    public int NgCarrierCount => _ngConveyor.CarrierCount;
    public int NgAlarmCarrierCount => _ngConveyor.AlarmCarrierCount;
    public bool NgAlarmRequired => _ngConveyor.AlarmRequired;
    public bool NgConveyorRunCommandOn => _ngConveyor.RunCommandOn;
    public NgShuttleLiftState NgShuttleLift => _ngConveyor.ShuttleLift;
    public NgConveyorState NgConveyorState => _ngConveyor.State;
    public bool IsHoming => _state.IsHoming;
    public bool ConveyorRunning => _state.ConveyorRunning;
    public MainConveyorState MainConveyorState =>
        _state.MainConveyorState;
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
    public bool BoltFasteningMoving => _boltFasteningMotion.IsMoving;
    public bool InspectionGantryMoving => _inspectionGantryMotion.IsMoving;
    public bool PcbSupplyMoving => _pcbSupplyMotion.IsMoving;
    public bool PcbPlacementMoving => _pcbPlacementMotion.IsMoving;
    public bool BufferConflict => _state.BufferConflict;
    public bool MainConveyorEnabled => _units.MainConveyor;
    public bool PcbSupplyEnabled => _units.PcbSupply;
    public bool PcbPlacementEnabled => _units.PcbPlacement;
    public bool PickupFeederEnabled => _units.PickupBoltFeeder;
    public bool LinearFeederEnabled => _units.LinearBoltFeeder;
    public bool BoltFasteningEnabled => _units.BoltFastening;
    public bool InspectionEnabled => _units.Inspection;
    public bool NgConveyorEnabled => _units.NgConveyor;
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
        _pcbPlacementProcess.State(_recipe.PcbPlacement);
    public HeatSinkSlot? PcbPlacementTargetHeatSink =>
        _pcbPlacementProcess.TargetHeatSink;
    public BoltFasteningProcessState BoltFasteningProcessState =>
        _boltFasteningProcess.State(_recipe.BoltFastening);
    public InspectionProcessState InspectionProcessState =>
        _inspectionProcess.State(_recipe.BoltFastening.BoltPoints);
    public BoltPoint? BoltFasteningActiveBolt =>
        BoltProcessStateVisible
            ? _boltFasteningProcess.ActiveBolt(_recipe.BoltFastening)
            : null;
    public BoltPoint? InspectionActiveBolt =>
        InspectionProcessStateVisible
            ? _inspectionProcess.ActiveBolt(_recipe.BoltFastening.BoltPoints)
            : null;
    public IReadOnlyList<WorkTargetView> BoltTargets =>
        CreateFasteningTargets();
    public IReadOnlyList<WorkTargetView> InspectionTargets =>
        CreateInspectionTargets();
    public bool BoltFasteningHeatSink1ResultVisible =>
        BoltFasteningEnabled
        && BoltFasteningCarrierPresent
        && BoltFasteningHeatSink1Present
        && BoltFasteningHeatSink1Result != PcbResult.Pending;
    public bool BoltFasteningHeatSink2ResultVisible =>
        BoltFasteningEnabled
        && BoltFasteningCarrierPresent
        && BoltFasteningHeatSink2Present
        && BoltFasteningHeatSink2Result != PcbResult.Pending;
    public bool InspectionHeatSink1ResultVisible =>
        InspectionEnabled
        && InspectionCarrierPresent
        && InspectionHeatSink1Present
        && InspectionHeatSink1Result != PcbResult.Pending;
    public bool InspectionHeatSink2ResultVisible =>
        InspectionEnabled
        && InspectionCarrierPresent
        && InspectionHeatSink2Present
        && InspectionHeatSink2Result != PcbResult.Pending;
    public PcbResult BoltFasteningHeatSink1Result =>
        Result(_boltFasteningWork, HeatSinkSlot.HeatSink1, inspection: false);
    public PcbResult BoltFasteningHeatSink2Result =>
        Result(_boltFasteningWork, HeatSinkSlot.HeatSink2, inspection: false);
    public PcbResult InspectionHeatSink1Result =>
        Result(_inspectionWork, HeatSinkSlot.HeatSink1, inspection: true);
    public PcbResult InspectionHeatSink2Result =>
        Result(_inspectionWork, HeatSinkSlot.HeatSink2, inspection: true);
    public bool BoltFasteningHasHeatSink =>
        HasHeatSink(
            InputIo.BoltFasteningHeatSink1Present,
            InputIo.BoltFasteningHeatSink2Present);
    public bool InspectionHasHeatSink =>
        HasHeatSink(
            InputIo.InspectionHeatSink1Present,
            InputIo.InspectionHeatSink2Present);
    public bool EmergencyStopReleased => _state.EmergencyStopReleased;
    public bool DoorClosed => _state.DoorClosed;
    public bool AirPressureOk => _state.AirPressureOk;
    public bool AutoMode => _state.AutoMode;
    public bool HasAlarm => _state.IsError;
    public MachineAlarm Alarm => _state.Alarm;
    public bool ServoReady =>
        _state.ServoMainContactorOn && _state.ServosOn;
    public bool Homed => _state.Homed;
    public bool SafetyBypass =>
        !_options.UseEmergencyStop
        || !_options.UseDoorInterlock
        || !_options.UseAirPressureInterlock;
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_startPreparations.Prepare(
                Application.Current.MainWindow))
        {
            return;
        }

        await _machine.StartAsync(cancellationToken);
    }

    [RelayCommand]
    private void Stop()
    {
        StartCommand.Cancel();
        _machine.Stop();
    }

    [RelayCommand(CanExecute = nameof(CanHome))]
    private Task HomeAsync(CancellationToken cancellationToken) =>
        _machine.HomeAsync(cancellationToken);

    [RelayCommand(CanExecute = nameof(CanStopHome))]
    private void StopHome() => HomeCommand.Cancel();

    private bool CanStart() => _machine.CanStart;
    private bool CanHome() => _machine.CanHome;
    private bool CanStopHome() => _state.IsHoming;
    private void NotifyCanExecuteChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        StopHomeCommand.NotifyCanExecuteChanged();
        OpenPcbPlacementRecoveryCommand.NotifyCanExecuteChanged();
        OpenBoltRecoveryCommand.NotifyCanExecuteChanged();
    }

    private bool Input(InputIo input) => _io.GetInput(input);

    private static bool HasAssembly(
        StationWork work,
        HeatSinkSlot heatSink) =>
        work.Assemblies.Any(assembly => assembly.HeatSink == heatSink);

    private static PcbResult Result(
        StationWork work,
        HeatSinkSlot heatSink,
        bool inspection)
    {
        var assembly = work.Assemblies.FirstOrDefault(
            item => item.HeatSink == heatSink);
        return assembly is null
            ? PcbResult.Pending
            : inspection
                ? assembly.InspectionResult
                : assembly.FasteningResult;
    }

    private bool HasHeatSink(InputIo heatSink1, InputIo heatSink2) =>
        Input(heatSink1) || Input(heatSink2);

    private IReadOnlyList<WorkTargetView> CreateFasteningTargets()
    {
        var active = BoltFasteningActiveBolt;
        return CreateTargets(
            BoltFasteningHeatSink1Present,
            BoltFasteningHeatSink2Present,
            BoltFasteningTargetPosition,
            bolt => ReferenceEquals(bolt, active)
                ? WorkTargetState.Active
                : FasteningTargetState(bolt));
    }

    private IReadOnlyList<WorkTargetView> CreateInspectionTargets()
    {
        var active = InspectionActiveBolt;
        return CreateTargets(
            InspectionHeatSink1Present,
            InspectionHeatSink2Present,
            InspectionTargetPosition,
            bolt => ReferenceEquals(bolt, active)
                ? WorkTargetState.Active
                : InspectionTargetState(bolt));
    }

    private IReadOnlyList<WorkTargetView> CreateTargets(
        bool heatSink1Present,
        bool heatSink2Present,
        Func<BoltPoint, (double X, double Y)> position,
        Func<BoltPoint, WorkTargetState> state)
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
                return new WorkTargetView(
                    bolt.Number,
                    bolt.Head,
                    target.X,
                    target.Y,
                    state(bolt));
            })
            .ToArray();
    }

    private WorkTargetState FasteningTargetState(BoltPoint bolt)
    {
        var assembly = _boltFasteningWork.Assemblies.FirstOrDefault(
            item => item.HeatSink == bolt.HeatSink);
        if (assembly is null)
        {
            return WorkTargetState.Pending;
        }

        if (bolt.Head == FasteningHead.Shooting)
        {
            return ResultState(assembly.PcbBoltResults, bolt.Number);
        }

        var seating = ResultState(
            assembly.IpmSeatingResults,
            bolt.Number);
        var final = ResultState(assembly.IpmFinalResults, bolt.Number);
        return seating == WorkTargetState.Ng || final == WorkTargetState.Ng
            ? WorkTargetState.Ng
            : final;
    }

    private WorkTargetState InspectionTargetState(BoltPoint bolt)
    {
        var assembly = _inspectionWork.Assemblies.FirstOrDefault(
            item => item.HeatSink == bolt.HeatSink);
        if (assembly is null
            || !assembly.BoltPresenceResults.TryGetValue(
                bolt.Number,
                out var present))
        {
            return WorkTargetState.Pending;
        }

        return present ? WorkTargetState.Ok : WorkTargetState.Ng;
    }

    private static WorkTargetState ResultState(
        IReadOnlyDictionary<int, BoltResult> results,
        int number) =>
        results.TryGetValue(number, out var result)
            ? result.Success
                ? WorkTargetState.Ok
                : WorkTargetState.Ng
            : WorkTargetState.Pending;

    private (double X, double Y) MapPcbSupply()
    {
        var current = _pcbSupplyMotion.GetPosition();
        var pcb1X = _recipe.PcbSupply.Pcb1PickPosition.X;
        var pcb2X = _recipe.PcbSupply.Pcb2PickPosition.X;
        var buffer = _pcbSupplySettings.BufferHandoffPosition;
        if (pcb1X == pcb2X
            || buffer.X == 0
            || buffer.Y == _pcbSupplySettings.CarrierY)
        {
            return (59, 116);
        }

        var carrierScale = (153d - 59) / (pcb2X - pcb1X);
        var originX = 59 - (pcb1X * carrierScale);
        var bufferScale = (284 - originX) / buffer.X;
        var lane = (current.Y - _pcbSupplySettings.CarrierY)
                   / (buffer.Y - _pcbSupplySettings.CarrierY);
        var xScale = carrierScale
                     + ((bufferScale - carrierScale) * lane);
        return (
            originX + (current.X * xScale),
            116 + ((177 - 116) * lane));
    }

    private (double X, double Y) MapPcbPlacement()
    {
        var current = _pcbPlacementMotion.GetPosition();
        return MapTriangle(
            current.X,
            current.Y,
            (_pcbPlacementSettings.BufferHandoffPosition.X,
                _pcbPlacementSettings.BufferHandoffPosition.Y),
            (_recipe.PcbPlacement.HeatSink1PcbPlacementPosition.X,
                _recipe.PcbPlacement.HeatSink1PcbPlacementPosition.Y),
            (_recipe.PcbPlacement.HeatSink2PcbPlacementPosition.X,
                _recipe.PcbPlacement.HeatSink2PcbPlacementPosition.Y),
            (282, 195),
            (220, 399),
            (348, 399));
    }

    private (double X, double Y) MapBoltFastening()
    {
        var current = _boltFasteningMotion.GetPosition();
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
            return (18, 390);
        }

        return MapTriangle(
            x,
            y,
            (shootingUpperLeft.X, shootingUpperLeft.Y),
            (shootingLowerRight.X, shootingLowerRight.Y),
            (pickupUpperLeft.X, pickupUpperLeft.Y),
            (-44.5, 355),
            (187.5, 403),
            (6.5, 355));
    }

    private (double X, double Y) BoltPickupCenter()
    {
        var pickup = _boltFasteningSettings.PickupPosition;
        var root = MapBoltGantry(pickup.X, pickup.Y);
        return (root.X + PickupToolX, root.Y + BoltToolY);
    }

    private (double X, double Y) BoltFasteningTargetPosition(
        BoltPoint bolt)
    {
        var target = _boltFasteningSettings.GetBoltPosition(
            bolt,
            _carrierReference);
        var root = MapBoltGantry(target.X, target.Y);
        var toolX = bolt.Head == FasteningHead.Pickup
            ? PickupToolX
            : ShootingToolX;
        return (
            root.X + toolX - BoltTargetOriginX,
            root.Y + BoltToolY - BoltTargetOriginY);
    }

    private (double X, double Y) MapInspectionGantry()
    {
        var current = _inspectionGantryMotion.GetPosition();
        return MapInspectionGantry(current.X, current.Y);
    }

    private (double X, double Y) MapInspectionGantry(double x, double y)
    {
        if (_carrierReference.UpperLeftPin is not { } upperLeft
            || _carrierReference.LowerRightPin is not { } lowerRight)
        {
            return (-40, 390);
        }

        var pickup = _ngConveyorSettings.CarrierPickupPosition;
        return MapTriangle(
            x,
            y,
            (upperLeft.X, upperLeft.Y),
            (lowerRight.X, lowerRight.Y),
            (pickup.X, pickup.Y),
            (6, 387),
            (238, 435),
            (80, 411));
    }

    private (double X, double Y) InspectionTargetPosition(BoltPoint bolt)
    {
        var target = _inspectionGantrySettings.GetBoltPosition(
            bolt,
            _carrierReference);
        var root = MapInspectionGantry(target.X, target.Y);
        return (
            root.X + InspectionCameraX - InspectionTargetOriginX,
            root.Y + InspectionToolY - InspectionTargetOriginY);
    }

    private (double X, double Y) MapNgConveyor()
    {
        if (_carrierReference.UpperLeftPin is null
            || _carrierReference.LowerRightPin is null)
        {
            return (0, 0);
        }

        var shuttle = _ngConveyorSettings.ShuttlePlacePosition;
        var root = MapInspectionGantry(shuttle.X, shuttle.Y);
        return (
            root.X + NgGripperX - NgConveyorPosition3X,
            root.Y + InspectionToolY - NgConveyorPosition3Y);
    }

    private static (double X, double Y) MapTriangle(
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

    private static string FormatPosition(
        (double X, double Y, double Z) position) =>
        $"X {position.X:F3}   Y {position.Y:F3}   Z {position.Z:F3}";

    private static string FormatXyPosition(
        (double X, double Y, double Z) position) =>
        $"X {position.X:F3}   Y {position.Y:F3}";

    private void OnPositionChanged(double _, double __, double ___) =>
        QueuePositionRefresh();

    private void QueuePositionRefresh()
    {
        if (Interlocked.Exchange(ref _positionRefreshQueued, 1) != 0)
        {
            return;
        }

        RunOnUi(() =>
        {
            Interlocked.Exchange(ref _positionRefreshQueued, 0);
            OnPropertyChanged(nameof(PcbSupplyPosition));
            OnPropertyChanged(nameof(PcbSupplyMapLeft));
            OnPropertyChanged(nameof(PcbSupplyMapTop));
            OnPropertyChanged(nameof(PcbPlacementPosition));
            OnPropertyChanged(nameof(PcbPlacementMapLeft));
            OnPropertyChanged(nameof(PcbPlacementMapTop));
            OnPropertyChanged(nameof(PcbPlacementProcessState));
            OnPropertyChanged(nameof(PcbPlacementTargetHeatSink));
            OnPropertyChanged(nameof(BoltFasteningPosition));
            OnPropertyChanged(nameof(BoltFasteningMapLeft));
            OnPropertyChanged(nameof(BoltFasteningMapTop));
            OnPropertyChanged(nameof(BoltFasteningProcessState));
            OnPropertyChanged(nameof(BoltFasteningActiveBolt));
            OnPropertyChanged(nameof(BoltTargets));
            OnPropertyChanged(nameof(InspectionGantryPosition));
            OnPropertyChanged(nameof(InspectionGantryMapLeft));
            OnPropertyChanged(nameof(InspectionGantryMapTop));
            OnPropertyChanged(nameof(InspectionProcessState));
            OnPropertyChanged(nameof(InspectionActiveBolt));
            OnPropertyChanged(nameof(InspectionTargets));
        });
    }

    private void OnMachineStateChanged() =>
        RunOnUi(() =>
        {
            OnPropertyChanged(new PropertyChangedEventArgs(null));
            NotifyCanExecuteChanged();
        });

    private static void RunOnUi(Action action) =>
        Application.Current.Dispatcher.BeginInvoke(action);
}
