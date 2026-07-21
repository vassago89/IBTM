
namespace IBTM.Infrastructure.Simulation;

public sealed class SimulatedInspectionService : IInspectionService
{
    private static readonly TimeSpan InspectionDelay = TimeSpan.FromMilliseconds(500);

    public async Task<InspectionOutcome> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(InspectionDelay, cancellationToken);
        var result = Random.Shared.NextDouble() > 0.15
            ? InspectionResult.Good
            : InspectionResult.Ng;
        return new InspectionOutcome(result, SimulatedImageFactory.CreateInspectionFrame());
    }
}
