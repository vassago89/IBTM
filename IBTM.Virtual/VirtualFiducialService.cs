using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Process;
using IBTM.Stations.BoltFastening;

namespace IBTM.Virtual;

public sealed class VirtualFiducialService : IFiducialService
{
    public async Task<FiducialResult> DetectFromCameraAsync(
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(400, cancellationToken);
        return new FiducialResult(
            true,
            (Random.Shared.NextDouble() - 0.5) * 0.4,
            (Random.Shared.NextDouble() - 0.5) * 0.4);
    }
}
