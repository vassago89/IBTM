using System;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Core;

public abstract class AutoUnit
{
    private readonly AsyncAutoResetEvent _stateChanged = new();

    public abstract event Action? Changed;

    protected Task WaitForChangeAsync(CancellationToken cancellationToken) =>
        _stateChanged.WaitAsync(cancellationToken);

    protected async Task RunLoopAsync(
        Func<CancellationToken, Task> execute,
        CancellationToken cancellationToken,
        Func<bool>? completed = null)
    {
        Changed += _stateChanged.Set;
        try
        {
            while (!cancellationToken.IsCancellationRequested && completed?.Invoke() != true)
            {
                await execute(cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Changed -= _stateChanged.Set;
        }
    }
}
