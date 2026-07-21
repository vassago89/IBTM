namespace IBTM.Core.Machine;

public sealed class StationOperations : IDisposable
{
    private static readonly TimeSpan SimulatedSensorDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan OutputTransitionDelay = TimeSpan.FromMilliseconds(200);

    private readonly int _stationNumber;
    private readonly IMotionService _motion;
    private readonly IIOService _io;
    private readonly ZoneMotionParams _motionParams;
    private readonly MachineRuntimeSettings _runtime;
    private readonly ProcessEventHub _events;

    public StationOperations(
        int stationNumber,
        IMotionService motion,
        IIOService io,
        ZoneMotionParams motionParams,
        MachineRuntimeSettings runtime,
        ProcessEventHub events)
    {
        _stationNumber = stationNumber;
        _motion = motion;
        _io = io;
        _motionParams = motionParams;
        _runtime = runtime;
        _events = events;
        _motion.PositionChanged += OnPositionChanged;
    }

    public void Initialize(int xAxis, int yAxis, int zAxis)
    {
        _motion.InitializeAxes(xAxis, yAxis, zAxis);
        _motion.Enable();
    }

    public async Task MoveToPositionAsync(
        AxisPos position,
        CancellationToken cancellationToken)
    {
        await _motion.MoveToXYAsync(
            position.X,
            position.Y,
            _motionParams.SpeedXY,
            cancellationToken);
        await _motion.MoveToZAsync(position.Z, _motionParams.SpeedZ, cancellationToken);
    }

    public Task MoveToZAsync(double position, CancellationToken cancellationToken) =>
        _motion.MoveToZAsync(position, _motionParams.SpeedZ, cancellationToken);

    public async Task PickAndPlaceAsync(
        AxisPos pickPosition,
        AxisPos placePosition,
        int gripperChannel,
        CancellationToken cancellationToken)
    {
        await MoveToPositionAsync(pickPosition, cancellationToken);
        SetGripper(gripperChannel, active: true);
        await Task.Delay(OutputTransitionDelay, cancellationToken);

        await _motion.MoveToZAsync(0, _motionParams.SpeedZ, cancellationToken);
        await _motion.MoveToXYAsync(
            placePosition.X,
            placePosition.Y,
            _motionParams.SpeedXY,
            cancellationToken);
        await _motion.MoveToZAsync(placePosition.Z, _motionParams.SpeedZ, cancellationToken);

        SetGripper(gripperChannel, active: false);
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        await _motion.MoveToZAsync(0, _motionParams.SpeedZ, cancellationToken);
    }

    public Task WaitForSignalAsync(CancellationToken cancellationToken) =>
        Task.Delay(SimulatedSensorDelay, cancellationToken);

    public async Task StopAlignLiftAsync(
        int stopperChannel,
        int alignChannel,
        int liftChannel,
        CancellationToken cancellationToken)
    {
        _io.SetOutput(stopperChannel, true);
        await Task.Delay(OutputTransitionDelay, cancellationToken);
        _io.SetOutput(alignChannel, true);
        await Task.Delay(TimeSpan.FromMilliseconds(_runtime.AlignSettleDelayMs), cancellationToken);
        _io.SetOutput(liftChannel, true);
        await Task.Delay(TimeSpan.FromMilliseconds(_runtime.LiftSettleDelayMs), cancellationToken);
    }

    public async Task ReleaseAsync(
        int stopperChannel,
        int alignChannel,
        int liftChannel,
        CancellationToken cancellationToken)
    {
        _io.SetOutput(liftChannel, false);
        await Task.Delay(TimeSpan.FromMilliseconds(_runtime.LiftSettleDelayMs), cancellationToken);
        _io.SetOutput(alignChannel, false);
        await Task.Delay(OutputTransitionDelay, cancellationToken);
        _io.SetOutput(stopperChannel, false);
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

    public void Stop() => _motion.Stop();

    public void EmergencyStop() => _motion.EmergencyStop();

    public void Dispose() => _motion.PositionChanged -= OnPositionChanged;

    private void SetGripper(int channel, bool active)
    {
        _io.SetOutput(channel, active);
        _events.Gripper((_stationNumber, active));
    }

    private void OnPositionChanged(object? sender, MotionPositionEventArgs position) =>
        _events.Position(new ZonePositionEventArgs(
            _stationNumber,
            position.X,
            position.Y,
            position.Z));
}
