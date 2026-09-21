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
    private readonly AsyncAutoResetEvent _changed;
    // Observed edges during the current command only, not motor state.
    private int _phase;
    private ushort? _requestedPreset;

    public IoBoltHead(IIoService io, FasteningHead head, IoBoltHardwareSettings settings)
    {
        _changed = new();

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
        InterruptFastening();
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
        await CheckReadyAsync(cancellationToken);
        _requestedPreset = null;
        foreach (var output in _presets)
            _io.SetOutput(output, false);
        _io.SetOutput(_presets[preset - 1], true);
        _requestedPreset = preset;
    }

    public async Task<BoltResult> TightenAsync(
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? feedAsync = null,
        int dryRunMilliseconds = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(dryRunMilliseconds);
        await CheckReadyAsync(cancellationToken);
        if (_requestedPreset is not { } preset)
            throw new InvalidOperationException("Select an IO fastening preset before starting.");
        for (var index = 0; index < _presets.Length; index++)
        {
            if (_io.GetOutput(_presets[index]) != (index == preset - 1))
                throw new InvalidOperationException($"{_head} preset outputs no longer match preset {preset}.");
        }
        if (dryRunMilliseconds == 0 && _io.GetInput(_fasten))
            throw new InvalidOperationException($"{_head} FASTEN is already ON. Confirm it is OFF before a new fastening.");
        if (dryRunMilliseconds == 0 && _settings.FasteningTimeoutMilliseconds <= 0)
            throw new InvalidOperationException("IO fastening timeout must be greater than zero.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (dryRunMilliseconds == 0)
            timeout.CancelAfter(_settings.FasteningTimeoutMilliseconds);
        Exception? failure = null;
        var waitingForResult = false;
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            Volatile.Write(ref _phase, WaitingForOn);
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
                    // A failed head DOWN command must not become a successful fastening.
                    Interlocked.Exchange(ref _phase, Interrupted);
                    throw;
                }
            }
            if (dryRunMilliseconds > 0)
                timeout.CancelAfter(dryRunMilliseconds);
            waitingForResult = dryRunMilliseconds == 0;
            while (dryRunMilliseconds > 0 || Volatile.Read(ref _phase) != Completed)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _phase) == Interrupted
                    || (dryRunMilliseconds > 0 && !_io.GetOutput(_start)))
                    throw new OperationCanceledException($"{_head} fastening was stopped before completion.", cancellationToken);
                // Read for availability; only the ordered input events advance the edge history.
                if (dryRunMilliseconds == 0)
                    _ = _io.GetInput(_fasten);
                if (dryRunMilliseconds > 0 || Volatile.Read(ref _phase) != Completed)
                    await _changed.WaitAsync(timeout.Token);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (dryRunMilliseconds > 0
            && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested
            && Volatile.Read(ref _phase) != Interrupted)
        {
            // The dry-run timer requests normal STOP; no FASTEN result is expected.
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            var waitingFor = Volatile.Read(ref _phase) switch
            {
                WaitingForOn => $"{_fasten}=ON (no rising edge received)",
                WaitingForOff => $"{_fasten}=OFF (ON received; no falling edge received)",
                _ => "head DOWN command or an interrupted FASTEN cycle",
            };
            failure = new TimeoutException(
                $"{_head} FASTEN ON/OFF cycle timed out after {_settings.FasteningTimeoutMilliseconds} ms; "
                + $"waiting for {waitingFor}. START will be turned OFF.");
            if (!waitingForResult || Volatile.Read(ref _phase) == Interrupted)
                throw failure;
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
                Stop(failure);
            }
            finally
            {
                Volatile.Write(ref _phase, 0);
            }
        }

        if (_io.GetOutput(_start))
            throw new InvalidOperationException($"{_head} START is still ON. Stop the controller before collecting the result.");
        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null)
            return new(false, null, BoltResultSource.IoResultUnavailable, failure.Message);
        return new(true, null, dryRunMilliseconds > 0 ? BoltResultSource.DryRun : BoltResultSource.IoAssumedOk);
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input != _fasten)
            return;
        if (value)
            Interlocked.CompareExchange(ref _phase, WaitingForOff, WaitingForOn);
        else if (Volatile.Read(ref _phase) == WaitingForOff)
        {
            // FASTEN may fall before the direct START OFF write publishes OutputChanged.
            var phase = _io.GetOutput(_start) ? Completed : Interrupted;
            Interlocked.CompareExchange(ref _phase, phase, WaitingForOff);
        }
        _changed.Set();
    }

    private void OnOutputChanged(OutputIo output, bool value)
    {
        if (output == _start && !value)
            InterruptFastening();
    }

    private void InterruptFastening()
    {
        Interlocked.CompareExchange(ref _phase, Interrupted, WaitingForOn);
        Interlocked.CompareExchange(ref _phase, Interrupted, WaitingForOff);
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
