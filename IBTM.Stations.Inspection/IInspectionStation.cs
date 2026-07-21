namespace IBTM.Stations.Inspection;

public interface IInspectionStation : IStation
{
    int NgStackCount { get; }
    int NgStackCapacity { get; }

    Task<InspectionResult> RunAsync(
        InspectionRecipe recipe,
        CancellationToken cancellationToken);

    void ResetNgStack();
}
