using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class IoBoltHead : IBoltHead, IDisposable
{
    private const int WaitingForOn = 1;
    private const int WaitingForOff = 2;
    private const int Completed = 3;
    private const int Interrupted = 4;
    private readonly IIoService _io;
    private readonly FasteningHead _head;
    private readonly IoBoltHardwareSettings _settings;
    private readonly InputIo _fasten;
    private readonly OutputIo _start;
    private readonly OutputIo[] _presets;
    private readonly AsyncAutoResetEvent _changed = new();
    // Observed edges for this command and its uncollected result, not motor state.
    private int _pendingPhase;
    private ushort? _requestedPreset;

    public IoBoltHead(IIoService io, FasteningHead head, IoBoltHardwareSettings settings)
    {
        _io = io;
        _head = head;
        _settings = settings;
        (_fasten, _start) = head switch
        {
            FasteningHead.Pickup => (InputIo.PickupBoltFasten, OutputIo.PickupBoltStart),
            FasteningHead.Shooting => (InputIo.ShootingBoltFasten, OutputIo.ShootingBoltStart),
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
        _presets = head == FasteningHead.Pickup
            ? [OutputIo.PickupBoltPreset1, OutputIo.PickupBoltPreset2, OutputIo.PickupBoltPreset3]
            : [OutputIo.ShootingBoltPreset1, OutputIo.ShootingBoltPreset2, OutputIo.ShootingBoltPreset3];
        io.InputChanged += OnInputChanged;
        io.OutputChanged += OnOutputChanged;
        io.Faulted += OnIoFaulted;
    }

    public bool HasPendingResult
    {
        get
        {
            return Volatile.Read(ref _pendingPhase) != 0;
        }
    }

    public bool RequiresRecovery
    {
        get
        {
            return Volatile.Read(ref _pendingPhase) == Interrupted;
        }
    }

    public Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.CheckReady();
        if (_io.GetOutput(_start))
            throw new InvalidOperationException($"{_head} IO bolt controller requires START OFF before a new command.");
        return Task.CompletedTask;
    }

    public void Stop(Exception? operationFailure = null)
    {
        // A falling FASTEN caused by our STOP is not normal completion.
        InterruptPending();
        // Confirmed interface: START is held ON while running; OFF requests stop.
        try
        {
            _io.SetOutput(_start, false);
        }
        catch (Exception stopFailure) when (operationFailure is not null)
        {
            throw new AggregateException(operationFailure, stopFailure);
        }
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stop();
        // RESET is exposed as a raw output; no unconfirmed reset pulse is generated here.
        return CheckReadyAsync(cancellationToken);
    }

    public async Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (preset is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(preset), "IO bolt presets are 1, 2 and 3.");
        if (HasPendingResult)
            throw new InvalidOperationException("Resolve the previous IO fastening before changing its preset.");
        await CheckReadyAsync(cancellationToken);
        _requestedPreset = null;
        foreach (var output in _presets)
            _io.SetOutput(output, false);
        _io.SetOutput(_presets[preset - 1], true);
        _requestedPreset = preset;
    }

    public async Task<BoltResult> TightenAsync(
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? feedAsync = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HasPendingResult)
        {
            return await ReadPendingResultAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    $"{_head} IO fastening was interrupted without a complete FASTEN ON/OFF cycle. Check the bolt and resolve it in Recovery before restarting.");
        }

        await CheckReadyAsync(cancellationToken);
        if (_requestedPreset is not { } preset)
            throw new InvalidOperationException("Select an IO fastening preset before starting.");
        for (var index = 0; index < _presets.Length; index++)
        {
            if (_io.GetOutput(_presets[index]) != (index == preset - 1))
                throw new InvalidOperationException($"{_head} preset outputs no longer match preset {preset}.");
        }
        if (_io.GetInput(_fasten))
            throw new InvalidOperationException($"{_head} FASTEN is already ON. Confirm it is OFF before a new fastening.");
        if (_settings.FasteningTimeoutMilliseconds <= 0)
            throw new InvalidOperationException("IO fastening timeout must be greater than zero.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_settings.FasteningTimeoutMilliseconds);
        Exception? failure = null;
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            Volatile.Write(ref _pendingPhase, WaitingForOn);
            _io.SetOutput(_start, true);
            if (feedAsync is not null)
            {
                try
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    await feedAsync(timeout.Token);
                }
                catch
                {
                    // FASTEN alone cannot establish OK when axial feed was not confirmed.
                    Interlocked.Exchange(ref _pendingPhase, Interrupted);
                    throw;
                }
            }
            while (Volatile.Read(ref _pendingPhase) != Completed)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _pendingPhase) == Interrupted)
                    throw new OperationCanceledException($"{_head} fastening was stopped before completion.", cancellationToken);
                // Read for availability; only the ordered input events advance the edge history.
                _ = _io.GetInput(_fasten);
                if (Volatile.Read(ref _pendingPhase) != Completed)
                    await _changed.WaitAsync(timeout.Token);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            failure = new TimeoutException(
                $"{_head} FASTEN ON/OFF cycle timed out after {_settings.FasteningTimeoutMilliseconds} ms.");
            throw failure;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            Stop(failure);
        }

        return await ReadPendingResultAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("The completed IO fastening result was cleared before collection.");
    }

    public Task<BoltResult?> ReadPendingResultAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _pendingPhase) != Completed)
            return Task.FromResult<BoltResult?>(null);
        if (_io.GetOutput(_start))
            throw new InvalidOperationException($"{_head} START is still ON. Stop the controller before collecting the result.");

        Volatile.Write(ref _pendingPhase, 0);
        return Task.FromResult<BoltResult?>(new(true, null, BoltResultSource.IoAssumedOk));
    }

    public void DiscardPendingResult()
    {
        Volatile.Write(ref _pendingPhase, 0);
        _requestedPreset = null;
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input != _fasten)
            return;
        if (value)
            Interlocked.CompareExchange(ref _pendingPhase, WaitingForOff, WaitingForOn);
        else if (Volatile.Read(ref _pendingPhase) == WaitingForOff)
        {
            // FASTEN may fall before the direct START OFF write publishes OutputChanged.
            var phase = _io.GetOutput(_start) ? Completed : Interrupted;
            Interlocked.CompareExchange(ref _pendingPhase, phase, WaitingForOff);
        }
        _changed.Set();
    }

    private void OnOutputChanged(OutputIo output, bool value)
    {
        if (output == _start && !value)
            InterruptPending();
    }

    private void InterruptPending()
    {
        Interlocked.CompareExchange(ref _pendingPhase, Interrupted, WaitingForOn);
        Interlocked.CompareExchange(ref _pendingPhase, Interrupted, WaitingForOff);
        _changed.Set();
    }

    private void OnIoFaulted(Exception exception)
    {
        _changed.Set();
    }

    public void Dispose()
    {
        _io.InputChanged -= OnInputChanged;
        _io.OutputChanged -= OnOutputChanged;
        _io.Faulted -= OnIoFaulted;
    }
}
