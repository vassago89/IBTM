using System.Diagnostics;

namespace IBTM.Application.Process;

/// <summary>Runs the three zone workflows as one sequential machine cycle.</summary>
public sealed class ProcessOrchestrator : IDisposable
{
    private readonly object _stateLock = new();
    private readonly MachineOperations _machine;
    private readonly Zone1Workflow _zone1;
    private readonly Zone2Workflow _zone2;
    private readonly Zone3Workflow _zone3;
    private readonly ProcessEventHub _events;
    private CancellationTokenSource? _runCancellation;

    public ProcessOrchestrator(
        MachineOperations machine,
        Zone1Workflow zone1,
        Zone2Workflow zone2,
        Zone3Workflow zone3,
        ProcessEventHub events)
    {
        _machine = machine;
        _zone1 = zone1;
        _zone2 = zone2;
        _zone3 = zone3;
        _events = events;

        _machine.PositionChanged += OnPositionChanged;
        _machine.GripperChanged += OnGripperChanged;
    }

    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _runCancellation is { IsCancellationRequested: false };
            }
        }
    }

    public ProductionStats Stats { get; } = new();
    public Recipe CurrentRecipe { get; set; } = new();
    public int NgStackCount => _zone3.NgStackCount;
    public int NgStackCapacity => _zone3.NgStackCapacity;

    public void Initialize()
    {
        _machine.Initialize();
        Stats.StartTime = DateTime.Now;
    }

    public async Task StartAsync()
    {
        CancellationTokenSource runCancellation;
        lock (_stateLock)
        {
            if (_runCancellation is not null)
            {
                return;
            }

            _runCancellation = new CancellationTokenSource();
            runCancellation = _runCancellation;
        }

        _events.Log("Process started", ProcessStage.Idle);

        try
        {
            await _zone2.InitializeAsync(runCancellation.Token);
            await RunPipelineAsync(runCancellation.Token);
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
            _events.Log("Process stopped", ProcessStage.Idle);
            _events.Stage(ProcessStage.Idle, StageStatus.Idle);
        }
        catch (Exception exception)
        {
            _events.Log(
                $"Process error: {exception.Message}",
                ProcessStage.Error,
                LogLevel.Error);
            _events.Stage(ProcessStage.Error, StageStatus.Error);
        }
        finally
        {
            _machine.Stop();
            lock (_stateLock)
            {
                _runCancellation = null;
            }

            runCancellation.Dispose();
        }
    }

    public void Stop()
    {
        if (!CancelRun())
        {
            return;
        }

        _machine.Stop();
        _events.Log("Stop requested", ProcessStage.Idle, LogLevel.Warning);
    }

    public void EmergencyStop()
    {
        CancelRun();
        _machine.EmergencyStop();
        _events.Log("Emergency stop (E-STOP)!", ProcessStage.Idle, LogLevel.Error);
        _events.Stage(ProcessStage.Idle, StageStatus.Error);
    }

    public void ResetNgStack() => _zone3.ResetNgStack();

    public void Dispose()
    {
        CancelRun();
        _machine.Stop();
        _machine.PositionChanged -= OnPositionChanged;
        _machine.GripperChanged -= OnGripperChanged;
    }

    private async Task RunPipelineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recipe = CurrentRecipe;
            var cycleTimer = Stopwatch.StartNew();

            await _zone1.RunAsync(recipe, cancellationToken);
            await _machine.TransferConveyorAsync(cancellationToken);
            await _zone2.RunAsync(recipe, cancellationToken);
            await _machine.TransferConveyorAsync(cancellationToken);
            var result = await _zone3.RunAsync(recipe, cancellationToken);
            await _machine.TransferConveyorAsync(cancellationToken);

            CompleteCycle(result, cycleTimer.Elapsed.TotalSeconds);
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }
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
        Stats.AverageCycleTimeSeconds = Stats.AverageCycleTimeSeconds == 0
            ? elapsedSeconds
            : (Stats.AverageCycleTimeSeconds * 0.7) + (elapsedSeconds * 0.3);

        _events.Stats(Stats);
        _events.Stage(ProcessStage.Complete, StageStatus.Done);
        _events.Log(
            $"Cycle complete CT:{elapsedSeconds:F1}s total:{Stats.TotalCount:N0}",
            ProcessStage.Complete);
    }

    private bool CancelRun()
    {
        lock (_stateLock)
        {
            if (_runCancellation is not { IsCancellationRequested: false })
            {
                return false;
            }

            _runCancellation.Cancel();
            return true;
        }
    }

    private void OnPositionChanged(object? sender, ZonePositionEventArgs position) =>
        _events.Position(position);

    private void OnGripperChanged(object? sender, (int Zone, bool Active) state) =>
        _events.Gripper(state);
}
