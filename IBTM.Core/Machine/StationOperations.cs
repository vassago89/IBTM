using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Geometry;
using IBTM.Core.Process;
using IBTM.Device;

namespace IBTM.Core.Machine;

public sealed class StationOperations : IDisposable
{
    private readonly int _stationNumber;
    private readonly IMotionService _motion;
    private readonly IIoService _io;
    private readonly ZoneMotionParams _motionParams;
    private readonly ProcessEvents _events;

    public StationOperations(
        int stationNumber,
        IMotionService motion,
        IIoService io,
        ZoneMotionParams motionParams,
        ProcessEvents events)
    {
        _stationNumber = stationNumber;
        _motion = motion;
        _io = io;
        _motionParams = motionParams;
        _events = events;
        _motion.PositionChanged += OnPositionChanged;
    }

    public void Initialize() => _motion.Initialize();

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
        _io.SetOutput(gripperChannel, true);
        await _io.WaitForInputAsync(gripperChannel, true, cancellationToken);

        await _motion.MoveToZAsync(0, _motionParams.SpeedZ, cancellationToken);
        await _motion.MoveToXYAsync(
            placePosition.X,
            placePosition.Y,
            _motionParams.SpeedXY,
            cancellationToken);
        await _motion.MoveToZAsync(placePosition.Z, _motionParams.SpeedZ, cancellationToken);

        _io.SetOutput(gripperChannel, false);
        await _io.WaitForInputAsync(gripperChannel, false, cancellationToken);
        await _motion.MoveToZAsync(0, _motionParams.SpeedZ, cancellationToken);
    }

    public Task WaitForInputAsync(int channel, CancellationToken cancellationToken) =>
        _io.WaitForInputAsync(channel, true, cancellationToken);

    public async Task StopAlignLiftAsync(
        int stopperChannel,
        int alignChannel,
        int liftChannel,
        CancellationToken cancellationToken)
    {
        _io.SetOutput(stopperChannel, true);
        await _io.WaitForInputAsync(stopperChannel, true, cancellationToken);
        _io.SetOutput(alignChannel, true);
        await _io.WaitForInputAsync(alignChannel, true, cancellationToken);
        _io.SetOutput(liftChannel, true);
        await _io.WaitForInputAsync(liftChannel, true, cancellationToken);
    }

    public async Task ReleaseAsync(
        int stopperChannel,
        int alignChannel,
        int liftChannel,
        CancellationToken cancellationToken)
    {
        _io.SetOutput(liftChannel, false);
        await _io.WaitForInputAsync(liftChannel, false, cancellationToken);
        _io.SetOutput(alignChannel, false);
        await _io.WaitForInputAsync(alignChannel, false, cancellationToken);
        _io.SetOutput(stopperChannel, false);
        await _io.WaitForInputAsync(stopperChannel, false, cancellationToken);
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

    private void OnPositionChanged(double x, double y, double z) =>
        _events.Position(_stationNumber, x, y, z);
}
