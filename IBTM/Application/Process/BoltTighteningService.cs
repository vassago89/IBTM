
namespace IBTM.Application.Process;

public sealed record BoltProcessLog(string Message, LogLevel Level = LogLevel.Info);

public sealed class BoltTighteningService(
    MachineOperations machine,
    IBoltService boltController,
    MachineConfig config)
{
    private const int Zone = 2;
    private bool _initialized;

    public event EventHandler<BoltProgressEventArgs>? ProgressChanged;
    public event EventHandler<BoltResult>? BoltCompleted;
    public event EventHandler<BoltProcessLog>? LogAdded;

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
        Recipe recipe,
        FiducialResult fiducial,
        CancellationToken cancellationToken)
    {
        var pcbCentres = new[]
        {
            (X: recipe.Zone2_Pcb1CenterX, Y: recipe.Zone2_PcbCenterY, Label: "PCB1"),
            (X: recipe.Zone2_Pcb2CenterX, Y: recipe.Zone2_PcbCenterY, Label: "PCB2"),
        };
        var totalBolts = recipe.BoltPoints.Count * pcbCentres.Length;
        var boltIndex = 0;

        foreach (var pcb in pcbCentres)
        {
            Log($"{pcb.Label} bolt tightening (center X={pcb.X:F0} Y={pcb.Y:F0})");

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
                Log($"Bolt {boltIndex}/{totalBolts} [{boltLabel}] moving");

                await machine.MoveToPositionAsync(Zone, position, cancellationToken);

                Log($"[{boltLabel}] Shooting");
                if (!await boltController.ShootAsync(cancellationToken))
                {
                    throw new InvalidOperationException($"Bolt shooting failed for {boltLabel}.");
                }

                var result = await TightenAsync(
                    boltLabel,
                    boltPoint.TargetTorqueNm,
                    cancellationToken);
                BoltCompleted?.Invoke(this, result);
                Log(
                    $"[{boltLabel}] Tighten done actual: {result.Torque:F2} Nm [{result.Message}]",
                    result.Success ? LogLevel.Info : LogLevel.Warning);

                await machine.MoveToZAsync(Zone, 0, cancellationToken);
            }
        }

        Log("All bolts tightened");
    }

    private async Task<BoltResult> TightenAsync(
        string boltLabel,
        double targetTorque,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            Log($"[{boltLabel}] Tightening target: {targetTorque:F1} Nm (attempt {attempt + 1})");
            var result = await boltController.TightenAsync(targetTorque, cancellationToken);
            if (result.Success || attempt >= config.BoltRetryCount)
            {
                return result;
            }
        }
    }

    private void Log(string message, LogLevel level = LogLevel.Info) =>
        LogAdded?.Invoke(this, new BoltProcessLog(message, level));
}
