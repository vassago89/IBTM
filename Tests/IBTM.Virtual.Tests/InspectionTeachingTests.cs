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
using System.Windows.Threading;
using System.Windows.Media.Imaging;
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
    public async Task PointSelectionUpdatesBoundParameters()
    {
        var store = VirtualTest.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipe = store.LoadRecipe<Recipe>("Inspection");
        recipe.BoltInspection.DataMatrix1.BinaryThreshold = 51;
        recipe.BoltInspection.DataMatrix2.BinaryThreshold = 180;
        recipe.BoltInspection.DataMatrix2.TryInverted = false;
        recipe.Pcb.BoltPoints[0].BrightnessThreshold = 91;
        recipe.Pcb.BoltPoints[0].MinimumBrightRatio = 0.25;
        recipe.Pcb.BoltPoints.Add(new() { Number = 2, BrightnessThreshold = 172, MinimumBrightRatio = 0.75 });
        recipe.CarrierImages.Add(new() { Number = 3, BoltNumber = 2, Region = new(2, 2, 10, 10) });
        recipe.CarrierImages.Add(new() { Number = 4, IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink2, Region = new(2, 2, 10, 10) });
        store.SaveRecipe(recipe.Name, recipe, [1, 2, 3, 4], images: [new(1, png), new(2, png), new(3, png), new(4, png)]);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = new InspectionTeachingViewModel(store, recipes, new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.LoadRecipeCommand.ExecuteAsync(null);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
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
                list.SelectedItem = editor.Points.Single(point => point.Metadata.IsBarcode && point.Metadata.HeatSink == HeatSinkSlot.HeatSink2);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal("180", matrixThreshold.Text);
                Assert.Equal(73, editor.Draft.BoltInspection.DataMatrix1.BinaryThreshold);
                Assert.False(inverted.IsChecked);
                list.SelectedItem = editor.Points.Single(point => point.Metadata.BoltNumber == 1);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(1, editor.SelectedBolt!.Number);
                Assert.Equal("91", threshold.Text);
                Assert.Equal("25", minimum.Text);
                threshold.Text = "103";
                list.SelectedItem = editor.Points.Single(point => point.Metadata.BoltNumber == 2);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(2, editor.SelectedBolt!.Number);
                Assert.Equal("172", threshold.Text);
                Assert.Equal("75", minimum.Text);
                Assert.Equal(103, editor.Draft.Pcb.BoltPoints[0].BrightnessThreshold);
                GC.KeepAlive(root);
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task BinaryPreviewFollowsSelectedPointThresholdAndRoi()
    {
        var recipe = new Recipe();
        var bolt = new BoltPoint { Number = 1, BrightnessThreshold = 128 };
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

    [Fact]
    public async Task ReopeningTeachingRefreshesGrabbedPixelsAndKeepsSelectionAndUnsavedInspectionEdits()
    {
        var store = VirtualTest.OpenMachineStore();
        await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = new InspectionTeachingViewModel(store, recipes, new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.LoadRecipeCommand.ExecuteAsync(null);
        editor.SelectedPoint = editor.Points.Single(point => point.Metadata.BoltNumber == 1);
        editor.DrawRegionCommand.Execute(new Rect(0, 0, 8, 8));
        editor.Preview.BrightnessThreshold = 173;
        editor.Preview.MinimumBrightPercent = 42;
        editor.Draft.BoltInspection.DataMatrix1.TryInverted = false;
        var previousImage = editor.Preview.Image;
        var png = await SaveRecipeAsync(store, brightness: 90);

        editor.Activate();
        await editor.RefreshImagesCommand.ExecutionTask!;

        Assert.Null(editor.Error);
        Assert.Equal(1, editor.SelectedPoint!.Metadata.BoltNumber);
        Assert.NotSame(previousImage, editor.Preview.Image);
        Assert.All(InspectionPreview.CreateFrame(editor.Preview.Image!).Pixels, pixel => Assert.Equal(90, pixel));
        Assert.Equal(new PixelRegion(6, 6, 8, 8), editor.SelectedPoint.Metadata.Region);
        Assert.Equal(173, editor.Preview.BrightnessThreshold);
        Assert.Equal(42, editor.Preview.MinimumBrightPercent);
        Assert.False(editor.Draft.BoltInspection.DataMatrix1.TryInverted);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.Equal(png, store.LoadRecipeImage("Inspection", 2));
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
        recipe.Pcb.BoltPoints.Add(new() { Number = 1, X = 10, Y = 20 });
        recipe.CarrierImages = [
            new() { Number = 1, IsBarcode = true, Region = new(2, 2, 10, 10), Center = new() { X = 1, Y = 2 } },
            new() { Number = 2, BoltNumber = 1, Region = new(2, 2, 10, 10), Center = new() { X = 10, Y = 20 } },
        ];
        store.SaveRecipe(recipe.Name, recipe, [1, 2], images: [new(1, png), new(2, png)]);
        return png;
    }
}
