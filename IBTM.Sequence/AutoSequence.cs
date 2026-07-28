using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using IBTM.Transport;

namespace IBTM.Sequence;

public sealed class AutoSequence(
    IIoService _io,
    Conveyor _conveyor,
    PcbFeeder _pcbFeeder,
    PcbPlacementStation _pcbPlacement,
    BoltFasteningStation _boltFastening,
    InspectionStation _inspection,
    ILightController _light,
    IReadOnlyDictionary<EquipmentUnit, MotionService> _motions,
    MachineOptions _options,
    ProcessEvents _events) : IDisposable
{
    private readonly ProductionStats _stats = new();
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _resetMonitorCancellation;
    private bool _emergencyStopped;
    private bool _faulted;

    public Recipe CurrentRecipe { get; set; } = new();
    public int NgStackCount => _inspection.NgStackCount;
    public int NgStackCapacity => _inspection.NgStackCapacity;

    public void Initialize()
    {
        _io.Initialize();
        _light.Initialize();
        _conveyor.Initialize();
        _pcbFeeder.Initialize();
        _pcbPlacement.Initialize();
        _boltFastening.Initialize();
        _inspection.Initialize();
        SetStatusOutputs(OutputIo.TowerLampYellow, alarm: false);

        if (_options.UseResetButton)
        {
            _resetMonitorCancellation = new CancellationTokenSource();
            _ = MonitorResetButtonAsync(_resetMonitorCancellation.Token);
        }
    }

    public async Task StartAsync()
    {
        using var runCancellation = new CancellationTokenSource();
        _runCancellation = runCancellation;

        try
        {
            EnsureHardwareReady();
            SetStatusOutputs(OutputIo.TowerLampGreen, alarm: false);
            await _boltFastening.PrepareAsync(runCancellation.Token);
            await RunStationLoopsAsync(runCancellation.Token);
            SetStatusOutputs(OutputIo.TowerLampYellow, alarm: false);
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
            if (!_emergencyStopped)
            {
                SetStatusOutputs(OutputIo.TowerLampYellow, alarm: false);
                _events.Stage(ProcessStage.Idle, StageStatus.Idle);
            }
        }
        catch (IoFeedbackTimeoutException)
        {
            _faulted = true;
            SetStatusOutputs(OutputIo.TowerLampRed, alarm: true);
            throw;
        }
        catch (Exception)
        {
            _faulted = true;
            SetStatusOutputs(OutputIo.TowerLampRed, alarm: true);
            _events.Stage(ProcessStage.Error, StageStatus.Error);
            throw;
        }
        finally
        {
            StopEquipment();
            _runCancellation = null;
        }
    }

    public void Stop()
    {
        _runCancellation?.Cancel();
        StopEquipment();
        if (!_faulted && !_emergencyStopped)
        {
            SetStatusOutputs(OutputIo.TowerLampYellow, alarm: false);
        }
    }

    public void EmergencyStop()
    {
        _emergencyStopped = true;
        _runCancellation?.Cancel();
        _pcbFeeder.EmergencyStop();
        _pcbPlacement.EmergencyStop();
        _boltFastening.EmergencyStop();
        _inspection.EmergencyStop();
        _conveyor.EmergencyStop();
        _io.TurnOffAll();
        _faulted = true;
        SetStatusOutputs(OutputIo.TowerLampRed, alarm: true);
        _events.Stage(ProcessStage.Idle, StageStatus.Error);
    }

    public void Reset()
    {
        _runCancellation?.Cancel();
        StopEquipment();
        foreach (var motion in _motions.Values)
        {
            motion.ResetAlarm();
        }

        _conveyor.ResetAlarm();
        _emergencyStopped = false;
        _faulted = false;
        SetStatusOutputs(OutputIo.TowerLampYellow, alarm: false);
        _events.Stage(ProcessStage.Idle, StageStatus.Idle);
    }

    public void ResetNgStack() => _inspection.ResetNgStack();

    public void Dispose()
    {
        _runCancellation?.Cancel();
        _resetMonitorCancellation?.Cancel();
        _resetMonitorCancellation?.Dispose();
        StopEquipment();
    }

    private void EnsureHardwareReady()
    {
        foreach (var (unit, motion) in _motions)
        {
            foreach (var axis in motion.Axes)
            {
                var state = motion.GetAxisState(axis);
                if (!state.ServoOn
                    || !state.Homed
                    || state.Alarm
                    || state.Emergency
                    || state.PositiveLimit
                    || state.NegativeLimit)
                {
                    throw new InvalidOperationException(
                        $"{unit} {axis} axis is not ready.");
                }
            }
        }

        var conveyor = _conveyor.GetAxisState();
        if (!conveyor.ServoOn || conveyor.Alarm || conveyor.Emergency)
        {
            throw new InvalidOperationException("Conveyor axis is not ready.");
        }

        if (_options.UseEmergencyStop
            && !_io.GetInput(InputIo.EmergencyStopReleased))
        {
            throw new InvalidOperationException("Emergency stop is active.");
        }

        if (_options.UseDoorInterlock && !_io.GetInput(InputIo.DoorClosed))
        {
            throw new InvalidOperationException("Safety door is open.");
        }

        if (_options.UseAirPressureInterlock
            && !_io.GetInput(InputIo.AirPressureOk))
        {
            throw new InvalidOperationException("Air pressure is not ready.");
        }
    }

    private async Task RunStationLoopsAsync(CancellationToken cancellationToken)
    {
        using var workersCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = workersCancellation.Token;
        var boltFasteningInput = Channel.CreateBounded<CarrierJigState>(1);
        var inspectionInput = Channel.CreateBounded<CarrierJigState>(1);
        var pcbSupply = _pcbFeeder.RunAsync(CurrentRecipe.PcbSupply, token);
        var pcbPlacement = RunPcbPlacementLoopAsync(
            boltFasteningInput.Writer,
            token);
        var boltFastening = RunBoltFasteningLoopAsync(
            boltFasteningInput.Reader,
            inspectionInput.Writer,
            token);
        var inspection = RunInspectionLoopAsync(
            inspectionInput.Reader,
            token);
        List<Task> workers = [pcbSupply, pcbPlacement, boltFastening, inspection];
        var interlockMonitor = StartInterlockMonitor(token);
        if (interlockMonitor is not null)
        {
            workers.Add(interlockMonitor);
        }

        var completed = await Task.WhenAny(workers);
        workersCancellation.Cancel();

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) when (
            ReferenceEquals(completed, inspection)
            && inspection.IsCompletedSuccessfully
            && !cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Task? StartInterlockMonitor(CancellationToken cancellationToken)
    {
        List<InputIo> interlocks = [];
        if (_options.UseEmergencyStop)
        {
            interlocks.Add(InputIo.EmergencyStopReleased);
        }

        if (_options.UseDoorInterlock)
        {
            interlocks.Add(InputIo.DoorClosed);
        }

        if (_options.UseAirPressureInterlock)
        {
            interlocks.Add(InputIo.AirPressureOk);
        }

        return interlocks.Count == 0
            ? null
            : MonitorInterlocksAsync(interlocks, cancellationToken);
    }

    private async Task MonitorInterlocksAsync(
        IReadOnlyList<InputIo> interlocks,
        CancellationToken cancellationToken)
    {
        var waits = new Dictionary<Task, InputIo>();
        foreach (var interlock in interlocks)
        {
            waits.Add(
                _io.WaitForInputAsync(
                    interlock,
                    value: false,
                    cancellationToken),
                interlock);
        }

        var completed = await Task.WhenAny(waits.Keys);
        cancellationToken.ThrowIfCancellationRequested();
        var signal = waits[completed];
        EmergencyStop();
        throw new InvalidOperationException($"{signal} interlock opened.");
    }

    private async Task MonitorResetButtonAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _io.WaitForInputAsync(
                    InputIo.ResetButton,
                    value: true,
                    cancellationToken);
                if (_runCancellation is null && (_faulted || _emergencyStopped))
                {
                    Reset();
                }

                await _io.WaitForInputAsync(
                    InputIo.ResetButton,
                    value: false,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void SetStatusOutputs(OutputIo activeLamp, bool alarm)
    {
        if (_options.UseTowerLamp)
        {
            _io.SetOutput(OutputIo.TowerLampGreen, activeLamp == OutputIo.TowerLampGreen);
            _io.SetOutput(OutputIo.TowerLampYellow, activeLamp == OutputIo.TowerLampYellow);
            _io.SetOutput(OutputIo.TowerLampRed, activeLamp == OutputIo.TowerLampRed);
        }

        if (_options.UseBuzzer)
        {
            _io.SetOutput(OutputIo.Buzzer, alarm);
        }
    }

    private async Task RunPcbPlacementLoopAsync(
        ChannelWriter<CarrierJigState> output,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await _events.RunStageAsync(
                ProcessStage.ReceiveCarrierJig,
                cancellationToken,
                token => _conveyor.ReceiveAsync(
                    _pcbPlacement.CarrierJigPositioner,
                    token));
            var carrierJig = await _pcbPlacement.ProcessAsync(
                CurrentRecipe.PcbPlacement,
                cancellationToken);
            await _events.RunStageAsync(
                ProcessStage.TransferToBoltFastening,
                cancellationToken,
                token => _conveyor.TransferAsync(
                    _pcbPlacement.CarrierJigPositioner,
                    _boltFastening.CarrierJigPositioner,
                    token));
            await output.WriteAsync(carrierJig, cancellationToken);
        }
    }

    private async Task RunBoltFasteningLoopAsync(
        ChannelReader<CarrierJigState> input,
        ChannelWriter<CarrierJigState> output,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var carrierJig = await input.ReadAsync(cancellationToken);
            await _boltFastening.ProcessAsync(
                CurrentRecipe.BoltFastening,
                carrierJig,
                cancellationToken);
            await _events.RunStageAsync(
                ProcessStage.TransferToInspection,
                cancellationToken,
                token => _conveyor.TransferAsync(
                    _boltFastening.CarrierJigPositioner,
                    _inspection.CarrierJigPositioner,
                    token));
            await output.WriteAsync(carrierJig, cancellationToken);
        }
    }

    private async Task RunInspectionLoopAsync(
        ChannelReader<CarrierJigState> input,
        CancellationToken cancellationToken)
    {
        var cycleTimer = Stopwatch.StartNew();
        while (true)
        {
            var carrierJig = await input.ReadAsync(cancellationToken);
            var result = await _inspection.ProcessAsync(
                CurrentRecipe.Inspection,
                carrierJig,
                cancellationToken);
            if (result.OverallResult == InspectionResult.Good)
            {
                await _events.RunStageAsync(
                    ProcessStage.SendCarrierJig,
                    cancellationToken,
                    token => _conveyor.SendAsync(
                        _inspection.CarrierJigPositioner,
                        token));
            }

            CompleteCycle(result.OverallResult, cycleTimer.Elapsed.TotalSeconds);
            cycleTimer.Restart();
            if (NgStackCount >= NgStackCapacity)
            {
                return;
            }
        }
    }

    private void CompleteCycle(InspectionResult result, double elapsedSeconds)
    {
        _stats.TotalCount++;
        if (result == InspectionResult.Good)
        {
            _stats.GoodCount++;
        }
        else
        {
            _stats.NgCount++;
        }

        _stats.LastCycleTimeSeconds = elapsedSeconds;
        _events.Stats(_stats);
        _events.Stage(ProcessStage.Complete, StageStatus.Done);
    }

    private void StopEquipment()
    {
        _pcbFeeder.Stop();
        _pcbPlacement.Stop();
        _boltFastening.Stop();
        _inspection.Stop();
        _conveyor.Stop();
        _light.TurnOffAll();
    }
}
