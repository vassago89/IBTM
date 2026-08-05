using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualBoltService : IBoltHead
{
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<BoltResult> TightenAsync(
        double targetTorqueNm,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new BoltResult(true, targetTorqueNm));
    }

    public void Stop()
    {
    }

    public void EmergencyStop()
    {
    }
}
