using System.Collections.Generic;
using IBTM.Core;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM;

public sealed class Recipe
{
    public const double DefaultCarrierImageMillimetersPerPixel = 0.05;

    public Recipe()
    {
        PcbSupply = new();
        PcbPlacement = new();
        Pcb = new();
        BoltInspection = new();
        CarrierImages = [];
    }

    public string Name { get; set; } = "Default";
    public PcbSupplyRecipe PcbSupply { get; set; }
    public PcbPlacementRecipe PcbPlacement { get; set; }
    public PcbLayout Pcb { get; set; }
    public BoltInspectionRecipe BoltInspection { get; set; }
    public double CarrierImageMillimetersPerPixel { get; set; } = DefaultCarrierImageMillimetersPerPixel;
    public List<CarrierImageTile> CarrierImages { get; set; }

    public void ReplaceWith(Recipe recipe)
    {
        Name = recipe.Name;
        PcbSupply = recipe.PcbSupply;
        PcbPlacement = recipe.PcbPlacement;
        Pcb = recipe.Pcb;
        BoltInspection = recipe.BoltInspection;
        CarrierImageMillimetersPerPixel = recipe.CarrierImageMillimetersPerPixel;
        CarrierImages = recipe.CarrierImages;
    }
}
