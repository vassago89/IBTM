namespace IBTM.Stations.Inspection;

public interface IInspectionService
{
    Task<InspectionOutcome> InspectAsync(CancellationToken cancellationToken = default);
}
