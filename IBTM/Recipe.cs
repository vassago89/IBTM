using System.Collections.Generic;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM;

public sealed class Recipe
{
    public string Name { get; set; } = "Default";
    public PcbSupplyRecipe PcbSupply { get; set; } = new();
    public PcbPlacementRecipe PcbPlacement { get; set; } = new();
    public BoltFasteningRecipe BoltFastening { get; set; } = new();
    public double CarrierImageMillimetersPerPixel { get; set; } = 0.05;
    public List<CarrierImageTile> CarrierImages { get; set; } = [];

    public void ReplaceWith(Recipe recipe)
    {
        Name = recipe.Name;
        PcbSupply = recipe.PcbSupply;
        PcbPlacement = recipe.PcbPlacement;
        BoltFastening = recipe.BoltFastening;
        CarrierImageMillimetersPerPixel =
            recipe.CarrierImageMillimetersPerPixel;
        CarrierImages = recipe.CarrierImages;
    }
}

public sealed class CarrierImageTile
{
    public int Number { get; set; }
    public AxisPos Center { get; set; } = new();
}
