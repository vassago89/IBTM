using System;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Core.Process;

public sealed class ProcessEvents
{
    public event Action<string, StageStatus>? StageChanged;
    public event Action<ProductionStats>? StatsUpdated;
    public event Action<int, double, double, double>? ZonePositionChanged;
    public event Action<FiducialResult>? FiducialDetected;
    public event Action<BoltResult>? BoltCompleted;
    public event Action<int, int, string>? BoltProgress;
    public event Action<InspectionOutcome>? InspectionCompleted;
    public event Action<int, bool>? NgStackChanged;

    public void Stage(string stage, StageStatus status) => StageChanged?.Invoke(stage, status);

    public void Stats(ProductionStats stats) => StatsUpdated?.Invoke(stats);
    public void Position(int zone, double x, double y, double z) =>
        ZonePositionChanged?.Invoke(zone, x, y, z);

    public void Fiducial(FiducialResult result) => FiducialDetected?.Invoke(result);
    public void Bolt(BoltResult result) => BoltCompleted?.Invoke(result);
    public void BoltProgressed(int current, int total, string boltName) =>
        BoltProgress?.Invoke(current, total, boltName);

    public void Inspection(InspectionOutcome outcome) =>
        InspectionCompleted?.Invoke(outcome);

    public void NgStack(int count, bool alarm) => NgStackChanged?.Invoke(count, alarm);

    public Task RunStageAsync(
        string stage,
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
        string stage,
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
        catch (Exception)
        {
            Stage(stage, StageStatus.Error);
            throw;
        }
    }
}
