using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Process;
using IBTM.Stations.Inspection;

namespace IBTM.Virtual;

public sealed class VirtualInspectionService : IInspectionService
{
    public async Task<InspectionOutcome> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(500, cancellationToken);
        var result = Random.Shared.NextDouble() > 0.15
            ? InspectionResult.Good
            : InspectionResult.Ng;
        return new InspectionOutcome(result, VirtualImageFactory.CreateInspectionFrame());
    }
}
