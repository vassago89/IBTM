using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Stations.BoltFastening;

namespace IBTM.Virtual;

public sealed class VirtualBoltService : IBoltHead
{
    public int SupplyCount { get; private set; }
    public int TightenCount { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SupplyAsync(CancellationToken cancellationToken = default)
    {
        SupplyCount++;
        return Task.CompletedTask;
    }

    public Task<BoltResult> TightenAsync(
        double targetTorqueNm,
        CancellationToken cancellationToken = default)
    {
        TightenCount++;
        return Task.FromResult(new BoltResult(true, targetTorqueNm));
    }

    public void Stop()
    {
    }

    public void EmergencyStop()
    {
    }
}
