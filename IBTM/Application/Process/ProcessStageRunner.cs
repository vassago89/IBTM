
namespace IBTM.Application.Process;

public sealed class ProcessStageRunner(ProcessEventHub events)
{
    public Task RunAsync(
        ProcessStage stage,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> action) =>
        RunAsync(
            stage,
            cancellationToken,
            async token =>
            {
                await action(token);
                return true;
            });

    public async Task<T> RunAsync<T>(
        ProcessStage stage,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> action)
    {
        cancellationToken.ThrowIfCancellationRequested();
        events.Stage(stage, StageStatus.Running);

        try
        {
            var result = await action(cancellationToken);
            events.Stage(stage, StageStatus.Done);
            return result;
        }
        catch (OperationCanceledException)
        {
            events.Stage(stage, StageStatus.Idle);
            throw;
        }
        catch (Exception exception)
        {
            events.Log($"[{stage}] Error: {exception.Message}", stage, LogLevel.Error);
            events.Stage(stage, StageStatus.Error);
            throw;
        }
    }
}
