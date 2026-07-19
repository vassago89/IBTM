
namespace IBTM.Infrastructure.Simulation;

public sealed class SimulatedInspectionService(MachineConfig machineConfig) : IInspectionService
{
    private static readonly TimeSpan InspectionDelay = TimeSpan.FromMilliseconds(500);
    private readonly MachineConfig _machineConfig = machineConfig;

    public async Task<InspectionOutcome> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_machineConfig.SimulationMode)
        {
            throw new InvalidOperationException(
                "The simulated inspection service is disabled outside simulation mode.");
        }

        await Task.Delay(InspectionDelay, cancellationToken);
        var result = Random.Shared.NextDouble() > 0.15
            ? InspectionResult.Good
            : InspectionResult.Ng;
        return new InspectionOutcome(result, SimulatedImageFactory.CreateInspectionFrame());
    }
}
