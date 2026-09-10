using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM;

public sealed partial class MachineController
{
    private static readonly InputIo[] CarrierInputs = [
        InputIo.MainConveyorEntryCarrierDetected,
        InputIo.PcbPlacementCarrierPresent,
        InputIo.BoltFasteningCarrierPresent,
        InputIo.InspectionCarrierPresent,
        InputIo.MainConveyorExitCarrierDetected,
        InputIo.NgCarrierDetected,
        InputIo.NgShuttleCarrierDetected,
        InputIo.NgConveyorPosition1Occupied,
        InputIo.NgConveyorPosition2Occupied,
    ];

    private readonly MachineState _state;
    private readonly OperationCancellation _operations;
    private readonly MachineOptions _options;
    private readonly UnitSettings _units;
    private readonly Recipe _recipe;
    private readonly CarrierReferenceSettings _carrierReference;
    private readonly IIoService _io;
    private readonly MainConveyor _conveyor;
    private readonly NgCarrierConveyor _ngConveyor;
    private readonly NgShuttle _ngShuttle;
    private readonly PcbSupplier _pcbSupply;
    private readonly PcbPlacer _pcbPlacement;
    private readonly BoltFasteningStation _fasteningStation;
    private readonly InspectionStation _inspectionStation;
    private readonly PickupBoltFeeder _pickupBoltFeeder;
    private readonly ShootingBoltFeeder _shootingBoltFeeder;
    private readonly PcbSupplyHandler _supplyHandler;
    private readonly PcbPlacementHandler _placementHandler;
    private readonly BoltFasteningGantry _fasteningGantry;
    private readonly InspectionGantry _inspectionGantry;
    private readonly NgCarrierTransfer _ngTransfer;
    private readonly NgCarrierMove _ngMove;
    private readonly InspectionWork _inspectionWork;
    private readonly BoltInspector _boltInspector;
    private readonly ApplicationLog? _log;
    private readonly Lock _resetGate = new();
    private Task _resetTask = Task.CompletedTask;

    public MachineController(
        MachineState state,
        OperationCancellation operations,
        MachineOptions options,
        UnitSettings units,
        Recipe recipe,
        CarrierReferenceSettings carrierReference,
        IIoService io,
        MainConveyor conveyor,
        NgCarrierConveyor ngConveyor,
        NgShuttle ngShuttle,
        PcbSupplier pcbSupply,
        PcbPlacer pcbPlacement,
        BoltFasteningStation fasteningStation,
        InspectionStation inspectionStation,
        PickupBoltFeeder pickupBoltFeeder,
        ShootingBoltFeeder shootingBoltFeeder,
        PcbSupplyHandler supplyHandler,
        PcbPlacementHandler placementHandler,
        BoltFasteningGantry fasteningGantry,
        InspectionGantry inspectionGantry,
        NgCarrierTransfer ngTransfer,
        NgCarrierMove ngMove,
        InspectionWork inspectionWork,
        BoltInspector boltInspector,
        ApplicationLog? log = null)
    {
        _state = state;
        _operations = operations;
        _options = options;
        _units = units;
        _recipe = recipe;
        _carrierReference = carrierReference;
        _io = io;
        _conveyor = conveyor;
        _ngConveyor = ngConveyor;
        _ngShuttle = ngShuttle;
        _pcbSupply = pcbSupply;
        _pcbPlacement = pcbPlacement;
        _fasteningStation = fasteningStation;
        _inspectionStation = inspectionStation;
        _pickupBoltFeeder = pickupBoltFeeder;
        _shootingBoltFeeder = shootingBoltFeeder;
        _supplyHandler = supplyHandler;
        _placementHandler = placementHandler;
        _fasteningGantry = fasteningGantry;
        _inspectionGantry = inspectionGantry;
        _ngTransfer = ngTransfer;
        _ngMove = ngMove;
        _inspectionWork = inspectionWork;
        _boltInspector = boltInspector;
        _log = log;
        io.InputChanged += OnInputChanged;
        io.Faulted += OnIoFaulted;
        placementHandler.Feedback.MovingChanged += _ => CheckMotionInterlocks();
        fasteningGantry.Feedback.StateChanged += CheckMotionInterlocks;
        inspectionGantry.Feedback.MovingChanged += _ => CheckMotionInterlocks();
        pcbSupply.Changed += state.RequestDisplayRefresh;
        pcbPlacement.Changed += state.RequestDisplayRefresh;
        fasteningStation.Changed += state.RequestDisplayRefresh;
        inspectionStation.Changed += state.RequestDisplayRefresh;
        pickupBoltFeeder.Changed += state.RequestDisplayRefresh;
        shootingBoltFeeder.Changed += state.RequestDisplayRefresh;
    }

    public bool CanReset
    {
        get
        {
            if (_operations.IsShuttingDown || _state.IsRunning)
            {
                return false;
            }

            if (_state.Alarm == MachineAlarm.IoCommunication)
            {
                return true;
            }

            if (!_state.SafetyReady || !(_state.ManualMode || _state.DoorInterlockReady))
            {
                return false;
            }

            // Hardware recovery admission, not permission to acknowledge the buzzer.
            // A failed feedback scan must leave recovery usable without another native read.
            if (_state.IsError || _state.Display.ReadError is not null)
                return true;
            var motion = _state.DisplayMotionReadiness;
            return motion.Faulted || !motion.ServosOn || !_state.ServoMainContactorOn;
        }
    }

