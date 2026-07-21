using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Geometry;
using IBTM.Core.Machine;
using IBTM.Core.Process;
using IBTM.Device;
using IBTM.Transport;

namespace IBTM.Stations.BoltFastening;

public sealed class BoltFasteningStation : IDisposable
{
    public const int CarrierJigPresentInputChannel = 20;
    public const int StopperUpOutputChannel = 21;
    public const int BackupPlateUpOutputChannel = 23;
    public const int LaserChannel = 25;

    private readonly IMotionService _motion;
    private readonly StationMotionSettings _motionSettings;
    private readonly ProcessEvents _events;
    private readonly ICameraStreamService _camera;
    private readonly IBoltService _boltController;
    private readonly BoltFasteningOptions _options;

    public BoltFasteningStation(
        IMotionService motion,
        IIoService io,
        BoltFasteningOptions options,
        ProcessEvents events,
        ICameraStreamService camera,
        IBoltService boltController)
    {
        _motion = motion;
        _motionSettings = options.Motion;
        _events = events;
        _camera = camera;
        _boltController = boltController;
        _options = options;
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

    public Task PrepareAsync(CancellationToken cancellationToken) =>
        _boltController.InitializeAsync(cancellationToken);

    public async Task ProcessAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken)
    {
        await _events.RunStageAsync(
            BoltFasteningStages.PositionCarrierJig,
            cancellationToken,
            CarrierJigPositioner.PositionAsync);

        var fiducial = await _events.RunStageAsync(
            BoltFasteningStages.Fiducial,
            cancellationToken,
            token => DetectFiducialAsync(recipe, token));

        await _events.RunStageAsync(
            BoltFasteningStages.Tighten,
            cancellationToken,
            token => TightenBoltsAsync(recipe, fiducial, token));
    }

    public void Stop() => _motion.Stop();

    public void EmergencyStop() => _motion.EmergencyStop();

    public void Dispose() => _motion.PositionChanged -= OnPositionChanged;

    private async Task<FiducialResult> DetectFiducialAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken)
    {
        await MoveToPositionAsync(recipe.FiducialPosition, cancellationToken);

        var result = DetectFiducial(_camera.Capture());
        _events.Fiducial(result);

        if (!result.Found)
        {
            throw new InvalidOperationException("Fiducial detection failed.");
        }

        return result;
    }

    private FiducialResult DetectFiducial(ImageFrame image)
    {
        long xTotal = 0;
        long yTotal = 0;
        var count = 0;

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var index = (y * image.Stride) + (x * 3);
                var blue = image.Pixels[index];
                var green = image.Pixels[index + 1];
                var red = image.Pixels[index + 2];
                if (green <= blue + 50 || green <= red + 50)
                {
                    continue;
                }

                xTotal += x;
                yTotal += y;
                count++;
            }
        }

        if (count == 0)
        {
            return new FiducialResult(false, 0, 0);
        }

        return new FiducialResult(
            true,
            ((xTotal / (double)count) - (image.Width / 2.0)) / _options.PixelsPerMm,
            ((yTotal / (double)count) - (image.Height / 2.0)) / _options.PixelsPerMm);
    }

    private async Task TightenBoltsAsync(
        BoltFasteningRecipe recipe,
        FiducialResult fiducial,
        CancellationToken cancellationToken)
    {
        var pcbCentres = new[]
        {
            (X: recipe.Pcb1CenterX, Y: recipe.PcbCenterY, Label: "PCB1"),
            (X: recipe.Pcb2CenterX, Y: recipe.PcbCenterY, Label: "PCB2"),
        };
        var totalBolts = recipe.BoltPoints.Count * pcbCentres.Length;
        var boltIndex = 0;

        foreach (var pcb in pcbCentres)
        {
            foreach (var boltPoint in recipe.BoltPoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                boltIndex++;
                var boltLabel = $"{pcb.Label}-{boltPoint.Name}";
                var position = new AxisPos
                {
                    X = pcb.X + boltPoint.X + fiducial.OffsetX,
                    Y = pcb.Y + boltPoint.Y + fiducial.OffsetY,
                    Z = boltPoint.Z,
                };

                _events.BoltProgressed(boltIndex, totalBolts, boltLabel);
                await MoveToPositionAsync(position, cancellationToken);

                await _boltController.ShootAsync(cancellationToken);
                _events.Bolt(await TightenAsync(boltPoint.TargetTorqueNm, cancellationToken));
                await MoveToZAsync(0, cancellationToken);
            }
        }
    }

    private async Task<BoltResult> TightenAsync(
        double targetTorque,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await _boltController.TightenAsync(targetTorque, cancellationToken);
            if (result.Success || attempt >= _options.RetryCount)
            {
                return result;
            }
        }
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
        _events.Position(2, x, y, z);
}
