
namespace IBTM.Infrastructure.Simulation;

/// <summary>Simulation-only bolt controller.</summary>
public sealed class StubBoltService(MachineConfig machineConfig) : IBoltService
{
    private readonly MachineConfig _machineConfig = machineConfig;

    public async Task<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (!_machineConfig.SimulationMode)
        {
            return false;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        return true;
    }

    public async Task<bool> ShootAsync(CancellationToken ct = default)
    {
        EnsureSimulationMode();
        await Task.Delay(TimeSpan.FromMilliseconds(800), ct);
        return true;
    }

    public async Task<BoltResult> TightenAsync(
        double targetTorqueNm,
        CancellationToken ct = default)
    {
        EnsureSimulationMode();
        if (!double.IsFinite(targetTorqueNm) || targetTorqueNm <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetTorqueNm),
                "Target torque must be finite and greater than zero.");
        }

        await Task.Delay(TimeSpan.FromMilliseconds(1_800), ct);
        var actualTorque = targetTorqueNm
            + ((Random.Shared.NextDouble() - 0.5) * targetTorqueNm * 0.08);
        var tolerance = targetTorqueNm * 0.05;
        var succeeded = Math.Abs(actualTorque - targetTorqueNm) <= tolerance;

        return new BoltResult(
            succeeded,
            actualTorque,
            succeeded ? "OK" : $"NG (±{tolerance:F2} Nm tolerance exceeded)");
    }

    private void EnsureSimulationMode()
    {
        if (!_machineConfig.SimulationMode)
        {
            throw new InvalidOperationException("The simulated bolt controller is disabled outside simulation mode.");
        }
    }
}
