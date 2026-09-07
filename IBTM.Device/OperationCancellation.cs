using System;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public sealed class OperationCancellation
{
    private readonly Lock _gate = new();
    private CancellationTokenSource _source = new();
    private int _activeOperations;
    private TaskCompletionSource? _shutdown;
    private TaskCompletionSource? _drained;

    public event Action? ActivityChanged;

    public bool HasActiveOperations
    {
        get { lock (_gate) return _activeOperations > 0; }
    }

    public bool IsShuttingDown
    {
        get { lock (_gate) return _shutdown is not null; }
    }

    public Operation Link(
        CancellationToken cancellationToken = default,
        CancellationToken additionalCancellationToken = default)
    {
        Operation operation;
        bool becameActive;
        lock (_gate)
        {
            if (_shutdown is not null)
            {
                throw new OperationCanceledException("The machine is shutting down.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            additionalCancellationToken.ThrowIfCancellationRequested();
            var source = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                additionalCancellationToken,
                _source.Token);
            becameActive = ++_activeOperations == 1;
            operation = new Operation(this, source);
        }

        if (becameActive)
        {
            ActivityChanged?.Invoke();
        }

        return operation;
    }

    public void Cancel()
    {
        CancellationTokenSource source;
        lock (_gate)
        {
            if (_shutdown is not null)
            {
                return;
            }

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

    public Task ShutdownAsync()
    {
        CancellationTokenSource source;
        TaskCompletionSource shutdown;
        Task drained;
        lock (_gate)
        {
            if (_shutdown is not null)
            {
                return _shutdown.Task;
            }

            shutdown = _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
            source = _source;
            drained = _activeOperations == 0
                ? Task.CompletedTask
                : (_drained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        _ = CompleteShutdownAsync(source, drained, shutdown);
        return shutdown.Task;
    }

    private static async Task CompleteShutdownAsync(
        CancellationTokenSource source,
        Task drained,
        TaskCompletionSource completion)
    {
        try
        {
            try
            {
                source.Cancel();
            }
            finally
            {
                await drained.ConfigureAwait(false);
                source.Dispose();
            }

            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private void CompleteOperation()
    {
        bool becameIdle;
        lock (_gate)
        {
            becameIdle = --_activeOperations == 0;
            if (becameIdle)
            {
                _drained?.TrySetResult();
            }
            // Shutdown may dispose subscribers once the drain completes.
            becameIdle &= _shutdown is null;
        }

        if (becameIdle)
        {
            ActivityChanged?.Invoke();
        }
    }

    public sealed class Operation(
        OperationCancellation owner,
        CancellationTokenSource source) : IDisposable
    {
        private readonly Lock _gate = new();
        private int _users = 1; // Scope ownership plus active cancellation callbacks.
        private bool _disposeRequested;
        public CancellationToken Token => source.Token;
        public bool IsCancellationRequested => source.IsCancellationRequested;

        public void Cancel()
        {
            lock (_gate)
            {
                if (_disposeRequested) return;
                _users++;
            }

            try
            {
                source.Cancel();
            }
            finally
            {
                Release();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposeRequested) return;
                _disposeRequested = true;
            }
            Release();
        }

        private void Release()
        {
            lock (_gate)
            {
                if (--_users != 0) return;
            }

            source.Dispose();
            owner.CompleteOperation();
        }
    }
}
