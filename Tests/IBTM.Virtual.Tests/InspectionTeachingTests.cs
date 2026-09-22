using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using IBTM.Core;
using IBTM.Storage;
using IBTM.UI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class InspectionTeachingTests
{
    [Fact]
    public async Task OfflineDraftSavesOnlyInspectionSettingsWithoutReplacingNewerPositionsOrImages()
    {
        var store = VirtualTest.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = new InspectionTeachingViewModel(store, recipes, new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.LoadRecipeCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.NotSame(recipes.Current, editor.Draft);
        Assert.True(editor.Preview.HasImage);
        editor.DrawRegionCommand.Execute(new Rect(0, 0, 8, 8));
        editor.DataMatrix!.TryInverted = false;
        editor.DataMatrix.BinaryThreshold = 72;
        editor.SelectedPoint = editor.Points.Single(point => !point.Metadata.IsBarcode);
        editor.Preview.BrightnessThreshold = 214;
        editor.Preview.MinimumBrightPercent = 40;
        editor.Draft.CarrierImageMillimetersPerPixel = 0.25;
        Assert.Null(recipes.Current.Pcb.BoltPoints[0].LightLevel);
        Assert.Equal(255, recipes.Current.BoltInspection.LightLevel);
        Assert.True(recipes.Current.BoltInspection.DataMatrix1.TryInverted);

        // Gantry coordinates and lights can change after the offline draft was opened.
        var bolt = recipes.Current.Pcb.BoltPoints[0];
        bolt.X = 333;
        bolt.Y = 444;
        bolt.Head = FasteningHead.Pickup;
        bolt.LightLevel = 87;
        recipes.Current.BoltInspection.LightLevel = 99;
        recipes.Current.BoltInspection.DataMatrix1.LightLevel = 53;
        recipes.Current.BoltInspection.DataMatrix2.LightLevel = 61;
        recipes.Current.CarrierImages[1].Center = new() { X = 333, Y = 444 };
        await recipes.SaveAsync("Inspection");
        editor.Draft.Pcb.BoltPoints[0].X = -999;
        editor.Draft.CarrierImages[1].Center.X = -999;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Null(editor.Error);
        var stored = store.LoadRecipe<Recipe>("Inspection");
        foreach (var recipe in new[] { stored, recipes.Current })
        {
            Assert.Equal((333d, 444d), (recipe.Pcb.BoltPoints[0].X, recipe.Pcb.BoltPoints[0].Y));
            Assert.Equal(FasteningHead.Pickup, recipe.Pcb.BoltPoints[0].Head);
            Assert.Equal(333, recipe.CarrierImages[1].Center.X);
            Assert.Equal(new PixelRegion(6, 6, 8, 8), recipe.CarrierImages[0].Region);
            Assert.Equal(53, recipe.BoltInspection.DataMatrix1.LightLevel);
            Assert.Equal(61, recipe.BoltInspection.DataMatrix2.LightLevel);
            Assert.Equal(99, recipe.BoltInspection.LightLevel);
            Assert.False(recipe.BoltInspection.DataMatrix1.TryInverted);
            Assert.Equal(72, recipe.BoltInspection.DataMatrix1.BinaryThreshold);
            Assert.Equal(87, recipe.Pcb.BoltPoints[0].LightLevel);
            Assert.Equal(214, recipe.Pcb.BoltPoints[0].BrightnessThreshold);
            Assert.Equal(0.4, recipe.Pcb.BoltPoints[0].MinimumBrightRatio);
            Assert.Equal(0.25, recipe.CarrierImageMillimetersPerPixel);
        }
        Assert.Equal(png, store.LoadRecipeImage("Inspection", 1));
        Assert.Equal(png, store.LoadRecipeImage("Inspection", 2));
        Assert.Same(bolt, recipes.Current.Pcb.BoltPoints[0]);
        editor.Draft.Pcb.BoltPoints[0].LightLevel = 12;
        Assert.Equal(87, bolt.LightLevel);

        // Saving another recipe never replaces the active machine recipe.
        recipes.Current.Name = "Other";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.Equal("Other", recipes.Current.Name);
        Assert.Equal(87, bolt.LightLevel);
        Assert.Equal(87, store.LoadRecipe<Recipe>("Inspection").Pcb.BoltPoints[0].LightLevel);
    }

    [Fact]
    public async Task FailedInspectionSaveDoesNotPublishDraftToAutomaticInspection()
    {
        var store = VirtualTest.OpenMachineStore();
        await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = new InspectionTeachingViewModel(store, recipes, new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.LoadRecipeCommand.ExecuteAsync(null);
        var before = JsonSerializer.Serialize(recipes.Current);
        editor.DataMatrix!.BinaryThreshold = 17;
        using var connection = new SqliteConnection($"Data Source={store.DatabaseFile}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER FailInspection BEFORE UPDATE ON Recipes BEGIN SELECT RAISE(ABORT, 'inspection write failed'); END";
        await command.ExecuteNonQueryAsync();

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(editor.Error);
        Assert.Equal(before, JsonSerializer.Serialize(recipes.Current));
        Assert.Equal(before, JsonSerializer.Serialize(store.LoadRecipe<Recipe>("Inspection")));
    }

    [Fact]
    public async Task SavedProductionImagesCanBeReinspectedWithoutChangingRecordedResults()
    {
        var store = VirtualTest.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var directory = Path.Combine(Path.GetDirectoryName(store.DatabaseFile)!, "Results");
        var databaseFile = Path.Combine(directory, "PCB-2026-09.db");
        var record = new PcbRecord(7, DateTimeOffset.Now, DateTimeOffset.Now, "Inspection", HeatSinkSlot.HeatSink1,
            "ABC", AssemblyResult.Ok, AssemblyResult.Ok, AssemblyResult.Ng, new Dictionary<int, BoltResult>(),
            new Dictionary<int, BoltResult>(), new Dictionary<int, bool> { [1] = false });
        var image = new PcbInspectionImage(1, DateTimeOffset.Now, new(2, 2, 10, 10), false, null, 0, 0.5, png);
        store.SavePcb(databaseFile, record);
        store.SavePcbImage(databaseFile, record.Number, image);
        var editor = new InspectionTeachingViewModel(store, recipes, new() { Directory = directory },
            NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.LoadRecipeCommand.ExecuteAsync(null);
        await editor.RefreshHistoryCommand.ExecuteAsync(null);
        editor.SelectedRecord = Assert.Single(editor.Records);
        await editor.LoadRecordCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.True(editor.UseHistoryImageCommand.CanExecute(null));
        editor.UseHistoryImageCommand.Execute(null);
        Assert.Equal(1, editor.SelectedPoint!.Metadata.BoltNumber);
        Assert.Contains("PCB 7", editor.ImageSource);
        Assert.Contains("NG", editor.OriginalResult);
        editor.Preview.BrightnessThreshold = 0;
        editor.Preview.MinimumBrightPercent = 0;
        await editor.InspectCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.StartsWith("OK", editor.Preview.Result);
        await editor.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(store.LoadPcbs(directory));
        Assert.Equal(AssemblyResult.Ng, saved.InspectionResult);
        Assert.False(saved.BoltPresenceResults[1]);
        var savedImage = Assert.Single(store.LoadPcbImages(saved));
        Assert.False(savedImage.Success);
        Assert.Equal(image.Region, savedImage.Region);
        Assert.Equal(png, savedImage.Png);
        Assert.Equal(png, store.LoadRecipeImage("Inspection", 2));
        editor.Draft.Name = "Other";
        Assert.False(editor.UseHistoryImageCommand.CanExecute(null));
    }

    private static async Task<byte[]> SaveRecipeAsync(MachineStore store)
    {
        var png = await Task.Run(() =>
        {
            var bitmap = InspectionPreview.CreateBitmap(new ImageFrame(20, 20, 60, new byte[1200]));
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap, null, null, null));
            encoder.Save(stream);
            return stream.ToArray();
        });
        var recipe = new Recipe { Name = "Inspection" };
        recipe.Pcb.BoltPoints.Add(new() { Number = 1, X = 10, Y = 20 });
        recipe.CarrierImages = [
            new() { Number = 1, IsBarcode = true, Region = new(2, 2, 10, 10), Center = new() { X = 1, Y = 2 } },
            new() { Number = 2, BoltNumber = 1, Region = new(2, 2, 10, 10), Center = new() { X = 10, Y = 20 } },
        ];
        store.SaveRecipe(recipe.Name, recipe, [1, 2], images: [new(1, png), new(2, png)]);
        return png;
    }
}
