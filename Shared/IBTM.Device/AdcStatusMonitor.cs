using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using IBTM.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IBTM.Device;

// Monotonic query timestamps; completion precedes feedback/change notification.
public sealed record AdcStatusSample(
    long StartedAt, long CompletedAt, AdcControllerStatus? Status, Exception? Error, string? Rejection = null);

// One communication loop per connection. Commands queue here alongside status sampling.
public sealed class AdcStatusMonitor : INotifyPropertyChanged
{
    private readonly IAdcBus _bus;
    private readonly ILogger<AdcStatusMonitor> _logger;
    private readonly SemaphoreSlim _startGate;
    private readonly Lock _stateGate;
    private readonly Channel<Func<CancellationToken, Task>> _requests;
    private CancellationTokenSource? _lifetime;
    private Task _completion;
    private AdcStatusSample? _sample;
    private AdcTorqueCurve? _torqueCurve;
    private long _torqueCurveRequestedAt;

    public AdcStatusMonitor(IAdcBus bus, ILogger<AdcStatusMonitor>? logger = null)
    {
        _bus = bus;
        _logger = logger ?? NullLogger<AdcStatusMonitor>.Instance;
        _startGate = new(1, 1);
        _stateGate = new();
        _requests = Channel.CreateUnbounded<Func<CancellationToken, Task>>(
            new UnboundedChannelOptions { SingleReader = true });
        _completion = Task.CompletedTask;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<AdcStatusSample>? Sampled;
    public event Action<AdcTorqueCurve>? TorqueCurveReceived;
    public AdcStatusSample? Sample => Volatile.Read(ref _sample);
    // Command ownership only; this does not claim that the controller accepted monitoring.
    public bool IsTorqueCurveMonitoringRequested { get; private set; }
    public string? TorqueCurveError { get; private set; }
    public long TorqueCurveCaptureStartedAt { get; private set; }
    public byte SlaveAddress { get; private set; }
    public int IntervalMilliseconds
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 100;

    public async Task StartAsync(byte slaveAddress, CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lifetime is { IsCancellationRequested: false })
            {
                if (SlaveAddress != slaveAddress)
                    throw new InvalidOperationException("Close the ADC connection before changing the monitored slave.");
                return;
            }
            await _completion.ConfigureAwait(false);
            lock (_stateGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_bus.IsOpen)
                    throw new IOException("Open the ADC connection before starting its status monitor.");
                _lifetime?.Dispose();
                _lifetime = new();
                SlaveAddress = slaveAddress;
                TorqueCurveError = null;
                // Open clears the previous connection. Its disconnect sample is not
                // feedback from this new session; stay unknown until the first reply.
                var now = Stopwatch.GetTimestamp();
                Publish(new(now, now, null, null));
                var token = _lifetime.Token;
                _completion = Task.Run(() => RunAsync(token), CancellationToken.None);
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public void Stop()
    {
        lock (_stateGate)
        {
            _lifetime?.Cancel();
            IsTorqueCurveMonitoringRequested = false;
            TorqueCurveCaptureStartedAt = 0;
            Interlocked.Exchange(ref _torqueCurve, null);
            var now = Stopwatch.GetTimestamp();
            Publish(new(now, now, null,
                new IOException($"ADC {_bus.PortName}/{SlaveAddress} status monitor is disconnected.")));
        }
    }

    public Task<T> EnqueueAsync<T>(
        Func<CancellationToken, Task<T>> request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queuedAt = Stopwatch.GetTimestamp();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task ExecuteAsync(CancellationToken lifetimeToken)
        {
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
                operation.Token.ThrowIfCancellationRequested();
                var result = await request(operation.Token).ConfigureAwait(false);
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
            {
                completion.TrySetException(new IOException($"ADC {_bus.PortName}/{SlaveAddress} communication stopped."));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                _logger.LogDebug(
                    "ADC {Port}/{Slave} queued request timing: response type={ResponseType}; "
                        + "queue wait={QueueWait:F1} ms; execution={Execution:F1} ms; outcome={Outcome}.",
                    _bus.PortName, SlaveAddress, typeof(T).Name,
                    Stopwatch.GetElapsedTime(queuedAt, startedAt).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, completion.Task.Status);
            }
        }

        lock (_stateGate)
        {
            if (_lifetime is not { IsCancellationRequested: false })
                throw new IOException("Start the ADC status monitor before queuing a request.");
            _requests.Writer.TryWrite(ExecuteAsync);
        }
        return completion.Task;
    }

    public async Task SetTorqueCurveMonitoringAsync(bool enabled, CancellationToken cancellationToken)
    {
        await EnqueueAsync(async token =>
        {
            lock (_stateGate)
            {
                Interlocked.Exchange(ref _torqueCurve, null);
                IsTorqueCurveMonitoringRequested = enabled;
                TorqueCurveCaptureStartedAt = 0;
                TorqueCurveError = null;
            }
            _torqueCurveRequestedAt = 0;
            if (!enabled)
                return true;
            // Torque / channel 2 off / 5 ms / fastening.
            // ADC requests use GetGraph(4200, 1), not the MDC 4100 enable/disable write.
            await _bus.WriteRegisterAsync(SlaveAddress, 4101, 1, token).ConfigureAwait(false);
            await _bus.WriteRegisterAsync(SlaveAddress, 4102, 0, token).ConfigureAwait(false);
            await _bus.WriteRegisterAsync(SlaveAddress, 4103, 1, token).ConfigureAwait(false);
            await _bus.WriteRegisterAsync(SlaveAddress, 4104, 1, token).ConfigureAwait(false);
            await _bus.RequestTorqueCurveAsync(SlaveAddress, token).ConfigureAwait(false);
            _torqueCurveRequestedAt = Stopwatch.GetTimestamp();
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public void ReceiveTorqueCurve(AdcTorqueCurve? curve, string? error = null)
    {
        TorqueCurveError = error;
        Interlocked.Exchange(ref _torqueCurve, curve);
        if (curve is not null)
            TorqueCurveReceived?.Invoke(curve);
    }

    public void BeginTorqueCurveCapture()
    {
        lock (_stateGate)
        {
            Interlocked.Exchange(ref _torqueCurve, null);
            TorqueCurveError = null;
            TorqueCurveCaptureStartedAt = Stopwatch.GetTimestamp();
        }
    }

    public async Task<AdcTorqueCurve> WaitForTorqueCurveAsync(
        long after, IBTM.Core.BoltControllerData result, double? torque, CancellationToken token)
    {
        var received = new TaskCompletionSource<AdcTorqueCurve>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReceived(AdcTorqueCurve curve)
        {
            if (curve.ReceivedAt >= after && curve.Matches(result, torque))
                received.TrySetResult(curve);
        }
        TorqueCurveReceived += OnReceived;
        try
        {
            if (Volatile.Read(ref _torqueCurve) is { } curve)
                OnReceived(curve);
            if (!received.Task.IsCompleted)
            {
                // Collect this bolt before the next fastening can replace the controller's graph.
                await EnqueueAsync(async cancellationToken =>
                {
                    await _bus.RequestTorqueCurveAsync(SlaveAddress, cancellationToken).ConfigureAwait(false);
                    _torqueCurveRequestedAt = Stopwatch.GetTimestamp();
                    return true;
                }, token).ConfigureAwait(false);
            }
            return await received.Task.WaitAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
        finally
        {
            TorqueCurveReceived -= OnReceived;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        long? lastStatusAt = null;
        long? previousStartedAt = null;
        Task<bool>? requestAvailable = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = lastStatusAt is { } sampledAt
                    ? TimeSpan.FromMilliseconds(IntervalMilliseconds) - Stopwatch.GetElapsedTime(sampledAt)
                    : TimeSpan.Zero;
                if (remaining > TimeSpan.Zero)
                {
                    // Keep one queue wait across idle samples; a polling deadline is not a cancellation.
                    requestAvailable ??= _requests.Reader.WaitToReadAsync(token).AsTask();
                    await Task.WhenAny(requestAvailable, Task.Delay(remaining, token)).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (requestAvailable.IsCompleted)
                    {
                        var canRead = await requestAvailable.ConfigureAwait(false);
                        requestAvailable = null;
                        if (canRead && _requests.Reader.TryRead(out var request))
                        {
                            // Await the complete exchange before another queued request or status query.
                            await request(token).ConfigureAwait(false);
                            continue;
                        }
                    }
                }
                var startedAt = Stopwatch.GetTimestamp();
                AdcStatusSample sample;
                try
                {
                    if (IsTorqueCurveMonitoringRequested && _torqueCurveRequestedAt != 0
                        && Stopwatch.GetElapsedTime(_torqueCurveRequestedAt).TotalSeconds >= 5)
                    {
                        // Hantas HComm's ADC example repeats GetGraph every five seconds.
                        await _bus.RequestTorqueCurveAsync(SlaveAddress, token).ConfigureAwait(false);
                        _torqueCurveRequestedAt = Stopwatch.GetTimestamp();
                    }
                    var response = await _bus.ReadControllerStatusAsync(SlaveAddress, token).ConfigureAwait(false);
                    sample = new(startedAt, Stopwatch.GetTimestamp(), response.Status, null, response.Rejection);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    sample = new(startedAt, Stopwatch.GetTimestamp(), null, exception);
                }
                lock (_stateGate)
                {
                    token.ThrowIfCancellationRequested();
                    Publish(sample);
                }
                var publishedAt = Stopwatch.GetTimestamp();
                _logger.LogDebug(
                    "ADC {Port}/{Slave} status timing: start gap={StartGap:F1} ms; "
                        + "since previous publish={SincePublish:F1} ms; query={Query:F1} ms; "
                        + "publish={Publish:F1} ms; configured pause after publish={Interval} ms; "
                        + "READY={Ready}, RUN={Running}, ALARM={Alarm}; rejected={Rejected}; error={ErrorType}.",
                    _bus.PortName, SlaveAddress,
                    previousStartedAt is { } previous
                        ? Stopwatch.GetElapsedTime(previous, startedAt).TotalMilliseconds : (double?)null,
                    lastStatusAt is { } last
                        ? Stopwatch.GetElapsedTime(last, startedAt).TotalMilliseconds : (double?)null,
                    Stopwatch.GetElapsedTime(startedAt, sample.CompletedAt).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(sample.CompletedAt, publishedAt).TotalMilliseconds,
                    IntervalMilliseconds, sample.Status?.Ready, sample.Status?.Running, sample.Status?.Alarm,
                    sample.Rejection is not null, sample.Error?.GetType().Name);
                previousStartedAt = startedAt;
                lastStatusAt = publishedAt;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lock (_stateGate)
                _lifetime?.Cancel();
            // Closing a port must finish pending callers without sending their requests.
            while (_requests.Reader.TryRead(out var request))
                await request(new CancellationToken(canceled: true)).ConfigureAwait(false);
        }
    }

    public void InvalidateSample(string reason)
    {
        lock (_stateGate)
        {
            var now = Stopwatch.GetTimestamp();
            Publish(new(now, now, null, null, reason));
        }
    }

    private void Publish(AdcStatusSample sample)
    {
        var previous = Interlocked.Exchange(ref _sample, sample);
        if (previous?.Status != sample.Status || previous?.Error != sample.Error || previous?.Rejection != sample.Rejection)
            PropertyChanged?.Invoke(this, new(nameof(Sample)));
        Sampled?.Invoke(sample);
    }

    public async Task<AdcControllerStatus> WaitForSampleAsync(long after, CancellationToken token)
    {
        var received = new TaskCompletionSource<AdcControllerStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSampled(AdcStatusSample sample)
        {
            if (sample.StartedAt < after)
                return;
            if (sample.Error is { } error)
                received.TrySetException(error);
            // Rejected replies and retried timeouts carry no feedback.
            // Exhausted retries and other transport failures still fail above.
            else if (sample.Rejection is null && sample.Status is { } status)
                received.TrySetResult(status);
        }
        Sampled += OnSampled;
        try
        {
            if (Sample is { } sample)
                OnSampled(sample);
            var status = await received.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return status;
        }
        finally
        {
            Sampled -= OnSampled;
        }
    }
}
