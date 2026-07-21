using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Process;
using IBTM.Stations.BoltFastening;

namespace IBTM.Virtual;

public sealed class VirtualBoltService : IBoltService
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.Delay(200, cancellationToken);

    public Task ShootAsync(CancellationToken cancellationToken = default) =>
        Task.Delay(800, cancellationToken);

    public async Task<BoltResult> TightenAsync(
        double targetTorqueNm,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(1_800, cancellationToken);
        var torque = targetTorqueNm
            + ((Random.Shared.NextDouble() - 0.5) * targetTorqueNm * 0.08);
        return new BoltResult(true, torque);
    }
}
