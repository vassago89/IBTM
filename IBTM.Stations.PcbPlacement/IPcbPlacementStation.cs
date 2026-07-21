namespace IBTM.Stations.PcbPlacement;

public interface IPcbPlacementStation : IStation
{
    Task RunAsync(PcbPlacementRecipe recipe, CancellationToken cancellationToken);
}
