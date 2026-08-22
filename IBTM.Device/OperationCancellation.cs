using System.Threading;

namespace IBTM.Device;

public sealed class OperationCancellation
{
    private readonly Lock _gate = new();
    private CancellationTokenSource _source = new();

    public CancellationTokenSource Link(
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _source.Token);
        }
    }

    public void Cancel()
    {
        CancellationTokenSource source;
        lock (_gate)
        {
            source = _source;
            _source = new CancellationTokenSource();
        }

        try
        {
            source.Cancel();
        }
        finally
        {
            source.Dispose();
        }
    }
}
