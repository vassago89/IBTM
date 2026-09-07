using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using IBTM.Core;

namespace IBTM;

public sealed class RecipeStore
{
    private const string RecipeFileName = "Recipe.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _recipeDirectory =
        Path.Combine(AppContext.BaseDirectory, "Recipes");

    public RecipeStore() => Directory.CreateDirectory(_recipeDirectory);

    public async Task SaveRecipeAsync(
        Recipe recipe,
        CancellationToken cancellationToken = default)
    {
        var recipeDirectory = GetRecipeDirectory(recipe.Name);
        Directory.CreateDirectory(recipeDirectory);
        var filePath = Path.Combine(recipeDirectory, RecipeFileName);
        var temporaryPath = $"{filePath}.tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                recipe,
                JsonOptions,
                cancellationToken);
        }

        File.Move(temporaryPath, filePath, overwrite: true);
        DeleteUnusedRecipeImages(recipe);
    }

    public async Task<Recipe> LoadRecipeAsync(
        string recipeName,
        CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(
            GetRecipeDirectory(recipeName),
            RecipeFileName);
        await using var stream = File.OpenRead(filePath);
        var recipe = await JsonSerializer.DeserializeAsync<Recipe>(
            stream,
            JsonOptions,
            cancellationToken);
        return recipe
            ?? throw new InvalidDataException(
                $"Recipe '{recipeName}' is empty or invalid.");
    }

    public IReadOnlyList<string> GetRecipeNames() =>
        Directory.GetDirectories(_recipeDirectory)
            .Where(path => File.Exists(Path.Combine(path, RecipeFileName)))
            .Select(path => new DirectoryInfo(path).Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void DeleteUnusedRecipeImages(Recipe recipe)
    {
        var directory = GetRecipeImageDirectory(recipe.Name);
        if (!Directory.Exists(directory))
        {
            return;
        }

        var retained = recipe.CarrierImages
            .Select(tile => GetRecipeImagePath(recipe.Name, tile.Number))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(directory, "*.png"))
        {
            if (!retained.Contains(path))
            {
                File.Delete(path);
            }
        }
    }

    public List<CarrierImageTile> SaveRecipeImages(
        string recipeName,
        IEnumerable<(AxisPosition Center, BitmapSource Image)> images)
    {
        var number = GetNextRecipeImageNumber(recipeName);
        List<CarrierImageTile> tiles = [];
        foreach (var (center, image) in images)
        {
            var tile = new CarrierImageTile { Number = number++, Center = center };
            using var stream = File.Create(GetRecipeImagePath(recipeName, tile.Number));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(stream);
            tiles.Add(tile);
        }

        return tiles;
    }

    public BitmapSource LoadRecipeImage(string recipeName, int number)
    {
        using var stream = File.OpenRead(GetRecipeImagePath(recipeName, number));
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var image = decoder.Frames[0];
        image.Freeze();
        return image;
    }

    public List<CarrierImageTile> CopyRecipeImages(
        string sourceRecipe,
        string targetRecipe,
        IEnumerable<CarrierImageTile> sourceTiles)
    {
        var number = GetNextRecipeImageNumber(targetRecipe);
        List<CarrierImageTile> tiles = [];
        foreach (var source in sourceTiles)
        {
            var tile = new CarrierImageTile { Number = number++, Center = source.Center };
            File.Copy(
                GetRecipeImagePath(sourceRecipe, source.Number),
                GetRecipeImagePath(targetRecipe, tile.Number));
            tiles.Add(tile);
        }

        return tiles;
    }

    private int GetNextRecipeImageNumber(string recipeName)
    {
        var directory = GetRecipeImageDirectory(recipeName);
        Directory.CreateDirectory(directory);
        return Directory.GetFiles(directory, "*.png")
            .Select(path => int.Parse(
                Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture))
            .DefaultIfEmpty()
            .Max() + 1;
    }

    private string GetRecipeImageDirectory(string recipeName) =>
        Path.Combine(
            GetRecipeDirectory(recipeName),
            "Carrier");

    private string GetRecipeImagePath(string recipeName, int number) =>
        Path.Combine(
            GetRecipeImageDirectory(recipeName),
            $"{number:D4}.png");

    private string GetRecipeDirectory(string recipeName)
    {
        var directoryName = recipeName.Trim();
        if (directoryName.Length == 0
            || directoryName.EndsWith('.')
            || directoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Invalid recipe name.", nameof(recipeName));
        }

        return Path.Combine(_recipeDirectory, directoryName);
    }

}
