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
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.UI;
using IBTM.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class RecipeTests
{
    [Theory]
    [InlineData(100, 200, 350, -200)]
    [InlineData(200, 400, 450, 0)]
    [InlineData(150, 300, 400, -100)]
    [InlineData(110, 200, 360, -200)]
    [InlineData(110, 220, 360, -180)]
    public void FasteningCoordinatesAddCenterOffsetWithoutScaling(
        double cameraX, double cameraY, double expectedX, double expectedY)
    {
        var reference = new CarrierReferenceSettings
        {
            UpperLeftLocatingPin = new() { X = 100, Y = 200 },
            LowerRightLocatingPin = new() { X = 200, Y = 400 },
        };
        var settings = new BoltFasteningSettings
        {
            ShootingHead = new()
            {
                UpperLeftLocatingPin = new() { X = 300, Y = 400 },
                LowerRightLocatingPin = new() { X = 500, Y = 1000 },
                FasteningZ = 12,
            },
        };
        var bolt = new BoltPoint { X = cameraX, Y = cameraY };

        var position = settings.GetBoltPosition(bolt, reference);

        Assert.Equal(expectedX, position.X, 6);
        Assert.Equal(expectedY, position.Y, 6);
        Assert.Equal(12, position.Z);
        Assert.Equal((cameraX, cameraY), (bolt.X, bolt.Y));
    }

    [Theory]
    [InlineData(0, 100, 27, 419)]
    [InlineData(100, 0, -23, 369)]
    [InlineData(0, 0, 27, 369)]
    public void FasteningCenterOffsetDoesNotRequireReferenceAxisSpans(
        double spanX, double spanY, double expectedX, double expectedY)
    {
        var upper = new AxisPosition { X = 100, Y = 200 };
        var lower = new AxisPosition { X = upper.X + spanX, Y = upper.Y + spanY };

        Assert.True(CarrierCoordinates.IsDefined(upper, lower));
        var position = CarrierCoordinates.ToMachine(
            new() { X = 107, Y = 209, Z = 12 }, upper, lower,
            new() { X = -20, Y = 30 }, new() { X = 60, Y = 50 });
        Assert.Equal((expectedX, expectedY, 12d), (position.X, position.Y, position.Z));
    }

    [Fact]
    public void FasteningCenterOffsetRequiresRecordedReferences()
    {
        var upper = new AxisPosition { X = 100, Y = 200 };
        var lower = new AxisPosition { X = 200, Y = 400 };

        Assert.False(CarrierCoordinates.IsDefined(null, lower));
        Assert.False(CarrierCoordinates.IsDefined(upper, null));
        Assert.Throws<InvalidOperationException>(() =>
            CarrierCoordinates.ToMachine(new(), upper, null!, upper, lower));
        Assert.Throws<InvalidOperationException>(() =>
            CarrierCoordinates.ToMachine(new(), upper, lower, null!, lower));
    }

    [Fact]
    public async Task InvalidInspectionRecipeKeepsTheActiveRecipeUntilCorrected()
    {
        var database = VirtualTest.OpenMachineStore();
        var selection = new RecipeSelectionSettings();
        var recipes = new RecipeManager(database, selection) { Current = { Name = "Active" } };
        var recipe = recipes.Current;
        var editor = new RecipeEditor(recipes, new());
        await editor.SaveAsync();
        var currentInspection = recipe.BoltInspection;
        var corrected = new Recipe
        {
            Name = "Other",
            BoltInspection = new() { LightLevel = 192 },
        };
        database.SaveRecipe(corrected.Name, corrected, []);
        using (var connection = new SqliteConnection($"Data Source={database.DatabaseFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Recipes SET Value = $value WHERE Name = 'Other'";
            command.Parameters.AddWithValue("$value", "{\"Name\":\"Other\",\"BoltInspection\":{\"LightLevel\":256}}");
            command.ExecuteNonQuery();
        }

        await editor.LoadCommand.ExecuteAsync("Other");
        Assert.NotNull(editor.Error);
        Assert.Same(currentInspection, recipe.BoltInspection);
        Assert.Equal("Active", editor.ActiveName);
        Assert.Equal("Active", selection.LastRecipeName);
        Assert.Equal("Active", database.LoadSettings().Get<RecipeSelectionSettings>().LastRecipeName);

        database.SaveRecipe(corrected.Name, corrected, []);
        await editor.LoadCommand.ExecuteAsync("Other");
        Assert.Null(editor.Error);
        Assert.Equal("Other", selection.LastRecipeName);
        Assert.Equal(192, recipe.BoltInspection.LightLevel);
    }

    [Fact]
    public async Task HeatSinkBoltTeachingAndStorageAreIndependent()
    {
        var recipe = new Recipe { Name = "Independent heat sinks" };
        var layout = recipe.Pcb;
        layout.BoltPoints.Add(new()
        {
            Number = 1,
            HeatSink = HeatSinkSlot.HeatSink1,
            X = 13,
            Y = 24,
            BrightnessThreshold = 140,
            MinimumBrightRatio = 0.2,
        });
        layout.BoltPoints.Add(new()
        {
            Number = 1,
            HeatSink = HeatSinkSlot.HeatSink2,
            X = 73,
            Y = 29,
            BrightnessThreshold = 210,
            MinimumBrightRatio = 0.7,
        });
        var targets = layout.BoltPoints.ToArray();
        Assert.NotSame(targets[0], targets[1]);
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
                LowerRightLocatingPin = new() { X = 400, Y = 500 },
            },
        };
        targets[1].X = 175;
        targets[1].Y = 230;
        Assert.Equal((13d, 24d), (targets[0].X, targets[0].Y));
        Assert.Equal((175d, 230d), (targets[1].X, targets[1].Y));
        var firstCamera = inspection.GetBoltPosition(targets[0]);
        var secondHead = fastening.GetBoltPosition(targets[1], pins);
        Assert.Equal((13, 24), (firstCamera.X, firstCamera.Y));
        Assert.Equal(375, secondHead.X, 6);
        Assert.Equal(30, secondHead.Y, 6);

        var database = VirtualTest.OpenMachineStore();
        var recipes = new RecipeManager(database, new());
        recipes.Current.ReplaceWith(recipe);
        var editor = new RecipeEditor(recipes, new());
        await editor.SaveAsync();
        var loaded = database.LoadRecipe<Recipe>(recipe.Name);
        Assert.Equal(2, loaded.Pcb.BoltPoints.Count);
        Assert.Equal(2, loaded.Pcb.BoltPoints.Count());
        Assert.Equal(13d, loaded.Pcb.GetBolts(HeatSinkSlot.HeatSink1).Single().X);
        Assert.Equal(175d, loaded.Pcb.GetBolts(HeatSinkSlot.HeatSink2).Single().X);
        Assert.Equal(140, loaded.Pcb.GetBolts(HeatSinkSlot.HeatSink1).Single().BrightnessThreshold);
        Assert.Equal(0.2, loaded.Pcb.GetBolts(HeatSinkSlot.HeatSink1).Single().MinimumBrightRatio);
        Assert.Equal(210, loaded.Pcb.GetBolts(HeatSinkSlot.HeatSink2).Single().BrightnessThreshold);
        Assert.Equal(0.7, loaded.Pcb.GetBolts(HeatSinkSlot.HeatSink2).Single().MinimumBrightRatio);
        layout.BoltPoints.Remove(targets[1]);
        Assert.Empty(layout.GetBolts(HeatSinkSlot.HeatSink2));
        Assert.Single(layout.GetBolts(HeatSinkSlot.HeatSink1));
        var oldLayout = System.Text.Json.JsonSerializer.Deserialize<PcbLayout>(
            """{"BoltPoints":[{"Number":1,"X":5,"Y":6}],"Origins":{"HeatSink1":{"X":100,"Y":200}}}""");
        Assert.Empty(oldLayout!.BoltPoints);
    }

    [Fact]
    public void SupplyHandoffStoresItsOwnZAndDropsTheObsoleteClearZ()
    {
        var supply = System.Text.Json.JsonSerializer.Deserialize<PcbSupplySettings>(
            """{"RotationZ":3,"BufferHandoffPosition":{"X":50,"Y":10,"Z":8},"BufferClearZ":12}""")!;
        var definition = supply.GetTeachingPositions(new())
            .Single(point => point.Target == TeachingTarget.SupplyHandoff);
        Assert.Equal(TeachMode.Full, definition.Mode);
        Assert.Equal(8, supply.HandoffPosition.Z);
        definition.Apply(new() { X = 60, Y = 20, Z = 99 });
        Assert.Equal((60, 20), (supply.HandoffPosition.X, supply.HandoffPosition.Y));
        Assert.Equal(3, supply.RotationZ);
        Assert.Equal(99, supply.HandoffPosition.Z);

        using var saved = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(supply));
        Assert.False(saved.RootElement.TryGetProperty("BufferClearZ", out _));
        Assert.Equal(99, saved.RootElement.GetProperty("BufferHandoffPosition").GetProperty("Z").GetDouble());
    }

    [Fact]
    public void PlacementReceiveZIsTaughtAndStoredSeparatelyFromStandby()
    {
        var placement = System.Text.Json.JsonSerializer.Deserialize<PcbPlacementHandlerSettings>(
            """{"BufferEntryZ":3,"BufferHandoffPosition":{"X":50,"Y":10,"Z":8}}""")!;
        var definition = placement.GetHandoffTeachingPosition();
        Assert.Equal(TeachMode.Full, definition.Mode);
        Assert.Equal(8, placement.HandoffPosition.Z);
        definition.Apply(new() { X = 60, Y = 20, Z = 9 });
        Assert.Equal(9, placement.HandoffPosition.Z);

        var receive = placement.GetTeachingPositions(new())
            .Single(point => point.Target == TeachingTarget.PlacementReceiveZ);
        Assert.Null(placement.ReceiveZ);
        Assert.False(receive.HasPosition);
        Assert.Equal(TeachMode.ZOnly, receive.Mode);
        Assert.Equal(TeachingStorage.Machine, receive.Storage);
        receive.Apply(new() { X = 100, Y = 200, Z = 12 });
        Assert.Equal(12, placement.ReceiveZ);
        Assert.True(receive.HasPosition);
        Assert.Equal((60, 20, 9), (placement.HandoffPosition.X, placement.HandoffPosition.Y, placement.HandoffPosition.Z));

        using var saved = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(placement));
        Assert.False(saved.RootElement.TryGetProperty("BufferEntryZ", out _));
        Assert.Equal(12, saved.RootElement.GetProperty(nameof(placement.ReceiveZ)).GetDouble());
        Assert.Equal(12, System.Text.Json.JsonSerializer.Deserialize<PcbPlacementHandlerSettings>(saved.RootElement)!.ReceiveZ);
    }

    [Fact]
    public void TeachingRecordsOwnerCoordinatesWithoutAStagedCopy()
    {
        var supply = new PcbSupplySettings();
        var placement = new PcbPlacementHandlerSettings();
        var recipe = new PcbSupplyRecipe();
        var supplyHandoff = supply.HandoffPosition;
        var placementHandoff = placement.HandoffPosition;
        TeachingPosition[] definitions = [
            .. supply.GetTeachingPositions(recipe),
            placement.GetHandoffTeachingPosition(),
        ];
        var points = definitions.Select(p => new TeachingPoint(p)).ToArray();
        Assert.Equal(5, points.Length);
        var handoffs = points.Where(p => p.Position.Storage == TeachingStorage.Handoff).ToArray();
        foreach (var point in handoffs)
            point.Teach(10, 20, 30);
        Assert.Equal(10, supply.HandoffPosition.X);
        Assert.Equal(10, placement.HandoffPosition.X);

        var picks = points.Where(p => p.Position.Storage == TeachingStorage.Recipe).ToArray();
        Assert.All(picks, p => Assert.Equal(TeachMode.Full, p.Position.Mode));
        Assert.Equal(10, handoffs[0].X);
        var moveTarget = handoffs[0].Read();
        moveTarget.Z = 999;
        handoffs[0].Refresh();
        Assert.Equal(30, supply.HandoffPosition.Z);
        Assert.Equal(30, handoffs[0].Z);

        picks[0].Teach(12, 45, 34);
        picks[1].Teach(22, 65, 44);

        Assert.Equal((12d, 45d, 34d), (recipe.Pcb1PickPosition.X, recipe.Pcb1PickPosition.Y!.Value, recipe.Pcb1PickPosition.Z));
        Assert.Equal((22d, 65d, 44d), (recipe.Pcb2PickPosition.X, recipe.Pcb2PickPosition.Y!.Value, recipe.Pcb2PickPosition.Z));
        var saved = System.Text.Json.JsonSerializer.Serialize(recipe);
        var restored = System.Text.Json.JsonSerializer.Deserialize<PcbSupplyRecipe>(saved)!;
        Assert.Equal(45, restored.Pcb1PickPosition.Y);
        Assert.Equal(65, restored.Pcb2PickPosition.Y);
        Assert.Same(supplyHandoff, supply.HandoffPosition);
        Assert.Same(placementHandoff, placement.HandoffPosition);
        Assert.Equal((10, 20, 30), (supplyHandoff.X, supplyHandoff.Y, supplyHandoff.Z));
        Assert.Equal(TeachMode.Full,
            handoffs.Single(point => point.Position.Target == TeachingTarget.SupplyHandoff).Position.Mode);
        Assert.Equal(0, supply.RotationZ);
        Assert.Equal((10, 20, 30), (placementHandoff.X, placementHandoff.Y, placementHandoff.Z));
        Assert.Equal(2, handoffs.Select(p => p.Position.Setting).Distinct().Count());
    }

    [Fact]
    public void NgPickupTeachingCombinesStoredCoordinatesWithoutRewritingThem()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<NgCarrierTransferSettings>(
            """{"PickupSafeX":157.283,"CarrierPickupPosition":{"X":999,"Y":456.789,"Z":12},"ShuttlePlacePosition":{"X":146.46,"Y":1085.274}}""")!;
        var before = System.Text.Json.JsonSerializer.Serialize(settings);
        var database = VirtualTest.OpenMachineStore();
        database.SaveSettings([settings]);
        settings = database.LoadSettings().Get<NgCarrierTransferSettings>();
        var point = new TeachingPoint(settings.GetTeachingPositions()
            .Single(position => position.Target == TeachingTarget.NgCarrierPickup));

        Assert.Equal((157.283, 456.789), (point.X, point.Y));
        var target = point.Read();
        target.X = -1;
        target.Y = -2;
        point.Refresh();
        database.SaveSettings([settings]);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(
            database.LoadSettings().Get<NgCarrierTransferSettings>()));

        point.Teach(160, 460, 999);
        database.SaveSettings([settings]);
        var saved = database.LoadSettings().Get<NgCarrierTransferSettings>();
        var pickup = saved.GetCarrierPickupPosition()!;
        Assert.Equal((160, 460), (pickup.X, pickup.Y));
        Assert.Equal((999, 12), (saved.CarrierPickupPosition.X, saved.CarrierPickupPosition.Z));
        Assert.Equal((146.46, 1085.274), (saved.ShuttlePlacePosition.X, saved.ShuttlePlacePosition.Y));
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
            PickupPosition = new() { Z = 10 },
            ShootingHead = new()
            {
                FasteningZ = 12,
                UpperLeftLocatingPin = new() { X = 300, Y = 400 },
                LowerRightLocatingPin = new() { X = 400, Y = 500 },
            },
            PickupHead = new()
            {
                FasteningZ = 16,
                UpperLeftLocatingPin = new() { X = 300, Y = 400 },
                LowerRightLocatingPin = new() { X = 400, Y = 500 },
            },
        };
        var bolt = new BoltPoint { Number = 1 };
        var recipe = new PcbLayout();
        recipe.BoltPoints = [bolt];
        var position = new TeachingPoint(
            fastening.GetTeachingPositions(recipe, HeatSinkSlot.HeatSink1, reference)
                .Single(p => p.Target == TeachingTarget.BoltPosition));
        Assert.False(position.Position.HasPosition);
        Assert.False(position.Position.IsTeachAllowed);
        Assert.Equal(TeachMode.Full, position.Position.Mode);
        bolt.X = 110;
        bolt.Y = 220;
        position.Refresh();
        Assert.True(position.Position.HasPosition);
        Assert.Equal(310, position.X, 6);
        Assert.Equal(20, position.Y, 6);
        Assert.Equal(fastening.ShootingHead.FasteningZ, position.Z);
        fastening.SafeZ = 7;
        position.Refresh();
        Assert.Equal(12, position.Z);
        var shootingZ = new TeachingPoint(
            fastening.GetTeachingPositions(recipe, HeatSinkSlot.HeatSink1, reference)
                .Single(point => point.Target == TeachingTarget.ShootingHeadFasteningZ));
        Assert.Equal(TeachMode.ZOnly, shootingZ.Position.Mode);
        Assert.Same(fastening, shootingZ.Position.Setting);
        shootingZ.Teach(999, 999, 14);
        Assert.Equal(7, fastening.SafeZ);
        Assert.Equal(14, fastening.ShootingHead.FasteningZ);
        Assert.Equal(16, fastening.PickupHead.FasteningZ);
        Assert.All(
            recipe.BoltPoints,
            bolt => Assert.Equal(14, fastening.GetBoltPosition(bolt, reference).Z));

        var lowerRight = reference.LowerRightLocatingPin;
        var upperLeft = new TeachingPoint(
            pins.Single(p => p.Target == TeachingTarget.CarrierUpperLeftLocatingPin));
        upperLeft.Teach(105, 205, 0);
        Assert.Same(lowerRight, reference.LowerRightLocatingPin);
        var camera = inspection.GetBoltPosition(recipe.GetBolts(HeatSinkSlot.HeatSink1).Single());
        Assert.Equal((110, 220), (camera.X, camera.Y));
        Assert.Equal((110d, 220d), (bolt.X, bolt.Y));
        Assert.Same(reference, upperLeft.Position.Setting);
        var transfer = new NgCarrierTransferSettings();
        Assert.All(transfer.GetTeachingPositions(), p => Assert.Same(transfer, p.Setting));

        recipe.BoltPoints = [
            new() { Number = 2, Head = FasteningHead.Pickup, X = 10, Y = 20 },
            bolt,
            new() { Number = 3, Head = FasteningHead.Pickup, X = 20, Y = 30 },
        ];
        var ordered = fastening.GetTeachingPositions(recipe, HeatSinkSlot.HeatSink1, reference);
        Assert.Equal(TeachingTarget.SafeZ, ordered[0].Target);
        Assert.Equal(TeachingTarget.ShootingHeadFasteningZ, ordered[1].Target);
        var pickupZ = new TeachingPoint(
            ordered.Single(point => point.Target == TeachingTarget.PickupHeadFasteningZ));
        Assert.Equal(TeachMode.ZOnly, pickupZ.Position.Mode);
        Assert.Same(fastening, pickupZ.Position.Setting);
        pickupZ.Teach(999, 999, 18);
        Assert.Equal(7, fastening.SafeZ);
        Assert.Equal(14, fastening.ShootingHead.FasteningZ);
        Assert.Equal(18, fastening.PickupHead.FasteningZ);
        Assert.Equal(10, fastening.PickupPosition.Z);
        Assert.All(recipe.BoltPoints, target => Assert.Equal(
            target.Head == FasteningHead.Pickup ? 18 : 14,
            fastening.GetBoltPosition(target, reference).Z));
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
            BoltInspection = new() { LightLevel = 192, BrightnessThreshold = 180, MinimumBrightRatio = 0.02 },
        };
        var savedName = recipe.Name;
        var operations = new OperationCancellation();
        var database = VirtualTest.OpenMachineStore();
        var selection = new RecipeSelectionSettings();
        var recipes = new RecipeManager(database, selection);
        recipes.Current.ReplaceWith(recipe);
        recipe = recipes.Current;
        var editor = new RecipeEditor(recipes, operations);
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
        Assert.False(editor.IsSaveAllowed);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.False(await editor.SaveAsync()); // Direct autosave calls use the same name check.
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
        Assert.False(await editor.SaveAsync(cancellation.Token));
        operations.ActivityChanged -= CancelWhenStarted;
        Assert.Null(editor.Error);
        Assert.Empty(database.GetRecipeNames());
        Assert.Null(selection.LastRecipeName);
        Assert.Null(database.LoadSettings().Get<RecipeSelectionSettings>().LastRecipeName);
        Assert.False(operations.HasActiveOperations);

        Assert.True(await editor.SaveAsync());
        Assert.Null(editor.Error);
        Assert.True(activeAtChange);
        Assert.Equal(savedName, selectedAtChange);
        Assert.Equal(savedName, selection.LastRecipeName);
        Assert.Contains(savedName, editor.Recipes);
        Assert.False(operations.HasActiveOperations);

        editor.NewCommand.Execute(null);
        var defaults = new BoltInspectionRecipe();
        Assert.Equal(defaults.LightLevel, recipe.BoltInspection.LightLevel);
        Assert.Equal(defaults.BrightnessThreshold, recipe.BoltInspection.BrightnessThreshold);
        Assert.Equal(defaults.MinimumBrightRatio, recipe.BoltInspection.MinimumBrightRatio);

        activeAtChange = null;
        await editor.LoadCommand.ExecuteAsync(savedName);
        Assert.True(activeAtChange);
        Assert.False(operations.HasActiveOperations);
        Assert.Equal(192, recipe.BoltInspection.LightLevel);
        Assert.Equal(180, recipe.BoltInspection.BrightnessThreshold);
        Assert.Equal(0.02, recipe.BoltInspection.MinimumBrightRatio);

        editor.Name = "Other";
        await editor.SaveAsync();
        editor.NewCommand.Execute(null);
        var changed = false;
        recipes.Changed += () => changed = true;
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
        var database = VirtualTest.OpenMachineStore();
        var sourceRecipes = new RecipeManager(database, new()) { Current = { Name = "Source" } };
        var source = sourceRecipes.Current;
        var sourceEditor = new RecipeEditor(sourceRecipes, new());
        var targetSelection = new RecipeSelectionSettings();
        var targetRecipes = new RecipeManager(database, targetSelection) { Current = { Name = "Target" } };
        var target = targetRecipes.Current;
        var targetEditor = new RecipeEditor(targetRecipes, new());
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
            () => sourceEditor.LoadCarrierImagesAsync(),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        Assert.All(reloaded, tile => Assert.True(tile.Image.IsFrozen));
        Assert.Equal("FOV 1", reloaded[0].ToString());
        Assert.True(await sourceEditor.SaveCarrierImagesAsync(reloaded));
        var pixels = new byte[3];
        (await sourceEditor.LoadCarrierImagesAsync())[0].Image.CopyPixels(pixels, 3, 0);
        Assert.Equal(new byte[] { 10, 11, 12 }, pixels);
        var savedFov = database.LoadRecipe<Recipe>("Source").CarrierImages[0];
        Assert.Equal(new PixelRegion(0, 0, 1, 1), savedFov.Region);
        Assert.Equal(3, savedFov.BoltNumber);
        Assert.Equal(HeatSinkSlot.HeatSink2, savedFov.HeatSink);
        var loadedImages = await sourceEditor.LoadCarrierImagesAsync();
        Assert.Same(source.CarrierImages[0], loadedImages[0].Metadata);
        Assert.Equal(savedFov.Region, loadedImages[0].Metadata.Region);
        var savedBarcode = database.LoadRecipe<Recipe>("Source").CarrierImages[1];
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
            database.LoadRecipe<Recipe>("Target").CarrierImages.Select(tile => tile.Center.X));
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
            database.LoadRecipe<Recipe>("Target").CarrierImages.Select(tile => tile.Center.X));
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
            database.LoadRecipe<Recipe>("Target").CarrierImages.Select(tile => tile.Center.X));
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
        Assert.Equal("Target", database.LoadRecipe<Recipe>("Target").Name);
        Assert.Equal(beforeCancel, database.LoadRecipeImage("Target", 1));
        Assert.False(await sourceEditor.SaveCarrierImagesAsync(Images(30, 200), cancellation.Token));
        Assert.Null(sourceEditor.Error);
        Assert.Equal([1d, 2d], source.CarrierImages.Select(tile => tile.Center.X));

        await sourceEditor.SaveCarrierImagesAsync(Images(50, 200).Take(1).ToArray());
        Assert.Single(database.LoadRecipe<Recipe>("Target").CarrierImages);
        Assert.Throws<InvalidOperationException>(() => database.LoadRecipeImage("Target", 2));
        Assert.Equal(2, database.LoadRecipe<Recipe>("Source").CarrierImages.Count);
    }

}