    public bool CanHome
    {
        get
        {
            return IsHomeAllowed(_state.MotionReadiness);
        }
    }

    private bool IsHomeAllowed(MotionReadiness motion)
    {
        return !_operations.IsShuttingDown
            && _state.SafetyReady
            && motion.ServosOn
            && !motion.Faulted
            && _state.ServoMainContactorOn
            && !motion.Homed
            && !_state.IsError
            && !_state.IsRunning
            && HomeBlock == HomeBlockReason.None
            && (!_units.PcbSupply || _pcbSupply.CanHome);
    }

    public HomeBlockReason HomeBlock
    {
        get
        {
            return GetHomeBlock();
        }
    }

    public bool CanRaiseCylinders
    {
        get
        {
            return _state.ManualSetupEnabled
                && (BufferHandlersEnabled || _units.BoltFastening || InspectionGantryEnabled)
                && !Array.Exists(CarrierInputs, _io.GetInput);
        }
    }

    internal HomeBlockReason GetHomeBlock(MotionGroup? group = null)
    {
        if (!_io.IsReady)
            return HomeBlockReason.IoUnavailable;
        if (group is { } motionGroup && !_units.IsMotionEnabled(motionGroup))
            return HomeBlockReason.UnitDisabled;
        if (!_state.ManualMode && !_state.DoorInterlockReady)
            return HomeBlockReason.DoorOpen;
        if (Array.Exists(CarrierInputs, _io.GetInput))
            return HomeBlockReason.CarrierDetected;

        if ((group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler
            || group is null
            && BufferHandlersEnabled)
            && (!_placementHandler.CanMoveHorizontal
                || _placementHandler.IpmLift != PlacementCylinderState.Up))
            return HomeBlockReason.PlacementNotRaised;

        if ((group == MotionGroup.BoltFastening || group is null && _units.BoltFastening)
            && !_fasteningGantry.CanMoveHorizontal)
            return HomeBlockReason.FasteningNotRaised;

        if ((group == MotionGroup.InspectionGantry
            || group is null
            && InspectionGantryEnabled)
            && !_inspectionGantry.CanMove)
            return HomeBlockReason.NgPickupNotRaised;

        return HomeBlockReason.None;
    }

    public bool CanStart
    {
        get
        {
            return IsStartAllowed(StartBlock);
        }
    }

    private bool IsStartAllowed(StartBlockReason block)
    {
        return !_operations.IsShuttingDown
            && !_state.IsRunning
            && block == StartBlockReason.None;
    }

    public StartBlockReason StartBlock
    {
        get
        {
            return GetStartBlock(_state.MotionReadiness);
        }
    }

    private StartBlockReason GetStartBlock(MotionReadiness motion)
    {
        if (_state.Alarm == MachineAlarm.EmergencyStop)
            return StartBlockReason.EmergencyStop;
        if (_state.Alarm == MachineAlarm.DoorOpen)
            return StartBlockReason.DoorOpen;
        if (_state.Alarm == MachineAlarm.AirPressureLow)
            return StartBlockReason.AirPressure;
        if (_state.Alarm == MachineAlarm.BufferConflict || _state.BufferConflict)
            return StartBlockReason.BufferConflict;
        if (_state.IsError)
            return StartBlockReason.Alarm;
        if (_options.UseEmergencyStop && !_state.EmergencyStopReleased)
            return StartBlockReason.EmergencyStop;
        if (_options.UseAirPressureInterlock && !_state.AirPressureOk)
            return StartBlockReason.AirPressure;
        if (motion.Faulted)
            return StartBlockReason.MotionFault;
        if (!motion.ServosOn || !_state.ServoMainContactorOn)
            return StartBlockReason.ServoOff;
        if (!_state.DoorInterlockReady)
            return StartBlockReason.DoorOpen;
        if (!motion.Homed)
            return StartBlockReason.HomeRequired;
        if (_state.RepeatEnabled && !_state.ManualMode)
            return StartBlockReason.TeachingMode;
        if (!TeachingReady)
            return StartBlockReason.TeachingIncomplete;
        if (_state.RepeatEnabled
            && (_repeatPhase == RepeatPhase.ReturnToShuttle && !_units.NgConveyor
                || _repeatPhase == RepeatPhase.CycleShuttle && !_units.NgShuttle))
            return StartBlockReason.RepeatReturnUnitDisabled;
        if (_state.RepeatEnabled
            && (!_units.MainConveyor
                || !_units.NgCarrierTransfer
                || _units.NgConveyor && !_units.NgShuttle))
            return StartBlockReason.RepeatRouteUnavailable;
        return _units.HasEnabledUnit() ? StartBlockReason.None : StartBlockReason.NoUnitEnabled;
    }

    public bool CanTestBoltHead
    {
        get
        {
            return _state.ManualSetupEnabled;
        }
    }

    public bool CanUseAdcProtocol
    {
        get
        {
            return AdcProtocolAvailable && !_state.IsRunning;
        }
    }

    public bool AdcProtocolAvailable
    {
        get
        {
            return !_operations.IsShuttingDown && _state.ManualMode && _state.SafetyReady;
        }
    }

