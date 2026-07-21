using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Geometry;
using IBTM.Core.Machine;
using IBTM.Core.Process;
using IBTM.Device;
using IBTM.Transport;

namespace IBTM.Stations.PcbPlacement;

public sealed class PcbPlacementStation : IDisposable
{
    public const int CarrierJigPresentInputChannel = 10;
    public const int StopperUpOutputChannel = 11;
    public const int BackupPlateUpOutputChannel = 13;
    public const int GripperChannel = 14;
    public const int LaserChannel = 15;
    public const int PcbAvailableInputChannel = 16;

    private readonly IMotionService _motion;
    private readonly IIoService _io;
    private readonly StationMotionSettings _motionSettings;
    private readonly ProcessEvents _events;

    public PcbPlacementStation(
        IMotionService motion,
        IIoService io,
        StationMotionSettings motionSettings,
        ProcessEvents events)
    {
        _motion = motion;
        _io = io;
        _motionSettings = motionSettings;
        _events = events;
        CarrierJigPositioner = new CarrierJigPositioner(
            io,
            CarrierJigPresentInputChannel,
            StopperUpOutputChannel,
            BackupPlateUpOutputChannel);
        _motion.PositionChanged += OnPositionChanged;
    }

    public CarrierJigPositioner CarrierJigPositioner { get; }

    public void Initialize()
    {
        _motion.Initialize();
        CarrierJigPositioner.Initialize();
    }

    public async Task ProcessAsync(
        PcbPlacementRecipe recipe,
        CancellationToken cancellationToken)
    {
        await _events.RunStageAsync(
            PcbPlacementStages.PositionCarrierJig,
            cancellationToken,
            async token =>
            {
                await CarrierJigPositioner.PositionAsync(token);
                await _io.WaitForInputAsync(PcbAvailableInputChannel, true, token);
            });

        await _events.RunStageAsync(
            PcbPlacementStages.PickPlace,
            cancellationToken,
            async token =>
            {
                await PickAndPlaceAsync(recipe.PcbPick1, recipe.PcbPlace1, token);
                await PickAndPlaceAsync(recipe.PcbPick2, recipe.PcbPlace2, token);
            });
    }

    public void Stop() => _motion.Stop();

    public void EmergencyStop() => _motion.EmergencyStop();

    public void Dispose() => _motion.PositionChanged -= OnPositionChanged;

    private async Task PickAndPlaceAsync(
        AxisPos pickPosition,
        AxisPos placePosition,
        CancellationToken cancellationToken)
    {
        await MoveToPositionAsync(pickPosition, cancellationToken);
        _io.SetOutput(GripperChannel, true);
        await _io.WaitForInputAsync(GripperChannel, true, cancellationToken);

        await MoveToZAsync(0, cancellationToken);
        await _motion.MoveToXYAsync(
            placePosition.X,
            placePosition.Y,
            _motionSettings.SpeedXY,
            cancellationToken);
        await MoveToZAsync(placePosition.Z, cancellationToken);

        _io.SetOutput(GripperChannel, false);
        await _io.WaitForInputAsync(GripperChannel, false, cancellationToken);
        await MoveToZAsync(0, cancellationToken);
    }

    private async Task MoveToPositionAsync(
        AxisPos position,
        CancellationToken cancellationToken)
    {
        await _motion.MoveToXYAsync(
            position.X,
            position.Y,
            _motionSettings.SpeedXY,
            cancellationToken);
        await MoveToZAsync(position.Z, cancellationToken);
    }

    private Task MoveToZAsync(double position, CancellationToken cancellationToken) =>
        _motion.MoveToZAsync(
            position,
            _motionSettings.SpeedZ,
            cancellationToken);

    private void OnPositionChanged(double x, double y, double z) =>
        _events.Position(1, x, y, z);
}
