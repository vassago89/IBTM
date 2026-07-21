namespace IBTM.Orchestration;

public sealed class Recipe
{
    public string Name { get; set; } = "Default";
    public PcbPlacementRecipe PcbPlacement { get; set; } = new();
    public BoltFasteningRecipe BoltFastening { get; set; } = new();
    public InspectionRecipe Inspection { get; set; } = new();
}