    public bool TeachingReady
    {
        get
        {
            return !_units.BoltFastening
                && !_units.Inspection
                || _recipe.Pcb.IsDefined
                && _recipe.Pcb.BoltPoints.Count > 0
                && CarrierCoordinates.IsDefined(
                    _carrierReference.UpperLeftLocatingPin,
                    _carrierReference.LowerRightLocatingPin)
                && _recipe.Pcb.GetBolts()
                    .All(
                        bolt =>
                            bolt is { X: not null, Y: not null }
                                && (!_units.BoltFastening || _fasteningGantry.HasReference(bolt.Head)))
                && (!_units.Inspection
                    || Enum.GetValues<HeatSinkSlot>().All(_boltInspector.HasBarcodeRegion));
        }
    }

    public async Task InitializeAsync()
    {
        _log?.Write("Machine initialization started.");
        using (var operation = _operations.Link())
        {
            var (alarm, error) = await InitializeHardwareAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (alarm == MachineAlarm.None)
                alarm = SafetyAlarm();

            if (alarm == MachineAlarm.None)
                _state.Refresh();
            else
                _state.SetError(alarm, error);
        }

        _state.UpdateMachineIndicators();
        await _state.StartDisplayUpdatesAsync(ReadDisplay);
        _log?.Write($"Machine initialization finished. Alarm={_state.Alarm}.");
    }

    public void Stop()
    {
        _log?.Write("Machine STOP requested.");
        try
        {
            _operations.Cancel();
        }
        finally
        {
            StopRunOutputs();
            _state.Refresh();
        }
    }

    public async Task ShutdownAsync()
    {
        _log?.Write("Machine shutdown requested.");
        var displayStopped = _state.StopDisplayUpdatesAsync();
        var shutdown = _operations.ShutdownAsync();
        try
        {
            StopRunOutputs();
        }
        finally
        {
            await Task.WhenAll(shutdown, displayStopped, _boltInspector.StopLiveViewAsync());
            _state.UpdateMachineIndicators();
        }
    }

    internal bool CanUseManualMotion(MotionGroup group, bool live = true)
    {
        return !(live ? _state.IsRunning : _state.Display.IsRunning)
            && ManualMotionReady(group, live);
    }

    private bool ManualMotionReady(MotionGroup group, bool live = true)
    {
        if (_operations.IsShuttingDown
            || !_io.IsReady
            || !_state.ManualMode
            || !_state.SafetyReady
            || !_units.IsMotionEnabled(group))
            return false;
        if (group == MotionGroup.PcbSupply
            && (live ? _state.PlacementInBufferArea : _state.Display.PlacementInBufferArea)
            || group == MotionGroup.PcbPlacementHandler
            && (live ? _state.SupplyInBufferArea : _state.Display.SupplyInBufferArea))
            return false;

        var motion = _state.GetMotionStatus(group);
        return (!live || motion.Feedback.IsReady)
            && motion.Feedback.Axes.All(
                axis =>
                    (live ? motion.Feedback.GetAxisState(axis) : motion.Axes[axis].State)
                        is { Homed: true, ServoOn: true, Alarm: false, Emergency: false });
    }

    internal Task RunManualMotionAsync(
        MotionGroup group,
        Func<CancellationToken, Task> move,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation)
    {
        return RunManualAsync(
            move,
            group switch
            {
                MotionGroup.PcbSupply => MachineAlarm.PcbSupply,
                MotionGroup.PcbPlacementHandler => MachineAlarm.PcbPlacement,
                MotionGroup.BoltFastening => MachineAlarm.BoltFastening,
                MotionGroup.InspectionGantry => _units.Inspection
                    ? MachineAlarm.Inspection
                    : MachineAlarm.NgCarrierTransfer,
                _ => throw new ArgumentOutOfRangeException(nameof(group)),
            },
            () => CanUseManualMotion(group),
            cancellationToken,
            viewCancellation,
            canContinue: () => ManualMotionReady(group));
    }

    internal Task RunTeachingEditAsync(
        Func<CancellationToken, Task> edit,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation)
    {
        return RunManualAsync(
            edit,
            MachineAlarm.IoCommunication,
            () => _state.SetupEditingEnabled,
            cancellationToken,
            viewCancellation,
            canContinue: () => _state.ManualMode);
    }

    private bool MainConveyorPathClear
    {
        get
        {
            return GetMainConveyorPathBlock() == OutputBlockReason.None;
        }
    }

    private OutputBlockReason GetMainConveyorPathBlock()
    {
        if (_units.PcbPlacement)
        {
            if (!_placementHandler.CanMoveHorizontal)
                return OutputBlockReason.PlacementNotRaised;
            if (!_placementHandler.AtHorizontalZ)
                return OutputBlockReason.PlacementNotAtSafeZ;
        }

        if (_units.BoltFastening)
        {
            if (!_fasteningGantry.CanMoveHorizontal)
                return OutputBlockReason.FasteningNotRaised;
            if (!_fasteningGantry.AtSafeZ)
                return OutputBlockReason.FasteningNotAtSafeZ;
        }

        if (_units.Inspection || _units.NgCarrierTransfer)
        {
            if (!_ngTransfer.IsRaised)
                return OutputBlockReason.NgPickupNotRaised;
            if (_ngTransfer.CarrierDetected)
                return OutputBlockReason.NgCarrierDetected;
        }

        return OutputBlockReason.None;
    }

