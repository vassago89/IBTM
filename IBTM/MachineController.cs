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
    private readonly MachineState _state;
    private readonly OperationCancellation _operations;
    private readonly MachineOptions _options;
    private readonly UnitSettings _units;
    private readonly Recipe _recipe;
    private readonly HomeSettings _home;
    private readonly CarrierReferenceSettings _carrierReference;
    private readonly IIoService _io;
    private readonly MainConveyor _conveyor;
    private readonly NgConveyorLine _ngConveyor;
    private readonly PcbSupplyProcess _pcbSupply;
    private readonly PcbPlacementProcess _pcbPlacement;
    private readonly BoltFasteningProcess _boltFasteningProcess;
    private readonly InspectionProcess _inspectionProcess;
    private readonly PickupBoltFeeder _pickupBoltFeeder;
    private readonly ShootingBoltFeeder _shootingBoltFeeder;
    private readonly PcbSupplyHandler _supplyHandler;
    private readonly PcbPlacementHandler _placementHandler;
    private readonly BoltFasteningStation _boltFastening;
    private readonly InspectionGantry _inspectionGantry;
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
        NgConveyorLine ngConveyor,
        PcbSupplyProcess pcbSupply,
        PcbPlacementProcess pcbPlacement,
        BoltFasteningProcess boltFasteningProcess,
        InspectionProcess inspectionProcess,
        PickupBoltFeeder pickupBoltFeeder,
        ShootingBoltFeeder shootingBoltFeeder,
        PcbSupplyHandler supplyHandler,
        PcbPlacementHandler placementHandler,
        BoltFasteningStation boltFastening,
        InspectionGantry inspectionGantry,
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
        _pcbSupply = pcbSupply;
        _pcbPlacement = pcbPlacement;
        _boltFasteningProcess = boltFasteningProcess;
        _inspectionProcess = inspectionProcess;
        _pickupBoltFeeder = pickupBoltFeeder;
        _shootingBoltFeeder = shootingBoltFeeder;
        _supplyHandler = supplyHandler;
        _placementHandler = placementHandler;
        _boltFastening = boltFastening;
        _inspectionGantry = inspectionGantry;
        _boltInspector = boltInspector;
        io.InputChanged += OnInputChanged;
        io.Faulted += OnIoFaulted;
    }

    public bool CanReset =>
        !_state.IsRunning
        && (_state.Alarm == MachineAlarm.IoCommunication
            || _state.SafetyReady
            && (_state.ManualMode || _state.DoorInterlockReady)
            && (_state.IsError
                || _state.Faulted
                || !_state.ServosOn
                || !_state.ServoMainContactorOn));
    public bool CanHome =>
        _state.SafetyReady
        && _state.DoorInterlockReady
        && _state.ServosOn
        && !_state.Homed
        && !_state.IsError
        && !_state.IsRunning
        && (!BufferHandlersEnabled || _pcbSupply.CanHome);
    public bool CanStart =>
        _state.CanAutomaticOperate
        && !_state.IsRunning
        && _units.HasEnabledUnit()
        && TeachingReady;
    public bool TeachingReady =>
        !_units.BoltFastening && !_units.Inspection
        || _recipe.BoltFastening.BoltPoints.Count > 0
        && CarrierCoordinates.IsDefined(
            _carrierReference.UpperLeftLocatingPin,
            _carrierReference.LowerRightLocatingPin)
        && _recipe.BoltFastening.BoltPoints.All(bolt =>
            bolt is { X: not null, Y: not null }
            && (!_units.BoltFastening
                || (bolt.Z is not null
                    && _boltFastening.HasReference(bolt.Head))));

    public async Task InitializeAsync()
    {
        var alarm = await CheckHardwareAsync(CancellationToken.None);
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
            _state.SetError(alarm);
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

    public async Task ResetAsync()
    {
        if (!CanReset)
        {
            return;
        }

        var alarm = await CheckHardwareAsync(CancellationToken.None);
        if (alarm != MachineAlarm.None)
        {
            _state.SetError(alarm);
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
                _boltFastening.ResetMotion();
            }

            if (_units.Inspection || _units.NgConveyor)
            {
                _inspectionGantry.ResetMotion();
            }
        }
        catch
        {
            _state.SetError(MachineAlarm.MotionUnavailable);
            return;
        }

        _state.ClearError();
        _state.Refresh();
    }

    public async Task HomeAsync(CancellationToken cancellationToken)
    {
        if (!CanHome)
        {
            return;
        }

        using var operation = _operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        _state.SetHoming(true);
        try
        {
            var zHomeTasks = new List<Task<bool>>(3);
            if (BufferHandlersEnabled)
            {
                zHomeTasks.Add(_placementHandler.HomeZAsync(
                    _home.ZSpeed,
                    cancellationToken));
                zHomeTasks.Add(_supplyHandler.PrepareHomeAsync(
                    _home.ZSpeed,
                    cancellationToken));
            }

            if (_units.BoltFastening)
            {
                zHomeTasks.Add(_boltFastening.HomeZAsync(
                    _home.ZSpeed,
                    cancellationToken));
            }

            var zReady = await Task.WhenAll(zHomeTasks);
            if (zReady.Any(ready => !ready))
            {
                _state.SetError(MachineAlarm.HomeFailed);
                return;
            }

            var safeZTasks = new List<Task>(2);
            if (BufferHandlersEnabled)
            {
                safeZTasks.Add(
                    _placementHandler.MoveToHorizontalZAsync(
                        cancellationToken));
            }

            if (_units.BoltFastening)
            {
                safeZTasks.Add(
                    _boltFastening.MoveToSafeZAsync(cancellationToken));
            }

            await Task.WhenAll(safeZTasks);

            var horizontalHomeTasks = new List<Task<bool>>(4);
            if (BufferHandlersEnabled)
            {
                horizontalHomeTasks.Add(
                    _placementHandler.HomeHorizontalAsync(
                        _home.HorizontalSpeed,
                        cancellationToken));
                horizontalHomeTasks.Add(_supplyHandler.CompleteHomeAsync(
                    _home.HorizontalSpeed,
                    _home.ZSpeed,
                    cancellationToken));
            }

            if (_units.BoltFastening)
            {
                horizontalHomeTasks.Add(
                    _boltFastening.HomeHorizontalAsync(
                        _home.HorizontalSpeed,
                        cancellationToken));
            }

            if (_units.Inspection || _units.NgConveyor)
            {
                horizontalHomeTasks.Add(
                    _inspectionGantry.HomeHorizontalAsync(
                    _home.HorizontalSpeed,
                    cancellationToken));
            }

            var horizontalHomed = await Task.WhenAll(horizontalHomeTasks);
            if (horizontalHomed.Any(homed => !homed))
            {
                _state.SetError(MachineAlarm.HomeFailed);
                return;
            }

            if (BufferHandlersEnabled)
            {
                await _supplyHandler.MoveToRotationZAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IoTimeoutException)
        {
            _state.SetError(MachineAlarm.HomeFailed);
        }
        catch (MotionException)
        {
            _state.SetError(MachineAlarm.HomeFailed);
        }
        finally
        {
            _state.SetHoming(false);
            _state.Refresh();
        }
    }

    private void OnInputChanged(InputIo input, bool value)
    {
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

        var startAlarm = await CheckHardwareAsync(cancellationToken);
        if (startAlarm != MachineAlarm.None)
        {
            _state.SetError(startAlarm);
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
            catch
            {
                _state.SetError(MachineAlarm.MotionUnavailable);
            }

            operation.Cancel();
        }

        _state.Changed += StopWhenOperationBecomesUnavailable;
        try
        {
            StopWhenOperationBecomesUnavailable();
            if (operation.IsCancellationRequested)
            {
                return;
            }

            var runningProcesses = new List<(Task Task, MachineAlarm Alarm)>();
            void StartProcess(
                bool enabled,
                MachineAlarm alarm,
                Func<Task> start)
            {
                if (enabled)
                {
                    runningProcesses.Add((start(), alarm));
                }
            }

            StartProcess(
                _units.MainConveyor,
                MachineAlarm.MainConveyor,
                () => _conveyor.RunAsync(operation.Token));
            StartProcess(
                _units.PcbSupply,
                MachineAlarm.PcbSupply,
                () => _pcbSupply.RunAsync(
                    _recipe.PcbSupply,
                    operation.Token));
            StartProcess(
                _units.PcbPlacement,
                MachineAlarm.PcbPlacement,
                () => _pcbPlacement.RunAsync(
                    _recipe.PcbPlacement,
                    operation.Token));
            StartProcess(
                _units.PickupBoltFeeder,
                MachineAlarm.PickupBoltFeeder,
                () => _pickupBoltFeeder.RunAsync(operation.Token));
            StartProcess(
                _units.ShootingBoltFeeder,
                MachineAlarm.ShootingBoltFeeder,
                () => _shootingBoltFeeder.RunAsync(operation.Token));
            StartProcess(
                _units.BoltFastening,
                MachineAlarm.BoltFastening,
                () => _boltFasteningProcess.RunAsync(
                    _recipe.BoltFastening,
                    operation.Token));
            StartProcess(
                _units.Inspection,
                MachineAlarm.Inspection,
                () => _inspectionProcess.RunAsync(
                    _recipe.BoltFastening.BoltPoints,
                    operation.Token));
            StartProcess(
                _units.NgConveyor,
                MachineAlarm.NgConveyor,
                () => _ngConveyor.RunAsync(operation.Token));

            var completed = await Task.WhenAny(
                runningProcesses.Select(process => process.Task));
            var completedProcess = runningProcesses.Single(
                process => ReferenceEquals(process.Task, completed));
            try
            {
                await completed;
                if (!operation.IsCancellationRequested && !_state.IsError)
                {
                    _state.SetError(completedProcess.Alarm);
                }
            }
            catch (Exception exception)
            {
                if (!operation.IsCancellationRequested && !_state.IsError)
                {
                    _state.SetError(exception is MotionException
                        ? MachineAlarm.MotionUnavailable
                        : completedProcess.Alarm);
                }
            }
            finally
            {
                operation.Cancel();
                await Task.WhenAll(runningProcesses
                    .Where(process => !ReferenceEquals(
                        process.Task,
                        completed))
                    .Select(process => process.Task))
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        finally
        {
            _state.Changed -= StopWhenOperationBecomesUnavailable;
            _state.SetAutomaticRunning(false);
            StopRunOutputs();
            _state.Refresh();
        }
    }

    private async Task<MachineAlarm> CheckHardwareAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            _io.Initialize();
            _io.CheckReady();
            StopRunOutputs();
        }
        catch
        {
            return MachineAlarm.IoCommunication;
        }

        try
        {
            if (BufferHandlersEnabled)
            {
                _supplyHandler.InitializeMotion();
                _placementHandler.InitializeMotion();
            }

            if (_units.BoltFastening)
            {
                _boltFastening.InitializeMotion();
            }

            if (_units.Inspection || _units.NgConveyor)
            {
                _inspectionGantry.InitializeMotion();
            }
        }
        catch
        {
            return MachineAlarm.MotionUnavailable;
        }

        if (_units.Inspection)
        {
            try
            {
                _boltInspector.InitializeVision();
            }
            catch
            {
                return MachineAlarm.Inspection;
            }
        }

        if (_units.BoltFastening)
        {
            try
            {
                await _boltFastening.CheckReadyAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return MachineAlarm.BoltFastening;
            }
        }

        return MachineAlarm.None;
    }

    private bool BufferHandlersEnabled =>
        _units.PcbSupply || _units.PcbPlacement;

    private void StopRunOutputs()
    {
        if (_io.IsReady)
        {
            _conveyor.Stop();
            _shootingBoltFeeder.Stop();
            _boltFastening.Stop();
            _ngConveyor.Stop();
            _supplyHandler.SetUpstreamReady(false);
        }
    }

    private void OnIoFaulted()
    {
        _state.SetError(MachineAlarm.IoCommunication);
        _operations.Cancel();
    }
}
