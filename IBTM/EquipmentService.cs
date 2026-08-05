using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbSupply;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM;

public sealed class EquipmentService
{
    private readonly EquipmentState _state;
    private readonly MachineSettings _settings;
    private readonly IIoService _io;
    private readonly IConveyorServo _conveyor;
    private readonly BufferStage _buffer;
    private readonly PcbSupplyHandler _supplyHandler;
    private readonly PcbPlacementStation _pcbPlacement;
    private readonly BoltFasteningStation _boltFastening;
    private readonly InspectionStation _inspection;
    private readonly ILightController _light;
    private readonly MotionService _supplyMotion;
    private readonly MotionService[] _motions;

    public EquipmentService(
        EquipmentState state,
        MachineSettings settings,
        IIoService io,
        IConveyorServo conveyor,
        BufferStage buffer,
        PcbSupplyHandler supplyHandler,
        PcbPlacementStation pcbPlacement,
        BoltFasteningStation boltFastening,
        InspectionStation inspection,
        ILightController light,
        [FromKeyedServices(MotionGroup.PcbSupply)] MotionService supplyMotion,
        MotionService[] motions)
    {
        _state = state;
        _settings = settings;
        _io = io;
        _conveyor = conveyor;
        _buffer = buffer;
        _supplyHandler = supplyHandler;
        _pcbPlacement = pcbPlacement;
        _boltFastening = boltFastening;
        _inspection = inspection;
        _light = light;
        _supplyMotion = supplyMotion;
        _motions = motions;

        io.InputChanged += OnInputChanged;
    }

    public event Action? Stopping;

    public bool CanReset =>
        _state.SafetyReady && (_state.IsError || _state.Faulted);
    public bool CanHome =>
        _state.SafetyReady
        && _state.ServosOn
        && !_state.Homed
        && !_state.IsError
        && !_state.IsHoming
        && _supplyHandler.CanHome(
            _io.GetInput(InputIo.PcbBufferPcbPresent));

    public async Task InitializeAsync()
    {
        _io.Initialize();
        _light.Initialize();
        _conveyor.Initialize();
        ResetSmema();
        _supplyHandler.Initialize();
        _pcbPlacement.Initialize();
        await _boltFastening.InitializeAsync();
        _inspection.Initialize();
        _state.Refresh();
    }

    public void Stop()
    {
        Stopping?.Invoke();
        _buffer.Cancel();
        _supplyHandler.Stop();
        _pcbPlacement.Stop();
        _boltFastening.Stop();
        _inspection.Stop();
        _conveyor.Stop();
        ResetSmema();
        _light.TurnOffAll();
    }

    public void EmergencyStop()
    {
        Stopping?.Invoke();
        _buffer.Cancel();
        _supplyHandler.EmergencyStop();
        _pcbPlacement.EmergencyStop();
        _boltFastening.EmergencyStop();
        _inspection.EmergencyStop();
        _conveyor.EmergencyStop();
        ResetSmema();
        _light.TurnOffAll();
        _state.SetError(EquipmentAlarm.EmergencyStop);
    }

    public void Reset()
    {
        foreach (var motion in _motions)
        {
            motion.ResetAlarm();
        }

        _conveyor.ResetAlarm();
        _state.ClearError();
        _state.Refresh();
    }

    public async Task HomeAsync(CancellationToken cancellationToken)
    {
        if (!CanHome)
        {
            return;
        }

        _state.SetHoming(true);
        try
        {
            var otherMotions = _motions.Where(
                motion => !ReferenceEquals(motion, _supplyMotion));
            var zHomed = await Task.WhenAll(otherMotions
                .Select(motion => motion.HomeAsync(
                    MotionAxis.Z,
                    _settings.Home.ZSpeed,
                    cancellationToken))
                .Append(_supplyHandler.HomeAsync(
                    _io.GetInput(InputIo.PcbBufferPcbPresent),
                    _settings.Home.HorizontalSpeed,
                    _settings.Home.ZSpeed,
                    cancellationToken)));
            if (zHomed.Any(homed => !homed))
            {
                _state.SetError();
                return;
            }

            await Task.WhenAll(_motions.Select(
                motion => motion.MoveToSafeZAsync(cancellationToken)));

            var horizontalHomed = await Task.WhenAll(otherMotions.Select(
                motion => motion.HomeHorizontalAsync(
                    _settings.Home.HorizontalSpeed,
                    cancellationToken)));
            if (horizontalHomed.Any(homed => !homed))
            {
                _state.SetError();
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _state.SetHoming(false);
            _state.Refresh();
        }
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        var interlockOpened = !value
            && (input == InputIo.EmergencyStopReleased
                && _settings.Options.UseEmergencyStop
                || input == InputIo.DoorClosed
                && _settings.Options.UseDoorInterlock
                || input == InputIo.AirPressureOk
                && _settings.Options.UseAirPressureInterlock);
        if (interlockOpened)
        {
            EmergencyStop();
            return;
        }

        if (input == InputIo.ResetButton
            && value
            && _settings.Options.UseResetButton
            && CanReset)
        {
            Reset();
        }
    }

    private void ResetSmema()
    {
        _io.SetOutput(OutputIo.ConveyorUpstreamMachineReady, false);
        _io.SetOutput(OutputIo.ConveyorDownstreamBoardAvailable, false);
    }

}