    private async Task RunManualAsync(
        Func<CancellationToken, Task> execute,
        MachineAlarm alarm,
        Func<bool> canStart,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation = default,
        Func<bool>? canContinue = null)
    {
        var activeCancellation = cancellationToken;
        try
        {
            if (!canStart())
                return;
            using var operation = _operations.Link(cancellationToken, viewCancellation);
            activeCancellation = operation.Token;
            void StopWhenUnavailable()
            {
                if (!(canContinue?.Invoke() ?? (_io.IsReady && _state.ManualMode && _state.SafetyReady)))
                    operation.Cancel();
            }

            _state.Changed += StopWhenUnavailable;
            try
            {
                StopWhenUnavailable();
                operation.Token.ThrowIfCancellationRequested();
                await execute(operation.Token);
            }
            finally
            {
                _state.Changed -= StopWhenUnavailable;
            }
        }
        catch (OperationCanceledException) when (activeCancellation.IsCancellationRequested
            || viewCancellation.IsCancellationRequested
            || _operations.IsShuttingDown)
        {
        }
        catch (IoTimeoutException exception)
        {
            if (!_state.IsError)
                _state.SetError(alarm, exception);
        }
        catch (Exception exception) when (exception is IOException or MotionException)
        {
            if (!_state.IsError)
                _state.SetError(
                    exception is MotionException && alarm != MachineAlarm.HomeFailed
                        ? MachineAlarm.MotionUnavailable
                        : alarm,
                    exception);
            Stop();
        }
    }

    internal bool CanHomeAxis(MotionGroup group, MotionAxis axis, bool live = true)
    {
        return _units.IsMotionEnabled(group)
            && group != MotionGroup.PcbSupply
            && !_state.IsRunning
            && (group != MotionGroup.PcbPlacementHandler || !_state.SupplyInBufferArea)
            && HomeAxisConditionsReady(group, axis, live);
    }

    private bool HomeAxisConditionsReady(MotionGroup group, MotionAxis axis, bool live = true)
    {
        if (!_units.IsMotionEnabled(group)
            || !_state.ManualMode
            || !_state.SafetyReady
            || !_state.ServoMainContactorOn
            || GetHomeBlock(group) != HomeBlockReason.None)
            return false;

        var motion = _state.GetMotionStatus(group);
        return motion.Feedback.IsReady
            && motion.Feedback.Axes.Where(candidate => candidate == axis || candidate == MotionAxis.Z)
                .All(
                    candidate =>
                        (live ? motion.Feedback.GetAxisState(candidate) : motion.Axes[candidate].State)
                            is { ServoOn: true, Alarm: false, Emergency: false });
    }

    internal Task HomeAxisAsync(MotionGroup group, MotionAxis axis, CancellationToken cancellationToken)
    {
        return RunManualAsync(
            async token =>
            {
                _state.SetHoming(true);
                try
                {
                    var homed = await (group switch
                    {
                        MotionGroup.PcbPlacementHandler => _placementHandler.HomeAxisAsync(axis, token),
                        MotionGroup.BoltFastening => _fasteningGantry.HomeAxisAsync(axis, token),
                        MotionGroup.InspectionGantry => _inspectionGantry.HomeAxisAsync(axis, token),
                        _ => throw new ArgumentOutOfRangeException(nameof(group)),
                    });
                    if (!homed && !token.IsCancellationRequested)
                        _state.SetError(MachineAlarm.HomeFailed);
                }
                finally
                {
                    _state.SetHoming(false);
                    _state.Refresh();
                }
            },
            MachineAlarm.HomeFailed,
            () => CanHomeAxis(group, axis),
            cancellationToken,
            canContinue: () => HomeAxisConditionsReady(group, axis));
    }

    internal bool CanHomeUnit(MotionGroup group, bool live = true)
    {
        return _state.GetMotionStatus(group).Feedback.Axes
            .All(axis => CanHomeAxis(group, axis, live));
    }

    internal Task HomeUnitAsync(MotionGroup group, CancellationToken cancellationToken)
    {
        return RunManualAsync(
            async token =>
            {
                _state.SetHoming(true);
                try
                {
                    bool homed;
                    switch (group)
                    {
                        case MotionGroup.PcbPlacementHandler:
                            homed = await _placementHandler.HomeAxisAsync(MotionAxis.Z, token);
                            if (homed)
                            {
                                await _placementHandler.MoveToHorizontalZAsync(token);
                                homed = await _placementHandler.HomeHorizontalAsync(token);
                            }
                            break;

                        case MotionGroup.BoltFastening:
                            homed = await _fasteningGantry.HomeAxisAsync(MotionAxis.Z, token);
                            if (homed)
                            {
                                await _fasteningGantry.MoveToSafeZAsync(token);
                                homed = await _fasteningGantry.HomeHorizontalAsync(token);
                            }
                            break;

                        case MotionGroup.InspectionGantry:
                            homed = await _inspectionGantry.HomeHorizontalAsync(token);
                            break;

                        default:
                            throw new ArgumentOutOfRangeException(nameof(group));
                    }

                    if (!homed && !token.IsCancellationRequested)
                        _state.SetError(MachineAlarm.HomeFailed);
                }
                finally
                {
                    _state.SetHoming(false);
                    _state.Refresh();
                }
            },
            MachineAlarm.HomeFailed,
            () => CanHomeUnit(group),
            cancellationToken,
            canContinue: () => _state.GetMotionStatus(group).Feedback.Axes
                .All(axis => HomeAxisConditionsReady(group, axis)));
    }

    internal bool CanSetServo(MotionGroup group, bool live = true)
    {
        return _units.IsMotionEnabled(group)
            && (live
                ? _state.ManualMode && _state.SafetyReady && !_state.IsRunning
                : _state.Display.Available
                    && !_state.Display.AutoMode
                    && _state.Display.SafetyReady
                    && !_state.Display.IsRunning)
            && (live
                ? _state.GetMotionStatus(group).Feedback.IsReady
                : _state.GetMotionStatus(group).Axes.Values.All(axis => axis.State is not null));
    }

