using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public sealed record AdcStatusSample(long StartedAt, AdcControllerStatus? Status, Exception? Error);

// One acquisition loop per serial connection, shared by production and diagnostic heads.
public sealed class AdcStatusMonitor : INotifyPropertyChanged
{
    private readonly IAdcBus _bus;
    private readonly SemaphoreSlim _startGate;
    private readonly Lock _stateGate;
    private CancellationTokenSource? _lifetime;
    private Task _completion;
    private AdcStatusSample? _sample;

    public AdcStatusMonitor(IAdcBus bus)
    {
        _bus = bus;
        _startGate = new(1, 1);
        _stateGate = new();
        _completion = Task.CompletedTask;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<AdcStatusSample>? Sampled;
    public AdcStatusSample? Sample => Volatile.Read(ref _sample);
    public AdcControllerStatus? Status => Sample?.Status;
    public Exception? Error => Sample?.Error;
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
            lock (_stateGate)
            {
                if (_lifetime is { IsCancellationRequested: false })
                {
                    if (SlaveAddress != slaveAddress)
                        throw new InvalidOperationException("Close the ADC connection before changing the monitored slave.");
                    return;
                }
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
                // Open clears the previous connection. Its disconnect sample is not
                // feedback from this new session; stay unknown until the first reply.
                Publish(new(Stopwatch.GetTimestamp(), null, null));
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
            Publish(new(Stopwatch.GetTimestamp(), null,
                new IOException($"ADC {_bus.PortName}/{SlaveAddress} status monitor is disconnected.")));
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var startedAt = Stopwatch.GetTimestamp();
                AdcStatusSample sample;
                try
                {
                    var status = await _bus.ReadControllerStatusAsync(SlaveAddress, token).ConfigureAwait(false);
                    sample = new(startedAt, status, null);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    sample = new(startedAt, null, exception);
                }
                lock (_stateGate)
                {
                    token.ThrowIfCancellationRequested();
                    Publish(sample);
                }
                await Task.Delay(IntervalMilliseconds, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void Publish(AdcStatusSample sample)
    {
        var previous = Interlocked.Exchange(ref _sample, sample);
        if (previous?.Status != sample.Status)
            PropertyChanged?.Invoke(this, new(nameof(Status)));
        if (previous?.Error != sample.Error)
            PropertyChanged?.Invoke(this, new(nameof(Error)));
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
            else if (sample.Status is { } status)
                received.TrySetResult(status);
        }
        Sampled += OnSampled;
        try
        {
            if (Sample is { } sample)
                OnSampled(sample);
            return await received.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            Sampled -= OnSampled;
        }
    }
}
