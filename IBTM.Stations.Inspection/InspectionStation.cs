using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Geometry;
using IBTM.Core.Machine;
using IBTM.Core.Process;
using IBTM.Device;
using IBTM.Transport;

namespace IBTM.Stations.Inspection;

public sealed class InspectionStation : IDisposable
{
    private const int MinimumFeaturePixels = 400;

    public const int CarrierJigPresentInputChannel = 30;
    public const int StopperUpOutputChannel = 31;
    public const int BackupPlateUpOutputChannel = 33;
    public const int GripperChannel = 34;
    public const int LaserChannel = 35;

    private readonly IMotionService _motion;
    private readonly IIoService _io;
    private readonly StationMotionSettings _motionSettings;
    private readonly ProcessEvents _events;
    private readonly ICameraStreamService _camera;
    private readonly InspectionOptions _options;

    public InspectionStation(
        IMotionService motion,
        IIoService io,
        InspectionOptions options,
        ProcessEvents events,
        ICameraStreamService camera)
    {
        _motion = motion;
        _io = io;
        _motionSettings = options.Motion;
        _events = events;
        _camera = camera;
        _options = options;
        CarrierJigPositioner = new CarrierJigPositioner(
            io,
            CarrierJigPresentInputChannel,
            StopperUpOutputChannel,
            BackupPlateUpOutputChannel);
        _motion.PositionChanged += OnPositionChanged;
    }

    public int NgStackCount { get; private set; }
    public int NgStackCapacity => _options.NgStackMaxCount;
    public CarrierJigPositioner CarrierJigPositioner { get; }

    public void Initialize()
    {
        _motion.Initialize();
        CarrierJigPositioner.Initialize();
    }

    public async Task<CarrierInspectionResult> ProcessAsync(
        InspectionRecipe recipe,
        CancellationToken cancellationToken)
    {
        await _events.RunStageAsync(
            InspectionStages.PositionCarrierJig,
            cancellationToken,
            CarrierJigPositioner.PositionAsync);

        var result = await _events.RunStageAsync(
            InspectionStages.Inspect,
            cancellationToken,
            token => InspectAsync(recipe, token));

        if (result.Result == InspectionResult.Ng)
        {
            await StackNgCarrierJigAsync(recipe, cancellationToken);
        }

        return result;
    }

    public void ResetNgStack()
    {
        NgStackCount = 0;
        _events.NgStack(0, alarm: false);
    }

    public void Stop() => _motion.Stop();

    public void EmergencyStop() => _motion.EmergencyStop();

    public void Dispose() => _motion.PositionChanged -= OnPositionChanged;

    private async Task<CarrierInspectionResult> InspectAsync(
        InspectionRecipe recipe,
        CancellationToken cancellationToken)
    {
        var pcb1 = await InspectPcbAsync(
            recipe.Pcb1InspectionPosition,
            cancellationToken);
        var pcb2 = await InspectPcbAsync(
            recipe.Pcb2InspectionPosition,
            cancellationToken);
        var result = new CarrierInspectionResult(pcb1, pcb2);
        _events.Inspection(result);
        return result;
    }

    private async Task<InspectionOutcome> InspectPcbAsync(
        AxisPos position,
        CancellationToken cancellationToken)
    {
        await MoveToPositionAsync(position, cancellationToken);

        var image = _camera.Capture();
        var featurePixels = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var index = (y * image.Stride) + (x * 3);
                if (image.Pixels[index] >= 100
                    && image.Pixels[index + 1] >= 100
                    && image.Pixels[index + 2] >= 100)
                {
                    featurePixels++;
                }
            }
        }

        var result = featurePixels >= MinimumFeaturePixels
            ? InspectionResult.Good
            : InspectionResult.Ng;
        return new InspectionOutcome(result, image);
    }

    private Task StackNgCarrierJigAsync(
        InspectionRecipe recipe,
        CancellationToken cancellationToken) =>
        _events.RunStageAsync(
            InspectionStages.StackNgCarrierJig,
            cancellationToken,
            async token =>
            {
                await PickAndPlaceAsync(
                    recipe.NgCarrierPickupPosition,
                    recipe.NgStackPosition,
                    token);
                await CarrierJigPositioner.CompleteRemovalAsync(token);

                NgStackCount++;
                _events.NgStack(NgStackCount, NgStackCount >= NgStackCapacity);
            });

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
        _events.Position(3, x, y, z);
}
