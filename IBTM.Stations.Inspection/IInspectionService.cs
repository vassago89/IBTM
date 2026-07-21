using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Process;

namespace IBTM.Stations.Inspection;

public interface IInspectionService
{
    Task<InspectionOutcome> InspectAsync(CancellationToken cancellationToken = default);
}
