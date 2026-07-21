namespace IBTM.Stations.BoltFastening;

internal sealed class BoltTighteningService(
    StationOperations machine,
    IBoltService boltController,
    BoltFasteningOptions options)
{
    private bool _initialized;

    public event EventHandler<BoltProgressEventArgs>? ProgressChanged;
    public event EventHandler<BoltResult>? BoltCompleted;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = await boltController.InitializeAsync(cancellationToken);
        if (!_initialized)
        {
            throw new InvalidOperationException("Failed to initialize the bolt controller.");
        }
    }

    public async Task RunAsync(
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

                ProgressChanged?.Invoke(
                    this,
                    new BoltProgressEventArgs(boltIndex, totalBolts, boltLabel));

                await machine.MoveToPositionAsync(position, cancellationToken);

                if (!await boltController.ShootAsync(cancellationToken))
                {
                    throw new InvalidOperationException($"Bolt shooting failed for {boltLabel}.");
                }

                var result = await TightenAsync(
                    boltPoint.TargetTorqueNm,
                    cancellationToken);
                BoltCompleted?.Invoke(this, result);

                await machine.MoveToZAsync(0, cancellationToken);
            }
        }
    }

    private async Task<BoltResult> TightenAsync(
        double targetTorque,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await boltController.TightenAsync(targetTorque, cancellationToken);
            if (result.Success || attempt >= options.RetryCount)
            {
                return result;
            }
        }
    }
}
