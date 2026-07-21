namespace IBTM.Stations.BoltFastening;

public interface IBoltFasteningStation : IStation
{
    Task PrepareAsync(CancellationToken cancellationToken);
    Task RunAsync(BoltFasteningRecipe recipe, CancellationToken cancellationToken);
}
