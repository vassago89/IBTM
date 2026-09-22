using System;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IBTM.Hantas;

public sealed class AdcBoltHead : IBoltHead, INotifyPropertyChanged
{
    private readonly IAdcBus _bus;
    private readonly IIoService _io;
    private readonly OutputIo _start;
    private readonly OutputIo _direction;
    private readonly OutputIo _reset;
    private readonly OutputIo[] _presets;
    private const int ResetPulseMilliseconds = 100;
    private readonly HantasSettings _connection;
    private readonly byte _slaveAddress;
    private readonly ILogger<AdcBoltHead> _logger;
    // Connection edits apply to a newly created head, together with its slave address.
    private readonly string _portName;
    private readonly int _baudRate;
    // Requested preset; never a substitute for controller feedback.
    private ushort? _requestedPreset;

    public AdcBoltHead(
        IAdcBus bus,
        IIoService io,
        FasteningHead head,
        HantasSettings connection,
        byte slaveAddress,
        string portName,
        int baudRate,
        ILogger<AdcBoltHead>? logger = null)
    {
        _bus = bus;
        _io = io;
        (_start, _direction, _reset) = head switch
        {
            FasteningHead.Pickup => (OutputIo.PickupBoltStart, OutputIo.PickupBoltDirection, OutputIo.PickupBoltReset),
            FasteningHead.Shooting => (OutputIo.ShootingBoltStart, OutputIo.ShootingBoltDirection, OutputIo.ShootingBoltReset),
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
        _presets = head == FasteningHead.Pickup
            ? [OutputIo.PickupBoltPreset1, OutputIo.PickupBoltPreset2, OutputIo.PickupBoltPreset3]
            : [OutputIo.ShootingBoltPreset1, OutputIo.ShootingBoltPreset2, OutputIo.ShootingBoltPreset3];
        _connection = connection;
        _slaveAddress = slaveAddress;
        _portName = portName;
        _baudRate = baudRate;
        _logger = logger ?? NullLogger<AdcBoltHead>.Instance;
    }

    // A single ADC sample after a command; never continuous physical feedback.
    public AdcControllerStatus? LastStatus
    {
        get;
        private set
        {
            field = value;
            PropertyChanged?.Invoke(this, new(nameof(LastStatus)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.CheckReady();
        var status = await ReadStatusAsync(cancellationToken);
        if (status.Alarm != 0 || !status.Ready || status.Running || _io.GetOutput(_start))
            throw new InvalidOperationException(
                $"ADC {_portName}/{_slaveAddress} not ready: "
                + $"{AdcControllerError.Describe(status.Alarm)}, READY={status.Ready}, "
                + $"RUN={status.Running}, START={_io.GetOutput(_start)}.");
    }

    private async Task<AdcControllerStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        LastStatus = null;
        _bus.Open(_portName, _baudRate);
        var status = await _bus.ReadControllerStatusAsync(_slaveAddress, cancellationToken);
        LastStatus = status;
        _logger.LogInformation("ADC {Port}/{Slave} command completed: READY={Ready}, RUN={Running}, ALARM={Alarm}.",
            _portName, _slaveAddress, status.Ready, status.Running, status.Alarm);
        return status;
    }

    public async Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (preset is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(preset), "IO bolt presets are 1, 2 and 3.");
        if (LastStatus is { Alarm: > 0 })
        {
            _logger.LogWarning("ADC {Port}/{Slave} last ADC sample reported an alarm; resetting once before the next bolt.",
                _portName, _slaveAddress);
            await ResetAsync(cancellationToken);
        }
        _io.CheckReady();
        if (_io.GetOutput(_start))
            throw new InvalidOperationException("Turn START OFF before selecting a preset.");
        LastStatus = null;
        _requestedPreset = null;
        foreach (var output in _presets)
            _io.SetOutput(output, false);
        _io.SetOutput(_presets[preset - 1], true);
        await CheckReadyAsync(cancellationToken);
        _requestedPreset = preset;
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastStatus = null;
        _io.SetOutput(_start, false);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _io.SetOutput(_reset, true);
            await Task.Delay(ResetPulseMilliseconds, cancellationToken);
        }
        finally
        {
            _io.SetOutput(_reset, false);
        }
        await CheckReadyAsync(cancellationToken);
    }

    // Manual hold-to-run only. Cancellation stops rotation; it is not a loose-complete result.
    public async Task RunReverseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.CheckReady();
        if (_io.GetOutput(_start))
            throw new InvalidOperationException("Turn START OFF before loosening.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? failure = null;
        Exception? ioFailure = null;
        void OnIoFaulted(Exception exception)
        {
            ioFailure = exception;
            operation.Cancel();
        }
        _io.Faulted += OnIoFaulted;
        try
        {
            _io.SetOutput(_direction, true);
            await CheckReadyAsync(operation.Token);
            LastStatus = null;
            _io.SetOutput(_start, true);
            await Task.Delay(Timeout.Infinite, operation.Token);
        }
        catch (OperationCanceledException) when (ioFailure is not null)
        {
            failure = ioFailure;
            throw ioFailure;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _io.Faulted -= OnIoFaulted;
            await StopAfterOperationAsync(failure);
        }
    }

    public async Task<BoltResult> TightenAsync(
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? feedAsync = null,
        int dryRunMilliseconds = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(dryRunMilliseconds);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        (ushort EventCount, ushort Preset)? started = null;
        AdcFasteningResult? completed = null;
        AdcFasteningResult? lastResult = null;
        Exception? failure = null;
        var waitingForResult = false;
        Exception? ioFailure = null;
        void OnIoFaulted(Exception exception)
        {
            Interlocked.CompareExchange(ref ioFailure, exception, null);
            timeout.Cancel();
        }
        _io.Faulted += OnIoFaulted;
        try
        {
            _io.CheckReady();
            if (_io.GetOutput(_start))
                throw new InvalidOperationException("Turn START OFF before fastening.");
            _bus.Open(_portName, _baudRate);
            if (_requestedPreset is not { } preset)
                throw new InvalidOperationException("Select an I/O fastening preset before starting.");
            for (var index = 0; index < _presets.Length; index++)
            {
                if (_io.GetOutput(_presets[index]) != (index == preset - 1))
                    throw new InvalidOperationException($"Preset outputs no longer match preset {preset}.");
            }
            var current = dryRunMilliseconds > 0
                ? null : await _bus.ReadFasteningResultAsync(_slaveAddress, cancellationToken);
            lastResult = current;
            // One pre-START baseline excludes previously received bolt results.
            var fastening = (EventCount: current?.EventCount ?? (ushort)0, Preset: preset);
            _io.SetOutput(_direction, false);
            LastStatus = null;

            if (dryRunMilliseconds == 0)
                timeout.CancelAfter(_connection.FasteningTimeoutMilliseconds);
            timeout.Token.ThrowIfCancellationRequested();
            started = fastening;
            // Own STOP cleanup before requesting START or lowering the head.
            _io.SetOutput(_start, true);
            if (feedAsync is not null && ioFailure is null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await feedAsync(timeout.Token);
            }
            if (dryRunMilliseconds > 0)
                await Task.Delay(dryRunMilliseconds, timeout.Token);
            waitingForResult = dryRunMilliseconds == 0;
            while (dryRunMilliseconds == 0)
            {
                try
                {
                    // Keep one request in flight; wait this interval before the next query.
                    await Task.Delay(_connection.ResultPollingIntervalMilliseconds, timeout.Token);
                    var result = await _bus.ReadFasteningResultAsync(_slaveAddress, timeout.Token);
                    lastResult = result;
                    if (IsCompleted(result, fastening))
                    {
                        completed = result;
                        break;
                    }
                }
                catch (AdcResponseException exception)
                {
                    failure = exception;
                    break;
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            failure = exception;
            throw;
        }
        catch (OperationCanceledException) when (ioFailure is not null)
        {
            failure = ioFailure;
            throw failure;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            failure = new TimeoutException(
                $"ADC {_portName}/{_slaveAddress} fastening timed out after {_connection.FasteningTimeoutMilliseconds} ms; "
                + $"waiting for a new fastening result (query interval={_connection.ResultPollingIntervalMilliseconds} ms); "
                + $"start event={started?.EventCount}, expected preset={started?.Preset}, "
                + $"last event={lastResult?.EventCount}, status={lastResult?.Status}, preset={lastResult?.Preset}, "
                + $"direction={lastResult?.Direction}, error={lastResult?.Error}.");
            if (!waitingForResult)
                throw failure;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _io.Faulted -= OnIoFaulted;
            await StopAfterOperationAsync(failure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (ioFailure is not null)
            throw ioFailure;
        if (completed is null && LastStatus is { Alarm: > 0 } status)
            failure ??= new InvalidOperationException(AdcControllerError.Describe(status.Alarm));
        if (failure is not null)
        {
            // START is OFF. No torque was received for this bolt.
            return new BoltResult(false, null,
                Error: $"ADC {_portName}/{_slaveAddress}: {failure.Message}");
        }

        if (dryRunMilliseconds > 0)
            return new BoltResult(true, null, BoltResultSource.DryRun);

        if (completed is not null)
        {
            var error = completed.Status == AdcEventStatus.Error || completed.Error != 0
                ? $"ADC {_portName}/{_slaveAddress} controller error: "
                    + (completed.Error == 0 ? "Error 이벤트 수신; 상세 오류 코드 없음."
                        : AdcControllerError.Describe(completed.Error))
                    + $" event={completed.EventCount}, status={completed.Status}."
                : null;
            return new BoltResult(completed.Status == AdcEventStatus.FasteningOk && error is null, completed.Torque,
                Error: error)
            {
                RecordedAt = DateTimeOffset.Now,
                Controller = new(_portName, _slaveAddress, completed.EventCount,
                    completed.FasteningTimeMilliseconds, completed.Preset, completed.TargetTorque,
                    completed.TargetSpeedRpm, completed.Angle1, completed.Angle2, completed.Angle3,
                    completed.ScrewCount, completed.Error, (ushort)completed.Direction, (ushort)completed.Status,
                    completed.SnugAngle, completed.Registers),
            };
        }

        throw new InvalidOperationException("ADC fastening ended without a result.");
    }

    public async Task StopAsync()
    {
        // START is held while running; OFF stops the controller, including on timeout.
        _io.SetOutput(_start, false);
        _logger.LogInformation("ADC {Port}/{Slave}: I/O START OFF.", _portName, _slaveAddress);
        await ReadStatusAsync(CancellationToken.None);
    }

    private async Task StopAfterOperationAsync(Exception? failure)
    {
        try
        {
            await StopAsync();
        }
        catch (Exception stopError) when (failure is not null)
        {
            throw new AggregateException("ADC operation and STOP cleanup both failed.", failure, stopError);
        }
    }

    private bool IsCompleted(AdcFasteningResult result, (ushort EventCount, ushort Preset) pending)
    {
        switch (true)
        {
            case true when result.EventCount == pending.EventCount:
                return false;
            case true when result.Status == AdcEventStatus.Error:
                return true;
            case true when result.Status is not (AdcEventStatus.FasteningOk or AdcEventStatus.FasteningNg):
                return false;
        }
        if (result.Preset != pending.Preset
            || result.Direction != AdcDirection.Fastening)
            throw new InvalidOperationException(
                $"ADC {_slaveAddress} result event {result.EventCount} does not match this fastening: "
                + $"preset {result.Preset}, direction {result.Direction}; expected preset {pending.Preset}, Fastening.");
        return true;
    }
}
