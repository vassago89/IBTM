using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.UI;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class RecipeTests
{
    [Fact]
    public void TeachingDefinitionsKeepBufferEditsStagedAndUpdateTheOwningSettings()
    {
        var supply = new PcbSupplySettings { CarrierY = 7 };
        var placement = new PcbPlacementHandlerSettings();
        var buffer = new PcbBufferSettings();
        var recipe = new PcbSupplyRecipe();
        var supplyHandoff = supply.BufferHandoffPosition;
        var placementHandoff = placement.BufferHandoffPosition;
        TeachingPosition[] definitions =
        [
            .. supply.GetTeachingPositions(recipe),
            .. placement.GetTeachingPositions(),
            placement.GetBufferTeachingPosition(),
            .. buffer.GetTeachingPositions(),
        ];
        var points = definitions.Select(p => new TeachingPoint(p)).ToArray();
        Assert.Equal(12, points.Length);
        var staged = points.Where(p => p.Storage == TeachingStorage.Buffer).ToArray();
        foreach (var point in staged) point.Teach(10, 20, 30);
        Assert.Equal(0, supply.BufferHandoffPosition.X);
        Assert.Equal(0, placement.BufferHandoffPosition.X);
        Assert.Equal(0, buffer.SupplyBoundary1);

        var carrierY = points.Single(p => p.Target == TeachingTarget.SupplyCarrierY);
        carrierY.Teach(0, 45, 0);
        carrierY.Apply();
        foreach (var point in points.Where(p => p.Storage != TeachingStorage.Buffer)) point.Refresh();
        var picks = points.Where(p => p.Storage == TeachingStorage.Recipe).ToArray();
        Assert.All(picks, p => Assert.Equal(45, p.Y));
        Assert.Equal(10, staged[0].X);
        Assert.Equal(0, supply.BufferHandoffPosition.X);
        Assert.Same(supply, carrierY.Position.Setting);

        foreach (var point in picks) { point.Teach(12, 999, 34); point.Apply(); }
        Assert.Equal((12, 34), (recipe.Pcb1PickPosition.X, recipe.Pcb1PickPosition.Z));
        Assert.Equal((12, 34), (recipe.Pcb2PickPosition.X, recipe.Pcb2PickPosition.Z));
        Assert.Equal(45, supply.CarrierY);
        foreach (var point in staged) point.Apply();
        Assert.Same(supplyHandoff, supply.BufferHandoffPosition);
        Assert.Same(placementHandoff, placement.BufferHandoffPosition);
        Assert.Equal((10, 20, 30), (supplyHandoff.X, supplyHandoff.Y, supplyHandoff.Z));
        Assert.Equal((10, 20, 30), (placementHandoff.X, placementHandoff.Y, placementHandoff.Z));
        Assert.Equal(10, buffer.SupplyBoundary1);
        Assert.Equal(20, buffer.PlacementBoundary2.Y);
        Assert.Equal(3, staged.Select(p => p.Position.Setting).Distinct().Count());
    }

    [Fact]
    public void TeachingUsesOwnerCoordinatesAndRetainsTheOtherReferencePin()
    {
        var reference = new CarrierReferenceSettings
        {
            UpperLeftLocatingPin = new() { X = 100, Y = 200 },
            LowerRightLocatingPin = new() { X = 200, Y = 300 },
        };
        var inspection = new InspectionGantrySettings();
        var pins = inspection.GetTeachingPositions(reference);
        Assert.Equal(2, pins.Length);
        Assert.All(pins, pin => Assert.Equal(TeachMode.XYOnly, pin.Mode));
        var fastening = new BoltFasteningSettings
        {
            ShootingHead = new()
            {
                UpperLeftLocatingPin = new() { X = 300, Y = 400 },
                LowerRightLocatingPin = new() { X = 200, Y = 500 },
            },
        };
        var bolt = new BoltPoint { Number = 1 };
        var recipe = new BoltFasteningRecipe { BoltPoints = [bolt] };
        var image = new TeachingPoint(inspection.GetBoltTeachingPositions([bolt], reference).Single());
        var height = new TeachingPoint(fastening.GetTeachingPositions(recipe, reference)
            .Single(p => p.Target == TeachingTarget.BoltPointZ));
        Assert.False(image.Position.HasPosition);
        Assert.Equal("—", image.PositionLabel);
        Assert.False(height.Position.HasPosition);
        Assert.Null(height.Z);
        image.Teach(110, 220, 0);
        image.Apply();
        image.Refresh();
        Assert.Equal($"{10d:F3}, {20d:F3}", image.PositionLabel);
        Assert.Equal((10d, 20d), (bolt.X, bolt.Y));
        Assert.True(image.Position.HasPosition);
        Assert.False(height.Position.HasPosition);
        height.Refresh();
        Assert.Equal(280, height.X, 6);
        Assert.Equal(410, height.Y, 6);
        Assert.Null(height.Z);
        height.Teach(999, 999, 5);
        height.Apply();
        height.Refresh();
        Assert.True(height.Position.HasPosition);
        Assert.Equal(280, height.X, 6);
        Assert.Equal(410, height.Y, 6);
        Assert.Equal(5, height.Z);

        var lowerRight = reference.LowerRightLocatingPin;
        var upperLeft = new TeachingPoint(pins
            .Single(p => p.Target == TeachingTarget.CarrierUpperLeftLocatingPin));
        upperLeft.Teach(105, 205, 0);
        upperLeft.Apply();
        image.Refresh();
        Assert.Same(lowerRight, reference.LowerRightLocatingPin);
        Assert.Equal((115, 225), (image.X, image.Y));
        Assert.Equal((10d, 20d), (bolt.X, bolt.Y));
        Assert.Same(reference, upperLeft.Position.Setting);
        var transfer = new NgCarrierTransferSettings();
        Assert.All(transfer.GetTeachingPositions(), p => Assert.Same(transfer, p.Setting));

        recipe.BoltPoints =
        [
            new() { Number = 2, Head = FasteningHead.Pickup },
            bolt,
            new() { Number = 3, Head = FasteningHead.Pickup },
        ];
        var ordered = fastening.GetTeachingPositions(recipe, reference);
        Assert.Equal(TeachingTarget.SafeZ, ordered[0].Target);
        Assert.Equal(new[] { 1, 2, 3 }, ordered.Where(point => point.Bolt is not null).Select(point => point.Bolt!.Number));
        Assert.Equal(new[] { 2, 1, 3 }, recipe.BoltPoints.Select(point => point.Number));
    }

    [Fact]
    public async Task RecipeSaveAndLoadKeepTheirOperationActive()
    {
        var recipe = new Recipe { Name = $"RecipeActivity-{Guid.NewGuid():N}" };
        var operations = new OperationCancellation();
        var editor = new RecipeEditor(new RecipeStore(),
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
        var store = new RecipeStore();
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
        var store = new RecipeStore();
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
