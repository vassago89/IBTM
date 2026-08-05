using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.PcbBuffer;

public enum BufferOwner
{
    [Description("Available")]
    None,

    [Description("Supply Handler")]
    Supply,

    [Description("Placement Handler")]
    Placement,
}

public sealed class BufferStage(PcbBufferSettings settings) : IDisposable
{
    private readonly SemaphoreSlim _access = new(1, 1);
    private CancellationTokenSource _cancellation = new();

    public event Action? Changed;

    public BufferOwner Owner { get; private set; }
    public bool CancellationRequested =>
        _cancellation.IsCancellationRequested;

    public bool CanEnter(
        double supplyX,
        double placementX,
        double placementY) =>
        settings.IsConfigured
        && !CancellationRequested
        && Owner == BufferOwner.None
        && IsClear(supplyX, placementX, placementY);

    public async Task<CancellationToken> EnterAsync(
        BufferOwner owner,
        double supplyX,
        double placementX,
        double placementY)
    {
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException(
                "PCB buffer collision area is not configured.");
        }

        var cancellationToken = _cancellation.Token;
        await _access.WaitAsync(cancellationToken);
        if (!IsClear(supplyX, placementX, placementY))
        {
            _access.Release();
            throw new InvalidOperationException(
                "A handler is inside the PCB buffer collision area.");
        }

        Owner = owner;
        Changed?.Invoke();
        return cancellationToken;
    }

    public void ExitSupply(double x)
    {
        if (settings.ContainsSupply(x))
        {
            throw new InvalidOperationException(
                "Supply handler is still inside the PCB buffer collision area.");
        }

        Release();
    }

    public void ExitPlacement(double x, double y)
    {
        if (settings.ContainsPlacement(x, y))
        {
            throw new InvalidOperationException(
                "Placement handler is still inside the PCB buffer collision area.");
        }

        Release();
    }

    public void Cancel()
    {
        _cancellation.Cancel();
        if (Owner == BufferOwner.None)
        {
            ResetCancellation();
            return;
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        _access.Dispose();
    }

    private void Release()
    {
        Owner = BufferOwner.None;
        if (CancellationRequested)
        {
            _cancellation.Dispose();
            _cancellation = new CancellationTokenSource();
        }

        _access.Release();
        Changed?.Invoke();
    }

    private void ResetCancellation()
    {
        _cancellation.Dispose();
        _cancellation = new CancellationTokenSource();
        Changed?.Invoke();
    }

    private bool IsClear(
        double supplyX,
        double placementX,
        double placementY) =>
        !settings.ContainsSupply(supplyX)
        && !settings.ContainsPlacement(placementX, placementY);
}
