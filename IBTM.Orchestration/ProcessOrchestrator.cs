using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Process;
using IBTM.Device;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;

namespace IBTM.Orchestration;

public sealed class ProcessOrchestrator : IDisposable
{
    private const int ConveyorChannel = 0;

    private readonly IIoService _io;
    private readonly PcbPlacementStation _pcbPlacement;
    private readonly BoltFasteningStation _boltFastening;
    private readonly InspectionStation _inspection;
    private readonly ProcessEvents _events;
    private readonly ProductionStats _stats = new();
    private CancellationTokenSource? _runCancellation;
    private bool _emergencyStopped;

    public ProcessOrchestrator(
        IIoService io,
        PcbPlacementStation pcbPlacement,
        BoltFasteningStation boltFastening,
        InspectionStation inspection,
        ProcessEvents events)
    {
        _io = io;
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
        _io.SetOutput(ConveyorChannel, true);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recipe = CurrentRecipe;
            var cycleTimer = Stopwatch.StartNew();

            await _pcbPlacement.RunAsync(recipe.PcbPlacement, cancellationToken);
            await _boltFastening.RunAsync(recipe.BoltFastening, cancellationToken);
            var result = await _inspection.RunAsync(recipe.Inspection, cancellationToken);

            CompleteCycle(result, cycleTimer.Elapsed.TotalSeconds);
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
        _io.TurnOffAll();
    }
}
