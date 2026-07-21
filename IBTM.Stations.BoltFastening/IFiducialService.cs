using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Process;

namespace IBTM.Stations.BoltFastening;

public interface IFiducialService
{
    Task<FiducialResult> DetectFromCameraAsync(CancellationToken cancellationToken = default);
}
