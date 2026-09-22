using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IBTM.Hantas;

public sealed class AdcBoltHead : IBoltHead
{
    private readonly IAdcBus _bus;
    private readonly IIoService _io;
    private readonly InputIo _ready;
    private readonly InputIo _alarm;
    private readonly OutputIo _start;
    private readonly OutputIo _direction;
    private readonly OutputIo _reset;
    private readonly OutputIo[] _presets;
    private const int ResetPulseMilliseconds = 100;
    private readonly HantasSettings _connection;
    private readonly byte _slaveAddress;
    private readonly ILogger<AdcBoltHead> _logger;
    private const int StatusPollMilliseconds = 50;
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
        (_ready, _alarm, _start, _direction, _reset) = head switch
        {
            FasteningHead.Pickup => (InputIo.PickupBoltReady, InputIo.PickupBoltAlarm,
                OutputIo.PickupBoltStart, OutputIo.PickupBoltDirection, OutputIo.PickupBoltReset),
            FasteningHead.Shooting => (InputIo.ShootingBoltReady, InputIo.ShootingBoltAlarm,
                OutputIo.ShootingBoltStart, OutputIo.ShootingBoltDirection, OutputIo.ShootingBoltReset),
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

    public Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _bus.Open(_portName, _baudRate);
        _io.CheckReady();
        if (_io.GetInput(_alarm) || !_io.GetInput(_ready)
            || _io.GetOutput(_start))
            throw new InvalidOperationException(
                $"ADC {_portName}/{_slaveAddress} I/O not ready: "
                + $"ALARM={_io.GetInput(_alarm)}, READY={_io.GetInput(_ready)}, "
                + $"START={_io.GetOutput(_start)}.");
        return Task.CompletedTask;
    }

    public async Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (preset is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(preset), "IO bolt presets are 1, 2 and 3.");
        if (_io.GetInput(_alarm))
        {
            _logger.LogWarning("ADC {Port}/{Slave} ALARM input ON; resetting once before the next bolt.",
                _portName, _slaveAddress);
            await ResetAsync(cancellationToken);
        }
        await CheckReadyAsync(cancellationToken);
        _requestedPreset = null;
        foreach (var output in _presets)
            _io.SetOutput(output, false);
        _io.SetOutput(_presets[preset - 1], true);
        _requestedPreset = preset;
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await StopAsync();
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connection.ResponseTimeoutMilliseconds);
        try
        {
            while (_io.GetInput(_alarm) || !_io.GetInput(_ready))
            {
                _io.CheckReady();
                await Task.Delay(StatusPollMilliseconds, timeout.Token);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"ADC {_portName}/{_slaveAddress} I/O RESET 후 준비 상태가 확인되지 않았습니다 "
                + $"({_connection.ResponseTimeoutMilliseconds} ms): "
                + $"ALARM={_io.GetInput(_alarm)}, READY={_io.GetInput(_ready)}.",
                exception);
        }
        _logger.LogInformation("ADC {Port}/{Slave} I/O RESET confirmed: ALARM OFF, READY ON.",
            _portName, _slaveAddress);
    }

    // Manual hold-to-run only. Cancellation stops rotation; it is not a loose-complete result.
    public async Task RunReverseAsync(CancellationToken cancellationToken)
    {
        await CheckReadyAsync(cancellationToken);
        Exception? failure = null;
        try
        {
            _io.SetOutput(_direction, true);
            _io.SetOutput(_start, true);
            while (true)
            {
                if (_io.GetInput(_alarm))
                    throw new InvalidOperationException($"{_alarm.GetDescription()}=ON during loosening.");
                _io.CheckReady();
                await Task.Delay(StatusPollMilliseconds, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
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
        void OnInputChanged(InputIo input, bool value)
        {
            if (input == _alarm && value)
            {
                Interlocked.CompareExchange(ref ioFailure,
                    new InvalidOperationException($"{_alarm.GetDescription()}=ON during fastening."), null);
                try
                {
                    _io.SetOutput(_start, false);
                }
                catch (Exception exception)
                {
                    Interlocked.Exchange(ref ioFailure, exception);
                    timeout.Cancel();
                    return;
                }
                // Allow the controller's error result to arrive after its ALARM input.
                timeout.CancelAfter(_connection.ResponseTimeoutMilliseconds);
            }
        }
        void OnIoFaulted(Exception exception)
        {
            Interlocked.CompareExchange(ref ioFailure, exception, null);
            timeout.Cancel();
        }
        _io.InputChanged += OnInputChanged;
        _io.Faulted += OnIoFaulted;
        try
        {
            await CheckReadyAsync(cancellationToken);
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
            await CheckReadyAsync(cancellationToken);

            if (dryRunMilliseconds == 0)
                timeout.CancelAfter(_connection.FasteningTimeoutMilliseconds);
            timeout.Token.ThrowIfCancellationRequested();
            started = fastening;
            // Own STOP cleanup before requesting START or lowering the head.
            _io.SetOutput(_start, true);
            if (feedAsync is not null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await feedAsync(timeout.Token);
            }
            if (dryRunMilliseconds > 0)
                await Task.Delay(dryRunMilliseconds, cancellationToken);
            waitingForResult = dryRunMilliseconds == 0;
            while (dryRunMilliseconds == 0)
            {
                try
                {
                    var result = await _bus.ReceiveFasteningResultAsync(_slaveAddress, timeout.Token);
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
            if (!waitingForResult)
                throw failure;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            failure = new TimeoutException(
                $"ADC {_portName}/{_slaveAddress} fastening timed out after {_connection.FasteningTimeoutMilliseconds} ms; "
                + "waiting for Auto Data Output (check controller output enable/port); "
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
            _io.InputChanged -= OnInputChanged;
            _io.Faulted -= OnIoFaulted;
            await StopAfterOperationAsync(failure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        failure ??= completed is null ? ioFailure : null;
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

    public Task StopAsync()
    {
        // START is held while running; OFF stops the controller, including on timeout.
        _io.SetOutput(_start, false);
        _logger.LogInformation("ADC {Port}/{Slave}: I/O START OFF.", _portName, _slaveAddress);
        return Task.CompletedTask;
    }

    private async Task StopAfterOperationAsync(Exception? failure)
    {
        try
        {
            await StopAsync();
        }
        catch (Exception stopError) when (failure is not null)
        {
            throw new AggregateException("ADC operation and I/O STOP both failed.", failure, stopError);
        }
    }

    private bool IsCompleted(AdcFasteningResult result, (ushort EventCount, ushort Preset) pending)
    {
        switch (true)
        {
            case true when unchecked((ushort)(result.EventCount - pending.EventCount)) is 0 or >= 32768:
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
