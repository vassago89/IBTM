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
        // Gantry teaching owns coordinates, reference images and capture lighting.
        var inspection = JsonSerializer.Deserialize<BoltInspectionRecipe>(JsonSerializer.Serialize(source.BoltInspection))!;
        inspection.LightLevel = BoltInspection.LightLevel;
        inspection.DataMatrix1.LightLevel = BoltInspection.DataMatrix1.LightLevel;
        inspection.DataMatrix2.LightLevel = BoltInspection.DataMatrix2.LightLevel;
        BoltInspection = inspection;
        CarrierImageMillimetersPerPixel = source.CarrierImageMillimetersPerPixel;
        foreach (var bolt in Pcb.BoltPoints)
        {
            var edited = source.Pcb.BoltPoints.SingleOrDefault(item => item.HeatSink == bolt.HeatSink && item.Number == bolt.Number);
            if (edited is null)
                continue;
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