    internal void ToggleServo(MotionGroup group, MotionAxis axis)
    {
        try
        {
            if (!CanSetServo(group))
                return;
            var on = !_state.GetMotionStatus(group).Feedback.GetAxisState(axis).ServoOn;
            switch (group)
            {
                case MotionGroup.PcbSupply:
                    _supplyHandler.SetServo(axis, on);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    _placementHandler.SetServo(axis, on);
                    break;
                case MotionGroup.BoltFastening:
                    _fasteningGantry.SetServo(axis, on);
                    break;
                case MotionGroup.InspectionGantry:
                    _inspectionGantry.SetServo(axis, on);
                    break;
            }
        }
        catch (Exception exception)
        {
            _state.SetError(MachineAlarm.MotionUnavailable, exception);
            _operations.Cancel();
        }
    }

    public async Task RunAdcProtocolAsync(
        Func<CancellationToken, Task> command,
        CancellationToken cancellationToken)
    {
        if (!CanUseAdcProtocol)
        {
            throw new InvalidOperationException("ADC diagnostics require an idle machine in manual mode.");
        }

        using var operation = _operations.Link(cancellationToken);
        void StopWhenUnavailable()
        {
            if (!AdcProtocolAvailable)
                operation.Cancel();
        }

        _state.Changed += StopWhenUnavailable;
        try
        {
            StopWhenUnavailable();
            operation.Token.ThrowIfCancellationRequested();
            await command(operation.Token);
        }
        finally
        {
            _state.Changed -= StopWhenUnavailable;
        }
    }

