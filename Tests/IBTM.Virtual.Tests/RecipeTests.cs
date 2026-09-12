using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
using IBTM.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class RecipeTests
{
    [Fact]
    public async Task HeatSinkBoltTeachingAndStorageAreIndependent()
    {
        var recipe = new Recipe { Name = "Independent heat sinks" };
        var layout = recipe.Pcb;
        layout.BoltPoints.Add(new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink1, X = 13, Y = 24 });
        layout.BoltPoints.Add(new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink2, X = 73, Y = 29 });
        var targets = layout.GetBolts().ToArray();
        Assert.NotSame(targets[0].Point, targets[1].Point);
        Assert.Equal((13d, 24d), (targets[0].X, targets[0].Y));
        Assert.Equal((73d, 29d), (targets[1].X, targets[1].Y));

        var pins = new CarrierReferenceSettings
        {
            UpperLeftLocatingPin = new() { X = 100, Y = 200 },
            LowerRightLocatingPin = new() { X = 200, Y = 300 },
        };
        var inspection = new InspectionGantrySettings();
        var fastening = new BoltFasteningSettings
        {
            ShootingHead = new()
            {
                UpperLeftLocatingPin = new() { X = 300, Y = 400 },
                LowerRightLocatingPin = new() { X = 200, Y = 500 },
            },
        };
        var second = CarrierCoordinates.FromMachine(new() { X = 175, Y = 230 }, pins.UpperLeftLocatingPin);
        targets[1].Point.X = second.X;
        targets[1].Point.Y = second.Y;
        Assert.Equal((13d, 24d), (targets[0].X, targets[0].Y));
        Assert.Equal((75d, 30d), (targets[1].X, targets[1].Y));
        var firstCamera = inspection.GetBoltPosition(targets[0], pins);
        var secondHead = fastening.GetBoltPosition(targets[1], pins);
        Assert.Equal((113, 224), (firstCamera.X, firstCamera.Y));
        Assert.Equal(270, secondHead.X, 6);
        Assert.Equal(475, secondHead.Y, 6);

        var (_, store) = CreateStore();
        var editor = new RecipeEditor(store, new(), recipe, new());
        await editor.SaveAsync();
        var loaded = await store.LoadRecipeAsync(recipe.Name);
        Assert.Equal(2, loaded.Pcb.BoltPoints.Count);
        Assert.Equal(2, loaded.Pcb.GetBolts().Count());
        Assert.Equal(13d, loaded.Pcb.GetBolts(HeatSinkSlot.HeatSink1).Single().X);
        Assert.Equal(75d, loaded.Pcb.GetBolts(HeatSinkSlot.HeatSink2).Single().X);
        layout.BoltPoints.Remove(targets[1].Point);
        Assert.Empty(layout.GetBolts(HeatSinkSlot.HeatSink2));
        Assert.Single(layout.GetBolts(HeatSinkSlot.HeatSink1));
        var oldLayout = System.Text.Json.JsonSerializer.Deserialize<PcbLayout>(
            """{"BoltPoints":[{"Number":1,"X":5,"Y":6}],"Origins":{"HeatSink1":{"X":100,"Y":200}}}""");
        Assert.Empty(oldLayout!.BoltPoints);
    }

    [Fact]
    public void TeachingDefinitionsKeepBufferEditsStagedAndUpdateTheOwningSettings()
    {
        var supply = new PcbSupplySettings { CarrierY = 7 };
        var placement = new PcbPlacementHandlerSettings();
        var buffer = new PcbBufferSettings();
        var recipe = new PcbSupplyRecipe();
        var supplyHandoff = supply.BufferHandoffPosition;
        var placementHandoff = placement.BufferHandoffPosition;
        TeachingPosition[] definitions = [
            .. supply.GetTeachingPositions(recipe),
            .. placement.GetTeachingPositions(),
            placement.GetBufferTeachingPosition(),
            .. buffer.GetTeachingPositions(),
        ];
        var points = definitions.Select(p => new TeachingPoint(p)).ToArray();
        Assert.Equal(12, points.Length);
        var staged = points.Where(p => p.Position.Storage == TeachingStorage.Buffer).ToArray();
        foreach (var point in staged)
            point.Teach(10, 20, 30);
        Assert.Equal(0, supply.BufferHandoffPosition.X);
        Assert.Equal(0, placement.BufferHandoffPosition.X);
        Assert.Equal(0, buffer.SupplyBoundary1);

        var carrierY = points.Single(p => p.Position.Target == TeachingTarget.SupplyCarrierY);
        carrierY.Teach(0, 45, 0);
        carrierY.Apply();
        foreach (var point in points.Where(p => p.Position.Storage != TeachingStorage.Buffer))
            point.Refresh();
        var picks = points.Where(p => p.Position.Storage == TeachingStorage.Recipe).ToArray();
        Assert.All(picks, p => Assert.Equal(45, p.Y));
        Assert.Equal(10, staged[0].X);
        Assert.Equal(0, supply.BufferHandoffPosition.X);
        Assert.Same(supply, carrierY.Position.Setting);

        foreach (var point in picks)
        {
            point.Teach(12, 999, 34);
            point.Apply();
        }

        Assert.Equal((12, 34), (recipe.Pcb1PickPosition.X, recipe.Pcb1PickPosition.Z));
        Assert.Equal((12, 34), (recipe.Pcb2PickPosition.X, recipe.Pcb2PickPosition.Z));
        Assert.Equal(45, supply.CarrierY);
        foreach (var point in staged)
            point.Apply();
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
            SafeZ = 5,
            ShootingHead = new()
            {
                UpperLeftLocatingPin = new() { X = 300, Y = 400 },
                LowerRightLocatingPin = new() { X = 200, Y = 500 },
            },
        };
        var bolt = new BoltPoint { Number = 1 };
        var recipe = new PcbLayout();
        recipe.BoltPoints = [bolt];
        var position = new TeachingPoint(
            fastening.GetTeachingPositions(recipe, HeatSinkSlot.HeatSink1, reference)
                .Single(p => p.Target == TeachingTarget.BoltPosition));
        Assert.False(position.Position.HasPosition);
        Assert.False(position.Position.CanTeach);
        Assert.Equal(TeachMode.XYOnly, position.Position.Mode);
        var relative = CarrierCoordinates.FromMachine(new() { X = 110, Y = 220 }, reference.UpperLeftLocatingPin);
        bolt.X = relative.X;
        bolt.Y = relative.Y;
        Assert.Equal((10d, 20d), (bolt.X, bolt.Y));
        position.Refresh();
        Assert.True(position.Position.HasPosition);
        Assert.Equal(280, position.X, 6);
        Assert.Equal(410, position.Y, 6);
        Assert.Equal(fastening.SafeZ, position.Z);
        fastening.SafeZ = 7;
        position.Refresh();
        Assert.Equal(7, position.Z);
        Assert.All(
            recipe.GetBolts(),
            bolt => Assert.Equal(7, fastening.GetBoltPosition(bolt, reference).Z));

        var lowerRight = reference.LowerRightLocatingPin;
        var upperLeft = new TeachingPoint(
            pins.Single(p => p.Target == TeachingTarget.CarrierUpperLeftLocatingPin));
        upperLeft.Teach(105, 205, 0);
        upperLeft.Apply();
        Assert.Same(lowerRight, reference.LowerRightLocatingPin);
        var camera = inspection.GetBoltPosition(recipe.GetBolts(HeatSinkSlot.HeatSink1).Single(), reference);
        Assert.Equal((115, 225), (camera.X, camera.Y));
        Assert.Equal((10d, 20d), (bolt.X, bolt.Y));
        Assert.Same(reference, upperLeft.Position.Setting);
        var transfer = new NgCarrierTransferSettings();
        Assert.All(transfer.GetTeachingPositions(), p => Assert.Same(transfer, p.Setting));

        recipe.BoltPoints = [
            new() { Number = 2, Head = FasteningHead.Pickup },
            bolt,
            new() { Number = 3, Head = FasteningHead.Pickup },
        ];
        var ordered = fastening.GetTeachingPositions(recipe, HeatSinkSlot.HeatSink1, reference);
        Assert.Equal(TeachingTarget.SafeZ, ordered[0].Target);
        Assert.Equal(
            new[] { 1, 2, 3 },
            ordered.Where(point => point.Bolt is not null).Select(point => point.Bolt!.Number));
        Assert.Equal(new[] { 2, 1, 3 }, recipe.BoltPoints.Select(point => point.Number));
    }

    [Fact]
    public async Task RecipeSaveAndLoadKeepTheirOperationActive()
    {
        var recipe = new Recipe
        {
            Name = $"RecipeActivity-{Guid.NewGuid():N}",
            BoltInspection = new() { LightLevel = 192, MinimumMaskRatio = 0.02 },
        };
        var savedName = recipe.Name;
        var operations = new OperationCancellation();
        var (database, store) = CreateStore();
        var selection = new RecipeSelectionSettings();
        var editor = new RecipeEditor(store, selection, recipe, operations);
        bool? activeAtChange = null;
        string? selectedAtChange = null;
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RecipeEditor.ActiveName))
            {
                activeAtChange = operations.HasActiveOperations;
                selectedAtChange = database.LoadSettings().Get<RecipeSelectionSettings>().LastRecipeName;
            }
        };

        editor.Name = " ";
        Assert.False(editor.CanSave);
        Assert.False(editor.SaveCommand.CanExecute(null));
        await editor.SaveAsync(); // Direct autosave calls use the same name check.
        Assert.NotNull(editor.Error);
        Assert.False(await editor.SaveCarrierImagesAsync([]));
        Assert.Empty(database.GetRecipeNames());
        Assert.Equal(savedName, recipe.Name);
        editor.Name = savedName;

        using var cancellation = new CancellationTokenSource();
        void CancelWhenStarted()
        {
            if (operations.HasActiveOperations)
                cancellation.Cancel();
        }

        operations.ActivityChanged += CancelWhenStarted;
        await editor.SaveAsync(cancellation.Token);
        operations.ActivityChanged -= CancelWhenStarted;
        Assert.Null(editor.Error);
        Assert.Empty(database.GetRecipeNames());
        Assert.Null(selection.LastRecipeName);
        Assert.Null(database.LoadSettings().Get<RecipeSelectionSettings>().LastRecipeName);
        Assert.False(operations.HasActiveOperations);

        await editor.SaveAsync();
        Assert.Null(editor.Error);
        Assert.True(activeAtChange);
        Assert.Equal(savedName, selectedAtChange);
        Assert.Equal(savedName, selection.LastRecipeName);
        Assert.Contains(savedName, editor.Recipes);
        Assert.False(operations.HasActiveOperations);

        editor.NewCommand.Execute(null);
        var defaults = new BoltInspectionRecipe();
        Assert.Equal(defaults.LightLevel, recipe.BoltInspection.LightLevel);
        Assert.Equal(defaults.MinimumMaskRatio, recipe.BoltInspection.MinimumMaskRatio);

        activeAtChange = null;
        await editor.LoadCommand.ExecuteAsync(savedName);
        Assert.True(activeAtChange);
        Assert.False(operations.HasActiveOperations);
        Assert.Equal(192, recipe.BoltInspection.LightLevel);
        Assert.Equal(0.02, recipe.BoltInspection.MinimumMaskRatio);

        editor.Name = "Other";
        await editor.SaveAsync();
        editor.NewCommand.Execute(null);
        var changed = false;
        editor.Changed += () => changed = true;
        using var connection = new SqliteConnection($"Data Source={database.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER FailSelection BEFORE UPDATE ON Settings WHEN NEW.Key = 'RecipeSelectionSettings' BEGIN SELECT RAISE(ABORT, 'test failure'); END";
        command.ExecuteNonQuery();
        await editor.LoadCommand.ExecuteAsync(savedName);
        Assert.Contains("test failure", editor.Error);
        Assert.False(changed);
        Assert.Equal("New", editor.ActiveName);
        Assert.Equal("New", editor.Name);
        Assert.Equal(defaults.LightLevel, recipe.BoltInspection.LightLevel);
        Assert.Equal("Other", selection.LastRecipeName);
        Assert.Equal("Other", database.LoadSettings().Get<RecipeSelectionSettings>().LastRecipeName);
        Assert.False(operations.HasActiveOperations);

        command.CommandText = "DROP TRIGGER FailSelection";
        command.ExecuteNonQuery();
        await editor.LoadCommand.ExecuteAsync(savedName);
        Assert.Null(editor.Error);
        Assert.True(changed);
        Assert.True(activeAtChange);
        Assert.Equal(savedName, selectedAtChange);
        Assert.Equal(savedName, editor.ActiveName);
        Assert.Equal(savedName, selection.LastRecipeName);
        Assert.Equal(192, recipe.BoltInspection.LightLevel);
        Assert.False(operations.HasActiveOperations);
    }

    [Fact]
    public async Task RecipeImagesAndSaveAsAreAtomic()
    {
        var (database, store) = CreateStore();
        var source = new Recipe { Name = "Source" };
        var target = new Recipe { Name = "Target" };
        var sourceEditor = new RecipeEditor(store, new(), source, new());
        var targetSelection = new RecipeSelectionSettings();
        var targetEditor = new RecipeEditor(store, targetSelection, target, new());
        CarrierImageTileView[] Images(double x, byte value)
        {
            return [
                new(new CarrierImageTile
                {
                    Number = 1,
                    Center = new() { X = x },
                    Region = new(0, 0, 1, 1),
                    BoltNumber = 3,
                    HeatSink = HeatSinkSlot.HeatSink2,
                }, Image(value)),
                new(new CarrierImageTile
                {
                    Number = 2,
                    Center = new() { X = x + 1 },
                    Region = new(0, 0, 1, 1),
                    IsBarcode = true,
                }, Image(value)),
            ];
        }

        static BitmapSource Image(byte value)
        {
            var image = BitmapSource.Create(
                1,
                1,
                96,
                96,
                PixelFormats.Bgr24,
                null,
                new byte[] { value, (byte)(value + 1), (byte)(value + 2) },
                3);
            image.Freeze();
            return image;
        }

        var sourceImages = Images(1, 10);
        Assert.True(await sourceEditor.SaveCarrierImagesAsync(sourceImages));
        Assert.Same(sourceImages[0].Metadata, source.CarrierImages[0]);
        Assert.Same(sourceImages[1].Metadata, source.CarrierImages[1]);
        var reloaded = await Task.Factory.StartNew(
            () => Images(1, 10).Select(tile => tile with
            {
                Image = store.LoadRecipeImage("Source", tile.Metadata.Number),
            }).ToArray(),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.All(reloaded, tile => Assert.True(tile.Image.IsFrozen));
        Assert.Equal("FOV 1", reloaded[0].ToString());
        await store.SaveRecipeImagesAsync(
            source, source.Name, reloaded, new() { LastRecipeName = source.Name });
        var pixels = new byte[3];
        store.LoadRecipeImage("Source", 1).CopyPixels(pixels, 3, 0);
        Assert.Equal(new byte[] { 10, 11, 12 }, pixels);
        var savedFov = (await store.LoadRecipeAsync("Source")).CarrierImages[0];
        Assert.Equal(new PixelRegion(0, 0, 1, 1), savedFov.Region);
        Assert.Equal(3, savedFov.BoltNumber);
        Assert.Equal(HeatSinkSlot.HeatSink2, savedFov.HeatSink);
        var loadedImages = await sourceEditor.LoadCarrierImagesAsync();
        Assert.Same(source.CarrierImages[0], loadedImages[0].Metadata);
        Assert.Equal(savedFov.Region, loadedImages[0].Metadata.Region);
        var savedBarcode = (await store.LoadRecipeAsync("Source")).CarrierImages[1];
        Assert.True(savedBarcode.IsBarcode);
        Assert.Null(savedBarcode.BoltNumber);
        Assert.Equal(new PixelRegion(0, 0, 1, 1), savedBarcode.Region);
        Assert.Same(source.CarrierImages[1], loadedImages[1].Metadata);
        Assert.True(loadedImages[1].Metadata.IsBarcode);
        Assert.Equal("Source", database.LoadSettings().Get<RecipeSelectionSettings>().LastRecipeName);
        Assert.True(await targetEditor.SaveCarrierImagesAsync(Images(10, 100)));
        Assert.Equal("Target", database.LoadSettings().Get<RecipeSelectionSettings>().LastRecipeName);
        await sourceEditor.SaveAsync();
        Assert.Null(sourceEditor.Error);
        var original = database.LoadRecipeImage(target.Name, 1);
        using var connection = new SqliteConnection($"Data Source={database.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "CREATE TRIGGER FailSelection BEFORE UPDATE ON Settings WHEN NEW.Key = 'RecipeSelectionSettings' BEGIN SELECT RAISE(ABORT, 'selection failure'); END";
        command.ExecuteNonQuery();
        targetEditor.Name = "Rejected";
        await targetEditor.SaveAsync();
        Assert.Contains("selection failure", targetEditor.Error);
        Assert.Equal("Target", targetEditor.ActiveName);
        Assert.Equal("Target", targetSelection.LastRecipeName);
        Assert.DoesNotContain("Rejected", database.GetRecipeNames());
        Assert.DoesNotContain("Rejected", targetEditor.Recipes);
        Assert.Throws<InvalidOperationException>(() => database.LoadRecipeImage("Rejected", 1));

        targetEditor.Name = "Target";
        Assert.False(await targetEditor.SaveCarrierImagesAsync(Images(30, 200)));
        Assert.Contains("selection failure", targetEditor.Error);
        Assert.Equal([10d, 11d], target.CarrierImages.Select(tile => tile.Center.X));
        Assert.Equal(
            [10d, 11d],
            (await store.LoadRecipeAsync("Target")).CarrierImages.Select(tile => tile.Center.X));
        Assert.Equal(original, database.LoadRecipeImage("Target", 1));
        Assert.Equal("Source", database.LoadSettings().Get<RecipeSelectionSettings>().LastRecipeName);
        command.CommandText = "DROP TRIGGER FailSelection";
        command.ExecuteNonQuery();

        command.CommandText = "CREATE TRIGGER FailImage BEFORE INSERT ON RecipeImages WHEN NEW.Number = 2 BEGIN SELECT RAISE(ABORT, 'test failure'); END";
        command.ExecuteNonQuery();
        Assert.False(await targetEditor.SaveCarrierImagesAsync(Images(30, 200)));
        Assert.Contains("test failure", targetEditor.Error);
        Assert.Equal([10d, 11d], target.CarrierImages.Select(tile => tile.Center.X));
        Assert.Equal(
            [10d, 11d],
            (await store.LoadRecipeAsync("Target")).CarrierImages.Select(tile => tile.Center.X));
        Assert.Equal(original, database.LoadRecipeImage("Target", 1));

        sourceEditor.Name = target.Name;
        await sourceEditor.SaveAsync();
        Assert.Contains("test failure", sourceEditor.Error);
        Assert.Equal("Source", sourceEditor.ActiveName);
        Assert.Equal(original, database.LoadRecipeImage("Target", 1));
        command.CommandText = "DROP TRIGGER FailImage";
        command.ExecuteNonQuery();
        await sourceEditor.SaveAsync();
        Assert.Null(sourceEditor.Error);
        Assert.Equal("Target", sourceEditor.ActiveName);
        Assert.Equal(
            [1d, 2d],
            (await store.LoadRecipeAsync("Target")).CarrierImages.Select(tile => tile.Center.X));
        Assert.Equal(database.LoadRecipeImage("Source", 1), database.LoadRecipeImage("Target", 1));
        Assert.Equal(2, (await sourceEditor.LoadCarrierImagesAsync()).Length);

        using var cancellation = new CancellationTokenSource();
        IEnumerable<RecipeImage> CancelAfterFirstImage()
        {
            yield return new(1, [200]);
            cancellation.Cancel();
            yield return new(2, [200]);
        }

        var beforeCancel = database.LoadRecipeImage("Target", 1);
        Assert.Throws<OperationCanceledException>(
            () => database.SaveRecipe(
                "Target",
                new Recipe { Name = "Cancelled" },
                [1, 2],
                images: CancelAfterFirstImage(),
                cancellationToken: cancellation.Token));
        Assert.Equal("Target", (await store.LoadRecipeAsync("Target")).Name);
        Assert.Equal(beforeCancel, database.LoadRecipeImage("Target", 1));
        Assert.False(await sourceEditor.SaveCarrierImagesAsync(Images(30, 200), cancellation.Token));
        Assert.Null(sourceEditor.Error);
        Assert.Equal([1d, 2d], source.CarrierImages.Select(tile => tile.Center.X));

        await sourceEditor.SaveCarrierImagesAsync(Images(50, 200).Take(1).ToArray());
        Assert.Single((await store.LoadRecipeAsync("Target")).CarrierImages);
        Assert.Throws<InvalidOperationException>(() => database.LoadRecipeImage("Target", 2));
        Assert.Equal(2, (await store.LoadRecipeAsync("Source")).CarrierImages.Count);
    }

    private static (MachineStore Database, RecipeStore Store) CreateStore()
    {
        var database = VirtualTest.OpenMachineStore();
        return (database, new RecipeStore(database));
    }
}
