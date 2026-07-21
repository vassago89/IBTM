using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Process;
using IBTM.Device;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using IBTM.Transport;

namespace IBTM.Orchestration;

public sealed class ProcessOrchestrator : IDisposable
{
    private readonly IIoService _io;
    private readonly Conveyor _conveyor;
    private readonly PcbPlacementStation _pcbPlacement;
    private readonly BoltFasteningStation _boltFastening;
    private readonly InspectionStation _inspection;
    private readonly ProcessEvents _events;
    private readonly ProductionStats _stats = new();
    private CancellationTokenSource? _runCancellation;
    private bool _emergencyStopped;

    public ProcessOrchestrator(
        IIoService io,
        Conveyor conveyor,
        PcbPlacementStation pcbPlacement,
        BoltFasteningStation boltFastening,
        InspectionStation inspection,
        ProcessEvents events)
    {
        _io = io;
        _conveyor = conveyor;
        _pcbPlacement = pcbPlacement;
        _boltFastening = boltFastening;
        _inspection = inspection;
        _events = events;
    }

    public Recipe CurrentRecipe { get; set; } = new();
    public int NgStackCount => _inspection.NgStackCount;
    public int NgStackCapacity => _inspection.NgStackCapacity;

    public void Initialize()
    {
        _io.Initialize();
        _conveyor.Initialize();
        _pcbPlacement.Initialize();
        _boltFastening.Initialize();
        _inspection.Initialize();
    }

    public async Task StartAsync()
    {
        using var runCancellation = new CancellationTokenSource();
        _runCancellation = runCancellation;

        try
        {
            await _boltFastening.PrepareAsync(runCancellation.Token);
            await RunPipelineAsync(runCancellation.Token);
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
            if (!_emergencyStopped)
            {
                _events.Stage(SystemStages.Idle, StageStatus.Idle);
            }
        }
        catch (Exception)
        {
            _events.Stage(SystemStages.Error, StageStatus.Error);
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
    }

    public void EmergencyStop()
    {
        _emergencyStopped = true;
        _runCancellation?.Cancel();
        _pcbPlacement.EmergencyStop();
        _boltFastening.EmergencyStop();
        _inspection.EmergencyStop();
        _conveyor.EmergencyStop();
        _io.TurnOffAll();
        _events.Stage(SystemStages.Idle, StageStatus.Error);
    }

    public void Reset()
    {
        _runCancellation?.Cancel();
        StopEquipment();
        _emergencyStopped = false;
        _events.Stage(SystemStages.Idle, StageStatus.Idle);
    }

    public void ResetNgStack() => _inspection.ResetNgStack();

    public void Dispose()
    {
        _runCancellation?.Cancel();
        StopEquipment();
    }

    private async Task RunPipelineAsync(CancellationToken cancellationToken)
    {
        using var pipelineCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = pipelineCancellation.Token;
        var pcbPlacement = RunPcbPlacementLoopAsync(token);
        var boltFastening = RunBoltFasteningLoopAsync(token);
        var inspection = RunInspectionLoopAsync(token);
        Task[] workers = [pcbPlacement, boltFastening, inspection];

        var completed = await Task.WhenAny(workers);
        pipelineCancellation.Cancel();

        if (ReferenceEquals(completed, inspection) && inspection.IsCompletedSuccessfully)
        {
            try
            {
                await Task.WhenAll(workers);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            return;
        }

        await Task.WhenAll(workers);
    }

    private async Task RunPcbPlacementLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _events.RunStageAsync(
                PcbPlacementStages.ReceiveCarrierJig,
                cancellationToken,
                token => _conveyor.ReceiveAsync(
                    _pcbPlacement.CarrierJigPositioner,
                    token));
            await _pcbPlacement.ProcessAsync(
                CurrentRecipe.PcbPlacement,
                cancellationToken);
            await _conveyor.TransferAsync(
                _pcbPlacement.CarrierJigPositioner,
                _boltFastening.CarrierJigPositioner,
                cancellationToken);
        }
    }

    private async Task RunBoltFasteningLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _boltFastening.ProcessAsync(
                CurrentRecipe.BoltFastening,
                cancellationToken);
            await _conveyor.TransferAsync(
                _boltFastening.CarrierJigPositioner,
                _inspection.CarrierJigPositioner,
                cancellationToken);
        }
    }

    private async Task RunInspectionLoopAsync(CancellationToken cancellationToken)
    {
        var cycleTimer = Stopwatch.StartNew();
        while (true)
        {
            var result = await _inspection.ProcessAsync(
                CurrentRecipe.Inspection,
                cancellationToken);
            if (result.Result == InspectionResult.Good)
            {
                await _events.RunStageAsync(
                    InspectionStages.SendCarrierJig,
                    cancellationToken,
                    token => _conveyor.SendAsync(
                        _inspection.CarrierJigPositioner,
                        token));
            }

            CompleteCycle(result.Result, cycleTimer.Elapsed.TotalSeconds);
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
        _events.Stage(SystemStages.Complete, StageStatus.Done);
    }

    private void StopEquipment()
    {
        _pcbPlacement.Stop();
        _boltFastening.Stop();
        _inspection.Stop();
        _conveyor.Stop();
        _io.TurnOffAll();
    }
}
