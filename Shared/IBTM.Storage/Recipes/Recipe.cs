using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System;
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

    public void ValidateBoltIds()
    {
        var ids = new HashSet<Guid>();
        for (var index = 0; index < Pcb.BoltPoints.Count; index++)
        {
            var bolt = Pcb.BoltPoints[index];
            if (bolt.Id == Guid.Empty)
                throw new InvalidDataException(
                    $"Recipe '{Name}': bolt entry {index + 1} ({bolt.HeatSink}) has no valid GUID. Its images and results cannot be linked.");
            if (!ids.Add(bolt.Id))
                throw new InvalidDataException(
                    $"Recipe '{Name}': bolt entry {index + 1} ({bolt.HeatSink}) has duplicate GUID {bolt.Id}. Each bolt must have its own GUID.");
        }
    }

    public AxisPosition GetInspectionPosition(CarrierImageTile tile)
    {
        var position = tile.IsBarcode ? tile.Center : Pcb.BoltPoints.SingleOrDefault(
            bolt => bolt.Id == tile.BoltId)?.InspectionPosition;
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
            var edited = source.Pcb.BoltPoints.SingleOrDefault(item => item.Id == bolt.Id);
            if (edited is null)
                continue;
            bolt.BrightnessThreshold = edited.BrightnessThreshold;
            bolt.MinimumBrightRatio = edited.MinimumBrightRatio;
        }
        foreach (var tile in CarrierImages)
        {
            if (!tile.IsBarcode)
            {
                var bolt = Pcb.BoltPoints.SingleOrDefault(item => item.Id == tile.BoltId);
                if (bolt is null || !source.Pcb.BoltPoints.Any(item => item.Id == bolt.Id))
                    continue;
            }
            var edited = source.CarrierImages.SingleOrDefault(item => item.Number == tile.Number
                && item.HeatSink == tile.HeatSink && item.IsBarcode == tile.IsBarcode && item.BoltId == tile.BoltId);
            if (edited is not null)
                tile.Region = edited.Region;
        }
    }

    public void ReplaceWith(Recipe recipe)
    {
        Name = recipe.Name;
        PcbSupply = recipe.PcbSupply;
        PcbPlacement = recipe.PcbPlacement;
        Pcb.BoltPoints = recipe.Pcb.BoltPoints;
        Pcb.FasteningOrder = recipe.Pcb.FasteningOrder;
        BoltInspection = recipe.BoltInspection;
        CarrierImageMillimetersPerPixel = recipe.CarrierImageMillimetersPerPixel;
        CarrierImages = recipe.CarrierImages;
    }
}
