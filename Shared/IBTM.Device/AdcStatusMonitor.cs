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

// Monotonic timestamps distinguish events from earlier operations.
public sealed record AdcEventSample(long ReceivedAt, AdcFasteningResult Result);

public sealed record AdcStatusSample(
    AdcControllerStatus? Status, Exception? Error, string? Rejection = null);

// One request queue per connection, shared by result polling and explicit requests.
public sealed class AdcStatusMonitor : INotifyPropertyChanged
{
    private const int ResultPollMilliseconds = 50;
    private readonly IAdcBus _bus;
    private readonly ILogger<AdcStatusMonitor> _logger;
    private readonly SemaphoreSlim _startGate;
    private readonly Lock _stateGate;
    private readonly Channel<Func<CancellationToken, Task>> _requests;
    private CancellationTokenSource? _lifetime;
    private Task _completion;
    private AdcStatusSample? _sample;

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
    public event Action<AdcEventSample>? EventReceived;
    public AdcEventSample? LastEvent { get; private set; }
    public AdcStatusSample? Sample => Volatile.Read(ref _sample);
    // Command ownership only; this does not claim that the controller accepted monitoring.
    public bool IsTorqueCurveMonitoringRequested { get; private set; }
    public string? TorqueCurveError { get; private set; }
    public long TorqueCurveCaptureStartedAt { get; private set; }
    public byte SlaveAddress { get; private set; }

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
                LastEvent = null;
                PropertyChanged?.Invoke(this, new(nameof(LastEvent)));
                // Open clears the previous connection. Its disconnect sample is not
                // feedback from this new session; stay unknown until the first reply.
                Publish(new(null, null));
                var token = _lifetime.Token;
                _completion = RunAsync(token);
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
            Publish(new(null,
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

    public async Task SetTorqueCurveMonitoringAsync(
        bool enabled, CancellationToken cancellationToken,
        AdcTorqueCurveSampling sampling = AdcTorqueCurveSampling.Milliseconds30)
    {
        if (!Enum.IsDefined(sampling))
            throw new ArgumentOutOfRangeException(nameof(sampling));
        await EnqueueAsync(async token =>
        {
            lock (_stateGate)
            {
                IsTorqueCurveMonitoringRequested = enabled;
                TorqueCurveCaptureStartedAt = 0;
                TorqueCurveError = null;
            }
            if (!enabled)
                return true;
            // Torque / angle / selected sampling / fastening.
            // ADC retains 200 samples: the sampling interval also determines the time window.
            // ADC requests use GetGraph(4200, 1), not the MDC 4100 enable/disable write.
            await _bus.WriteRegisterAsync(SlaveAddress, 4101, 1, token).ConfigureAwait(false);
            await _bus.WriteRegisterAsync(SlaveAddress, 4102, 4, token).ConfigureAwait(false);
            await _bus.WriteRegisterAsync(SlaveAddress, 4103, (ushort)sampling, token).ConfigureAwait(false);
            await _bus.WriteRegisterAsync(SlaveAddress, 4104, 1, token).ConfigureAwait(false);
            await _bus.RequestTorqueCurveAsync(SlaveAddress, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public void ReceiveTorqueCurve(AdcTorqueCurve? curve, string? error = null)
    {
        TorqueCurveError = error;
        if (curve is not null)
            TorqueCurveReceived?.Invoke(curve);
    }

    public void BeginTorqueCurveCapture()
    {
        lock (_stateGate)
        {
            TorqueCurveError = null;
            TorqueCurveCaptureStartedAt = Stopwatch.GetTimestamp();
        }
    }

    public async Task<AdcTorqueCurve> ReadTorqueCurveAsync(
        BoltControllerData result, double? torque, int timeoutMilliseconds, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);
        var received = new TaskCompletionSource<AdcTorqueCurve>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestedAt = long.MaxValue;
        void OnReceived(AdcTorqueCurve curve)
        {
            if (curve.ReceivedAt >= Volatile.Read(ref requestedAt)
                && curve.FasteningMilliseconds == result.FasteningTimeMilliseconds
                && curve.TargetTorque == result.TargetTorque
                && curve.FinalTorque == torque
                && curve.ScrewCount == result.ScrewCount
                && curve.ErrorCode == result.ErrorCode)
                received.TrySetResult(curve);
        }
        TorqueCurveReceived += OnReceived;
        try
        {
            // Query after completion: an earlier curve can have identical torque/time/count
            // values, so those fields alone cannot identify this bolt.
            await EnqueueAsync(async cancellationToken =>
            {
                TorqueCurveError = null;
                Volatile.Write(ref requestedAt, Stopwatch.GetTimestamp());
                await _bus.RequestTorqueCurveAsync(SlaveAddress, cancellationToken).ConfigureAwait(false);
                return true;
            }, token).ConfigureAwait(false);
            return await received.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds), token).ConfigureAwait(false);
        }
        finally
        {
            TorqueCurveReceived -= OnReceived;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var polledAt = Stopwatch.GetTimestamp();
        Task<bool>? requestAvailable = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = TimeSpan.FromMilliseconds(ResultPollMilliseconds) - Stopwatch.GetElapsedTime(polledAt);
                if (remaining <= TimeSpan.Zero)
                {
                    var result = await _bus.ReadFasteningResultAsync(SlaveAddress, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    ReceiveEvent(result);
                    polledAt = Stopwatch.GetTimestamp();
                    continue;
                }
                requestAvailable ??= _requests.Reader.WaitToReadAsync(token).AsTask();
                await Task.WhenAny(requestAvailable, Task.Delay(remaining, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (requestAvailable.IsCompleted)
                {
                    var canRead = await requestAvailable.ConfigureAwait(false);
                    requestAvailable = null;
                    if (canRead && _requests.Reader.TryRead(out var request))
                        await request(token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Publish(new(null, exception));
            _logger.LogError(exception, "ADC {Port}/{Slave}: result polling failed.", _bus.PortName, SlaveAddress);
        }
        finally
        {
            lock (_stateGate)
                _lifetime?.Cancel();
            while (_requests.Reader.TryRead(out var request))
                await request(new CancellationToken(canceled: true)).ConfigureAwait(false);
        }
    }

    public void ReceiveEvent(AdcFasteningResult result)
    {
        if (LastEvent?.Result.EventCount == result.EventCount)
            return;
        var now = Stopwatch.GetTimestamp();
        var received = new AdcEventSample(now, result);
        LastEvent = received;
        // Events contain results, not READY/RUN feedback. Do not retain an old status snapshot.
        Publish(new(null, null));
        EventReceived?.Invoke(received);
        PropertyChanged?.Invoke(this, new(nameof(LastEvent)));
    }

    public async Task<AdcControllerStatus> ReadStatusAsync(CancellationToken token)
    {
        return await EnqueueAsync(async cancellationToken =>
        {
            try
            {
                var response = await _bus.ReadControllerStatusAsync(SlaveAddress, cancellationToken).ConfigureAwait(false);
                Publish(new(response.Status, null, response.Rejection));
                return response.Status ?? throw new IOException(response.Rejection ?? "ADC status is unavailable.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Publish(new(null, exception));
                throw;
            }
        }, token).ConfigureAwait(false);
    }

    public void InvalidateSample(string reason)
    {
        lock (_stateGate)
            Publish(new(null, null, reason));
    }

    private void Publish(AdcStatusSample sample)
    {
        var previous = Interlocked.Exchange(ref _sample, sample);
        if (previous?.Status != sample.Status || previous?.Error != sample.Error || previous?.Rejection != sample.Rejection)
            PropertyChanged?.Invoke(this, new(nameof(Sample)));
        Sampled?.Invoke(sample);
    }
}
