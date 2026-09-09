using System;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Core;

public sealed class AsyncAutoResetEvent
{
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _signal = new(0, 1);

    public Task WaitAsync(CancellationToken cancellationToken = default) =>
        _signal.WaitAsync(cancellationToken);

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        _signal.WaitAsync(timeout, cancellationToken);

    public void Set()
    {
        lock (_lock)
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
    }
}
