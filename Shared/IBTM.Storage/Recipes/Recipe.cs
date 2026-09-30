using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IBTM.Core;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;

namespace IBTM;

public sealed class Recipe
{
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
            bolt => tile.IsForTarget(bolt.HeatSink, bolt.Id))?.InspectionPosition;
        return position ?? throw new InvalidOperationException(
            $"Record an inspection position for {tile.HeatSink}, image {tile.Number}.");
    }

    public CarrierImageTile? FindInspectionImage(HeatSinkSlot heatSink, Guid? boltId)
    {
        var images = CarrierImages.Where(image => image.IsForTarget(heatSink, boltId)).Take(2).ToArray();
        return images.Length == 1 ? images[0] : null;
    }

    public void CopyFrom(Recipe recipe)
    {
        // Apply an owned copy/load result while retaining the bound recipe and bolt collection.
        Name = recipe.Name;
        PcbSupply = recipe.PcbSupply;
        PcbPlacement = recipe.PcbPlacement;
        Pcb.BoltPoints = recipe.Pcb.BoltPoints;
        Pcb.FasteningOrder = recipe.Pcb.FasteningOrder;
        BoltInspection = recipe.BoltInspection;
        CarrierImages = recipe.CarrierImages;
    }
}
