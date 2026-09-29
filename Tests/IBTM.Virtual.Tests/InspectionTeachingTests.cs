using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IBTM.Core;
using IBTM.Inspection;
using IBTM.Storage;
using IBTM.UI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class InspectionTeachingTests
{
    [Fact]
    public async Task RecipePointsShareNamesCoordinatesAndImagesAcrossTeaching()
    {
        var store = VirtualTest.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipe = store.LoadRecipe("Inspection");
        var bolt = recipe.Pcb.BoltPoints[0];
        bolt.Name = "Connector bolt";
        bolt.BrightnessThreshold = 91;
        recipe.CarrierImages[1].BoltId = null;
        store.SaveRecipe(recipe);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var images = new InspectionImages(store, NullLogger<InspectionImages>.Instance);
        var editor = new InspectionTeachingViewModel(store, recipes, images, new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.LoadRecipeCommand.ExecuteAsync(null);
        editor.SelectedPoint = Assert.Single(editor.Points, point => point.Bolt is not null);
        var teaching = new TeachingPoint(new(IBTM.Device.TeachingTarget.BoltReference,
            MotionGroup.InspectionGantry, IBTM.Device.TeachMode.Image) { Bolt = recipes.Current.Pcb.BoltPoints[0] },
            new(), recipes);

        Assert.Null(editor.Error);
        Assert.Null(editor.SelectedPoint.Metadata);
        Assert.Equal(teaching.Name, editor.SelectedPoint.Name);
        Assert.Equal(teaching.PositionLabel, editor.SelectedPoint.PositionLabel);
        Assert.Equal((10d, 20d), (editor.SelectedPoint.Position!.X, editor.SelectedPoint.Position.Y));
        Assert.Equal("Connector bolt Inspection", editor.SelectedPoint.Name);
        Assert.Equal(91, editor.Preview.BrightnessThreshold);
        Assert.False(editor.Preview.HasImage);
        Assert.False(editor.InspectCommand.CanExecute(null));
        Assert.False(editor.DrawRegionCommand.CanExecute(new Rect(0, 0, 8, 8)));
        Assert.Single(editor.Points, point => point.Metadata is not null);
        Assert.False(string.IsNullOrWhiteSpace(editor.Message));
        await editor.InspectCommand.ExecuteAsync(null);
        Assert.Null(editor.Preview.Result);
        editor.Preview.BrightnessThreshold = 173;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.Equal(173, store.LoadRecipe("Inspection").Pcb.BoltPoints[0].BrightnessThreshold);

        var tiles = recipes.Current.CarrierImages.ToList();
        tiles.Add(new() { Number = 3, BoltId = bolt.Id, Region = new(2, 2, 10, 10) });
        await recipes.SaveImagesAsync("Inspection", tiles, [new(1, png), new(2, png), new(3, png)]);
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        var loadedImages = await images.LoadRecipeAsync(recipes.Current);
        Assert.Equal(bolt.Id, editor.SelectedPoint!.Bolt!.Id);
        Assert.Equal(teaching.Name, editor.SelectedPoint.Name);
        Assert.Equal(teaching.PositionLabel, editor.SelectedPoint.PositionLabel);
        Assert.Equal(InspectionPreview.CreateFrame(teaching.Inspection!.GetImage(loadedImages)!).Pixels,
            InspectionPreview.CreateFrame(editor.Preview.Image!).Pixels);
        Assert.NotNull(editor.Preview.Overlay);
        Assert.True(editor.InspectCommand.CanExecute(null));
        Assert.True(editor.DrawRegionCommand.CanExecute(new Rect(0, 0, 8, 8)));
        Assert.Null(store.LoadRecipe("Inspection").CarrierImages.Single(tile => tile.Number == 2).BoltId);
    }

    [Fact]
    public async Task RecreatedBoltDoesNotInheritDeletedPointInspectionEdits()
    {
        var store = VirtualTest.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = new InspectionTeachingViewModel(store, recipes, new InspectionImages(store, NullLogger<InspectionImages>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.LoadRecipeCommand.ExecuteAsync(null);
        var removedId = editor.Draft.Pcb.BoltPoints[0].Id;
        editor.Draft.Pcb.BoltPoints[0].BrightnessThreshold = 91;
        editor.Draft.CarrierImages[1].Region = new(2, 2, 10, 10);
        editor.Draft.BoltInspection.DataMatrix1.BinaryThreshold = 73;

        recipes.Current.Pcb.BoltPoints.Clear();
        recipes.Current.CarrierImages.RemoveAll(tile => !tile.IsBarcode);
        var replacement = new BoltPoint { X = 100, Y = 200, BrightnessThreshold = 180 };
        recipes.Current.Pcb.BoltPoints.Add(replacement);
        var tile = new CarrierImageTile { Number = 2, BoltId = replacement.Id };
        await recipes.SaveImagesAsync("Inspection", [recipes.Current.CarrierImages[0], tile], [new(1, png), new(2, png)]);

        // Saving the old draft before refreshing must also leave the replacement untouched.
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.Equal(180, recipes.Current.Pcb.BoltPoints[0].BrightnessThreshold);
        Assert.Null(recipes.Current.CarrierImages[1].Region);

        await editor.RefreshImagesCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.NotEqual(removedId, editor.Draft.Pcb.BoltPoints[0].Id);
        Assert.Equal(replacement.Id, editor.Draft.Pcb.BoltPoints[0].Id);
        Assert.Equal(180, editor.Draft.Pcb.BoltPoints[0].BrightnessThreshold);
        Assert.Null(editor.Draft.CarrierImages[1].Region);
        Assert.Equal(73, editor.Draft.BoltInspection.DataMatrix1.BinaryThreshold);

        await editor.SaveCommand.ExecuteAsync(null);
        var saved = store.LoadRecipe("Inspection");
        Assert.Equal(replacement.Id, saved.Pcb.BoltPoints[0].Id);
        Assert.Equal(180, saved.Pcb.BoltPoints[0].BrightnessThreshold);
        Assert.Null(saved.CarrierImages[1].Region);
    }

    [Fact]
    public async Task CaseOnlyRecipeNameChangeKeepsActiveInspectionEditsAndOneListEntry()
    {
        var store = VirtualTest.OpenMachineStore();
        await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var inspection = new InspectionTeachingViewModel(store, recipes, new InspectionImages(store, NullLogger<InspectionImages>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await inspection.LoadRecipeCommand.ExecuteAsync(null);
        var editor = new RecipeEditor(recipes, store, new()) { Name = "inspection" };
        Assert.True(await editor.SaveAsync());
        Assert.Equal("Inspection", Assert.Single(editor.Recipes));
        Assert.Single(store.RecipeNames);

        inspection.Draft.BoltInspection.DataMatrix1.BinaryThreshold = 73;
        await inspection.SaveCommand.ExecuteAsync(null);
        Assert.Null(inspection.Error);
        Assert.Equal(73, recipes.Current.BoltInspection.DataMatrix1.BinaryThreshold);
        Assert.Equal(73, store.LoadRecipe("Inspection").BoltInspection.DataMatrix1.BinaryThreshold);

        // Unsaved point deletion belongs to the same active recipe despite the name casing.
        recipes.Current.Pcb.BoltPoints.Clear();
        recipes.Current.CarrierImages.RemoveAll(tile => !tile.IsBarcode);
        await inspection.RefreshImagesCommand.ExecuteAsync(null);
        Assert.Null(inspection.Error);
        Assert.True(Assert.Single(inspection.Points, point => point.Metadata is not null).IsDataMatrix);
        Assert.Empty(inspection.Draft.Pcb.BoltPoints);

        var reopened = new InspectionTeachingViewModel(store, recipes, new InspectionImages(store, NullLogger<InspectionImages>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        reopened.Activate();
        Assert.Contains(reopened.SelectedRecipeName, reopened.RecipeNames);
        Assert.NotNull(reopened.LoadRecipeCommand.ExecutionTask);
        await reopened.LoadRecipeCommand.ExecutionTask;
        Assert.Null(reopened.Error);
        Assert.True(Assert.Single(reopened.Points, point => point.Metadata is not null).IsDataMatrix);
    }

    [Fact]
    public async Task DistinctNonAsciiRecipeNamesKeepTheirOwnImagesAndInspectionSettings()
    {
        var store = VirtualTest.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        await recipes.SaveAsync("검사Ä");
        var inspection = new InspectionTeachingViewModel(store, recipes, new InspectionImages(store, NullLogger<InspectionImages>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await inspection.LoadRecipeCommand.ExecuteAsync(null);
        var editor = new RecipeEditor(recipes, store, new()) { Name = "검사ä" };
        Assert.True(await editor.SaveAsync());
        Assert.Equal(3, editor.Recipes.Count);
        Assert.Equal(3, store.RecipeNames.Count);
        Assert.Equal(png, store.LoadRecipeImage("검사ä", 1));
        var originalThreshold = recipes.Current.BoltInspection.DataMatrix1.BinaryThreshold;
        inspection.Draft.BoltInspection.DataMatrix1.BinaryThreshold = 73;
        await inspection.SaveCommand.ExecuteAsync(null);
        Assert.Null(inspection.Error);
        Assert.Equal(73, store.LoadRecipe("검사Ä").BoltInspection.DataMatrix1.BinaryThreshold);
        Assert.Equal(originalThreshold, recipes.Current.BoltInspection.DataMatrix1.BinaryThreshold);
        Assert.Equal(originalThreshold, store.LoadRecipe("검사ä").BoltInspection.DataMatrix1.BinaryThreshold);
    }

    [Fact]
    public async Task CaptureSavePreservesInspectionEditsMadeAfterCaptureStarted()
    {
        var store = VirtualTest.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var captured = JsonSerializer.Deserialize<List<CarrierImageTile>>(
            JsonSerializer.Serialize(recipes.Current.CarrierImages))!;
        captured[0].Center = new() { X = 30, Y = 40 };
        var edited = JsonSerializer.Deserialize<Recipe>(JsonSerializer.Serialize(recipes.Current))!;
        edited.CarrierImages[0].Region = new(4, 5, 6, 7);
        edited.BoltInspection.DataMatrix1.BinaryThreshold = 81;
        await recipes.SaveInspectionAsync(edited);

        await recipes.SaveImagesAsync("Inspection", captured, [new(1, png), new(2, png)]);

        foreach (var recipe in new[] { recipes.Current, store.LoadRecipe("Inspection") })
        {
            Assert.Equal(new PixelRegion(4, 5, 6, 7), recipe.CarrierImages[0].Region);
            Assert.Equal(81, recipe.BoltInspection.DataMatrix1.BinaryThreshold);
            Assert.Equal(30, recipe.CarrierImages[0].Center!.X);
        }
        Assert.Equal(new PixelRegion(4, 5, 6, 7), captured[0].Region);
    }

    [Fact]
    public async Task InspectionSaveWaitsForCaptureCommitAndKeepsCapturedPosition()
    {
        var store = VirtualTest.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var edited = JsonSerializer.Deserialize<Recipe>(JsonSerializer.Serialize(recipes.Current))!;
        edited.CarrierImages[0].Region = new(4, 5, 6, 7);
        var captured = JsonSerializer.Deserialize<List<CarrierImageTile>>(
            JsonSerializer.Serialize(recipes.Current.CarrierImages))!;
        captured[0].Center = new() { X = 30, Y = 40 };
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        IEnumerable<RecipeImage> Images()
        {
            writing.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            yield return new(1, png);
            yield return new(2, png);
        }
        var capture = recipes.SaveImagesAsync("Inspection", captured, Images());
        Task save = Task.CompletedTask;
        try
        {
            Assert.True(await Task.Run(() => writing.Wait(TimeSpan.FromSeconds(5))));
            save = recipes.SaveInspectionAsync(edited);
            Assert.False(save.IsCompleted);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(capture, save);
        }
        foreach (var recipe in new[] { recipes.Current, store.LoadRecipe("Inspection") })
        {
            Assert.Equal(new PixelRegion(4, 5, 6, 7), recipe.CarrierImages[0].Region);
            Assert.Equal(30, recipe.CarrierImages[0].Center!.X);
        }
    }

    [Fact]
    public async Task PointSelectionUpdatesBoundParameters()
    {
        var store = VirtualTest.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipe = store.LoadRecipe("Inspection");
        recipe.BoltInspection.DataMatrix1.BinaryThreshold = 51;
        recipe.BoltInspection.DataMatrix2.BinaryThreshold = 180;
        recipe.BoltInspection.DataMatrix2.TryInverted = false;
        recipe.Pcb.BoltPoints[0].BrightnessThreshold = 91;
        recipe.Pcb.BoltPoints[0].MinimumBrightRatio = 0.25;
        recipe.Pcb.BoltPoints.Add(new() { Id = VirtualTest.BoltId(2), BrightnessThreshold = 172, MinimumBrightRatio = 0.75 });
        recipe.CarrierImages.Add(new() { Number = 3, BoltId = VirtualTest.BoltId(2), Region = new(2, 2, 10, 10) });
        recipe.CarrierImages.Add(new() { Number = 4, IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink2, Region = new(2, 2, 10, 10) });
        store.SaveRecipe(recipe, images: [new(1, png), new(2, png), new(3, png), new(4, png)]);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = new InspectionTeachingViewModel(store, recipes, new InspectionImages(store, NullLogger<InspectionImages>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.LoadRecipeCommand.ExecuteAsync(null);
        await VirtualTest.RunOnStaAsync(() =>
        {
            var panel = new StackPanel();
            var root = new UserControl { DataContext = editor, BindingGroup = new BindingGroup { Name = "InspectionInputs" }, Content = panel };
            var list = new ListBox();
            list.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(editor.Points)));
            list.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                new Binding(nameof(editor.SelectedPoint)) { BindingGroupName = null });
            var threshold = new TextBox();
            threshold.SetBinding(TextBox.TextProperty, new Binding("Preview.BrightnessThreshold")
            {
                BindingGroupName = "InspectionInputs", UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            var minimum = new TextBox();
            minimum.SetBinding(TextBox.TextProperty, new Binding("Preview.MinimumBrightPercent")
            {
                BindingGroupName = "InspectionInputs", UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            var matrixThreshold = new TextBox();
            matrixThreshold.SetBinding(TextBox.TextProperty, new Binding("Preview.DataMatrixThreshold")
            {
                BindingGroupName = "InspectionInputs", UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            var inverted = new CheckBox();
            inverted.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding("DataMatrix.TryInverted") { BindingGroupName = null });
            panel.Children.Add(list);
            panel.Children.Add(threshold);
            panel.Children.Add(minimum);
            panel.Children.Add(matrixThreshold);
            panel.Children.Add(inverted);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal("51", matrixThreshold.Text);
            Assert.True(inverted.IsChecked);
            inverted.IsChecked = false;
            Assert.False(editor.Draft.BoltInspection.DataMatrix1.TryInverted);
            matrixThreshold.Text = "73";
            list.SelectedItem = editor.Points.Single(point => point.IsDataMatrix && point.HeatSink == HeatSinkSlot.HeatSink2);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal("180", matrixThreshold.Text);
            Assert.Equal(73, editor.Draft.BoltInspection.DataMatrix1.BinaryThreshold);
            Assert.False(inverted.IsChecked);
            list.SelectedItem = editor.Points.Single(point => point.Bolt?.Id == VirtualTest.BoltId(1));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(VirtualTest.BoltId(1), editor.SelectedPoint!.Bolt!.Id);
            Assert.Equal("91", threshold.Text);
            Assert.Equal("25", minimum.Text);
            threshold.Text = "103";
            list.SelectedItem = editor.Points.Single(point => point.Bolt?.Id == VirtualTest.BoltId(2));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(VirtualTest.BoltId(2), editor.SelectedPoint!.Bolt!.Id);
            Assert.Equal("172", threshold.Text);
            Assert.Equal("75", minimum.Text);
            Assert.Equal(103, editor.Draft.Pcb.BoltPoints[0].BrightnessThreshold);
            GC.KeepAlive(root);
        });
    }

    [Fact]
    public async Task BinaryPreviewFollowsSelectedPointThresholdAndRoi()
    {
        var recipe = new Recipe();
        var bolt = new BoltPoint { Id = VirtualTest.BoltId(1), BrightnessThreshold = 128 };
        recipe.Pcb.BoltPoints.Add(bolt);
        recipe.BoltInspection.DataMatrix1.BinaryThreshold = 128;
        var pixels = Enumerable.Repeat((byte)90, 20 * 20 * 3).ToArray();
        var frame = new ImageFrame(20, 20, 60, pixels);
        var original = InspectionPreview.CreateBitmap(frame);
        var preview = new InspectionPreview(recipe);
        preview.Clear(HeatSinkSlot.HeatSink1);
        preview.SetSavedImage(original, new(2, 3, 10, 12));
        Assert.Equal((10, 12), (preview.Overlay!.PixelWidth, preview.Overlay.PixelHeight));
        Assert.All(InspectionPreview.CreateFrame(preview.Overlay).Pixels, pixel => Assert.Equal(0, pixel));
        preview.DataMatrixThreshold = 80;
        Assert.All(InspectionPreview.CreateFrame(preview.Overlay!).Pixels, pixel => Assert.Equal(255, pixel));
        Assert.Equal(80, recipe.BoltInspection.DataMatrix1.BinaryThreshold);
        await preview.InspectAsync(CancellationToken.None);
        Assert.NotNull(preview.Overlay);
        Assert.Same(original, preview.Image);
        preview.SetSavedImage(original, new(1, 1, 6, 8));
        Assert.Equal((6, 8), (preview.Overlay!.PixelWidth, preview.Overlay.PixelHeight));
        preview.Clear(bolt: bolt);
        preview.SetSavedImage(original, new(2, 3, 10, 12));
        Assert.All(InspectionPreview.CreateFrame(preview.Overlay!).Pixels, pixel => Assert.Equal(0, pixel));
        preview.BrightnessThreshold = 80;
        Assert.All(InspectionPreview.CreateFrame(preview.Overlay!).Pixels, pixel => Assert.Equal(255, pixel));
        preview.MinimumBrightPercent = 42.5;
        Assert.Equal(0.425, bolt.MinimumBrightRatio);
        Assert.Throws<ArgumentOutOfRangeException>(() => preview.MinimumBrightPercent = 101);
        Assert.Throws<ArgumentOutOfRangeException>(() => preview.MinimumBrightPercent = double.NaN);
        Assert.Equal(0.425, bolt.MinimumBrightRatio);
        Assert.Same(original, preview.Image);
        Assert.Equal(pixels, InspectionPreview.CreateFrame(preview.Image!).Pixels);
    }

    [Fact]
    public async Task OfflineDraftSavesOnlyInspectionSettingsWithoutReplacingNewerPositionsOrImages()
    {
        var store = VirtualTest.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = new InspectionTeachingViewModel(store, recipes, new InspectionImages(store, NullLogger<InspectionImages>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.LoadRecipeCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.NotSame(recipes.Current, editor.Draft);
        Assert.True(editor.Preview.HasImage);
        editor.DrawRegionCommand.Execute(new Rect(0, 0, 8, 8));
        editor.DataMatrix!.TryInverted = false;
        editor.DataMatrix.BinaryThreshold = 72;
        editor.SelectedPoint = editor.Points.Single(point => !point.IsDataMatrix);
        editor.Preview.BrightnessThreshold = 214;
        editor.Preview.MinimumBrightPercent = 40;
        Assert.Null(recipes.Current.Pcb.BoltPoints[0].LightLevel);
        Assert.Equal(255, recipes.Current.BoltInspection.LightLevel);
        Assert.True(recipes.Current.BoltInspection.DataMatrix1.TryInverted);

        // Gantry coordinates and lights can change after the offline draft was opened.
        var bolt = recipes.Current.Pcb.BoltPoints[0];
        bolt.X = 333;
        bolt.Y = 444;
        bolt.FasteningX = 123;
        bolt.FasteningY = 234;
        bolt.FasteningZOffset = -0.75;
        bolt.Head = FasteningHead.Pickup;
        recipes.Current.Pcb.FasteningOrder.Add(bolt.Id);
        bolt.LightLevel = 87;
        recipes.Current.BoltInspection.LightLevel = 99;
        recipes.Current.BoltInspection.DataMatrix1.LightLevel = 53;
        recipes.Current.BoltInspection.DataMatrix2.LightLevel = 61;
        await recipes.SaveAsync("Inspection");
        editor.Draft.Pcb.BoltPoints[0].X = -999;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Null(editor.Error);
        var stored = store.LoadRecipe("Inspection");
        foreach (var recipe in new[] { stored, recipes.Current })
        {
            Assert.Equal((333d, 444d), (recipe.Pcb.BoltPoints[0].X, recipe.Pcb.BoltPoints[0].Y));
            Assert.Equal((123d, 234d), (recipe.Pcb.BoltPoints[0].FasteningX, recipe.Pcb.BoltPoints[0].FasteningY));
            Assert.Equal(-0.75, recipe.Pcb.BoltPoints[0].FasteningZOffset);
            Assert.Equal(FasteningHead.Pickup, recipe.Pcb.BoltPoints[0].Head);
            Assert.Equal(new[] { bolt.Id }, recipe.Pcb.FasteningOrder);
            Assert.Null(recipe.CarrierImages[1].Center);
            Assert.Equal(new PixelRegion(6, 6, 8, 8), recipe.CarrierImages[0].Region);
            Assert.Equal(53, recipe.BoltInspection.DataMatrix1.LightLevel);
            Assert.Equal(61, recipe.BoltInspection.DataMatrix2.LightLevel);
            Assert.Equal(99, recipe.BoltInspection.LightLevel);
            Assert.False(recipe.BoltInspection.DataMatrix1.TryInverted);
            Assert.Equal(72, recipe.BoltInspection.DataMatrix1.BinaryThreshold);
            Assert.Equal(87, recipe.Pcb.BoltPoints[0].LightLevel);
            Assert.Equal(214, recipe.Pcb.BoltPoints[0].BrightnessThreshold);
            Assert.Equal(0.4, recipe.Pcb.BoltPoints[0].MinimumBrightRatio);
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
        Assert.Equal(87, store.LoadRecipe("Inspection").Pcb.BoltPoints[0].LightLevel);
    }

    [Fact]
    public async Task FailedInspectionSaveDoesNotPublishDraftToAutomaticInspection()
    {
        var store = VirtualTest.OpenMachineStore();
        await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = new InspectionTeachingViewModel(store, recipes, new InspectionImages(store, NullLogger<InspectionImages>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
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
        Assert.Equal(before, JsonSerializer.Serialize(store.LoadRecipe("Inspection")));
    }

    [Fact]
    public async Task SavedProductionImagesCanBeReinspectedWithoutChangingRecordedResults()
    {
        var store = VirtualTest.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var directory = Path.Combine(Path.GetDirectoryName(store.DatabaseFile)!, $"Results-{Guid.NewGuid():N}");
        var databaseFile = Path.Combine(directory, "PCB-2026-09.db");
        var record = new PcbRecord(7, DateTimeOffset.Now, DateTimeOffset.Now, "INSPECTION", HeatSinkSlot.HeatSink1,
            "ABC", AssemblyResult.Ok, AssemblyResult.Ok, AssemblyResult.Ng, new Dictionary<Guid, BoltResult>(),
            new Dictionary<Guid, BoltResult>(), new Dictionary<Guid, bool> { [VirtualTest.BoltId(1)] = false }, [VirtualTest.BoltId(1)]);
        var image = new PcbInspectionImage(VirtualTest.BoltId(1), DateTimeOffset.Now, new(2, 2, 10, 10), false, null, 0, 0.5, png);
        store.SavePcb(databaseFile, record);
        store.SavePcbImage(databaseFile, record.Number, image);
        var editor = new InspectionTeachingViewModel(store, recipes, new InspectionImages(store, NullLogger<InspectionImages>.Instance), new() { Directory = directory },
            NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.LoadRecipeCommand.ExecuteAsync(null);
        await editor.RefreshHistoryCommand.ExecuteAsync(null);
        editor.SelectedRecord = Assert.Single(editor.Records);
        await editor.LoadRecordCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.True(editor.UseHistoryImageCommand.CanExecute(null));
        editor.UseHistoryImageCommand.Execute(null);
        Assert.Equal(VirtualTest.BoltId(1), editor.SelectedPoint!.Bolt?.Id);
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
        Assert.False(saved.BoltPresenceResults[VirtualTest.BoltId(1)]);
        var savedImage = Assert.Single(store.LoadPcbImages(saved));
        Assert.False(savedImage.Success);
        Assert.Equal(image.Region, savedImage.Region);
        Assert.Equal(png, savedImage.Png);
        Assert.Equal(png, store.LoadRecipeImage("Inspection", 2));
        editor.Draft.Name = "Other";
        Assert.False(editor.UseHistoryImageCommand.CanExecute(null));

        editor.SelectedRecord = editor.SelectedRecord! with { DatabaseFile = Path.Combine(directory, "missing.db") };
        await editor.LoadRecordCommand.ExecuteAsync(null);
        Assert.NotNull(editor.Error);
        Assert.Null(editor.LoadedRecord);
        Assert.Empty(editor.HistoryImages);
        Assert.Null(editor.SelectedHistoryImage);
        Assert.False(editor.UseHistoryImageCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReopeningTeachingRefreshesGrabbedPixelsAndKeepsSelectionAndUnsavedInspectionEdits()
    {
        var store = VirtualTest.OpenMachineStore();
        await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = new InspectionTeachingViewModel(store, recipes, new InspectionImages(store, NullLogger<InspectionImages>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.LoadRecipeCommand.ExecuteAsync(null);
        editor.SelectedPoint = editor.Points.Single(point => point.Bolt?.Id == VirtualTest.BoltId(1));
        editor.DrawRegionCommand.Execute(new Rect(0, 0, 8, 8));
        editor.Preview.BrightnessThreshold = 173;
        editor.Preview.MinimumBrightPercent = 42;
        editor.Draft.BoltInspection.DataMatrix1.TryInverted = false;
        var previousImage = editor.Preview.Image;
        var png = await SaveRecipeAsync(store, brightness: 90);

        editor.Activate();
        await editor.RefreshImagesCommand.ExecutionTask!;

        Assert.Null(editor.Error);
        Assert.Equal(VirtualTest.BoltId(1), editor.SelectedPoint!.Bolt?.Id);
        Assert.NotSame(previousImage, editor.Preview.Image);
        Assert.All(InspectionPreview.CreateFrame(editor.Preview.Image!).Pixels, pixel => Assert.Equal(90, pixel));
        Assert.Equal(new PixelRegion(6, 6, 8, 8), editor.SelectedPoint.Metadata!.Region);
        Assert.Equal(173, editor.Preview.BrightnessThreshold);
        Assert.Equal(42, editor.Preview.MinimumBrightPercent);
        Assert.False(editor.Draft.BoltInspection.DataMatrix1.TryInverted);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.Equal(png, store.LoadRecipeImage("Inspection", 2));
    }

    [Fact]
    public async Task ReopeningTeachingRemovesDeletedActivePointsAndKeepsRemainingInspectionEdits()
    {
        var store = VirtualTest.OpenMachineStore();
        await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = new InspectionTeachingViewModel(store, recipes, new InspectionImages(store, NullLogger<InspectionImages>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.LoadRecipeCommand.ExecuteAsync(null);
        editor.DrawRegionCommand.Execute(new Rect(0, 0, 8, 8));
        editor.DataMatrix!.BinaryThreshold = 73;
        editor.SelectedPoint = editor.Points.Single(point => point.Bolt?.Id == VirtualTest.BoltId(1));

        // Remove Bolt edits the active recipe before the operator saves it to the database.
        recipes.Current.Pcb.BoltPoints.Clear();
        recipes.Current.CarrierImages.RemoveAll(tile => !tile.IsBarcode);
        Assert.Single(store.LoadRecipe("Inspection").Pcb.BoltPoints);

        editor.Activate();
        await editor.RefreshImagesCommand.ExecutionTask!;

        Assert.Null(editor.Error);
        Assert.Empty(editor.Draft.Pcb.BoltPoints);
        Assert.True(Assert.Single(editor.Points, point => point.Metadata is not null).IsDataMatrix);
        Assert.Same(editor.Points[0], editor.SelectedPoint);
        Assert.Null(editor.SelectedPoint!.Bolt);
        Assert.True(editor.IsDataMatrixSelected);
        Assert.True(editor.Preview.HasImage);
        Assert.Equal(73, editor.DataMatrix!.BinaryThreshold);
        Assert.Equal(new PixelRegion(6, 6, 8, 8), editor.SelectedPoint!.Metadata!.Region);

        await recipes.SaveAsync("Inspection");
        await editor.SaveCommand.ExecuteAsync(null);
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.Empty(recipes.Current.Pcb.BoltPoints);
        Assert.Empty(store.LoadRecipe("Inspection").Pcb.BoltPoints);
        Assert.True(Assert.Single(store.LoadRecipe("Inspection").CarrierImages).IsBarcode);
        Assert.True(Assert.Single(editor.Points, point => point.Metadata is not null).IsDataMatrix);

        // With no images, keep the Data Matrix targets but clear the previous preview.
        recipes.Current.CarrierImages.Clear();
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.All(editor.Points, point => Assert.True(point.IsDataMatrix));
        Assert.All(editor.Points, point => Assert.Null(point.Metadata));
        Assert.False(editor.Preview.HasImage);
    }

    [Fact]
    public async Task LoadingAnotherInspectionRecipeKeepsItsOwnStoredPoints()
    {
        var store = VirtualTest.OpenMachineStore();
        await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        await recipes.SaveAsync("Other");
        recipes.Current.Pcb.BoltPoints.Clear();
        recipes.Current.CarrierImages.Clear();
        var editor = new InspectionTeachingViewModel(store, recipes, new InspectionImages(store, NullLogger<InspectionImages>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance)
        {
            SelectedRecipeName = "Inspection",
        };

        await editor.LoadRecipeCommand.ExecuteAsync(null);
        await editor.RefreshImagesCommand.ExecuteAsync(null);

        Assert.Null(editor.Error);
        Assert.Equal("Inspection", editor.Draft.Name);
        Assert.Equal(3, editor.Points.Count);
        Assert.Single(editor.Draft.Pcb.BoltPoints);
        Assert.Empty(recipes.Current.Pcb.BoltPoints);
        Assert.Empty(recipes.Current.CarrierImages);
    }

    private static async Task<byte[]> SaveRecipeAsync(MachineStore store, byte brightness = 0)
    {
        var png = await Task.Run(() =>
        {
            var bitmap = InspectionPreview.CreateBitmap(new ImageFrame(20, 20, 60, Enumerable.Repeat(brightness, 1200).ToArray()));
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap, null, null, null));
            encoder.Save(stream);
            return stream.ToArray();
        });
        var recipe = new Recipe { Name = "Inspection" };
        recipe.Pcb.BoltPoints.Add(new() { Id = VirtualTest.BoltId(1), X = 10, Y = 20 });
        recipe.CarrierImages = [
            new() { Number = 1, IsBarcode = true, Region = new(2, 2, 10, 10), Center = new() { X = 1, Y = 2 } },
            new() { Number = 2, BoltId = VirtualTest.BoltId(1), Region = new(2, 2, 10, 10) },
        ];
        store.SaveRecipe(recipe, images: [new(1, png), new(2, png)]);
        return png;
    }
}
