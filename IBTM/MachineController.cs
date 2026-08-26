using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFeeder;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM;

public sealed class MachineController
{
    private readonly MachineState _state;
    private readonly OperationCancellation _operations;
    private readonly MachineOptions _options;
    private readonly UnitSettings _units;
    private readonly Recipe _recipe;
    private readonly HomeSettings _home;
    private readonly IIoService _io;
    private readonly MainConveyor _conveyor;
    private readonly NgConveyorLine _ngConveyor;
    private readonly PcbSupplyProcess _pcbSupply;
    private readonly PcbPlacementProcess _pcbPlacement;
    private readonly BoltFasteningProcess _boltFasteningProcess;
    private readonly InspectionProcess _inspectionProcess;
    private readonly PickupBoltFeeder _pickupBoltFeeder;
    private readonly LinearBoltFeeder _linearBoltFeeder;
    private readonly PcbSupplyHandler _supplyHandler;
    private readonly BoltFasteningStation _boltFastening;
    private readonly ILightController _light;
    private readonly ICamera _inspectionCamera;
    private readonly IAxisMotion _supplyMotion;
    private readonly IXyMotion _placementMotion;
    private readonly IXyMotion _boltFasteningMotion;
    private readonly IXyMotion _inspectionGantryMotion;

    public MachineController(
        MachineState state,
        OperationCancellation operations,
        MachineOptions options,
        UnitSettings units,
        Recipe recipe,
        HomeSettings home,
        IIoService io,
        MainConveyor conveyor,
        NgConveyorLine ngConveyor,
        PcbSupplyProcess pcbSupply,
        PcbPlacementProcess pcbPlacement,
        BoltFasteningProcess boltFasteningProcess,
        InspectionProcess inspectionProcess,
        PickupBoltFeeder pickupBoltFeeder,
        LinearBoltFeeder linearBoltFeeder,
        PcbSupplyHandler supplyHandler,
        BoltFasteningStation boltFastening,
        ILightController light,
        ICamera inspectionCamera,
        [FromKeyedServices(MotionGroup.PcbSupply)] IAxisMotion supplyMotion,
        [FromKeyedServices(MotionGroup.PcbPlacementHandler)] IXyMotion placementMotion,
        [FromKeyedServices(MotionGroup.BoltFastening)] IXyMotion boltFasteningMotion,
        [FromKeyedServices(MotionGroup.InspectionGantry)] IXyMotion inspectionGantryMotion)
    {
        _state = state;
        _operations = operations;
        _options = options;
        _units = units;
        _recipe = recipe;
        _home = home;
        _io = io;
        _conveyor = conveyor;
        _ngConveyor = ngConveyor;
        _pcbSupply = pcbSupply;
        _pcbPlacement = pcbPlacement;
        _boltFasteningProcess = boltFasteningProcess;
        _inspectionProcess = inspectionProcess;
        _pickupBoltFeeder = pickupBoltFeeder;
        _linearBoltFeeder = linearBoltFeeder;
        _supplyHandler = supplyHandler;
        _boltFastening = boltFastening;
        _light = light;
        _inspectionCamera = inspectionCamera;
        _supplyMotion = supplyMotion;
        _placementMotion = placementMotion;
        _boltFasteningMotion = boltFasteningMotion;
        _inspectionGantryMotion = inspectionGantryMotion;
        io.InputChanged += OnInputChanged;
        io.Faulted += OnIoFaulted;
        conveyor.CarrierChanged += OnConveyorCarrierChanged;
    }

    public bool CanReset =>
        _state.Alarm == MachineAlarm.ControlCommunication
        || _state.SafetyReady && (_state.IsError || _state.Faulted);
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
        && (_units.MainConveyor
            || _units.PcbSupply
            || _units.PcbPlacement
            || _units.PickupBoltFeeder
            || _units.LinearBoltFeeder
            || _units.BoltFastening
            || _units.Inspection
            || _units.NgConveyor);

    public async Task InitializeAsync()
    {
        _io.Initialize();
        _light.Initialize();
        StopDevices();
        foreach (var motion in EnabledMotions())
        {
            motion.Initialize();
        }

        if (_units.Inspection)
        {
            _inspectionCamera.Initialize();
        }

        if (_units.BoltFastening)
        {
            await _boltFastening.CheckReadyAsync();
        }

        _state.Refresh();
    }

