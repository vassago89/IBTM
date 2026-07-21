using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Geometry;
using IBTM.Core.Machine;
using IBTM.Core.Process;
using IBTM.Device;

namespace IBTM.Stations.BoltFastening;

public sealed class BoltFasteningStation : IDisposable
{
    public const int ShuttlePresentInputChannel = 20;
    public const int StopperChannel = 21;
    public const int AlignChannel = 22;
    public const int LiftChannel = 23;
    public const int LaserChannel = 25;

    private readonly StationOperations _machine;
    private readonly ProcessEvents _events;
    private readonly IFiducialService _fiducial;
    private readonly IBoltService _boltController;
    private readonly BoltFasteningOptions _options;

    public BoltFasteningStation(
        IMotionService motion,
        IIoService io,
        BoltFasteningOptions options,
        ProcessEvents events,
        IFiducialService fiducial,
        IBoltService boltController)
    {
        _machine = new StationOperations(2, motion, io, options.Motion, events);
        _events = events;
        _fiducial = fiducial;
        _boltController = boltController;
        _options = options;
    }

    public void Initialize() => _machine.Initialize();

    public Task PrepareAsync(CancellationToken cancellationToken) =>
        _boltController.InitializeAsync(cancellationToken);

    public async Task RunAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken)
    {
        await _events.RunStageAsync(
            BoltFasteningStages.WaitShuttle,
            cancellationToken,
            token => _machine.WaitForInputAsync(ShuttlePresentInputChannel, token));

        await _events.RunStageAsync(
            BoltFasteningStages.StopAlignLift,
            cancellationToken,
            token => _machine.StopAlignLiftAsync(
                StopperChannel,
                AlignChannel,
                LiftChannel,
                token));

        var fiducial = await _events.RunStageAsync(
            BoltFasteningStages.Fiducial,
            cancellationToken,
            token => DetectFiducialAsync(recipe, token));

        await _events.RunStageAsync(
            BoltFasteningStages.Tighten,
            cancellationToken,
            token => TightenBoltsAsync(recipe, fiducial, token));

        await _events.RunStageAsync(
            BoltFasteningStages.Release,
            cancellationToken,
            token => _machine.ReleaseAsync(
                StopperChannel,
                AlignChannel,
                LiftChannel,
                token));
    }

    public void Stop() => _machine.Stop();

    public void EmergencyStop() => _machine.EmergencyStop();

    public void Dispose() => _machine.Dispose();

    private async Task<FiducialResult> DetectFiducialAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken)
    {
        await _machine.MoveToPositionAsync(recipe.FiducialPosition, cancellationToken);

        var result = await _fiducial.DetectFromCameraAsync(cancellationToken);
        _events.Fiducial(result);

        if (!result.Found)
        {
            throw new InvalidOperationException("Fiducial detection failed.");
        }

        return result;
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
                await _machine.MoveToPositionAsync(position, cancellationToken);

                await _boltController.ShootAsync(cancellationToken);
                _events.Bolt(await TightenAsync(boltPoint.TargetTorqueNm, cancellationToken));
                await _machine.MoveToZAsync(0, cancellationToken);
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
}