    internal async Task RunBoltTestAsync(
        Func<CancellationToken, Task> test,
        CancellationToken cancellationToken)
    {
        // RunAdcProtocolAsync owns admission and cancellation for this command.
        if (!AdcProtocolAvailable)
        {
            throw new InvalidOperationException("Bolt testing requires safe manual mode.");
        }

        try
        {
            _state.SetBoltTestRunning(true);
            await test(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _state.SetError(MachineAlarm.BoltFastening, exception);
            throw;
        }
        finally
        {
            _state.SetBoltTestRunning(false);
        }
    }

    public Task ResetAsync()
    {
        _state.SilenceBuzzer();
        lock (_resetGate)
        {
            // Repeated clicks acknowledge the buzzer, but share the current recovery.
            if (!_resetTask.IsCompleted)
                return _resetTask;
            if (!CanReset)
            {
                _log?.Write("Machine RESET: buzzer silenced; hardware recovery conditions are not satisfied.");
                return Task.CompletedTask;
            }

            return _resetTask = ResetHardwareAsync();
        }
    }

    private async Task ResetHardwareAsync()
    {
        _log?.Write("Machine RESET started.");
        using var operation = _operations.Link();
        var (alarm, error) = await InitializeIoAsync(operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        if (alarm != MachineAlarm.None)
        {
            _state.SetError(alarm, error);
            return;
        }

        if (!_state.SafetyReady || !_state.ManualMode && !_state.DoorInterlockReady)
        {
            return;
        }

        var failures = new List<Exception>();
        void RecordFailure(MachineAlarm deviceAlarm, string device, Exception exception)
        {
            _log?.Error($"{device} reset failed.", exception);
            if (alarm == MachineAlarm.None)
            {
                alarm = deviceAlarm;
            }

            failures.Add(exception);
        }

        foreach (var group in Enum.GetValues<MotionGroup>())
        {
            operation.Token.ThrowIfCancellationRequested();
            if (!_units.IsMotionEnabled(group))
            {
                continue;
            }

            try
            {
                switch (group)
                {
                    case MotionGroup.PcbSupply:
                        _supplyHandler.InitializeMotion();
                        operation.Token.ThrowIfCancellationRequested();
                        _supplyHandler.ResetMotion();
                        break;
                    case MotionGroup.PcbPlacementHandler:
                        _placementHandler.InitializeMotion();
                        operation.Token.ThrowIfCancellationRequested();
                        _placementHandler.ResetMotion();
                        break;
                    case MotionGroup.BoltFastening:
                        _fasteningGantry.InitializeMotion();
                        operation.Token.ThrowIfCancellationRequested();
                        _fasteningGantry.ResetMotion();
                        break;
                    case MotionGroup.InspectionGantry:
                        _inspectionGantry.InitializeMotion();
                        operation.Token.ThrowIfCancellationRequested();
                        _inspectionGantry.ResetMotion();
                        break;
                }
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordFailure(MachineAlarm.MotionUnavailable, group.ToString(), exception);
            }
        }

        operation.Token.ThrowIfCancellationRequested();
        if (_units.Inspection)
        {
            try
            {
                await _boltInspector.InitializeVisionAsync(operation.Token);
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordFailure(MachineAlarm.Inspection, "Vision / lighting", exception);
            }
        }

        operation.Token.ThrowIfCancellationRequested();
        if (_units.BoltFastening)
        {
            try
            {
                await _fasteningGantry.ResetHeadsAsync(operation.Token);
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordFailure(MachineAlarm.BoltFastening, "Bolt controllers", exception);
            }
        }

        operation.Token.ThrowIfCancellationRequested();
        if (failures.Count > 0)
        {
            _state.SetError(alarm, failures.Count == 1 ? failures[0] : new AggregateException(failures));
            return;
        }

        _state.ClearError();
        _state.Refresh();
    }

    public async Task RaiseCylindersAsync(CancellationToken cancellationToken)
    {
        if (!CanRaiseCylinders)
            return;

        using var operation = _operations.Link(cancellationToken);
        void StopWhenUnavailable()
        {
            if (!_state.ManualMode
                || !_state.SafetyReady
                || !_io.IsReady
                || Array.Exists(CarrierInputs, _io.GetInput))
                operation.Cancel();
        }

        async Task RaiseAsync(Func<CancellationToken, Task> raise, MachineAlarm alarm)
        {
            try
            {
                await raise(operation.Token);
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException
                    && !operation.IsCancellationRequested)
                    _state.SetError(alarm, exception);
                operation.Cancel();
            }
        }

        _state.Changed += StopWhenUnavailable;
        try
        {
            StopWhenUnavailable();
            var tasks = new List<Task>(3);
            if (BufferHandlersEnabled)
                tasks.Add(RaiseAsync(_placementHandler.RaiseAsync, MachineAlarm.PcbPlacement));
            if (_units.BoltFastening)
                tasks.Add(RaiseAsync(_fasteningGantry.RaiseCylindersAsync, MachineAlarm.BoltFastening));
            if (InspectionGantryEnabled)
                tasks.Add(RaiseAsync(_ngTransfer.RaiseAsync, MachineAlarm.NgCarrierTransfer));
            await Task.WhenAll(tasks);
        }
        finally
        {
            _state.Changed -= StopWhenUnavailable;
        }
    }

    public async Task HomeAsync(CancellationToken cancellationToken)
    {
        if (!CanHome)
        {
            return;
        }

        using var operation = _operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        void StopWhenHomeBecomesUnavailable()
        {
            if (operation.IsCancellationRequested)
            {
                return;
            }

            bool hardwareAvailable;
            try
            {
                var motion = _state.MotionReadiness;
                hardwareAvailable = !motion.Faulted && motion.ServosOn && _state.ServoMainContactorOn;
            }
            catch (Exception exception)
            {
                operation.Cancel();
                _state.SetError(MachineAlarm.MotionUnavailable, exception);
                return;
            }

            if (!hardwareAvailable)
            {
                operation.Cancel();
                _state.SetError(MachineAlarm.MotionUnavailable);
            }
            else if (_state.IsError || HomeBlock != HomeBlockReason.None)
            {
                operation.Cancel();
            }
        }

        async Task RunHomeStepAsync(Task step)
        {
            try
            {
                await step;
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException
                    && !cancellationToken.IsCancellationRequested)
                {
                    _state.SetError(MachineAlarm.HomeFailed, exception);
                }

                throw;
            }
        }

        async Task CheckHomeAsync(Task<bool> homing)
        {
            await RunHomeStepAsync(homing);
            if (!await homing)
            {
                _state.SetError(MachineAlarm.HomeFailed);
            }
        }

        _state.Changed += StopWhenHomeBecomesUnavailable;
        try
        {
            _state.SetHoming(true);
            var zHomeTasks = new List<Task>(3);
            if (_units.PcbPlacement)
            {
                zHomeTasks.Add(
                    CheckHomeAsync(_placementHandler.HomeAxisAsync(MotionAxis.Z, cancellationToken)));
            }

            if (_units.PcbSupply)
            {
                zHomeTasks.Add(CheckHomeAsync(_supplyHandler.PrepareHomeAsync(cancellationToken)));
            }

            if (_units.BoltFastening)
            {
                zHomeTasks.Add(
                    CheckHomeAsync(_fasteningGantry.HomeAxisAsync(MotionAxis.Z, cancellationToken)));
            }

            await Task.WhenAll(zHomeTasks);
            cancellationToken.ThrowIfCancellationRequested();

            var safeZTasks = new List<Task>(2);
            if (_units.PcbPlacement)
            {
                safeZTasks.Add(
                    RunHomeStepAsync(_placementHandler.MoveToHorizontalZAsync(cancellationToken)));
            }

            if (_units.BoltFastening)
            {
                safeZTasks.Add(RunHomeStepAsync(_fasteningGantry.MoveToSafeZAsync(cancellationToken)));
            }

            await Task.WhenAll(safeZTasks);

            var horizontalHomeTasks = new List<Task>(4);
            if (_units.PcbPlacement)
            {
                horizontalHomeTasks.Add(
                    CheckHomeAsync(_placementHandler.HomeHorizontalAsync(cancellationToken)));
            }

            if (_units.PcbSupply)
            {
                horizontalHomeTasks.Add(
                    CheckHomeAsync(_supplyHandler.CompleteHomeAsync(cancellationToken)));
            }

            if (_units.BoltFastening)
            {
                horizontalHomeTasks.Add(
                    CheckHomeAsync(_fasteningGantry.HomeHorizontalAsync(cancellationToken)));
            }

            if (InspectionGantryEnabled)
            {
                horizontalHomeTasks.Add(
                    CheckHomeAsync(_inspectionGantry.HomeHorizontalAsync(cancellationToken)));
            }

            await Task.WhenAll(horizontalHomeTasks);
            cancellationToken.ThrowIfCancellationRequested();

            if (_units.PcbSupply)
            {
                await _supplyHandler.MoveToRotationZAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _state.SetError(MachineAlarm.HomeFailed, exception);
        }
        finally
        {
            _state.Changed -= StopWhenHomeBecomesUnavailable;
            _state.SetHoming(false);
            _state.Refresh();
        }
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input == InputIo.AutoMode && _state.AutoMode && !_state.AutomaticRunning)
            StopRunOutputs();

        if (input is InputIo.AutoMode
            or InputIo.PcbPlacementHandlerUp
            or InputIo.PcbPlacementHandlerDown
            or InputIo.PickupHeadUp
            or InputIo.PickupHeadDown
            or InputIo.ShootingHeadUp
            or InputIo.ShootingHeadDown
            or InputIo.NgCarrierPickupUp
            or InputIo.NgCarrierPickupDown
            or InputIo.NgCarrierDetected)
        {
            CheckMotionInterlocks();
        }

        if (Array.IndexOf(CarrierInputs, input) >= 0
            || input is InputIo.PcbPlacementHandlerUp
                or InputIo.PcbPlacementHandlerDown
                or InputIo.PcbPlacementIpmUp
                or InputIo.PcbPlacementIpmDown
                or InputIo.PickupHeadUp
                or InputIo.PickupHeadDown
                or InputIo.ShootingHeadUp
                or InputIo.ShootingHeadDown
                or InputIo.NgCarrierPickupUp
                or InputIo.NgCarrierPickupDown)
            _state.Refresh();

        if (MachineState.IsSafetyInput(input))
        {
            var alarm = SafetyAlarm();
            if (alarm != MachineAlarm.None)
            {
                _state.SetError(alarm);
                Stop();
            }
        }

        if (input == InputIo.ServoMainContactorOn || MachineState.IsSafetyInput(input))
            _state.RequestDisplayRefresh();

        if (input == InputIo.ResetButton && value && _options.UseResetButton)
        {
            _ = ResetAsync();
        }
    }

