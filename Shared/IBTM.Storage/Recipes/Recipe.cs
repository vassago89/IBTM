using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

    public void ApplyInspectionSettings(Recipe source)
    {
        // Only inspection parameters are editable here. Position teaching owns all coordinates.
        BoltInspection = JsonSerializer.Deserialize<BoltInspectionRecipe>(JsonSerializer.Serialize(source.BoltInspection))!;
        CarrierImageMillimetersPerPixel = source.CarrierImageMillimetersPerPixel;
        foreach (var bolt in Pcb.BoltPoints)
        {
            var edited = source.Pcb.BoltPoints.SingleOrDefault(item => item.HeatSink == bolt.HeatSink && item.Number == bolt.Number);
            if (edited is null)
                continue;
            bolt.LightLevel = edited.LightLevel;
            bolt.BrightnessThreshold = edited.BrightnessThreshold;
            bolt.MinimumBrightRatio = edited.MinimumBrightRatio;
        }
        foreach (var tile in CarrierImages)
        {
            var edited = source.CarrierImages.SingleOrDefault(item => item.Number == tile.Number
                && item.HeatSink == tile.HeatSink && item.IsBarcode == tile.IsBarcode && item.BoltNumber == tile.BoltNumber);
            if (edited is not null)
                tile.Region = edited.Region;
        }
    }

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
