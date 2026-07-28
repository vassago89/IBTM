using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.Stations.BoltFastening;

public interface IBoltHead
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SupplyAsync(CancellationToken cancellationToken = default);
    Task<BoltResult> TightenAsync(
        double targetTorqueNm,
        CancellationToken cancellationToken = default);
    void Stop();
    void EmergencyStop();
}
