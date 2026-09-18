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
        get
        {
            lock (_gate)
                return _activeOperations > 0;
        }
    }

    public bool IsShuttingDown
    {
        get
        {
            lock (_gate)
                return _shutdown is not null;
        }
    }

    public Operation Link(
        CancellationToken cancellationToken = default,
        CancellationToken additionalCancellationToken = default)
    {
        return Register(cancellationToken, additionalCancellationToken, requireIdle: false)!;
    }

    // Only top-level commands acquire idle ownership. Child device/cleanup scopes
    // still use Link, and a cancelled owner remains busy until its cleanup drains.
    public Operation? TryBegin(
        CancellationToken cancellationToken = default,
        CancellationToken additionalCancellationToken = default)
    {
        return Register(cancellationToken, additionalCancellationToken, requireIdle: true);
    }

    private Operation? Register(
        CancellationToken cancellationToken,
        CancellationToken additionalCancellationToken,
        bool requireIdle)
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
            if (requireIdle && _activeOperations != 0)
                return null;
            var source = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                additionalCancellationToken,
                _source.Token);
            becameActive = ++_activeOperations == 1;
            operation = new Operation(this, source);
        }

        try
        {
            if (becameActive)
                ActivityChanged?.Invoke();
            return operation;
        }
        catch (Exception failure)
        {
            try
            {
                operation.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(failure, cleanupFailure);
            }

            throw;
        }
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

    public sealed class Operation : IDisposable
    {
        private readonly OperationCancellation _owner;
        private readonly CancellationTokenSource _source;
        private readonly Lock _gate;
        private int _users; // Scope ownership plus active cancellation callbacks.
        private bool _disposeRequested;

        public Operation(OperationCancellation owner, CancellationTokenSource source)
        {
            _owner = owner;
            _source = source;
            _gate = new();
            _users = 1;
        }

        public CancellationToken Token
        {
            get
            {
                return _source.Token;
            }
        }

        public bool IsCancellationRequested
        {
            get
            {
                return _source.IsCancellationRequested;
            }
        }

        public void Cancel()
        {
            lock (_gate)
            {
                if (_disposeRequested)
                    return;
                _users++;
            }

            Exception? failure = null;
            try
            {
                _source.Cancel();
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
            finally
            {
                try
                {
                    Release();
                }
                catch (Exception cleanupFailure) when (failure is not null)
                {
                    throw new AggregateException(failure, cleanupFailure);
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposeRequested)
                    return;
                _disposeRequested = true;
            }

            Release();
        }

        private void Release()
        {
            lock (_gate)
            {
                if (--_users != 0)
                    return;
            }

            _source.Dispose();
            _owner.CompleteOperation();
        }
    }
}