    private void CheckMotionInterlocks()
    {
        if (_units.BoltFastening
            && _fasteningGantry.Feedback.Command == MotionCommand.Adjustment
            && (!ManualMotionReady(MotionGroup.BoltFastening)
                || _state.AutomaticRunning
                || _state.IsHoming
                || _state.BoltTestRunning))
        {
            Stop();
        }

        var fasteningBlocked = _units.BoltFastening
            && _fasteningGantry.Feedback.Command != MotionCommand.Adjustment
            && !_fasteningGantry.CanMoveHorizontal
            && _fasteningGantry.Feedback.IsMovingHorizontal;
        var alarm = MachineAlarm.None;
        if (_units.PcbPlacement
            && !_placementHandler.CanMoveHorizontal
            && _placementHandler.Feedback.IsMovingHorizontal)
        {
            alarm = MachineAlarm.PcbPlacement;
        }
        else if (fasteningBlocked)
        {
            alarm = MachineAlarm.BoltFastening;
        }
        else if (InspectionGantryEnabled
            && ((!_inspectionGantry.CanMove && _inspectionGantry.Feedback.IsMoving)
                || (_state.IsHoming && !_inspectionGantry.CanHome)))
        {
            alarm = MachineAlarm.NgCarrierTransfer;
        }

        if (alarm != MachineAlarm.None)
        {
            _state.SetError(alarm);
            Stop();
        }
    }

    private MachineAlarm SafetyAlarm()
    {
        if (_options.UseEmergencyStop && !_state.EmergencyStopReleased)
        {
            return MachineAlarm.EmergencyStop;
        }

        if (!_state.DoorInterlockReady)
        {
            return MachineAlarm.DoorOpen;
        }

        return _options.UseAirPressureInterlock && !_state.AirPressureOk
            ? MachineAlarm.AirPressureLow
            : MachineAlarm.None;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!CanStart)
        {
            return;
        }

        var repeat = _state.RepeatEnabled;
        var startedInManual = _state.ManualMode;
        using var operation = _operations.Link(cancellationToken);
        void StopWhenOperationBecomesUnavailable()
        {
            if (_state.IsError)
            {
                operation.Cancel();
                return;
            }

            try
            {
                var modeReady = _state.ManualMode == startedInManual;
                if (modeReady && _state.CanOperate && _state.DoorInterlockReady)
                {
                    return;
                }

                if (modeReady
                    && _state.SafetyReady
                    && _state.DoorInterlockReady
                    && !_state.Ready)
                {
                    _state.SetError(MachineAlarm.MotionUnavailable);
                }
            }
            catch (Exception exception)
            {
                _state.SetError(MachineAlarm.MotionUnavailable, exception);
            }

            operation.Cancel();
        }

        void StopWhenDisplayedMotionBecomesUnavailable()
        {
            if (operation.IsCancellationRequested)
                return;
            if (_state.IsError)
            {
                operation.Cancel();
                return;
            }

            var display = _state.Display;
            if (display.Available
                && display.Homed
                && display.ServoPowerOn
                && !display.MotionFaulted)
                return;
            // Consume the completed scan, not a second native read that could miss a
            // transient fault. Disabled axes have already been excluded from this snapshot.
            try
            {
                _state.SetError(
                    _io.IsReady ? MachineAlarm.MotionUnavailable : MachineAlarm.IoCommunication,
                    display.ReadError ?? new InvalidOperationException(
                        $"Enabled motion feedback became unavailable during automatic operation: " + $"homed={display.Homed}, servoPower={display.ServoPowerOn}, faulted={display.MotionFaulted}."));
            }
            finally
            {
                operation.Cancel();
            }
        }