    public void Stop()
    {
        try
        {
            _operations.Cancel();
        }
        finally
        {
            StopDevices();
            _state.Refresh();
        }
    }

    public void EmergencyStop()
    {
        _state.SetError(MachineAlarm.EmergencyStop);
        Stop();
    }

    public void Reset()
    {
        if (_state.Alarm == MachineAlarm.ControlCommunication)
        {
            try
            {
                _io.CheckReady();
            }
            catch
            {
                return;
            }

            if (!_state.SafetyReady)
            {
                return;
            }
        }

        foreach (var motion in EnabledMotions())
        {
            motion.ResetAlarm();
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
            var otherMotions = EnabledXyMotions().ToArray();
            var zHomeTasks = otherMotions
                .Where(motion => motion.HasZ)
                .Select(motion => motion.HomeAsync(
                    MotionAxis.Z,
                    _home.ZSpeed,
                    cancellationToken))
                .ToList();
            if (BufferHandlersEnabled)
            {
                zHomeTasks.Add(_supplyHandler.PrepareHomeAsync(
                    _home.ZSpeed,
                    cancellationToken));
            }

            var zReady = await Task.WhenAll(zHomeTasks);
            if (zReady.Any(ready => !ready))
            {
                _state.SetError(MachineAlarm.HomeFailed);
                return;
            }

            await Task.WhenAll(otherMotions
                .Where(motion => motion.HasZ)
                .Select(
                motion => motion.MoveToHorizontalZAsync(cancellationToken)));

            var horizontalHomeTasks = otherMotions.Select(
                motion => motion.HomeHorizontalAsync(
                    _home.HorizontalSpeed,
                    cancellationToken))
                .ToList();
            if (BufferHandlersEnabled)
            {
                horizontalHomeTasks.Add(_supplyHandler.CompleteHomeAsync(
                    _home.HorizontalSpeed,
                    _home.ZSpeed,
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
                await _supplyMotion.MoveToHorizontalZAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IoTimeoutException)
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
        var alarm = input switch
        {
            InputIo.EmergencyStop1Pressed
                or InputIo.EmergencyStop2Pressed =>
                _options.UseEmergencyStop
                    ? MachineAlarm.EmergencyStop
                    : MachineAlarm.None,
            InputIo.Door1Open
                or InputIo.Door2Open
                or InputIo.Door3Open
                or InputIo.Door4Open
                or InputIo.Door5Open
                or InputIo.Door6Open =>
                _options.UseDoorInterlock && _state.AutoMode
                    ? MachineAlarm.DoorOpen
                    : MachineAlarm.None,
            InputIo.AutoMode when !_state.DoorInterlockReady =>
                MachineAlarm.DoorOpen,
            InputIo.AirPressureLow =>
                _options.UseAirPressureInterlock
                    ? MachineAlarm.AirPressureLow
                    : MachineAlarm.None,
            _ => MachineAlarm.None,
        };
        if (value && alarm != MachineAlarm.None)
        {
            _state.SetError(alarm);
            Stop();
            return;
        }

        if (input == InputIo.ResetButton
            && value
            && _options.UseResetButton
            && CanReset)
        {
            Reset();
        }
    }

    public async Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanStart)
        {
            return;
        }

        if (!await CheckStartHardwareAsync(cancellationToken))
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

            CompleteDisabledConveyorWork();

            var runningProcesses = new List<(Task Task, MachineAlarm Alarm)>();
            if (_units.MainConveyor)
            {
                runningProcesses.Add((
                    _conveyor.RunAsync(operation.Token),
                    MachineAlarm.MainConveyor));
            }

            if (_units.PcbSupply)
            {
                runningProcesses.Add((
                    _pcbSupply.RunAsync(
                        _recipe.PcbSupply,
                        operation.Token),
                    MachineAlarm.Supply));
            }

            if (_units.PcbPlacement)
            {
                runningProcesses.Add((
                    _pcbPlacement.RunAsync(
                        _recipe.PcbPlacement,
                        operation.Token),
                    MachineAlarm.Placement));
            }

            if (_units.PickupBoltFeeder)
            {
                runningProcesses.Add((
                    _pickupBoltFeeder.RunAsync(operation.Token),
                    MachineAlarm.PickupBoltFeeder));
            }

            if (_units.LinearBoltFeeder)
            {
                runningProcesses.Add((
                    _linearBoltFeeder.RunAsync(operation.Token),
                    MachineAlarm.LinearBoltFeeder));
            }

            if (_units.BoltFastening)
            {
                runningProcesses.Add((
                    _boltFasteningProcess.RunAsync(
                        _recipe.BoltFastening,
                        operation.Token),
                    MachineAlarm.BoltFastening));
            }

            if (_units.Inspection)
            {
                runningProcesses.Add((
                    _inspectionProcess.RunAsync(
                        _recipe.BoltFastening.BoltPoints,
                        operation.Token),
                    MachineAlarm.Inspection));
            }

            if (_units.NgConveyor)
            {
                runningProcesses.Add((
                    _ngConveyor.RunAsync(operation.Token),
                    MachineAlarm.NgConveyor));
            }

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
            catch (OperationCanceledException)
                when (operation.IsCancellationRequested)
            {
            }
            catch (MotionException)
            {
                if (!operation.IsCancellationRequested && !_state.IsError)
                {
                    _state.SetError(MachineAlarm.MotionUnavailable);
                }
            }
            catch
            {
                if (!operation.IsCancellationRequested && !_state.IsError)
                {
                    _state.SetError(completedProcess.Alarm);
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
            StopDevices();
            _state.Refresh();
        }
    }

    private IEnumerable<IXyMotion> EnabledXyMotions()
    {
        if (BufferHandlersEnabled)
        {
            yield return _placementMotion;
        }

        if (_units.BoltFastening)
        {
            yield return _boltFasteningMotion;
        }

        if (_units.Inspection || _units.NgConveyor)
        {
            yield return _inspectionGantryMotion;
        }
    }

    private async Task<bool> CheckStartHardwareAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            _io.CheckReady();
        }
        catch
        {
            _state.SetError(MachineAlarm.ControlCommunication);
            return false;
        }

        if (!_units.BoltFastening)
        {
            return true;
        }

        try
        {
            await _boltFastening.CheckReadyAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            _state.SetError(MachineAlarm.BoltFastening);
            return false;
        }
    }

