using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using IBTM.Storage;
using IBTM.Inspection;
using IBTM.UI;

namespace IBTM;
// WPF image conversion stays here; IBTM.Storage handles database records only.
public sealed class RecipeStore(MachineStore database)
{
    internal MachineStore Database
    {
        get
        {
            return database;
        }
    }

    public IReadOnlyList<string> GetRecipeNames()
    {
        return database.GetRecipeNames();
    }

    public Task<Recipe> LoadRecipeAsync(string name, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => database.LoadRecipe<Recipe>(name), cancellationToken);
    }

    public Task SaveRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        return SaveRecipeAsync(recipe, recipe.Name, null, null, cancellationToken);
    }

    internal Task SaveRecipeAsync(
        Recipe recipe,
        string name,
        string? sourceRecipe,
        RecipeSelectionSettings? selection,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                var document = JsonSerializer.SerializeToNode(recipe)!;
                document[nameof(Recipe.Name)] = name;
                database.SaveRecipe(
                    name,
                    document,
                    recipe.CarrierImages.Select(tile => tile.Number).ToArray(),
                    sourceRecipe,
                    selection: selection,
                    cancellationToken: cancellationToken);
            },
            cancellationToken);
    }

    internal Task<List<CarrierImageTile>> SaveRecipeImagesAsync(
        Recipe recipe,
        string name,
        IReadOnlyList<CarrierImageTileView> images,
        RecipeSelectionSettings selection,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                var tiles = images.Select(
                    (image, index) => new CarrierImageTile
                    {
                        Number = index + 1,
                        Center = image.Center,
                        Region = image.Region,
                        BoltNumber = image.BoltNumber,
                        IsBarcode = image.IsBarcode,
                        HeatSink = image.HeatSink,
                    })
                    .ToList();
                var encoded = images.Select(
                    (image, index) =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var stream = new MemoryStream();
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(image.Image));
                        encoder.Save(stream);
                        return new RecipeImage(index + 1, stream.ToArray());
                    });
                var document = JsonSerializer.SerializeToNode(recipe)!;
                document[nameof(Recipe.Name)] = name;
                document[nameof(Recipe.CarrierImages)] = JsonSerializer.SerializeToNode(tiles);
                database.SaveRecipe(
                    name,
                    document,
                    tiles.Select(tile => tile.Number).ToArray(),
                    images: encoded,
                    selection: selection,
                    cancellationToken: cancellationToken);
                return tiles;
            },
            cancellationToken);
    }

    public BitmapSource LoadRecipeImage(string name, int number)
    {
        using var stream = new MemoryStream(database.LoadRecipeImage(name, number), writable: false);
        var image = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();
        return image;
    }
}
