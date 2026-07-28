using System;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Core;

public sealed class ProcessEvents
{
    public event Action<ProcessStage, StageStatus>? StageChanged;
    public event Action<ProcessStage, Exception>? StageFailed;
    public event Action<ProductionStats>? StatsUpdated;
    public event Action<BoltResult>? BoltCompleted;
    public event Action<int, int, PcbSlot, int>? BoltProgress;
    public event Action<CarrierInspectionResult>? InspectionCompleted;
    public event Action<int, bool>? NgStackChanged;

    public void Stage(ProcessStage stage, StageStatus status) =>
        StageChanged?.Invoke(stage, status);

    public void Stats(ProductionStats stats) => StatsUpdated?.Invoke(stats);

    public void Bolt(BoltResult result) => BoltCompleted?.Invoke(result);
    public void BoltProgressed(
        int current,
        int total,
        PcbSlot pcb,
        int boltNumber) =>
        BoltProgress?.Invoke(current, total, pcb, boltNumber);

    public void Inspection(CarrierInspectionResult result) =>
        InspectionCompleted?.Invoke(result);

    public void NgStack(int count, bool alarm) => NgStackChanged?.Invoke(count, alarm);

    public Task RunStageAsync(
        ProcessStage stage,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> action) =>
        RunStageAsync(
            stage,
            cancellationToken,
            async token =>
            {
                await action(token);
                return true;
            });

    public async Task<T> RunStageAsync<T>(
        ProcessStage stage,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> action)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stage(stage, StageStatus.Running);

        try
        {
            var result = await action(cancellationToken);
            Stage(stage, StageStatus.Done);
            return result;
        }
        catch (OperationCanceledException)
        {
            Stage(stage, StageStatus.Idle);
            throw;
        }
        catch (Exception exception)
        {
            Stage(stage, StageStatus.Error);
            StageFailed?.Invoke(stage, exception);
            throw;
        }
    }
}
