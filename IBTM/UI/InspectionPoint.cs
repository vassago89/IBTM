using System.Collections.Generic;
using System.Linq;
using IBTM.Core;
using IBTM.Inspection;

namespace IBTM.UI;

// A recipe target, whether or not its reference image has been recorded.
public sealed class InspectionPoint
{
    private readonly Recipe _recipe;

    public InspectionPoint(Recipe recipe, HeatSinkSlot heatSink, BoltPoint? bolt = null)
    {
        _recipe = recipe;
        HeatSink = heatSink;
        Bolt = bolt;
    }

    public HeatSinkSlot HeatSink { get; }
    public BoltPoint? Bolt { get; }
    public bool IsDataMatrix => Bolt is null;
    public string Name => IsDataMatrix ? "Data Matrix Inspection" : $"{_recipe.Pcb.GetBoltName(Bolt!.Id)} Inspection";
    public string Title => $"{HeatSink.GetDescription()} · {Name}";
    public int ImageCount => _recipe.CarrierImages.Count(Matches);

    public CarrierImageTile? Metadata
    {
        get
        {
            var images = _recipe.CarrierImages.Where(Matches).Take(2).ToArray();
            return images.Length == 1 ? images[0] : null;
        }
    }

    public AxisPosition? Position => IsDataMatrix ? Metadata?.Center : Bolt!.InspectionPosition;

    public string PositionLabel
    {
        get
        {
            var imageCount = ImageCount;
            if (IsDataMatrix && imageCount > 1)
                return "Position ambiguous · multiple reference images";
            var coordinates = Position is { } position ? $"X {position.X:F3}  Y {position.Y:F3}" : "Not taught";
            return imageCount switch
            {
                0 => $"{coordinates} · No reference image",
                1 => coordinates,
                _ => $"{coordinates} · Multiple reference images",
            };
        }
    }

    public bool Matches(CarrierImageTile image)
    {
        return image.IsForTarget(HeatSink, Bolt?.Id);
    }

    public RecipeImageItem? FindImage(IReadOnlyList<RecipeImageItem> images)
    {
        return Metadata is { } metadata
            ? images.FirstOrDefault(image => image.Metadata.Number == metadata.Number && Matches(image.Metadata))
            : null;
    }

    public static IEnumerable<InspectionPoint> ForPcb(Recipe recipe, HeatSinkSlot heatSink)
    {
        yield return new(recipe, heatSink);
        foreach (var bolt in recipe.Pcb.GetBolts(heatSink))
            yield return new(recipe, heatSink, bolt);
    }
}
