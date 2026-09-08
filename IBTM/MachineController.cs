using System;
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

public sealed class MachineController
{
    private static readonly InputIo[] CarrierInputs =
    [
        InputIo.MainConveyorEntryCarrierDetected,
        InputIo.PcbPlacementCarrierPresent,
        InputIo.BoltFasteningCarrierPresent,
        InputIo.InspectionCarrierPresent,
        InputIo.MainConveyorExitCarrierDetected,
        InputIo.NgCarrierDetected,
        InputIo.NgShuttleCarrierDetected,
        InputIo.NgConveyorPosition1Occupied,
        InputIo.NgConveyorPosition2Occupied,
        InputIo.NgConveyorPosition3Occupied,
    ];

    private readonly MachineState _state;
    private readonly OperationCancellation _operations;
    private readonly MachineOptions _options;
    private readonly UnitSettings _units;
    private readonly Recipe _recipe;
    private readonly HomeSettings _home;
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
    private readonly BoltInspector _boltInspector;

    public MachineController(
        MachineState state,
        OperationCancellation operations,
        MachineOptions options,
        UnitSettings units,
        Recipe recipe,
        HomeSettings home,
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
        BoltInspector boltInspector)
    {
        _state = state;
        _operations = operations;
        _options = options;
        _units = units;
        _recipe = recipe;
        _home = home;
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
        _boltInspector = boltInspector;
        io.InputChanged += OnInputChanged;
        io.Faulted += OnIoFaulted;
        placementHandler.Feedback.MovingChanged += _ => CheckMotionInterlocks();
        fasteningGantry.Feedback.StateChanged += CheckMotionInterlocks;
        inspectionGantry.Feedback.MovingChanged += _ => CheckMotionInterlocks();
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

            if (!_state.SafetyReady
                || !(_state.ManualMode || _state.DoorInterlockReady))
            {
                return false;
            }

            var motion = _state.MotionReadiness;
            return _state.IsError
                || motion.Faulted
                || !motion.ServosOn
                || !_state.ServoMainContactorOn;
        }
    }

    public bool CanHome
    {
        get
        {
            if (_operations.IsShuttingDown || !_state.SafetyReady || !_state.DoorInterlockReady)
            {
                return false;
            }

            var motion = _state.MotionReadiness;
            return motion.ServosOn
                && !motion.Faulted
                && _state.ServoMainContactorOn
                && !motion.Homed
                && !_state.IsError
                && !_state.IsRunning
                && HomeBlock == HomeBlockReason.None
                && (!BufferHandlersEnabled || _pcbSupply.CanHome);
        }
    }
    public HomeBlockReason HomeBlock => GetHomeBlock();
    public bool CanRaiseCylinders =>
        _state.ManualOutputsEnabled
        && (BufferHandlersEnabled || _units.BoltFastening || InspectionGantryEnabled)
        && !Array.Exists(CarrierInputs, _io.GetInput);

    internal HomeBlockReason GetHomeBlock(MotionGroup? group = null)
    {
        if (!_io.IsReady) return HomeBlockReason.IoUnavailable;
        if (Array.Exists(CarrierInputs, _io.GetInput)) return HomeBlockReason.CarrierDetected;

        if ((group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler
                || group is null && BufferHandlersEnabled)
            && !_placementHandler.CanMoveHorizontal)
            return HomeBlockReason.PlacementNotRaised;

        if ((group == MotionGroup.BoltFastening || group is null && _units.BoltFastening)
            && !_fasteningGantry.CanMoveHorizontal)
            return HomeBlockReason.FasteningNotRaised;

        if ((group == MotionGroup.InspectionGantry || group is null && InspectionGantryEnabled)
            && !_inspectionGantry.CanMove)
            return HomeBlockReason.NgPickupNotRaised;

        return HomeBlockReason.None;
    }

    public bool CanStart =>
        !_operations.IsShuttingDown
        && !_state.IsRunning
        && StartBlock == StartBlockReason.None;

    public StartBlockReason StartBlock
    {
        get
        {
            if (_state.Alarm == MachineAlarm.EmergencyStop) return StartBlockReason.EmergencyStop;
            if (_state.Alarm == MachineAlarm.DoorOpen) return StartBlockReason.DoorOpen;
            if (_state.Alarm == MachineAlarm.AirPressureLow) return StartBlockReason.AirPressure;
            if (_state.Alarm == MachineAlarm.BufferConflict || _state.BufferConflict) return StartBlockReason.BufferConflict;
            if (_state.IsError) return StartBlockReason.Alarm;
            if (_options.UseEmergencyStop && !_state.EmergencyStopReleased) return StartBlockReason.EmergencyStop;
            if (_options.UseAirPressureInterlock && !_state.AirPressureOk) return StartBlockReason.AirPressure;
            var motion = _state.MotionReadiness;
            if (motion.Faulted) return StartBlockReason.MotionFault;
            if (!motion.ServosOn || !_state.ServoMainContactorOn) return StartBlockReason.ServoOff;
            if (!_state.DoorInterlockReady) return StartBlockReason.DoorOpen;
            if (!motion.Homed) return StartBlockReason.HomeRequired;
            if (!_state.AutoMode) return StartBlockReason.AutoMode;
            if (!TeachingReady) return StartBlockReason.TeachingIncomplete;
            return _units.HasEnabledUnit() ? StartBlockReason.None : StartBlockReason.NoUnitEnabled;
        }
    }
    public bool CanTestBoltHead =>
        !_operations.IsShuttingDown && _state.ManualControlsEnabled;
    public bool CanUseAdcProtocol =>
        AdcProtocolAvailable && !_state.IsRunning;
    public bool AdcProtocolAvailable =>
        !_operations.IsShuttingDown
        && _state.ManualMode
        && _state.SafetyReady;
    public bool TeachingReady =>
        !_units.BoltFastening && !_units.Inspection
        || _recipe.Pcb.IsDefined && _recipe.Pcb.BoltPoints.Count > 0
        && CarrierCoordinates.IsDefined(
            _carrierReference.UpperLeftLocatingPin,
            _carrierReference.LowerRightLocatingPin)
        && _recipe.Pcb.GetBolts().All(bolt =>
            bolt is { X: not null, Y: not null }
            && (!_units.BoltFastening
                || _fasteningGantry.HasReference(bolt.Head)))
        && (!_units.Inspection || Enum.GetValues<HeatSinkSlot>().All(_boltInspector.HasBarcodeRegion));

    public async Task InitializeAsync()
    {
        using var operation = _operations.Link();
        var (alarm, error) = await CheckHardwareAsync(operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        if (alarm == MachineAlarm.None)
        {
            alarm = SafetyAlarm();
        }

        if (alarm == MachineAlarm.None)
        {
            _state.Refresh();
        }
        else
        {
            _state.SetError(alarm, error);
        }
    }

    public void Stop()
    {
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
        var shutdown = _operations.ShutdownAsync();
        try
        {
            StopRunOutputs();
        }
        finally
        {
            await shutdown;
        }
    }

    internal MotionStatus GetMotionStatus(MotionGroup group) => group switch
    {
        MotionGroup.PcbSupply => _supplyHandler.Motion,
        MotionGroup.PcbPlacementHandler => _placementHandler.Motion,
        MotionGroup.BoltFastening => _fasteningGantry.Motion,
        MotionGroup.InspectionGantry => _inspectionGantry.Motion,
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    private IMotionFeedback GetMotionFeedback(MotionGroup group) => GetMotionStatus(group).Feedback;

    internal Task RunManualMotionAsync(
        MotionGroup group,
        Func<CancellationToken, Task> move,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation) =>
        RunManualAsync(move, group switch
        {
            MotionGroup.PcbSupply => MachineAlarm.PcbSupply,
            MotionGroup.PcbPlacementHandler => MachineAlarm.PcbPlacement,
            MotionGroup.BoltFastening => MachineAlarm.BoltFastening,
            MotionGroup.InspectionGantry => MachineAlarm.NgCarrierTransfer,
            _ => throw new ArgumentOutOfRangeException(nameof(group)),
        }, cancellationToken, viewCancellation);

    internal bool TrySetManualOutput(OutputIo signal, bool value)
    {
        if (!_state.ManualOutputsEnabled) return false;
        try
        {
            _io.SetOutput(signal, value);
            return true;
        }
        catch (Exception exception)
        {
            OnIoFaulted(exception);
            return false;
        }
    }

    internal Task RunManualOutputAsync(
        TeachingOutput output,
        bool value,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation) =>
        RunManualAsync(token => output.SetAsync(value, token), output.Owner switch
        {
            HardwareArea.MainConveyor => MachineAlarm.MainConveyor,
            HardwareArea.PcbSupply => MachineAlarm.PcbSupply,
            HardwareArea.PcbPlacementHandler => MachineAlarm.PcbPlacement,
            HardwareArea.BoltFastening => MachineAlarm.BoltFastening,
            HardwareArea.NgCarrierTransfer => MachineAlarm.NgCarrierTransfer,
            _ => throw new ArgumentOutOfRangeException(nameof(output)),
        }, cancellationToken, viewCancellation);

    private async Task RunManualAsync(
        Func<CancellationToken, Task> execute,
        MachineAlarm alarm,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation = default,
        Func<bool>? canContinue = null)
    {
        using var operation = _operations.Link(cancellationToken, viewCancellation);
        void StopWhenUnavailable()
        {
            if (!_state.ManualMode || !_state.SafetyReady || _state.IsError
                || canContinue?.Invoke() == false)
                operation.Cancel();
        }

        _state.Changed += StopWhenUnavailable;
        try
        {
            StopWhenUnavailable();
            operation.Token.ThrowIfCancellationRequested();
            await execute(operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (IoTimeoutException exception)
        {
            if (!_state.IsError) _state.SetError(alarm, exception);
        }
        catch (MotionException exception)
        {
            if (!_state.IsError)
                _state.SetError(alarm == MachineAlarm.HomeFailed ? alarm : MachineAlarm.MotionUnavailable, exception);
            Stop();
        }
        finally
        {
            _state.Changed -= StopWhenUnavailable;
        }
    }

    internal bool CanHomeAxis(MotionGroup group, MotionAxis axis) =>
        group != MotionGroup.PcbSupply
        && !_state.IsRunning
        && (group != MotionGroup.PcbPlacementHandler || !_state.SupplyInBufferArea)
        && HomeAxisConditionsReady(group, axis);

    private bool HomeAxisConditionsReady(MotionGroup group, MotionAxis axis)
    {
        if (!_state.ManualMode || !_state.SafetyReady || !_state.DoorInterlockReady
            || _state.IsError || !_state.ServoMainContactorOn
            || GetHomeBlock(group) != HomeBlockReason.None)
            return false;

        var motion = GetMotionFeedback(group);
        return motion.IsReady && motion.Axes
            .Where(candidate => candidate == axis || candidate == MotionAxis.Z)
            .All(candidate => motion.GetAxisState(candidate) is { ServoOn: true, Alarm: false, Emergency: false });
    }

    internal async Task HomeAxisAsync(
        MotionGroup group,
        MotionAxis axis,
        CancellationToken cancellationToken)
    {
        if (!CanHomeAxis(group, axis)) return;
        try
        {
            _state.SetHoming(true);
            await RunManualAsync(async token =>
            {
                var velocity = axis == MotionAxis.Z ? _home.ZSpeed : _home.HorizontalSpeed;
                var homed = await (group switch
                {
                    MotionGroup.PcbPlacementHandler => _placementHandler.HomeAxisAsync(axis, velocity, token),
                    MotionGroup.BoltFastening => _fasteningGantry.HomeAxisAsync(axis, velocity, token),
                    MotionGroup.InspectionGantry => _inspectionGantry.HomeAxisAsync(axis, velocity, token),
                    _ => throw new ArgumentOutOfRangeException(nameof(group)),
                });
                if (!homed && !token.IsCancellationRequested) _state.SetError(MachineAlarm.HomeFailed);
            }, MachineAlarm.HomeFailed, cancellationToken,
                canContinue: () => HomeAxisConditionsReady(group, axis));
        }
        finally
        {
            _state.SetHoming(false);
            _state.Refresh();
        }
    }

    internal bool CanSetServo(MotionGroup group) =>
        _state.ManualMode && _state.SafetyReady && !_state.IsRunning
        && GetMotionFeedback(group).IsReady;

    internal void SetServo(MotionGroup group, MotionAxis axis, bool on)
    {
        if (!CanSetServo(group)) return;
        switch (group)
        {
            case MotionGroup.PcbSupply: _supplyHandler.SetServo(axis, on); break;
            case MotionGroup.PcbPlacementHandler: _placementHandler.SetServo(axis, on); break;
            case MotionGroup.BoltFastening: _fasteningGantry.SetServo(axis, on); break;
            case MotionGroup.InspectionGantry: _inspectionGantry.SetServo(axis, on); break;
        }
    }

    public async Task RunAdcProtocolAsync(
        Func<CancellationToken, Task> command,
        CancellationToken cancellationToken)
    {
        if (!CanUseAdcProtocol)
        {
            throw new InvalidOperationException(
                "ADC diagnostics require an idle machine in manual mode.");
        }

        using var operation = _operations.Link(cancellationToken);
        void StopWhenUnavailable()
        {
            try
            {
                if (AdcProtocolAvailable
                    && (!_state.BoltTestRunning || _state.CanOperate))
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                _state.SetError(MachineAlarm.MotionUnavailable, exception);
            }

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

    internal async Task<BoltResult> TestBoltHeadAsync(
        IBoltHead head,
        CancellationToken cancellationToken)
    {
        // RunAdcProtocolAsync owns admission and cancellation for this command.
        if (!_state.CanOperate || !_state.ManualMode)
        {
            throw new InvalidOperationException(
                "Bolt testing requires a ready machine in manual mode.");
        }

        try
        {
            _state.SetBoltTestRunning(true);
            await head.CheckReadyAsync(cancellationToken);
            return await head.TightenAsync(cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            _state.SetError(MachineAlarm.BoltFastening, exception);
            throw;
        }
        finally
        {
            _state.SetBoltTestRunning(false);
        }
    }

    public async Task ResetAsync()
    {
        if (!CanReset)
        {
            return;
        }

        using var operation = _operations.Link();
        var (alarm, error) = await CheckHardwareAsync(operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        if (alarm != MachineAlarm.None)
        {
            _state.SetError(alarm, error);
            return;
        }

        if (!_state.SafetyReady
            || !_state.ManualMode && !_state.DoorInterlockReady)
        {
            return;
        }

        try
        {
            if (BufferHandlersEnabled)
            {
                _supplyHandler.ResetMotion();
                _placementHandler.ResetMotion();
            }

            if (_units.BoltFastening)
            {
                _fasteningGantry.ResetMotion();
            }

            if (InspectionGantryEnabled)
            {
                _inspectionGantry.ResetMotion();
            }
        }
        catch (Exception exception)
        {
            _state.SetError(MachineAlarm.MotionUnavailable, exception);
            return;
        }

        _state.ClearError();
        _state.Refresh();
    }

    public async Task RaiseCylindersAsync(CancellationToken cancellationToken)
    {
        if (!CanRaiseCylinders) return;

        using var operation = _operations.Link(cancellationToken);
        void StopWhenUnavailable()
        {
            if (!_state.ManualMode || !_state.SafetyReady || _state.IsError
                || !_io.IsReady || Array.Exists(CarrierInputs, _io.GetInput))
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
                if (exception is not OperationCanceledException && !operation.IsCancellationRequested)
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
                hardwareAvailable = !motion.Faulted
                    && motion.ServosOn
                    && _state.ServoMainContactorOn;
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
                if (exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
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
            if (BufferHandlersEnabled)
            {
                zHomeTasks.Add(CheckHomeAsync(_placementHandler.HomeZAsync(
                    _home.ZSpeed,
                    cancellationToken)));
                zHomeTasks.Add(CheckHomeAsync(_supplyHandler.PrepareHomeAsync(
                    _home.ZSpeed,
                    cancellationToken)));
            }

            if (_units.BoltFastening)
            {
                zHomeTasks.Add(CheckHomeAsync(_fasteningGantry.HomeZAsync(
                    _home.ZSpeed,
                    cancellationToken)));
            }

            await Task.WhenAll(zHomeTasks);
            cancellationToken.ThrowIfCancellationRequested();

            var safeZTasks = new List<Task>(2);
            if (BufferHandlersEnabled)
            {
                safeZTasks.Add(RunHomeStepAsync(
                    _placementHandler.MoveToHorizontalZAsync(
                        cancellationToken)));
            }

            if (_units.BoltFastening)
            {
                safeZTasks.Add(RunHomeStepAsync(
                    _fasteningGantry.MoveToSafeZAsync(cancellationToken)));
            }

            await Task.WhenAll(safeZTasks);

            var horizontalHomeTasks = new List<Task>(4);
            if (BufferHandlersEnabled)
            {
                horizontalHomeTasks.Add(
                    CheckHomeAsync(_placementHandler.HomeHorizontalAsync(
                        _home.HorizontalSpeed,
                        cancellationToken)));
                horizontalHomeTasks.Add(
                    CheckHomeAsync(_supplyHandler.CompleteHomeAsync(
                        _home.HorizontalSpeed,
                        _home.ZSpeed,
                        cancellationToken)));
            }

            if (_units.BoltFastening)
            {
                horizontalHomeTasks.Add(
                    CheckHomeAsync(_fasteningGantry.HomeHorizontalAsync(
                        _home.HorizontalSpeed,
                        cancellationToken)));
            }

            if (InspectionGantryEnabled)
            {
                horizontalHomeTasks.Add(
                    CheckHomeAsync(_inspectionGantry.HomeHorizontalAsync(
                        _home.HorizontalSpeed,
                        cancellationToken)));
            }

            await Task.WhenAll(horizontalHomeTasks);
            cancellationToken.ThrowIfCancellationRequested();

            if (BufferHandlersEnabled)
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
        if (input == InputIo.AutoMode && value && !_state.AutomaticRunning)
            _conveyor.Stop();

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
            || input is InputIo.PcbPlacementHandlerUp or InputIo.PcbPlacementHandlerDown
                or InputIo.PickupHeadUp or InputIo.PickupHeadDown
                or InputIo.ShootingHeadUp or InputIo.ShootingHeadDown
                or InputIo.NgCarrierPickupUp or InputIo.NgCarrierPickupDown)
            _state.Refresh();

        if (MachineState.IsSafetyInput(input))
        {
            var alarm = SafetyAlarm();
            if (alarm != MachineAlarm.None)
            {
                _state.SetError(alarm);
                Stop();
                return;
            }
        }

        if (input == InputIo.ResetButton
            && value
            && _options.UseResetButton
            && CanReset)
        {
            _ = ResetAsync();
        }
    }

    private void CheckMotionInterlocks()
    {
        if (_fasteningGantry.Feedback.Command == MotionCommand.Adjustment
            && (!_state.ManualMode || !_state.CanOperate
                || _state.AutomaticRunning || _state.IsHoming || _state.BoltTestRunning))
        {
            Stop();
        }

        var fasteningBlocked = _fasteningGantry.Feedback.Command == MotionCommand.Positioning
            && _fasteningGantry.Feedback.IsMovingHorizontal && !_fasteningGantry.CanMoveHorizontal;
        var alarm = _placementHandler.Feedback.IsMovingHorizontal
                    && !_placementHandler.CanMoveHorizontal
            ? MachineAlarm.PcbPlacement
            : fasteningBlocked
                ? MachineAlarm.BoltFastening
                : _inspectionGantry.Feedback.IsMoving
                  && !_inspectionGantry.CanMove
                  || InspectionGantryEnabled
                  && _state.IsHoming
                  && !_inspectionGantry.CanHome
                    ? MachineAlarm.NgCarrierTransfer
                    : MachineAlarm.None;
        if (alarm != MachineAlarm.None)
        {
            _state.SetError(alarm);
            Stop();
        }
    }

    private MachineAlarm SafetyAlarm()
    {
        if (_options.UseEmergencyStop
            && !_state.EmergencyStopReleased)
        {
            return MachineAlarm.EmergencyStop;
        }

        if (_options.UseDoorInterlock
            && _state.AutoMode
            && !_state.DoorClosed)
        {
            return MachineAlarm.DoorOpen;
        }

        return _options.UseAirPressureInterlock
               && !_state.AirPressureOk
            ? MachineAlarm.AirPressureLow
            : MachineAlarm.None;
    }

    public async Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanStart)
        {
            return;
        }

        using var operation = _operations.Link(cancellationToken);
        _state.SetAutomaticRunning(true);
        void StopWhenOperationBecomesUnavailable()
        {
            if (_state.IsError)
            {
                operation.Cancel();
                return;
            }

            try
            {
                if (_state.CanAutomaticOperate)
                {
                    return;
                }

                if (_state.AutoMode
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

        try
        {
            var (startAlarm, startError) = await CheckHardwareAsync(operation.Token);
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
            StopWhenOperationBecomesUnavailable();
            if (operation.IsCancellationRequested)
            {
                return;
            }

            var runningUnits = new List<Task>();
            void StartUnit(
                bool enabled,
                MachineAlarm alarm,
                Func<Task> start)
            {
                if (enabled && !operation.IsCancellationRequested)
                {
                    runningUnits.Add(RunUnitAsync(alarm, start));
                }
            }

            async Task RunUnitAsync(MachineAlarm alarm, Func<Task> start)
            {
                try
                {
                    await start();
                    if (!operation.IsCancellationRequested && !_state.IsError)
                    {
                        _state.SetError(alarm);
                    }
                }
                catch (Exception exception)
                {
                    if (!operation.IsCancellationRequested && !_state.IsError)
                    {
                        _state.SetError(exception is MotionException
                            ? MachineAlarm.MotionUnavailable
                            : alarm, exception);
                    }
                }
                finally
                {
                    operation.Cancel();
                }
            }

            StartUnit(
                _units.MainConveyor,
                MachineAlarm.MainConveyor,
                () => _conveyor.RunAsync(operation.Token));
            StartUnit(
                _units.PcbSupply,
                MachineAlarm.PcbSupply,
                () => _pcbSupply.RunAsync(
                    _recipe.PcbSupply,
                    operation.Token));
            StartUnit(
                _units.PcbPlacement,
                MachineAlarm.PcbPlacement,
                () => _pcbPlacement.RunAsync(
                    _recipe.PcbPlacement,
                    operation.Token));
            StartUnit(
                _units.PickupBoltFeeder,
                MachineAlarm.PickupBoltFeeder,
                () => _pickupBoltFeeder.RunAsync(operation.Token));
            StartUnit(
                _units.ShootingBoltFeeder,
                MachineAlarm.ShootingBoltFeeder,
                () => _shootingBoltFeeder.RunAsync(operation.Token));
            StartUnit(
                _units.BoltFastening,
                MachineAlarm.BoltFastening,
                () => _fasteningStation.RunAsync(
                    _recipe.BoltFastening,
                    operation.Token));
            StartUnit(
                InspectionGantryEnabled,
                _units.Inspection
                    ? MachineAlarm.Inspection
                    : MachineAlarm.NgCarrierTransfer,
                () => _inspectionStation.RunAsync(
                    _recipe.Pcb.GetBolts().ToArray(),
                    operation.Token));
            StartUnit(
                _units.NgShuttle,
                MachineAlarm.NgShuttle,
                () => _ngShuttle.RunAsync(operation.Token));
            StartUnit(
                _units.NgConveyor,
                MachineAlarm.NgConveyor,
                () => _ngConveyor.RunAsync(operation.Token));

            await Task.WhenAll(runningUnits)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        finally
        {
            _state.Changed -= StopWhenOperationBecomesUnavailable;
            _state.SetAutomaticRunning(false);
            StopRunOutputs();
            _state.Refresh();
        }
    }

    private async Task<(MachineAlarm Alarm, Exception? Error)> CheckHardwareAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Reset can come from InputChanged; do not make its monitor wait for itself.
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _io.Initialize();
                cancellationToken.ThrowIfCancellationRequested();
                _io.CheckReady();
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            StopRunOutputs();
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (MachineAlarm.IoCommunication, exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (BufferHandlersEnabled)
            {
                _supplyHandler.InitializeMotion();
                _placementHandler.InitializeMotion();
            }

            if (_units.BoltFastening)
            {
                _fasteningGantry.InitializeMotion();
            }

            if (InspectionGantryEnabled)
            {
                _inspectionGantry.InitializeMotion();
            }
        }
        catch (Exception exception)
        {
            return (MachineAlarm.MotionUnavailable, exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_units.Inspection)
        {
            try
            {
                _boltInspector.InitializeVision();
            }
            catch (Exception exception)
            {
                return (MachineAlarm.Inspection, exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_units.BoltFastening)
        {
            try
            {
                await _fasteningGantry.CheckReadyAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return (MachineAlarm.BoltFastening, exception);
            }
        }

        return (MachineAlarm.None, null);
    }

    private bool BufferHandlersEnabled =>
        _units.PcbSupply || _units.PcbPlacement;

    private bool InspectionGantryEnabled =>
        _units.Inspection || _units.NgCarrierTransfer;

    private void StopRunOutputs()
    {
        if (_io.IsReady)
        {
            _conveyor.Stop();
            _shootingBoltFeeder.Stop();
            _fasteningGantry.StopShooting();
            _ngConveyor.Stop();
            _supplyHandler.SetUpstreamReady(false);
        }
    }

    private void OnIoFaulted(Exception exception)
    {
        _state.SetError(MachineAlarm.IoCommunication, exception);
        _operations.Cancel();
    }
}
