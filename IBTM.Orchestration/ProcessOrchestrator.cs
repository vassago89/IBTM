using System.Diagnostics;

namespace IBTM.Orchestration;

public sealed class ProcessOrchestrator : IDisposable
{
    private const int ConveyorChannel = 0;
    private static readonly TimeSpan ConveyorTransferDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan OutputTransitionDelay = TimeSpan.FromMilliseconds(200);

    private readonly IIOService _io;
    private readonly IPcbPlacementStation _pcbPlacement;
    private readonly IBoltFasteningStation _boltFastening;
    private readonly IInspectionStation _inspection;
    private readonly ProcessEventHub _events;
    private Recipe _currentRecipe = new();
    private CancellationTokenSource? _runCancellation;

    public ProcessOrchestrator(
        IIOService io,
        IPcbPlacementStation pcbPlacement,
        IBoltFasteningStation boltFastening,
        IInspectionStation inspection,
        ProcessEventHub events)
    {
        _io = io;
        _pcbPlacement = pcbPlacement;
        _boltFastening = boltFastening;
        _inspection = inspection;
        _events = events;
    }

    public ProductionStats Stats { get; } = new();
    public Recipe CurrentRecipe
    {
        get => _currentRecipe;
        set
        {
            _currentRecipe = value;
            RecipeChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<Recipe>? RecipeChanged;
    public int NgStackCount => _inspection.NgStackCount;
    public int NgStackCapacity => _inspection.NgStackCapacity;

    public void Initialize()
    {
        _io.Initialize();
        _pcbPlacement.Initialize();
        _boltFastening.Initialize();
        _inspection.Initialize();
        Stats.StartTime = DateTime.Now;
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
            _events.Stage(SystemStages.Idle, StageStatus.Idle);
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
        _runCancellation?.Cancel();
        _pcbPlacement.EmergencyStop();
        _boltFastening.EmergencyStop();
        _inspection.EmergencyStop();
        _io.TurnOffAll();
        _events.Stage(SystemStages.Idle, StageStatus.Error);
    }

    public void ResetNgStack() => _inspection.ResetNgStack();

    public void Dispose()
    {
        _runCancellation?.Cancel();
        StopEquipment();
    }

    private async Task RunPipelineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recipe = CurrentRecipe;
            var cycleTimer = Stopwatch.StartNew();

            await _pcbPlacement.RunAsync(recipe.PcbPlacement, cancellationToken);
            await TransferConveyorAsync(cancellationToken);
            await _boltFastening.RunAsync(recipe.BoltFastening, cancellationToken);
            await TransferConveyorAsync(cancellationToken);
            var result = await _inspection.RunAsync(recipe.Inspection, cancellationToken);
            await TransferConveyorAsync(cancellationToken);

            CompleteCycle(result, cycleTimer.Elapsed.TotalSeconds);
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }
    }

    private async Task TransferConveyorAsync(CancellationToken cancellationToken)
    {
        _io.SetOutput(ConveyorChannel, true);
        try
        {
            await Task.Delay(ConveyorTransferDelay, cancellationToken);
        }
        finally
        {
            _io.SetOutput(ConveyorChannel, false);
        }

        await Task.Delay(OutputTransitionDelay, cancellationToken);
    }

    private void CompleteCycle(InspectionResult result, double elapsedSeconds)
    {
        Stats.TotalCount++;
        if (result == InspectionResult.Good)
        {
            Stats.GoodCount++;
        }
        else
        {
            Stats.NgCount++;
        }

        Stats.LastCycleTimeSeconds = elapsedSeconds;
        _events.Stats(Stats);
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
