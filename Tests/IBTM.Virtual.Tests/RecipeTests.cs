using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.UI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class RecipeTests
{
    [Fact]
    public async Task RecipeListQueriesDoNotBlockEntryAndDiscardCancelledResults()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        store.SaveRecipe(new Recipe { Name = "Stored recipe" });
        var recipes = new RecipeManager(store, new());
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = store.DatabaseFile }.ToString());
        SqliteConnection.ClearPool(connection);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "PRAGMA journal_mode=DELETE; BEGIN EXCLUSIVE";
        command.ExecuteNonQuery();
        var editor = new RecipeEditorViewModel(recipes, store, new());
        var inspection = new InspectionTeachingViewModel(store, recipes,
            new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance), new(),
            NullLogger<InspectionTeachingViewModel>.Instance);
        var refresh = editor.RefreshCommand.ExecuteAsync(null);
        inspection.Activate();
        Assert.False(refresh.IsCompleted);
        Assert.False(inspection.RefreshRecipesCommand.ExecutionTask!.IsCompleted);
        Assert.Empty(editor.Recipes);
        Assert.Empty(inspection.RecipeNames);
        editor.RefreshCommand.Cancel();
        inspection.RefreshRecipesCommand.Cancel();
        var shutdown = inspection.ShutdownAsync();
        command.CommandText = "COMMIT";
        command.ExecuteNonQuery();
        await Task.WhenAll(refresh, shutdown).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(editor.Recipes);
        Assert.Empty(inspection.RecipeNames);
        Assert.Null(editor.Error);
        Assert.Null(inspection.Error);

        await editor.RefreshCommand.ExecuteAsync(null);
        inspection.Activate();
        await inspection.RefreshRecipesCommand.ExecutionTask!;
        Assert.Equal(new[] { "Stored recipe" }, editor.Recipes);
        Assert.Equal(editor.Recipes, inspection.RecipeNames);

        var paused = new VirtualTestSupport.PausedSynchronizationContext();
        var context = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(paused);
            var pending = editor.RefreshCommand.ExecuteAsync(null);
            SynchronizationContext.SetSynchronizationContext(context);
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => paused.HasPending, TimeSpan.FromSeconds(2)));
            editor.Name = "New recipe";
            Assert.True(await editor.SaveAsync(), editor.Error);
            paused.Release();
            await pending;
            await editor.RefreshCommand.ExecutionTask!;
            Assert.Equal(new[] { "New recipe", "Stored recipe" }, editor.Recipes);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
            paused.Release();
        }
        await editor.ShutdownAsync();
        await inspection.ShutdownAsync();
    }

    [Fact]
    public void MinimumTurnsFollowEachBoltThroughRecipeSaveReorderAndClear()
    {
        var first = new BoltPoint();
        var second = new BoltPoint();
        Assert.Null(first.MinimumTurns);
        first.MinimumTurns = 0;
        Assert.Null(first.MinimumTurns);
        Assert.Throws<ArgumentOutOfRangeException>(() => first.MinimumTurns = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => first.MinimumTurns = double.NaN);
        first.MinimumTurns = 3.5;
        second.MinimumTurns = 10;
        var recipe = new Recipe { Pcb = new() { BoltPoints = [first, second] } };
        var store = VirtualTestSupport.OpenMachineStore();
        store.SaveRecipe(recipe);

        var loaded = store.LoadRecipe(recipe.Name);
        Assert.Equal(3.5, loaded.Pcb.BoltPoints.Single(bolt => bolt.Id == first.Id).MinimumTurns);
        Assert.Equal(10, loaded.Pcb.BoltPoints.Single(bolt => bolt.Id == second.Id).MinimumTurns);
        loaded.Pcb.BoltPoints.Move(0, 1);
        loaded.Pcb.BoltPoints.Single(bolt => bolt.Id == first.Id).MinimumTurns = 0;
        store.SaveRecipe(loaded);
        var reopened = store.LoadRecipe(recipe.Name);
        Assert.Equal(second.Id, reopened.Pcb.BoltPoints[0].Id);
        Assert.Equal(10, reopened.Pcb.BoltPoints[0].MinimumTurns);
        Assert.Equal(first.Id, reopened.Pcb.BoltPoints[1].Id);
        Assert.Null(reopened.Pcb.BoltPoints[1].MinimumTurns);
    }

    [Fact]
    public void PcbLayoutRegistersAndKeepsItsOwnBoltCollection()
    {
        var recipe = new Recipe();
        var original = recipe.Pcb.BoltPoints;
        BindingOperations.AccessCollection(original,
            () => Assert.True(Monitor.IsEntered(original)), writeAccess: false);
        var bolt = new BoltPoint { Name = "좌상단 고정" };
        recipe.Pcb.BoltPoints = [bolt];
        Assert.Same(original, recipe.Pcb.BoltPoints);
        var loaded = JsonSerializer.Deserialize<Recipe>(JsonSerializer.Serialize(recipe))!;
        BindingOperations.AccessCollection(loaded.Pcb.BoltPoints,
            () => Assert.True(Monitor.IsEntered(loaded.Pcb.BoltPoints)), writeAccess: false);
        recipe.CopyFrom(new Recipe());
        Assert.Empty(original);
        recipe.CopyFrom(loaded);
        Assert.Same(original, recipe.Pcb.BoltPoints);
        Assert.Equal(bolt.Id, Assert.Single(original).Id);
        BindingOperations.AccessCollection(original,
            () => Assert.True(Monitor.IsEntered(original)), writeAccess: false);
    }

    [Fact]
    public void AmbiguousDataMatrixTeachingDoesNotSelectAMotionTarget()
    {
        var recipe = new Recipe
        {
            CarrierImages = [
                new() { Number = 1, IsBarcode = true, Center = new() { X = 10, Y = 20 } },
                new() { Number = 2, IsBarcode = true, Center = new() { X = 30, Y = 40 } },
            ],
        };
        var point = VirtualTestSupport.CreateTeachingPoint(
            new(TeachingTarget.DataMatrix, MotionGroup.InspectionGantry, TeachMode.Image), new(), recipe);

        Assert.Null(point.Coordinates);
        Assert.Contains("ambiguous", point.PositionLabel);
        Assert.Throws<MotionInterlockException>(() => point.MovePosition);

        recipe.CarrierImages.RemoveAt(1);
        Assert.Equal((10d, 20d), (point.MovePosition.X, point.MovePosition.Y));
    }

    [Fact]
    public void InspectionCoordinatesAndNamesSurviveSavingWithoutLinkedImages()
    {
        var first = new BoltPoint { X = 148.637, Y = 244.938 };
        var second = new BoltPoint { X = 160, Y = 250 };
        var recipe = new Recipe { Pcb = new() { BoltPoints = [first, second] } };
        var firstPoint = VirtualTestSupport.CreateTeachingPoint(
            new(TeachingTarget.BoltReference, MotionGroup.InspectionGantry, TeachMode.Image) { Bolt = first }, new(), recipe);
        var secondPoint = VirtualTestSupport.CreateTeachingPoint(
            new(TeachingTarget.BoltReference, MotionGroup.InspectionGantry, TeachMode.Image) { Bolt = second }, new(), recipe);

        Assert.Equal("Bolt 1 Inspection", firstPoint.Name);
        Assert.Equal("Bolt 2 Inspection", secondPoint.Name);
        Assert.True(firstPoint.Position.HasPosition);
        Assert.Equal((148.637, 244.938), (firstPoint.MovePosition.X, firstPoint.MovePosition.Y));
        Assert.DoesNotContain("Not taught", firstPoint.PositionLabel);
        Assert.Contains("No reference image", firstPoint.PositionLabel);

        firstPoint.BoltName = "좌상단 고정";
        var store = VirtualTestSupport.OpenMachineStore();
        store.SaveRecipe(recipe);
        var saved = store.LoadRecipe(recipe.Name);
        Assert.Equal("좌상단 고정", saved.Pcb.BoltPoints[0].Name);
        Assert.Equal(first.Id, saved.Pcb.BoltPoints[0].Id);
        Assert.Equal(second.Id, saved.Pcb.BoltPoints[1].Id);
        Assert.Equal((first.X, first.Y), (saved.Pcb.BoltPoints[0].X, saved.Pcb.BoltPoints[0].Y));
        Assert.Equal(2, saved.Pcb.GetBoltOrdinal(second.Id));
    }

    [Fact]
    public async Task DuplicateBoltIdsCannotReplaceActiveRecipeOrOverwriteSavedData()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var recipes = new RecipeManager(store, new());
        var active = new Recipe { Name = "Active", Pcb = new() { BoltPoints = [new()] } };
        store.SaveRecipe(active);
        await recipes.LoadAsync(active.Name);
        var before = JsonSerializer.Serialize(recipes.Current);
        var id = Guid.NewGuid();
        var identity = $"\"Id\":\"{id}\",";
        var json = $$"""
            {"Name":"Invalid","Pcb":{"TaughtBolts":[
                {{{identity}}"Number":1,"X":148.637,"Y":244.938},
                {{{identity}}"Number":2,"X":160,"Y":250}
            ]} }
            """;
        using (var connection = new SqliteConnection($"Data Source={store.DatabaseFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Recipes (Name, Value) VALUES ('Invalid', $value)";
            command.Parameters.AddWithValue("$value", json);
            command.ExecuteNonQuery();
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => recipes.LoadAsync("Invalid"));
        Assert.Contains("duplicate GUID", error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(recipes.Current));
        var invalid = JsonSerializer.Deserialize<Recipe>(json)!;
        invalid.Name = active.Name;
        Assert.Throws<InvalidDataException>(() => store.SaveRecipe(invalid));
        Assert.Throws<InvalidDataException>(() => store.SaveInspectionSettings(invalid));
        Assert.Equal(before, JsonSerializer.Serialize(store.LoadRecipe(active.Name)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadingEmptyBoltGuidsPersistsUniqueIdsWithoutUsingLegacyNumbers(bool explicitEmpty)
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var preservedId = Guid.NewGuid();
        var identity = explicitEmpty ? $"\"Id\":\"{Guid.Empty}\"," : "";
        var json = $$"""
            {"Name":"Legacy","Pcb":{"TaughtBolts":[
                {{{identity}}"Number":7,"Name":"고정","HeatSink":"HeatSink1","X":68.936,"Y":216.493,"FasteningX":152.904,"FasteningY":73.405,"FasteningZOffset":2},
                {{{identity}}"Number":7,"Name":"고정","HeatSink":"HeatSink2","X":356.478,"Y":271.678},
                {"Id":"{{preservedId}}","Number":8,"HeatSink":"HeatSink1","X":28.4,"Y":216.495}
            ],"FasteningOrder":["{{preservedId}}"]},"CarrierImages":[
                {"Number":12,"BoltNumber":7,"HeatSink":"HeatSink1","Region":{"X":512,"Y":384,"Width":256,"Height":256} },
                {"Number":24,"BoltNumber":7,"HeatSink":"HeatSink2"},
                {"Number":17,"BoltId":"{{preservedId}}","HeatSink":"HeatSink1"},
                {"Number":5,"IsBarcode":true,"HeatSink":"HeatSink1","Center":{"X":148.637,"Y":244.938} }
            ]}
            """;
        using (var connection = new SqliteConnection($"Data Source={store.DatabaseFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Recipes (Name, Value) VALUES ('Legacy', $value)";
            command.Parameters.AddWithValue("$value", json);
            command.ExecuteNonQuery();
        }

        var loaded = await Task.WhenAll(Task.Run(() => store.LoadRecipe("Legacy")), Task.Run(() => store.LoadRecipe("Legacy")));
        var first = loaded[0];
        var ids = first.Pcb.BoltPoints.Select(bolt => bolt.Id).ToArray();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, ids);
        Assert.Equal(preservedId, ids[2]);
        Assert.Equal(ids, loaded[1].Pcb.BoltPoints.Select(bolt => bolt.Id));
        Assert.Equal(new[] { preservedId }, first.Pcb.FasteningOrder);
        Assert.Null(first.CarrierImages[0].BoltId);
        Assert.Null(first.CarrierImages[1].BoltId);
        Assert.Equal(preservedId, first.CarrierImages[2].BoltId);
        Assert.Null(first.CarrierImages[3].BoltId);
        Assert.Equal(new PixelRegion(512, 384, 256, 256), first.CarrierImages[0].Region);
        Assert.Equal((148.637, 244.938), (first.CarrierImages[3].Center!.X, first.CarrierImages[3].Center!.Y));
        var bolt = first.Pcb.BoltPoints[0];
        Assert.Equal("고정", bolt.Name);
        Assert.Equal((68.936, 216.493, 152.904, 73.405, 2d), (bolt.X, bolt.Y, bolt.FasteningX, bolt.FasteningY, bolt.FasteningZOffset));

        var reopened = new MachineStore(store.DatabaseFile);
        Assert.Equal(ids, reopened.LoadRecipe("Legacy").Pcb.BoltPoints.Select(point => point.Id));
        store.SaveInspectionSettings(first);
        store.SaveRecipe(first);
        Assert.Equal(ids, store.LoadRecipe("Legacy").Pcb.BoltPoints.Select(point => point.Id));
        using var verification = new SqliteConnection($"Data Source={store.DatabaseFile}");
        verification.Open();
        using var query = verification.CreateCommand();
        query.CommandText = "SELECT Value FROM Recipes WHERE Name='Legacy'";
        var savedJson = (string)query.ExecuteScalar()!;
        Assert.DoesNotContain("BoltNumber", savedJson);
        using var saved = JsonDocument.Parse(savedJson);
        Assert.All(saved.RootElement.GetProperty("Pcb").GetProperty("TaughtBolts").EnumerateArray(),
            point => Assert.False(point.TryGetProperty("Number", out _)));
    }

    [Fact]
    public void LoadingEmptyBoltGuidsWithoutLegacyNumbersKeepsImagesUnlinked()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var json = $$"""
            {"Name":"Empty","Pcb":{"TaughtBolts":[{"Id":"{{Guid.Empty}}"},{"Id":"{{Guid.Empty}}"}]},
            "CarrierImages":[{"Number":1,"BoltId":"{{Guid.Empty}}"}]}
            """;
        using var connection = new SqliteConnection($"Data Source={store.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Recipes (Name, Value) VALUES ('Empty', $value)";
        command.Parameters.AddWithValue("$value", json);
        command.ExecuteNonQuery();

        var loaded = store.LoadRecipe("Empty");
        Assert.Equal(2, loaded.Pcb.BoltPoints.Select(bolt => bolt.Id).Distinct().Count());
        Assert.DoesNotContain(loaded.Pcb.BoltPoints, bolt => bolt.Id == Guid.Empty);
        Assert.Equal(Guid.Empty, loaded.CarrierImages[0].BoltId);
        Assert.All(loaded.Pcb.BoltPoints, bolt => Assert.NotEqual(bolt.Id, loaded.CarrierImages[0].BoltId));
    }

    [Fact]
    public void FasteningOrderGroupsHeadsAndAppendsNewBoltsWithoutReorderingInspection()
    {
        var shooting1 = new BoltPoint();
        var pickup1 = new BoltPoint { Head = FasteningHead.Pickup };
        var shooting2 = new BoltPoint { HeatSink = HeatSinkSlot.HeatSink2 };
        var pickup2 = new BoltPoint { HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Pickup };
        var pcb = new PcbLayout { BoltPoints = [shooting1, pickup1, shooting2, pickup2] };
        Assert.Equal(new[] { shooting1, shooting2, pickup1, pickup2 }, pcb.FasteningPoints);
        // The configured point order cannot change the head or PCB processing order.
        pcb.FasteningOrder = [pickup2.Id, shooting2.Id, pickup1.Id, shooting1.Id];
        var added = new BoltPoint();
        pcb.BoltPoints.Add(added);
        Assert.Equal(new[] { shooting1, added, shooting2, pickup1, pickup2 }, pcb.FasteningPoints);
        Assert.Equal(new[] { shooting1, pickup1, added }, pcb.BoltPoints.Where(point => point.HeatSink == HeatSinkSlot.HeatSink1));
        Assert.Equal(1, pcb.GetBoltOrdinal(shooting1.Id));
        Assert.Equal(2, pcb.GetBoltOrdinal(pickup1.Id));
    }

    [Fact]
    public void BoltGuidKeepsImageAndInspectionSettingsLinkedAfterReordering()
    {
        var first = new BoltPoint { Name = "좌상단 고정", X = 11, Y = 22, FasteningX = 101, FasteningY = 202 };
        var second = new BoltPoint { X = 33, Y = 44 };
        var recipe = new Recipe { Pcb = new() { BoltPoints = [first, second] } };
        recipe.CarrierImages = [new() { Number = 1, BoltId = first.Id, Region = new(1, 2, 3, 4) },
            new() { Number = 2, BoltId = second.Id, Region = new(5, 6, 7, 8) }];
        var store = VirtualTestSupport.OpenMachineStore();
        store.SaveRecipe(recipe);
        var draft = store.LoadRecipe(recipe.Name);
        draft.Pcb.BoltPoints[0].BrightnessThreshold = 180;
        recipe.Pcb.BoltPoints.Move(0, 1);
        store.SaveRecipe(recipe);
        store.SaveInspectionSettings(draft);
        Assert.Equal(2, recipe.Pcb.GetBoltOrdinal(first.Id));
        Assert.Equal(11, recipe.GetInspectionPosition(recipe.CarrierImages[0]).X);
        Assert.Equal(101, first.FasteningX);
        var saved = store.LoadRecipe(recipe.Name);
        Assert.Equal(180, saved.Pcb.BoltPoints.Single(bolt => bolt.Id == first.Id).BrightnessThreshold);
        Assert.Equal(new[] { second.Id, first.Id }, saved.Pcb.BoltPoints.Select(bolt => bolt.Id));
        Assert.Equal(first.Id, saved.CarrierImages[0].BoltId);
        Assert.Equal("좌상단 고정", saved.Pcb.BoltPoints.Single(bolt => bolt.Id == first.Id).Name);
        Assert.DoesNotContain("\"Number\"", JsonSerializer.Serialize(saved.Pcb.BoltPoints));
        Assert.DoesNotContain("BoltNumber", JsonSerializer.Serialize(saved));
        Assert.NotEqual(first.Id, new BoltPoint().Id);
    }

    [Fact]
    public void InspectionImageIdentityRequiresMatchingPcbAndNonemptyBoltGuid()
    {
        var bolt = new BoltPoint { Name = "Same name", X = 11, Y = 22 };
        var other = new BoltPoint { Name = "Same name", X = 33, Y = 44 };
        var image = new CarrierImageTile { Number = 50, BoltId = bolt.Id };
        var recipe = new Recipe { Pcb = new() { BoltPoints = [bolt, other] }, CarrierImages = [image] };
        var point = new InspectionPoint(recipe, bolt.HeatSink, bolt);
        var otherPoint = new InspectionPoint(recipe, other.HeatSink, other);

        Assert.Same(image, point.Metadata);
        Assert.Null(otherPoint.Metadata);
        Assert.Equal(11, recipe.GetInspectionPosition(image).X);
        bolt.Name = "Renamed";
        recipe.Pcb.BoltPoints.Move(0, 1);
        image.Number = 99;
        Assert.Same(image, point.Metadata);
        Assert.Equal(11, recipe.GetInspectionPosition(image).X);

        image.HeatSink = HeatSinkSlot.HeatSink2;
        Assert.Null(point.Metadata);
        Assert.Throws<InvalidOperationException>(() => recipe.GetInspectionPosition(image));
        image.HeatSink = bolt.HeatSink;
        image.BoltId = null;
        Assert.Null(point.Metadata);
        Assert.False(image.IsForTarget(bolt.HeatSink, boltId: null));
        Assert.Throws<InvalidOperationException>(() => recipe.GetInspectionPosition(image));
        image.BoltId = Guid.Empty;
        var emptyBolt = new BoltPoint { Id = Guid.Empty, X = 11, Y = 22 };
        recipe.Pcb.BoltPoints.Add(emptyBolt);
        Assert.Null(new InspectionPoint(recipe, emptyBolt.HeatSink, emptyBolt).Metadata);
        Assert.Throws<InvalidOperationException>(() => recipe.GetInspectionPosition(image));

        image.IsBarcode = true;
        image.Center = new() { X = 55, Y = 66 };
        Assert.Null(point.Metadata);
        Assert.Same(image, new InspectionPoint(recipe, bolt.HeatSink).Metadata);
        Assert.Equal(55, recipe.GetInspectionPosition(image).X);
    }

    [Theory]
    [InlineData(340, 400, 310, 420)]
    [InlineData(300, 440, 280, 410)]
    [InlineData(300, 360, 320, 390)]
    [InlineData(260, 400, 290, 380)]
    [InlineData(324, 432, 290, 420)]
    [InlineData(380, 400, 310, 420)]
    public void InitialFasteningCoordinatesRotateOnceFromInspectionWithoutScaling(
        double lowerX, double lowerY, double expectedX, double expectedY)
    {
        var reference = new CarrierReferenceSettings
        {
            UpperLeftLocatingPin = new() { X = 100, Y = 200 },
            LowerRightLocatingPin = new() { X = 140, Y = 200 },
        };
        var settings = new BoltFasteningSettings
        {
            ShootingHead = new()
            {
                UpperLeftLocatingPin = new() { X = 300, Y = 400 },
                LowerRightLocatingPin = new() { X = lowerX, Y = lowerY },
                FasteningZ = 12,
            },
        };
        var bolt = new BoltPoint { X = 110, Y = 220 };

        settings.InitializeBoltPosition(bolt, reference);
        var position = settings.GetBoltPosition(bolt);

        Assert.Equal(expectedX, position.X, 6);
        Assert.Equal(expectedY, position.Y, 6);
        Assert.Equal(12, position.Z);
        Assert.Equal((110d, 220d), (bolt.X, bolt.Y));
        Assert.Equal(0, bolt.FasteningZOffset);

        bolt.X = 999;
        reference.UpperLeftLocatingPin = new() { X = -100, Y = -200 };
        settings.InitializeBoltPosition(bolt, reference);
        Assert.Equal((expectedX, expectedY), (bolt.FasteningX!.Value, bolt.FasteningY!.Value));
        bolt.FasteningZOffset = -0.25;
        Assert.Equal(11.75, settings.GetBoltPosition(bolt).Z);
        settings.ShootingHead.FasteningZ = 14;
        Assert.Equal(13.75, settings.GetBoltPosition(bolt).Z);
        Assert.Throws<ArgumentOutOfRangeException>(() => bolt.FasteningZOffset = double.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => bolt.FasteningZOffset = double.PositiveInfinity);
    }

    [Fact]
    public void FasteningRotationRequiresDistinctRecordedReferences()
    {
        var upper = new AxisPosition { X = 100, Y = 200 };
        var lower = new AxisPosition { X = 200, Y = 400 };
        var reference = new CarrierReferenceSettings { UpperLeftLocatingPin = upper, LowerRightLocatingPin = lower };
        var settings = new BoltFasteningSettings();
        settings.ShootingHead.UpperLeftLocatingPin = upper;
        settings.ShootingHead.LowerRightLocatingPin = lower;
        var bolt = new BoltPoint { X = 110, Y = 220 };

        // Missing, coincident and nonfinite pins must not create a taught fastening position.
        foreach (var invalid in new AxisPosition?[] { null, upper, new() { X = double.NaN, Y = 400 } })
        {
            reference.LowerRightLocatingPin = invalid;
            settings.InitializeBoltPosition(bolt, reference);
            Assert.Null(bolt.FasteningX);
            Assert.Null(bolt.FasteningY);

            reference.LowerRightLocatingPin = lower;
            settings.ShootingHead.LowerRightLocatingPin = invalid;
            settings.InitializeBoltPosition(bolt, reference);
            Assert.Null(bolt.FasteningX);
            Assert.Null(bolt.FasteningY);
            settings.ShootingHead.LowerRightLocatingPin = lower;
        }
    }

    [Fact]
    public async Task InvalidInspectionRecipeKeepsTheActiveRecipeUntilCorrected()
    {
        var database = VirtualTestSupport.OpenMachineStore();
        var selection = new RecipeSelectionSettings();
        var recipes = new RecipeManager(database, selection) { Current = { Name = "Active" } };
        var recipe = recipes.Current;
        var editor = new RecipeEditorViewModel(recipes, database, new());
        await editor.SaveAsync();
        var currentInspection = recipe.BoltInspection;
        var corrected = new Recipe
        {
            Name = "Other",
            BoltInspection = new() { LightLevel = 192 },
        };
        database.SaveRecipe(corrected);
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

        database.SaveRecipe(corrected);
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
            Id = VirtualTestSupport.BoltId(1),
            HeatSink = HeatSinkSlot.HeatSink1,
            X = 13,
            Y = 24,
            BrightnessThreshold = 140,
            MinimumBrightRatio = 0.2,
        });
        layout.BoltPoints.Add(new()
        {
            Id = VirtualTestSupport.BoltId(1, HeatSinkSlot.HeatSink2),
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
        var firstCamera = targets[0].InspectionPosition!;
        fastening.InitializeBoltPosition(targets[1], pins);
        var secondHead = fastening.GetBoltPosition(targets[1]);
        Assert.Equal((13, 24), (firstCamera.X, firstCamera.Y));
        Assert.Equal(375, secondHead.X, 6);
        Assert.Equal(430, secondHead.Y, 6);

        var database = VirtualTestSupport.OpenMachineStore();
        var recipes = new RecipeManager(database, new());
        recipes.Current.CopyFrom(recipe);
        var editor = new RecipeEditorViewModel(recipes, database, new());
        await editor.SaveAsync();
        var loaded = database.LoadRecipe(recipe.Name);
        Assert.Equal(2, loaded.Pcb.BoltPoints.Count);
        Assert.Equal(13d, loaded.Pcb.BoltPoints.Single(point => point.HeatSink == HeatSinkSlot.HeatSink1).X);
        Assert.Equal(175d, loaded.Pcb.BoltPoints.Single(point => point.HeatSink == HeatSinkSlot.HeatSink2).X);
        Assert.Equal(140, loaded.Pcb.BoltPoints.Single(point => point.HeatSink == HeatSinkSlot.HeatSink1).BrightnessThreshold);
        Assert.Equal(0.2, loaded.Pcb.BoltPoints.Single(point => point.HeatSink == HeatSinkSlot.HeatSink1).MinimumBrightRatio);
        Assert.Equal(210, loaded.Pcb.BoltPoints.Single(point => point.HeatSink == HeatSinkSlot.HeatSink2).BrightnessThreshold);
        Assert.Equal(0.7, loaded.Pcb.BoltPoints.Single(point => point.HeatSink == HeatSinkSlot.HeatSink2).MinimumBrightRatio);
        layout.BoltPoints.Remove(targets[1]);
        Assert.DoesNotContain(layout.BoltPoints, point => point.HeatSink == HeatSinkSlot.HeatSink2);
        Assert.Single(layout.BoltPoints, point => point.HeatSink == HeatSinkSlot.HeatSink1);
        var oldLayout = System.Text.Json.JsonSerializer.Deserialize<PcbLayout>(
            """{"BoltPoints":[{"Number":1,"X":5,"Y":6}],"Origins":{"HeatSink1":{"X":100,"Y":200}}}""");
        Assert.Empty(oldLayout!.BoltPoints);
    }

    [Fact]
    public void SupplyHandoffStoresItsOwnZAndDropsTheObsoleteClearZ()
    {
        var supply = System.Text.Json.JsonSerializer.Deserialize<PcbSupplySettings>(
            """{"RotationZ":3,"BufferHandoffPosition":{"X":50,"Y":10,"Z":8},"BufferClearZ":12}""")!;
        var definition = VirtualTestSupport.CreateTeachingPoint(
            new(TeachingTarget.SupplyHandoff, MotionGroup.PcbSupply, TeachMode.Full), new() { PcbSupply = supply });
        Assert.Equal(TeachMode.Full, definition.Position.Mode);
        Assert.Equal(8, supply.HandoffPosition.Z);
        definition.Teach(60, 20, 99);
        Assert.Equal((60, 20), (supply.HandoffPosition.X, supply.HandoffPosition.Y));
        Assert.Equal(3, supply.TravelZ);
        Assert.Equal(99, supply.HandoffPosition.Z);

        using var saved = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(supply));
        Assert.False(saved.RootElement.TryGetProperty("BufferClearZ", out _));
        Assert.Equal(3, saved.RootElement.GetProperty("RotationZ").GetDouble());
        Assert.Equal(99, saved.RootElement.GetProperty("BufferHandoffPosition").GetProperty("Z").GetDouble());
    }

    [Fact]
    public void PlacementReceiveZIsTaughtAndStoredSeparatelyFromStandby()
    {
        var placement = System.Text.Json.JsonSerializer.Deserialize<PcbPlacementHandlerSettings>(
            """{"BufferEntryZ":3,"BufferHandoffPosition":{"X":50,"Y":10,"Z":8}}""")!;
        var definition = VirtualTestSupport.CreateTeachingPoint(
            new(TeachingTarget.PlacementHandoff, MotionGroup.PcbPlacementHandler, TeachMode.Full), new() { PcbPlacementHandler = placement });
        Assert.Equal(TeachMode.Full, definition.Position.Mode);
        Assert.Equal(8, placement.HandoffPosition.Z);
        definition.Teach(60, 20, 9);
        Assert.Equal(9, placement.HandoffPosition.Z);

        var receive = VirtualTestSupport.CreateTeachingPoint(
            new(TeachingTarget.PlacementReceiveZ, MotionGroup.PcbPlacementHandler, TeachMode.ZOnly), new() { PcbPlacementHandler = placement });
        Assert.Null(placement.ReceiveZ);
        Assert.False(receive.Position.HasPosition);
        Assert.Equal(TeachMode.ZOnly, receive.Position.Mode);
        Assert.Equal(TeachingStorage.Machine, receive.Storage);
        receive.Teach(100, 200, 12);
        Assert.Equal(12, placement.ReceiveZ);
        Assert.True(receive.Position.HasPosition);
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
            new(TeachingTarget.SafeZ, MotionGroup.PcbSupply, TeachMode.ZOnly),
            new(TeachingTarget.SupplyPcb1Pick, MotionGroup.PcbSupply, TeachMode.Full),
            new(TeachingTarget.SupplyPcb2Pick, MotionGroup.PcbSupply, TeachMode.Full),
            new(TeachingTarget.SupplyHandoff, MotionGroup.PcbSupply, TeachMode.Full),
            new(TeachingTarget.PlacementHandoff, MotionGroup.PcbPlacementHandler, TeachMode.Full),
        ];
        var points = definitions.Select(p => VirtualTestSupport.CreateTeachingPoint(p,
            new() { PcbSupply = supply, PcbPlacementHandler = placement }, new() { PcbSupply = recipe })).ToArray();
        Assert.Equal(5, points.Length);
        var handoffs = points.Where(p => p.Storage == TeachingStorage.Handoff).ToArray();
        foreach (var point in handoffs)
            point.Teach(10, 20, 30);
        Assert.Equal(10, supply.HandoffPosition.X);
        Assert.Equal(10, placement.HandoffPosition.X);

        var picks = points.Where(p => p.Storage == TeachingStorage.Recipe).ToArray();
        Assert.All(picks, p => Assert.Equal(TeachMode.Full, p.Position.Mode));
        Assert.Equal(10, handoffs[0].Coordinates!.X);
        var moveTarget = handoffs[0].MovePosition;
        moveTarget.Z = 999;
        handoffs[0].Refresh();
        Assert.Equal(30, supply.HandoffPosition.Z);
        Assert.Equal(30, handoffs[0].Coordinates!.Z);

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
        Assert.Equal(0, supply.TravelZ);
        Assert.Equal((10, 20, 30), (placementHandoff.X, placementHandoff.Y, placementHandoff.Z));
        Assert.Equal(2, handoffs.Select(p => p.Setting).Distinct().Count());
    }

    [Fact]
    public void NgPickupTeachingMigratesLegacyCoordinatesAndStoresOnePosition()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<NgCarrierTransferSettings>(
            """{"PickupSafeX":157.283,"CarrierPickupPosition":{"X":999,"Y":456.789,"Z":12},"ShuttlePlacePosition":{"X":146.46,"Y":1085.274}}""")!;
        var before = System.Text.Json.JsonSerializer.Serialize(settings);
        var database = VirtualTestSupport.OpenMachineStore();
        database.SaveSettings([settings]);
        settings = database.LoadSettings().Get<NgCarrierTransferSettings>();
        var point = VirtualTestSupport.CreateTeachingPoint(
            new(TeachingTarget.NgCarrierPickup, MotionGroup.InspectionGantry, TeachMode.XYOnly),
            new() { NgCarrierTransfer = settings });

        Assert.Equal((157.283, 456.789), (point.Coordinates!.X, point.Coordinates.Y));
        var target = point.MovePosition;
        target.X = -1;
        target.Y = -2;
        point.Refresh();
        database.SaveSettings([settings]);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(
            database.LoadSettings().Get<NgCarrierTransferSettings>()));

        point.Teach(160, 460, 999);
        database.SaveSettings([settings]);
        var saved = database.LoadSettings().Get<NgCarrierTransferSettings>();
        var pickup = saved.CarrierPickupPosition!;
        Assert.Equal((160, 460), (pickup.X, pickup.Y));
        Assert.Equal(0, pickup.Z);
        Assert.DoesNotContain("PickupSafeX", System.Text.Json.JsonSerializer.Serialize(saved));
        Assert.Equal((146.46, 1085.274), (saved.ShuttlePlacePosition.X, saved.ShuttlePlacePosition.Y));
    }

    [Theory]
    [InlineData("{\"CarrierPickupPosition\":{\"X\":999,\"Y\":12},\"PickupSafeX\":null}", null)]
    [InlineData("{\"CarrierPickupPosition\":{\"X\":999,\"Y\":12},\"PickupSafeX\":0}", 0d)]
    [InlineData("{\"CarrierPickupPosition\":{\"X\":25,\"Y\":12}}", 25d)]
    public void NgPickupSettingsPreserveUntaughtAndZeroCoordinates(string json, double? expectedX)
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<NgCarrierTransferSettings>(json)!;
        var saved = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("PickupSafeX", saved);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<NgCarrierTransferSettings>(saved)!;
        if (expectedX is { } x)
            Assert.Equal((x, 12d), (reloaded.CarrierPickupPosition!.X, reloaded.CarrierPickupPosition.Y));
        else
            Assert.Null(reloaded.CarrierPickupPosition);
    }

    [Fact]
    public void TeachingUsesOwnerCoordinatesAndRetainsTheOtherReferencePin()
    {
        var reference = new CarrierReferenceSettings
        {
            UpperLeftLocatingPin = new() { X = 100, Y = 200 },
            LowerRightLocatingPin = new() { X = 200, Y = 300 },
        };
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
        var bolt = new BoltPoint { Id = VirtualTestSupport.BoltId(1) };
        var recipe = new PcbLayout();
        recipe.BoltPoints = [bolt];
        var settings = new MachineSettings { BoltFastening = fastening, CarrierReference = reference };
        var position = VirtualTestSupport.CreateTeachingPoint(
            new(TeachingTarget.BoltPosition, MotionGroup.BoltFastening, TeachMode.XYOnly) { Bolt = bolt }, settings);
        Assert.False(position.Position.HasPosition);
        Assert.Null(position.Coordinates);
        Assert.Equal(TeachMode.XYOnly, position.Position.Mode);
        bolt.X = 110;
        bolt.Y = 220;
        fastening.InitializeBoltPosition(bolt, reference);
        position.Refresh();
        Assert.True(position.Position.HasPosition);
        Assert.Equal(310, position.Coordinates!.X, 6);
        Assert.Equal(420, position.Coordinates!.Y, 6);
        Assert.Equal(fastening.ShootingHead.FasteningZ, position.Coordinates!.Z);
        fastening.SafeZ = 7;
        position.Refresh();
        Assert.Equal(12, position.Coordinates!.Z);
        var shootingZ = VirtualTestSupport.CreateTeachingPoint(
            new(TeachingTarget.ShootingHeadFasteningZ, MotionGroup.BoltFastening, TeachMode.ZOnly), settings);
        Assert.Equal(TeachMode.ZOnly, shootingZ.Position.Mode);
        Assert.Same(fastening, shootingZ.Setting);
        shootingZ.Teach(999, 999, 14);
        Assert.Equal(7, fastening.SafeZ);
        Assert.Equal(14, fastening.ShootingHead.FasteningZ);
        Assert.Equal(16, fastening.PickupHead.FasteningZ);
        Assert.All(
            recipe.BoltPoints,
            bolt => Assert.Equal(14, fastening.GetBoltPosition(bolt).Z));
        position.FasteningZOffset = 0.5;
        position.Teach(311, 421, 999);
        Assert.Equal((311, 421, 14.5), (position.Coordinates!.X, position.Coordinates!.Y, position.Coordinates!.Z));
        Assert.Equal(0.5, bolt.FasteningZOffset);
        Assert.Equal(14, fastening.ShootingHead.FasteningZ);

        var lowerRight = reference.LowerRightLocatingPin;
        var upperLeft = VirtualTestSupport.CreateTeachingPoint(
            new(TeachingTarget.CarrierUpperLeftLocatingPin, MotionGroup.InspectionGantry, TeachMode.XYOnly), settings);
        upperLeft.Teach(105, 205, 0);
        Assert.Same(lowerRight, reference.LowerRightLocatingPin);
        var camera = recipe.BoltPoints.Single(point => point.HeatSink == HeatSinkSlot.HeatSink1).InspectionPosition!;
        Assert.Equal((110, 220), (camera.X, camera.Y));
        Assert.Equal((110d, 220d), (bolt.X, bolt.Y));
        Assert.Same(reference, upperLeft.Setting);

        recipe.BoltPoints = [
            new() { Id = VirtualTestSupport.BoltId(2), Head = FasteningHead.Pickup, X = 10, Y = 20 },
            bolt,
            new() { Id = VirtualTestSupport.BoltId(3), Head = FasteningHead.Pickup, X = 20, Y = 30 },
        ];
        foreach (var target in recipe.BoltPoints)
            fastening.InitializeBoltPosition(target, reference);
        var pickupZ = VirtualTestSupport.CreateTeachingPoint(
            new(TeachingTarget.PickupHeadFasteningZ, MotionGroup.BoltFastening, TeachMode.ZOnly), settings);
        Assert.Equal(TeachMode.ZOnly, pickupZ.Position.Mode);
        Assert.Same(fastening, pickupZ.Setting);
        pickupZ.Teach(999, 999, 18);
        Assert.Equal(7, fastening.SafeZ);
        Assert.Equal(14, fastening.ShootingHead.FasteningZ);
        Assert.Equal(18, fastening.PickupHead.FasteningZ);
        Assert.Equal(10, fastening.PickupPosition.Z);
        Assert.All(recipe.BoltPoints, target => Assert.Equal(
            (target.Head == FasteningHead.Pickup ? 18 : 14) + target.FasteningZOffset,
            fastening.GetBoltPosition(target).Z));
        Assert.Equal(new[] { VirtualTestSupport.BoltId(2), VirtualTestSupport.BoltId(1), VirtualTestSupport.BoltId(3) }, recipe.BoltPoints.Select(point => point.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecipeSelectionCannotChangeWhileAnotherOperationOwnsMachine(bool createNew)
    {
        var database = VirtualTestSupport.OpenMachineStore();
        database.SaveRecipe(new Recipe { Name = "Other" });
        var recipes = new RecipeManager(database, new()) { Current = { Name = "Active" } };
        var operations = new OperationCancellation();
        var editor = new RecipeEditorViewModel(recipes, database, operations);

        using var running = operations.Link();
        if (createNew)
            editor.NewCommand.Execute(null);
        else
            await editor.LoadCommand.ExecuteAsync("Other");

        Assert.Equal("Active", recipes.Current.Name);
        Assert.NotNull(editor.Error);
        Assert.False(running.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelledRecipeLoadKeepsSelectionAndReleasesOwnershipForRetry()
    {
        var database = VirtualTestSupport.OpenMachineStore();
        database.SaveRecipe(new Recipe { Name = "Other" });
        var selection = new RecipeSelectionSettings();
        var recipes = new RecipeManager(database, selection) { Current = { Name = "Active" } };
        var operations = new OperationCancellation();
        var editor = new RecipeEditorViewModel(recipes, database, operations);
        void CancelLoad()
        {
            if (operations.HasActiveOperations)
                editor.LoadCommand.Cancel();
        }
        operations.ActivityChanged += CancelLoad;
        try
        {
            await editor.LoadCommand.ExecuteAsync("Other");
            Assert.True(editor.LoadCommand.IsCancellationRequested);
            Assert.Equal("Active", recipes.Current.Name);
            Assert.Null(selection.LastRecipeName);
            Assert.Null(editor.Error);
            Assert.False(operations.HasActiveOperations);
        }
        finally
        {
            operations.ActivityChanged -= CancelLoad;
        }

        await editor.LoadCommand.ExecuteAsync("Other");
        Assert.Equal("Other", recipes.Current.Name);
        Assert.Equal("Other", selection.LastRecipeName);
        Assert.Null(editor.Error);
        Assert.False(operations.HasActiveOperations);
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
        var database = VirtualTestSupport.OpenMachineStore();
        var selection = new RecipeSelectionSettings();
        var recipes = new RecipeManager(database, selection);
        recipes.Current.CopyFrom(recipe);
        recipe = recipes.Current;
        var editor = new RecipeEditorViewModel(recipes, database, operations);
        bool? activeAtChange = null;
        string? selectedAtChange = null;
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RecipeEditorViewModel.ActiveName))
            {
                activeAtChange = operations.HasActiveOperations;
                selectedAtChange = database.LoadSettings().Get<RecipeSelectionSettings>().LastRecipeName;
            }
        };

        editor.Name = " ";
        Assert.False(editor.IsSaveAllowed);
        Assert.False(await editor.SaveAsync()); // Direct autosave calls use the same name check.
        Assert.NotNull(editor.Error);
        Assert.Empty(database.RecipeNames);
        Assert.Equal(savedName, recipe.Name);
        editor.Name = savedName;

        using var cancellation = new CancellationTokenSource();
        void CancelWhenStarted()
        {
            if (operations.HasActiveOperations)
                cancellation.Cancel();
        }

        operations.ActivityChanged += CancelWhenStarted;
        Assert.False(await editor.SaveAsync(cancellationToken: cancellation.Token));
        operations.ActivityChanged -= CancelWhenStarted;
        Assert.Null(editor.Error);
        Assert.Empty(database.RecipeNames);
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

        activeAtChange = null;
        editor.NewCommand.Execute(null);
        Assert.True(activeAtChange);
        Assert.False(operations.HasActiveOperations);
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
        var database = VirtualTestSupport.OpenMachineStore();
        var sourceRecipes = new RecipeManager(database, new()) { Current = { Name = "Source" } };
        var source = sourceRecipes.Current;
        var sourceEditor = new RecipeEditorViewModel(sourceRecipes, database, new());
        var imagesLoader = new InspectionImageLoader(database, Microsoft.Extensions.Logging.Abstractions.NullLogger<InspectionImageLoader>.Instance);
        var targetSelection = new RecipeSelectionSettings();
        var targetRecipes = new RecipeManager(database, targetSelection) { Current = { Name = "Target" } };
        var target = targetRecipes.Current;
        var targetEditor = new RecipeEditorViewModel(targetRecipes, database, new());
        RecipeImageItem[] Images(double x, byte value)
        {
            return [
                new(new CarrierImageTile
                {
                    Number = 1,
                    Center = new() { X = x },
                    Region = new(0, 0, 1, 1),
                    BoltId = VirtualTestSupport.BoltId(3),
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
        Assert.True(await sourceEditor.SaveAsync(sourceImages));
        Assert.Same(sourceImages[0].Metadata, source.CarrierImages[0]);
        Assert.Same(sourceImages[1].Metadata, source.CarrierImages[1]);
        var reloaded = await Task.Factory.StartNew(
            () => imagesLoader.LoadRecipeAsync(sourceRecipes.Current),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        Assert.All(reloaded, tile =>
        {
            Assert.NotNull(tile.Image);
            Assert.True(tile.Image.IsFrozen);
        });
        Assert.Equal("FOV 1", reloaded[0].ToString());
        Assert.True(await sourceEditor.SaveAsync(reloaded));
        var pixels = new byte[3];
        var savedImage = (await imagesLoader.LoadRecipeAsync(sourceRecipes.Current))[0].Image;
        Assert.NotNull(savedImage);
        savedImage.CopyPixels(pixels, 3, 0);
        Assert.Equal(new byte[] { 10, 11, 12 }, pixels);
        var savedFov = database.LoadRecipe("Source").CarrierImages[0];
        Assert.Equal(new PixelRegion(0, 0, 1, 1), savedFov.Region);
        Assert.Equal(VirtualTestSupport.BoltId(3), savedFov.BoltId);
        Assert.Equal(HeatSinkSlot.HeatSink2, savedFov.HeatSink);
        var loadedImages = await imagesLoader.LoadRecipeAsync(sourceRecipes.Current);
        Assert.Same(source.CarrierImages[0], loadedImages[0].Metadata);
        Assert.Equal(savedFov.Region, loadedImages[0].Metadata.Region);
        var savedBarcode = database.LoadRecipe("Source").CarrierImages[1];
        Assert.True(savedBarcode.IsBarcode);
        Assert.Null(savedBarcode.BoltId);
        Assert.Equal(new PixelRegion(0, 0, 1, 1), savedBarcode.Region);
        Assert.Same(source.CarrierImages[1], loadedImages[1].Metadata);
        Assert.True(loadedImages[1].Metadata.IsBarcode);
        Assert.Equal("Source", database.LoadSettings().Get<RecipeSelectionSettings>().LastRecipeName);
        Assert.True(await targetEditor.SaveAsync(Images(10, 100)));
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
        Assert.DoesNotContain("Rejected", database.RecipeNames);
        Assert.DoesNotContain("Rejected", targetEditor.Recipes);
        Assert.Throws<FileNotFoundException>(() => database.LoadRecipeImage("Rejected", 1));

        targetEditor.Name = "Target";
        Assert.False(await targetEditor.SaveAsync(Images(30, 200)));
        Assert.Contains("selection failure", targetEditor.Error);
        Assert.Equal([10d, 11d], target.CarrierImages.Select(tile => tile.Center!.X));
        Assert.Equal(
            [10d, 11d],
            database.LoadRecipe("Target").CarrierImages.Select(tile => tile.Center!.X));
        Assert.Equal(original, database.LoadRecipeImage("Target", 1));
        Assert.Equal("Source", database.LoadSettings().Get<RecipeSelectionSettings>().LastRecipeName);
        command.CommandText = "DROP TRIGGER FailSelection";
        command.ExecuteNonQuery();

        command.CommandText = "CREATE TRIGGER FailImage BEFORE INSERT ON RecipeImages WHEN NEW.Number = 2 BEGIN SELECT RAISE(ABORT, 'test failure'); END";
        command.ExecuteNonQuery();
        Assert.False(await targetEditor.SaveAsync(Images(30, 200)));
        Assert.Contains("test failure", targetEditor.Error);
        Assert.Equal([10d, 11d], target.CarrierImages.Select(tile => tile.Center!.X));
        Assert.Equal(
            [10d, 11d],
            database.LoadRecipe("Target").CarrierImages.Select(tile => tile.Center!.X));
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
            database.LoadRecipe("Target").CarrierImages.Select(tile => tile.Center!.X));
        Assert.Equal(database.LoadRecipeImage("Source", 1), database.LoadRecipeImage("Target", 1));
        Assert.Equal(2, (await imagesLoader.LoadRecipeAsync(sourceRecipes.Current)).Length);

        using var cancellation = new CancellationTokenSource();
        IEnumerable<RecipeImage> CancelAfterFirstImage()
        {
            yield return new(1, [200]);
            cancellation.Cancel();
            yield return new(2, [200]);
        }

        var beforeCancel = database.LoadRecipeImage("Target", 1);
        var beforeCancelRecipe = JsonSerializer.Serialize(database.LoadRecipe("Target"));
        Assert.Throws<OperationCanceledException>(
            () => database.SaveRecipe(
                new Recipe { Name = "Target", CarrierImages = [new() { Number = 1 }, new() { Number = 2 }] },
                images: CancelAfterFirstImage(),
                cancellationToken: cancellation.Token));
        Assert.Equal(beforeCancelRecipe, JsonSerializer.Serialize(database.LoadRecipe("Target")));
        Assert.Equal(beforeCancel, database.LoadRecipeImage("Target", 1));
        Assert.False(await sourceEditor.SaveAsync(Images(30, 200), cancellation.Token));
        Assert.Null(sourceEditor.Error);
        Assert.Equal([1d, 2d], source.CarrierImages.Select(tile => tile.Center!.X));

        await sourceEditor.SaveAsync(Images(50, 200).Take(1).ToArray());
        Assert.Single(database.LoadRecipe("Target").CarrierImages);
        Assert.Throws<FileNotFoundException>(() => database.LoadRecipeImage("Target", 2));
        Assert.Equal(2, database.LoadRecipe("Source").CarrierImages.Count);
    }
}
