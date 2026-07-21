using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Process;

namespace IBTM.Stations.BoltFastening;

public interface IBoltService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task ShootAsync(CancellationToken cancellationToken = default);
    Task<BoltResult> TightenAsync(
        double targetTorqueNm,
        CancellationToken cancellationToken = default);
}
