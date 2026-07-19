using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Application.Process;

/// <summary>
/// Owns the mechanical sequences shared by the process flow.
/// Process decisions stay in <see cref="ProcessOrchestrator"/>.
/// </summary>
public sealed class MachineOperations : IDisposable
{
    private static readonly TimeSpan SimulatedSensorDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SensorPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ConveyorTransferDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan OutputTransitionDelay = TimeSpan.FromMilliseconds(200);

    private readonly IMotionService[] _motions;
    private readonly IIOService _io;
    private readonly MachineConfig _config;

    public MachineOperations(
        [FromKeyedServices(ZoneServiceKeys.Zone1)] IMotionService zone1Motion,
        [FromKeyedServices(ZoneServiceKeys.Zone2)] IMotionService zone2Motion,
        [FromKeyedServices(ZoneServiceKeys.Zone3)] IMotionService zone3Motion,
        IIOService io,
        MachineConfig config)
    {
        _motions = [zone1Motion, zone2Motion, zone3Motion];
        _io = io;
        _config = config;

        zone1Motion.PositionChanged += OnZone1PositionChanged;
        zone2Motion.PositionChanged += OnZone2PositionChanged;
        zone3Motion.PositionChanged += OnZone3PositionChanged;
    }

    public event EventHandler<ZonePositionEventArgs>? PositionChanged;
    public event EventHandler<(int Zone, bool Active)>? GripperChanged;

    public void Initialize()
    {
        _motions[0].InitializeAxes(0, 1, 2);
        _motions[1].InitializeAxes(3, 4, 5);
        _motions[2].InitializeAxes(6, 7, 8);

        foreach (var motion in _motions)
        {
            motion.Enable();
        }

        _io.Initialize();
    }

    public async Task MoveToPositionAsync(
        int zone,
        AxisPos position,
        CancellationToken cancellationToken)
    {
        var motion = Motion(zone);
        var parameters = _config.GetMotionParams(zone);
        await motion.MoveToXYAsync(
            position.X,
            position.Y,
            parameters.SpeedXY,
            cancellationToken);
        await motion.MoveToZAsync(position.Z, parameters.SpeedZ, cancellationToken);
    }

    public Task MoveToZAsync(int zone, double position, CancellationToken cancellationToken)
    {
        var parameters = _config.GetMotionParams(zone);
        return Motion(zone).MoveToZAsync(position, parameters.SpeedZ, cancellationToken);
    }

    public async Task PickAndPlaceAsync(
        int zone,
        AxisPos pickPosition,
        AxisPos placePosition,
        int gripperChannel,
        CancellationToken cancellationToken)
    {
        var motion = Motion(zone);
        var parameters = _config.GetMotionParams(zone);

        await MoveToPositionAsync(zone, pickPosition, cancellationToken);
        SetGripper(gripperChannel, zone, active: true);
        await Task.Delay(OutputTransitionDelay, cancellationToken);

        await motion.MoveToZAsync(0, parameters.SpeedZ, cancellationToken);
        await motion.MoveToXYAsync(
            placePosition.X,
            placePosition.Y,
            parameters.SpeedXY,
            cancellationToken);
        await motion.MoveToZAsync(placePosition.Z, parameters.SpeedZ, cancellationToken);

        SetGripper(gripperChannel, zone, active: false);
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        await motion.MoveToZAsync(0, parameters.SpeedZ, cancellationToken);
    }

    public async Task WaitForSensorAsync(
        int sensorChannel,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        if (_config.SimulationMode)
        {
            await Task.Delay(SimulatedSensorDelay, cancellationToken);
            return;
        }

        using var timeoutCancellation = timeout.HasValue
            ? new CancellationTokenSource(timeout.Value)
            : null;
        using var linkedCancellation = timeoutCancellation is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
        var token = linkedCancellation?.Token ?? cancellationToken;

        try
        {
            while (!_io.GetInput(sensorChannel))
            {
                await Task.Delay(SensorPollInterval, token);
            }
        }
        catch (OperationCanceledException) when (
            timeoutCancellation?.IsCancellationRequested == true
            && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for input channel {sensorChannel}.");
        }
    }

    public async Task StopAlignLiftAsync(
        int stopperChannel,
        int alignChannel,
        int liftChannel,
        CancellationToken cancellationToken)
    {
        _io.SetOutput(stopperChannel, true);
        await Task.Delay(OutputTransitionDelay, cancellationToken);
        _io.SetOutput(alignChannel, true);
        await Task.Delay(TimeSpan.FromMilliseconds(_config.AlignSettleDelayMs), cancellationToken);
        _io.SetOutput(liftChannel, true);
        await Task.Delay(TimeSpan.FromMilliseconds(_config.LiftSettleDelayMs), cancellationToken);
    }

    public async Task ReleaseAsync(
        int stopperChannel,
        int alignChannel,
        int liftChannel,
        CancellationToken cancellationToken)
    {
        _io.SetOutput(liftChannel, false);
        await Task.Delay(TimeSpan.FromMilliseconds(_config.LiftSettleDelayMs), cancellationToken);
        _io.SetOutput(alignChannel, false);
        await Task.Delay(OutputTransitionDelay, cancellationToken);
        _io.SetOutput(stopperChannel, false);
        await Task.Delay(OutputTransitionDelay, cancellationToken);
    }

    public async Task TransferConveyorAsync(CancellationToken cancellationToken)
    {
        _io.SetOutput(IoMap.Conveyor, true);
        try
        {
            await Task.Delay(ConveyorTransferDelay, cancellationToken);
        }
        finally
        {
            _io.SetOutput(IoMap.Conveyor, false);
        }

        await Task.Delay(OutputTransitionDelay, cancellationToken);
    }

    public async Task PulseOutputAsync(
        int channel,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        _io.SetOutput(channel, true);
        try
        {
            await Task.Delay(duration, cancellationToken);
        }
        finally
        {
            _io.SetOutput(channel, false);
        }
    }

    public void Stop()
    {
        foreach (var motion in _motions)
        {
            motion.Stop();
        }

        _io.TurnOffAll();
    }

    public void EmergencyStop()
    {
        foreach (var motion in _motions)
        {
            motion.EmergencyStop();
        }

        _io.TurnOffAll();
    }

    public void Dispose()
    {
        _motions[0].PositionChanged -= OnZone1PositionChanged;
        _motions[1].PositionChanged -= OnZone2PositionChanged;
        _motions[2].PositionChanged -= OnZone3PositionChanged;
    }

    private IMotionService Motion(int zone) => _motions[zone - 1];

    private void SetGripper(int channel, int zone, bool active)
    {
        _io.SetOutput(channel, active);
        GripperChanged?.Invoke(this, (zone, active));
    }

    private void OnZone1PositionChanged(object? sender, MotionPositionEventArgs position) =>
        RaisePositionChanged(1, position);

    private void OnZone2PositionChanged(object? sender, MotionPositionEventArgs position) =>
        RaisePositionChanged(2, position);

    private void OnZone3PositionChanged(object? sender, MotionPositionEventArgs position) =>
        RaisePositionChanged(3, position);

    private void RaisePositionChanged(int zone, MotionPositionEventArgs position) =>
        PositionChanged?.Invoke(
            this,
            new ZonePositionEventArgs(zone, position.X, position.Y, position.Z));
}
