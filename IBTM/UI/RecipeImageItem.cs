using System.Windows.Media.Imaging;
using IBTM.Inspection;

namespace IBTM.UI;

public sealed record RecipeImageItem(
    CarrierImageTile Metadata,
    BitmapSource? Image,
    string? Error = null)
{
    // Only undecodable images retain their original bytes for an unchanged save.
    public byte[]? UnreadablePng { get; init; }

    public override string ToString()
    {
        return $"FOV {Metadata.Number}";
    }
}
