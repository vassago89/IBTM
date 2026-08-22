using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
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
    private readonly ProcessSettings _processes;
    private readonly Recipe _recipe;
    private readonly HomeSettings _home;
    private readonly IIoService _io;
    private readonly MainConveyor _conveyor;
    private readonly NgConveyorLine _ngConveyor;
    private readonly PcbSupplyProcess _pcbSupply;
    private readonly PcbPlacementProcess _pcbPlacement;
    private readonly BoltFasteningProcess _boltFasteningProcess;
    private readonly PcbSupplyHandler _supplyHandler;
    private readonly BoltFasteningStation _boltFastening;
    private readonly ILightController _light;
    private readonly ICamera[] _cameras;
    private readonly IAxisMotion _supplyMotion;
    private readonly IXyMotion _placementMotion;
    private readonly IXyMotion _boltFasteningMotion;
    private readonly IXyMotion _inspectionGantryMotion;
    private readonly IAxisMotion[] _motions;

    public MachineController(
        MachineState state,
        OperationCancellation operations,
        MachineOptions options,
        ProcessSettings processes,
        Recipe recipe,
        HomeSettings home,
        IIoService io,
        MainConveyor conveyor,
        NgConveyorLine ngConveyor,
        PcbSupplyProcess pcbSupply,
        PcbPlacementProcess pcbPlacement,
        BoltFasteningProcess boltFasteningProcess,
        PcbSupplyHandler supplyHandler,
        BoltFasteningStation boltFastening,
        ILightController light,
        [FromKeyedServices(CameraRole.Alignment)] ICamera alignmentCamera,
        [FromKeyedServices(CameraRole.Inspection)] ICamera inspectionCamera,
        [FromKeyedServices(MotionGroup.PcbSupply)] IAxisMotion supplyMotion,
        [FromKeyedServices(MotionGroup.PcbPlacementHandler)] IXyMotion placementMotion,
        [FromKeyedServices(MotionGroup.BoltFastening)] IXyMotion boltFasteningMotion,
        [FromKeyedServices(MotionGroup.InspectionGantry)] IXyMotion inspectionGantryMotion)
    {
        _state = state;
        _operations = operations;
        _options = options;
        _processes = processes;
        _recipe = recipe;
        _home = home;
        _io = io;
        _conveyor = conveyor;
        _ngConveyor = ngConveyor;
        _pcbSupply = pcbSupply;
        _pcbPlacement = pcbPlacement;
        _boltFasteningProcess = boltFasteningProcess;
        _supplyHandler = supplyHandler;
        _boltFastening = boltFastening;
        _light = light;
        _cameras = [alignmentCamera, inspectionCamera];
        _supplyMotion = supplyMotion;
        _placementMotion = placementMotion;
        _boltFasteningMotion = boltFasteningMotion;
        _inspectionGantryMotion = inspectionGantryMotion;
        _motions =
        [
            supplyMotion,
            placementMotion,
            boltFasteningMotion,
            inspectionGantryMotion,
        ];

        io.InputChanged += OnInputChanged;
    }

    public bool CanReset =>
        _state.SafetyReady && (_state.IsError || _state.Faulted);
    public bool CanHome =>
        _state.SafetyReady
        && _state.ServosOn
        && !_state.Homed
        && !_state.IsError
        && !_state.IsRunning
        && (!_processes.PcbSupply || _pcbSupply.CanHome);
    public bool CanStart =>
        _state.CanOperate
        && !_state.IsRunning
        && (_processes.PcbSupply
            || _processes.PcbPlacement
            || _processes.BoltFastening);

    public async Task InitializeAsync()
    {
        _io.Initialize();
        _light.Initialize();
        _conveyor.Initialize();
        StopDevices();
        foreach (var motion in _motions)
        {
            motion.Initialize();
        }
        foreach (var camera in _cameras)
        {
            camera.Initialize();
        }
        await _boltFastening.InitializeAsync();
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
        foreach (var motion in _motions)
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
            if (_processes.PcbSupply)
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
                motion => motion.MoveToSafeZAsync(cancellationToken)));

            var horizontalHomeTasks = otherMotions.Select(
                motion => motion.HomeHorizontalAsync(
                    _home.HorizontalSpeed,
                    cancellationToken))
                .ToList();
            if (_processes.PcbSupply)
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

            if (_processes.PcbSupply)
            {
                await _supplyMotion.MoveToSafeZAsync(cancellationToken);
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
                _options.UseDoorInterlock
                    ? MachineAlarm.DoorOpen
                    : MachineAlarm.None,
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

        using var operation = _operations.Link(cancellationToken);
        _state.SetAutomaticRunning(true);
        void StopWhenOperationBecomesUnavailable()
        {
            if (!_state.CanOperate)
            {
                operation.Cancel();
            }
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
            if (_processes.PcbSupply)
            {
                runningProcesses.Add((
                    _pcbSupply.RunAsync(
                        _recipe.PcbSupply,
                        operation.Token),
                    MachineAlarm.Supply));
            }

            if (_processes.PcbPlacement)
            {
                runningProcesses.Add((
                    _pcbPlacement.RunAsync(
                        _recipe.PcbPlacement,
                        operation.Token),
                    MachineAlarm.Placement));
            }

            if (_processes.BoltFastening)
            {
                runningProcesses.Add((
                    _boltFasteningProcess.RunAsync(
                        _recipe.BoltFastening,
                        operation.Token),
                    MachineAlarm.BoltFastening));
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
            catch (TimeoutException)
            {
                _state.SetError(completedProcess.Alarm);
            }
            finally
            {
                operation.Cancel();
                await Task.WhenAll(runningProcesses
                    .Where(process => !ReferenceEquals(
                        process.Task,
                        completed))
                    .Select(process => process.Task));
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
        if (_processes.PcbPlacement)
        {
            yield return _placementMotion;
        }

        if (_processes.BoltFastening)
        {
            yield return _boltFasteningMotion;
        }

        if (_processes.Inspection)
        {
            yield return _inspectionGantryMotion;
        }
    }

    private void StopDevices()
    {
        _conveyor.Stop();
        _boltFastening.Stop();
        _ngConveyor.Stop();
        _conveyor.ResetSmema();
        _pcbSupply.SetReady(false);
        _light.TurnOffAll();
    }
}
