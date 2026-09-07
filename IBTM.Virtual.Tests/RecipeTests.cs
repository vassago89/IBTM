using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IBTM.Core;
using IBTM.Device;
using IBTM.UI;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class RecipeTests
{
    [Fact]
    public async Task RecipeSaveAndLoadKeepTheirOperationActive()
    {
        var recipe = new Recipe { Name = $"RecipeActivity-{Guid.NewGuid():N}" };
        var operations = new OperationCancellation();
        var editor = new RecipeEditor(new MachineStore(),
            new RecipeSelectionSettings(), recipe, operations);
        bool? activeAtChange = null;
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RecipeEditor.ActiveName))
                activeAtChange = operations.HasActiveOperations;
        };

        await editor.SaveAsync();
        Assert.True(activeAtChange);
        Assert.False(operations.HasActiveOperations);

        activeAtChange = null;
        await editor.LoadCommand.ExecuteAsync(recipe.Name);
        Assert.True(activeAtChange);
        Assert.False(operations.HasActiveOperations);
    }

    [Fact]
    public async Task FailedCarrierRecapturePreservesSavedImages()
    {
        var recipe = new Recipe { Name = $"ImageSave-{Guid.NewGuid():N}" };
        var store = new MachineStore();
        var editor = new RecipeEditor(store, new RecipeSelectionSettings(), recipe, new());
        var image = BitmapSource.Create(320, 240, 96, 96, PixelFormats.Bgr24,
            null, new byte[320 * 240 * 3], 320 * 3);
        image.Freeze();
        await editor.SaveCarrierImagesAsync([
            new(1, new AxisPosition { X = 10, Y = 20 }, image),
            new(2, new AxisPosition { X = 30, Y = 20 }, image),
        ]);
        CarrierImageTileView[] captured = [
            new(1, new AxisPosition { X = 11, Y = 21 }, image),
            new(2, new AxisPosition { X = 31, Y = 21 }, image),
        ];

        var blockedFile = Path.Combine(AppContext.BaseDirectory,
            "Recipes", recipe.Name, "Carrier", "0004.png");
        Directory.CreateDirectory(blockedFile);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                editor.SaveCarrierImagesAsync(captured));
        }
        finally
        {
            Directory.Delete(blockedFile);
        }

        var saved = await store.LoadRecipeAsync(recipe.Name);
        var reopened = new RecipeEditor(store, new RecipeSelectionSettings(), saved, new());
        Assert.Equal(2, reopened.LoadCarrierImages().Count);
        Assert.Equal([10d, 30d], saved.CarrierImages.Select(tile => tile.Center.X));
        Assert.Equal([10d, 30d], recipe.CarrierImages.Select(tile => tile.Center.X));

        var blockedRecipe = Path.Combine(AppContext.BaseDirectory,
            "Recipes", recipe.Name, "Recipe.json.tmp");
        Directory.CreateDirectory(blockedRecipe);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                editor.SaveCarrierImagesAsync(captured));
        }
        finally
        {
            Directory.Delete(blockedRecipe);
        }

        saved = await store.LoadRecipeAsync(recipe.Name);
        reopened = new RecipeEditor(store, new RecipeSelectionSettings(), saved, new());
        Assert.Equal(2, reopened.LoadCarrierImages().Count);
        Assert.Equal([10d, 30d], saved.CarrierImages.Select(tile => tile.Center.X));
        await editor.SaveAsync();
        saved = await store.LoadRecipeAsync(recipe.Name);
        Assert.Equal([11d, 31d], saved.CarrierImages.Select(tile => tile.Center.X));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(blockedFile)!, "*.png").Length);
        Assert.All(editor.LoadCarrierImages(), tile =>
        {
            Assert.Equal(320, tile.Image.PixelWidth);
            Assert.Equal(240, tile.Image.PixelHeight);
        });
    }

    [Fact]
    public async Task FailedRecipeCopyPreservesTargetImages()
    {
        var store = new MachineStore();
        var source = new Recipe { Name = $"CopySource-{Guid.NewGuid():N}" };
        var target = new Recipe { Name = $"CopyTarget-{Guid.NewGuid():N}" };
        var sourceEditor = new RecipeEditor(store, new RecipeSelectionSettings(), source, new());
        var targetEditor = new RecipeEditor(store, new RecipeSelectionSettings(), target, new());
        BitmapSource Image(byte value)
        {
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24,
                null, new byte[] { value, value, value }, 3);
            image.Freeze();
            return image;
        }

        await sourceEditor.SaveCarrierImagesAsync([
            new(1, new AxisPosition { X = 1 }, Image(10)),
            new(2, new AxisPosition { X = 2 }, Image(20)),
        ]);
        await targetEditor.SaveCarrierImagesAsync([
            new(1, new AxisPosition { X = 10 }, Image(100)),
            new(2, new AxisPosition { X = 20 }, Image(200)),
        ]);
        var sourceName = source.Name;
        var targetDirectory = Path.Combine(AppContext.BaseDirectory, "Recipes", target.Name, "Carrier");
        var targetFirst = Path.Combine(targetDirectory, "0001.png");
        var original = await File.ReadAllBytesAsync(targetFirst);
        sourceEditor.Name = target.Name;
        using (File.Open(Path.Combine(AppContext.BaseDirectory,
                   "Recipes", sourceName, "Carrier", "0002.png"),
                   FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => sourceEditor.SaveAsync());
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(targetFirst));
        var saved = await store.LoadRecipeAsync(target.Name);
        Assert.Equal([10d, 20d], saved.CarrierImages.Select(tile => tile.Center.X));
        Assert.Equal(sourceName, sourceEditor.ActiveName);
        await sourceEditor.SaveAsync();
        saved = await store.LoadRecipeAsync(target.Name);
        Assert.Equal([1d, 2d], saved.CarrierImages.Select(tile => tile.Center.X));
        Assert.Equal(2, Directory.GetFiles(targetDirectory, "*.png").Length);
        Assert.Equal(2, (await store.LoadRecipeAsync(sourceName)).CarrierImages.Count);
        Assert.Equal(2, sourceEditor.LoadCarrierImages().Count);
    }
}
