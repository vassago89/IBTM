using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.Device;

public interface IBoltHead
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<BoltResult> TightenAsync(
        double targetTorqueNm,
        CancellationToken cancellationToken = default);
    void Stop();
    void EmergencyStop();
}
