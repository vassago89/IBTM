using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System;
using IBTM.Core;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM;

public sealed class Recipe : IJsonOnDeserialized
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

    void IJsonOnDeserialized.OnDeserialized()
    {
        foreach (var bolt in Pcb.BoltPoints.Where(bolt => bolt.Id == Guid.Empty))
        {
            // Stable within a legacy recipe until saved with IDs. Newly added points use new GUIDs.
            bolt.Id = new Guid(bolt.Number, (short)bolt.HeatSink, 0, 0x49, 0x42, 0x54, 0x4d, 0x42, 0x4f, 0x4c, 0x54);
        }
        // Old recipes duplicated bolt XY in the image metadata. Automatic inspection
        // used Center, so preserve that location when consolidating the stored values.
        foreach (var tile in CarrierImages.Where(tile => !tile.IsBarcode && tile.Center is not null))
        {
            var bolt = Pcb.BoltPoints.SingleOrDefault(
                bolt => bolt.HeatSink == tile.HeatSink && bolt.Number == tile.BoltNumber);
            if (bolt is null)
                continue;
            bolt.X = tile.Center!.X;
            bolt.Y = tile.Center.Y;
            tile.Center = null;
        }
    }

    public AxisPosition GetInspectionPosition(CarrierImageTile tile)
    {
        var position = tile.IsBarcode ? tile.Center : Pcb.BoltPoints.SingleOrDefault(
            bolt => bolt.HeatSink == tile.HeatSink && bolt.Number == tile.BoltNumber)?.InspectionPosition;
        return position ?? throw new InvalidOperationException(
            $"Record an inspection position for {tile.HeatSink}, image {tile.Number}.");
    }

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
            var edited = source.Pcb.BoltPoints.SingleOrDefault(item => item.Id == bolt.Id
                && item.HeatSink == bolt.HeatSink && item.Number == bolt.Number);
            if (edited is null)
                continue;
            bolt.BrightnessThreshold = edited.BrightnessThreshold;
            bolt.MinimumBrightRatio = edited.MinimumBrightRatio;
        }
        foreach (var tile in CarrierImages)
        {
            if (!tile.IsBarcode)
            {
                var bolt = Pcb.BoltPoints.SingleOrDefault(item => item.HeatSink == tile.HeatSink && item.Number == tile.BoltNumber);
                if (bolt is null || !source.Pcb.BoltPoints.Any(item => item.Id == bolt.Id
                    && item.HeatSink == bolt.HeatSink && item.Number == bolt.Number))
                    continue;
            }
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