    private IEnumerable<IAxisMotion> EnabledMotions()
    {
        if (BufferHandlersEnabled)
        {
            yield return _supplyMotion;
            yield return _placementMotion;
        }

        if (_units.BoltFastening)
        {
            yield return _boltFasteningMotion;
        }

        if (_units.Inspection || _units.NgConveyor)
        {
            yield return _inspectionGantryMotion;
        }
    }

    private void OnConveyorCarrierChanged(
        ConveyorStation station,
        bool present)
    {
        if (present)
        {
            BypassDisabledWork(station);
        }
    }

    private bool BufferHandlersEnabled =>
        _units.PcbSupply || _units.PcbPlacement;

    private void CompleteDisabledConveyorWork()
    {
        BypassDisabledWork(ConveyorStation.PcbPlacement);
        BypassDisabledWork(ConveyorStation.BoltFastening);
        BypassDisabledWork(ConveyorStation.Inspection);
    }

    private void BypassDisabledWork(ConveyorStation station)
    {
        if (!StationWorkEnabled(station) && _conveyor.HasCarrier(station))
        {
            _conveyor.BypassWork(station);
        }
    }

    private bool StationWorkEnabled(ConveyorStation station) =>
        station switch
        {
            ConveyorStation.PcbPlacement => _units.PcbPlacement,
            ConveyorStation.BoltFastening => _units.BoltFastening,
            ConveyorStation.Inspection => _units.Inspection,
            _ => throw new ArgumentOutOfRangeException(nameof(station)),
        };

    private void StopDevices()
    {
        _conveyor.Stop();
        _linearBoltFeeder.Stop();
        _boltFastening.Stop();
        _ngConveyor.Stop();
        _supplyHandler.SetUpstreamReady(false);
        _light.TurnOffAll();
    }

    private void OnIoFaulted()
    {
        _state.SetError(MachineAlarm.ControlCommunication);
        _operations.Cancel();
    }
}