        try
        {
            var (startAlarm, startError) = await InitializeHardwareAsync(operation.Token);
            if (startAlarm == MachineAlarm.None && _units.Inspection)
            {
                try
                {
                    await Task.Run(_boltInspector.CheckReady, operation.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    startAlarm = MachineAlarm.Inspection;
                    startError = exception;
                }
            }

            operation.Token.ThrowIfCancellationRequested();
            if (startAlarm != MachineAlarm.None)
            {
                _state.SetError(startAlarm, startError);
                return;
            }

            _state.Changed += StopWhenOperationBecomesUnavailable;
            _state.DisplayChanged += StopWhenDisplayedMotionBecomesUnavailable;
            StopWhenOperationBecomesUnavailable();
            if (operation.IsCancellationRequested)
            {
                return;
            }

            _state.SetAutomaticRunning(true);
            if (repeat)
            {
                await RunRepeatAsync(operation.Token);
            }
            else
            {
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                await RunAutomaticUnitsAsync(cycle, repeat: false);
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        finally
        {
            _state.Changed -= StopWhenOperationBecomesUnavailable;
            _state.DisplayChanged -= StopWhenDisplayedMotionBecomesUnavailable;
            _state.SetAutomaticRunning(false);
            StopRunOutputs();
            _state.Refresh();
        }
    }

    private async Task<(MachineAlarm Alarm, Exception? Error)> InitializeIoAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stage = "Control I/O initialization";
        _log?.Write(stage + " started.");
        try
        {
            // Reset can come from InputChanged; do not make its monitor wait for itself.
            await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _io.Initialize();
                    cancellationToken.ThrowIfCancellationRequested();
                    stage = "Control I/O readiness check";
                    _io.CheckReady();
                },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            stage = "Stopping run outputs after I/O initialization";
            StopRunOutputs();
            stage = "Setting main conveyor forward direction";
            _io.SetOutput(OutputIo.MainConveyorForward, true);
            _log?.Write("Control I/O initialization and readiness check completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log?.Error($"{stage} failed. {exception.Message}");
            return (MachineAlarm.IoCommunication, exception);
        }

        return (MachineAlarm.None, null);
    }

    private async Task<(MachineAlarm Alarm, Exception? Error)> InitializeHardwareAsync(
        CancellationToken cancellationToken)
    {
        var ioResult = await InitializeIoAsync(cancellationToken);
        if (ioResult.Alarm != MachineAlarm.None)
        {
            return ioResult;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stage = "Motion initialization";
        try
        {
            if (_units.PcbSupply)
            {
                stage = "PCB supply motion initialization";
                _log?.Write(stage + " started.");
                _supplyHandler.InitializeMotion();
            }

            if (_units.PcbPlacement)
            {
                stage = "PCB placement motion initialization";
                _log?.Write(stage + " started.");
                _placementHandler.InitializeMotion();
            }

            if (_units.BoltFastening)
            {
                stage = "Bolt fastening motion initialization";
                _log?.Write(stage + " started.");
                _fasteningGantry.InitializeMotion();
            }

            if (InspectionGantryEnabled)
            {
                stage = "Inspection motion initialization";
                _log?.Write(stage + " started.");
                _inspectionGantry.InitializeMotion();
            }

            _log?.Write("Motion initialization completed.");
        }
        catch (Exception exception)
        {
            _log?.Error($"{stage} failed. {exception.Message}");
            return (MachineAlarm.MotionUnavailable, exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_units.Inspection)
        {
            try
            {
                _log?.Write("Vision / lighting initialization started.");
                await _boltInspector.InitializeVisionAsync(cancellationToken);
                _log?.Write("Vision / lighting initialization completed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log?.Error($"Vision / lighting initialization failed. {exception.Message}");
                return (MachineAlarm.Inspection, exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_units.BoltFastening)
        {
            try
            {
                _log?.Write("Bolt controller readiness check started.");
                await _fasteningGantry.CheckReadyAsync(cancellationToken);
                _log?.Write("Bolt controller readiness check completed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log?.Error($"Bolt controller readiness check failed. {exception.Message}");
                return (MachineAlarm.BoltFastening, exception);
            }
        }

        return (MachineAlarm.None, null);
    }

    private bool BufferHandlersEnabled
    {
        get
        {
            return _units.PcbSupply || _units.PcbPlacement;
        }
    }

    private bool InspectionGantryEnabled
    {
        get
        {
            return _units.IsMotionEnabled(MotionGroup.InspectionGantry);
        }
    }

    internal void StopRunOutputs()
    {
        // One device's failed STOP must not skip STOP on the remaining devices.
        Action[] stops = [
            _conveyor.Stop,
            _shootingBoltFeeder.Stop,
            _fasteningGantry.StopShooting,
            _ngConveyor.Stop,
            () => _supplyHandler.SetUpstreamReady(false),
        ];
        List<Exception>? failures = null;
        foreach (var stop in stops)
        {
            try
            {
                stop();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more devices could not be stopped.", failures);
    }

    private void OnIoFaulted(Exception exception)
    {
        _state.SetError(MachineAlarm.IoCommunication, exception);
        try
        {
            Stop();
        }
        catch (Exception stopError)
        {
            // Keep the communication fault as the alarm's cause, and report failed STOPs separately.
            _log?.Error("Stopping outputs after the I/O fault also failed.", stopError);
        }
    }
}
