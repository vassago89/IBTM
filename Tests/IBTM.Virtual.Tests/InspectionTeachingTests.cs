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
using IBTM.Device;
using IBTM.Inspection;
using IBTM.Storage;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class InspectionTeachingTests
{
    private static InspectionTeachingViewModel CreateEditor(MachineStore store, RecipeManager recipes,
        InspectionImageLoader images, PcbHistorySettings history,
        Microsoft.Extensions.Logging.ILogger<InspectionTeachingViewModel> log)
    {
        var results = new ResultsViewModel(store, history,
            new PcbResultsViewModel(store, recipes, images, NullLogger<PcbResultsViewModel>.Instance),
            NullLogger<ResultsViewModel>.Instance);
        return new(recipes, images, results, log);
    }

    [Fact]
    public async Task DamagedReferenceImagesKeepHealthyPreviewsAndSurviveSavingAndSaveAs()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipe = store.LoadRecipe("Inspection");
        var missing = new BoltPoint { X = 30, Y = 40 };
        recipe.Pcb.BoltPoints.Add(missing);
        recipe.CarrierImages.Add(new() { Number = 3, BoltId = missing.Id });
        byte[] damaged = [1, 2, 3];
        store.SaveRecipe(recipe, images: [new(1, png), new(2, damaged)]);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync(recipe.Name);
        var images = new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance);
        var inspection = CreateEditor(store, recipes, images, new(),
            NullLogger<InspectionTeachingViewModel>.Instance);
        await inspection.RefreshImagesCommand.ExecuteAsync(null);
        Assert.True(inspection.Preview.HasImage);
        Assert.Null(inspection.Error);
        Assert.Equal(3, inspection.Preview.Recipe.CarrierImages.Count);
        foreach (var point in inspection.Points.Where(point => point.Bolt is not null))
        {
            inspection.SelectedPoint = point;
            Assert.False(inspection.Preview.HasImage);
            Assert.NotNull(inspection.Error);
            Assert.False(inspection.IsInspectAllowed);
        }
        inspection.SelectedPoint = inspection.Points.First();
        inspection.DataMatrix!.ThresholdMinimum = 73;
        await inspection.SaveCommand.ExecuteAsync(null);
        Assert.Null(inspection.Error);

        var loaded = await images.LoadRecipeAsync(recipes.Current);
        Assert.NotNull(loaded[0].Image);
        Assert.Null(loaded[1].Image);
        Assert.Equal(damaged, loaded[1].UnreadablePng);
        Assert.Null(loaded[2].Image);
        Assert.Null(loaded[2].UnreadablePng);
        var editor = new RecipeEditorViewModel(recipes, store, new());
        loaded[0] = loaded[0] with
        {
            Image = InspectionPreviewViewModel.CreateBitmap(new ImageFrame(20, 20, 60, Enumerable.Repeat((byte)90, 1200).ToArray())),
        };
        foreach (var name in new[] { "Inspection", "Copy" })
        {
            editor.Name = name;
            Assert.True(await editor.SaveAsync(loaded), editor.Error);
            Assert.Equal(3, store.LoadRecipe(name).CarrierImages.Count);
            Assert.Equal(damaged, store.LoadRecipeImage(name, 2));
            Assert.Throws<FileNotFoundException>(() => store.LoadRecipeImage(name, 3));
            var reloaded = await images.LoadRecipeAsync(store.LoadRecipe(name));
            Assert.All(InspectionPreviewViewModel.CreateFrame(reloaded[0].Image!).Pixels, pixel => Assert.Equal(90, pixel));
        }
        loaded[1] = loaded[1] with { Image = loaded[0].Image, Error = null, UnreadablePng = null };
        Assert.True(await editor.SaveAsync(loaded), editor.Error);
        var repaired = await images.LoadRecipeAsync(store.LoadRecipe("Copy"));
        Assert.NotNull(repaired[1].Image);
        Assert.Null(repaired[1].Error);
        Assert.NotNull(repaired[2].Error);
    }

    [Fact]
    public async Task RecipePointsShareNamesCoordinatesAndImagesAcrossTeaching()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipe = store.LoadRecipe("Inspection");
        var bolt = recipe.Pcb.BoltPoints[0];
        bolt.Name = "Connector bolt";
        bolt.BrightnessThreshold = 91;
        recipe.CarrierImages[1].BoltId = null;
        store.SaveRecipe(recipe);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var images = new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance);
        var editor = CreateEditor(store, recipes, images, new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        editor.SelectedPoint = Assert.Single(editor.Points, point => point.Bolt is not null);
        var teaching = new TeachingPoint(new(IBTM.Device.TeachingTarget.BoltReference,
            MotionGroup.InspectionGantry, IBTM.Device.TeachMode.Image) { Bolt = recipes.Current.Pcb.BoltPoints[0] },
            new(), recipes);

        Assert.Null(editor.Error);
        Assert.Null(editor.SelectedPoint.Metadata);
        Assert.Equal(teaching.Name, editor.SelectedPoint.Name);
        Assert.Equal(teaching.PositionLabel, editor.SelectedPoint.PositionLabel);
        Assert.Equal((10d, 20d), (editor.SelectedPoint.Position!.X, editor.SelectedPoint.Position.Y));
        Assert.Equal("Connector bolt", editor.SelectedPoint.Name);
        Assert.Equal(91, editor.Preview.BrightnessThreshold);
        Assert.False(editor.Preview.HasImage);
        Assert.False(editor.IsInspectAllowed);
        Assert.False(editor.IsDrawRegionAllowed);
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
        await recipes.SaveAsync("Inspection", tiles, [new(1, png), new(2, png), new(3, png)]);
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        var loadedImages = await images.LoadRecipeAsync(recipes.Current);
        Assert.Equal(bolt.Id, editor.SelectedPoint!.Bolt!.Id);
        Assert.Equal(teaching.Name, editor.SelectedPoint.Name);
        Assert.Equal(teaching.PositionLabel, editor.SelectedPoint.PositionLabel);
        Assert.Equal(InspectionPreviewViewModel.CreateFrame(teaching.Inspection!.FindImage(loadedImages)!.Image!).Pixels,
            InspectionPreviewViewModel.CreateFrame(editor.Preview.Image!).Pixels);
        Assert.NotNull(editor.Preview.Overlay);
        Assert.True(editor.IsInspectAllowed);
        Assert.True(editor.IsDrawRegionAllowed);
        Assert.Null(store.LoadRecipe("Inspection").CarrierImages.Single(tile => tile.Number == 2).BoltId);
    }

    [Fact]
    public async Task RecreatedBoltDoesNotInheritDeletedPointInspectionEdits()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = CreateEditor(store, recipes, new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        var removedId = editor.Preview.Recipe.Pcb.BoltPoints[0].Id;
        editor.Preview.Recipe.Pcb.BoltPoints[0].BrightnessThreshold = 91;
        editor.Preview.Recipe.CarrierImages[1].Region = new(2, 2, 10, 10);
        editor.Preview.Recipe.BoltInspection.DataMatrix1.ThresholdMinimum = 73;

        recipes.Current.Pcb.BoltPoints.Clear();
        recipes.Current.CarrierImages.RemoveAll(tile => !tile.IsBarcode);
        var replacement = new BoltPoint { X = 100, Y = 200, BrightnessThreshold = 180 };
        recipes.Current.Pcb.BoltPoints.Add(replacement);
        var tile = new CarrierImageTile { Number = 2, BoltId = replacement.Id };
        await recipes.SaveAsync("Inspection", [recipes.Current.CarrierImages[0], tile], [new(1, png), new(2, png)]);

        // Saving before refreshing the point list must leave the replacement untouched.
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.Equal(180, recipes.Current.Pcb.BoltPoints[0].BrightnessThreshold);
        Assert.Null(recipes.Current.CarrierImages[1].Region);

        await editor.RefreshImagesCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.NotEqual(removedId, editor.Preview.Recipe.Pcb.BoltPoints[0].Id);
        Assert.Equal(replacement.Id, editor.Preview.Recipe.Pcb.BoltPoints[0].Id);
        Assert.Equal(180, editor.Preview.Recipe.Pcb.BoltPoints[0].BrightnessThreshold);
        Assert.Null(editor.Preview.Recipe.CarrierImages[1].Region);
        Assert.Equal(73, editor.Preview.Recipe.BoltInspection.DataMatrix1.ThresholdMinimum);

        await editor.SaveCommand.ExecuteAsync(null);
        var saved = store.LoadRecipe("Inspection");
        Assert.Equal(replacement.Id, saved.Pcb.BoltPoints[0].Id);
        Assert.Equal(180, saved.Pcb.BoltPoints[0].BrightnessThreshold);
        Assert.Null(saved.CarrierImages[1].Region);
    }

    [Fact]
    public async Task CaseOnlyRecipeNameChangeKeepsActiveInspectionEditsAndOneListEntry()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var inspection = CreateEditor(store, recipes, new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await inspection.RefreshImagesCommand.ExecuteAsync(null);
        var editor = new RecipeEditorViewModel(recipes, store, new()) { Name = "inspection" };
        await editor.RefreshCommand.ExecuteAsync(null);
        Assert.True(await editor.SaveAsync());
        Assert.Equal("Inspection", Assert.Single(editor.Recipes));
        Assert.Single(store.RecipeNames);

        inspection.Preview.Recipe.BoltInspection.DataMatrix1.ThresholdMinimum = 73;
        await inspection.SaveCommand.ExecuteAsync(null);
        Assert.Null(inspection.Error);
        Assert.Equal(73, recipes.Current.BoltInspection.DataMatrix1.ThresholdMinimum);
        Assert.Equal(73, store.LoadRecipe("Inspection").BoltInspection.DataMatrix1.ThresholdMinimum);

        // Unsaved point deletion belongs to the same active recipe despite the name casing.
        recipes.Current.Pcb.BoltPoints.Clear();
        recipes.Current.CarrierImages.RemoveAll(tile => !tile.IsBarcode);
        await inspection.RefreshImagesCommand.ExecuteAsync(null);
        Assert.Null(inspection.Error);
        Assert.True(Assert.Single(inspection.Points, point => point.Metadata is not null).IsDataMatrix);
        Assert.Empty(inspection.Preview.Recipe.Pcb.BoltPoints);

        var reopened = CreateEditor(store, recipes, new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        reopened.Activate();
        await reopened.RefreshImagesCommand.ExecutionTask!;
        Assert.Same(recipes.Current, reopened.Preview.Recipe);
        Assert.Null(reopened.Error);
        Assert.True(Assert.Single(reopened.Points, point => point.Metadata is not null).IsDataMatrix);
    }

    [Fact]
    public async Task DistinctNonAsciiRecipeNamesKeepTheirOwnImagesAndInspectionSettings()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        await recipes.SaveAsync("검사Ä");
        var inspection = CreateEditor(store, recipes, new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await inspection.RefreshImagesCommand.ExecuteAsync(null);
        var editor = new RecipeEditorViewModel(recipes, store, new()) { Name = "검사ä" };
        await editor.RefreshCommand.ExecuteAsync(null);
        Assert.True(await editor.SaveAsync());
        Assert.Equal(3, editor.Recipes.Count);
        Assert.Equal(3, store.RecipeNames.Count);
        Assert.Equal(png, store.LoadRecipeImage("검사ä", 1));
        var originalThreshold = recipes.Current.BoltInspection.DataMatrix1.ThresholdMinimum;
        await recipes.LoadAsync("검사Ä");
        await inspection.RefreshImagesCommand.ExecutionTask!;
        Assert.Same(recipes.Current, inspection.Preview.Recipe);
        inspection.Preview.Recipe.BoltInspection.DataMatrix1.ThresholdMinimum = 73;
        await inspection.SaveCommand.ExecuteAsync(null);
        Assert.Null(inspection.Error);
        Assert.Equal(73, store.LoadRecipe("검사Ä").BoltInspection.DataMatrix1.ThresholdMinimum);
        Assert.Equal(73, recipes.Current.BoltInspection.DataMatrix1.ThresholdMinimum);
        Assert.Equal(originalThreshold, store.LoadRecipe("검사ä").BoltInspection.DataMatrix1.ThresholdMinimum);
    }

    [Fact]
    public async Task CaptureSavePreservesInspectionEditsMadeAfterCaptureStarted()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        List<CarrierImageTile> captured = [
            new() { Number = 1, IsBarcode = true, Center = new() { X = 30, Y = 40 } },
            recipes.Current.CarrierImages[1],
        ];
        var edited = recipes.Current;
        edited.CarrierImages[0].Region = new(4, 5, 6, 7);
        edited.BoltInspection.DataMatrix1.ThresholdMinimum = 81;
        await recipes.SaveInspectionAsync(edited);

        await recipes.SaveAsync("Inspection", captured, [new(1, png), new(2, png)]);

        foreach (var recipe in new[] { recipes.Current, store.LoadRecipe("Inspection") })
        {
            Assert.Equal(new PixelRegion(4, 5, 6, 7), recipe.CarrierImages[0].Region);
            Assert.Equal(81, recipe.BoltInspection.DataMatrix1.ThresholdMinimum);
            Assert.Equal(30, recipe.CarrierImages[0].Center!.X);
        }
        Assert.Equal(new PixelRegion(4, 5, 6, 7), captured[0].Region);
    }

    [Fact]
    public async Task InspectionSaveWaitsForCaptureCommitAndKeepsCapturedPosition()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var edited = recipes.Current;
        List<CarrierImageTile> captured = [
            new() { Number = 1, IsBarcode = true, Center = new() { X = 30, Y = 40 } },
            recipes.Current.CarrierImages[1],
        ];
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        IEnumerable<RecipeImage> Images()
        {
            writing.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            yield return new(1, png);
            yield return new(2, png);
        }
        var capture = recipes.SaveAsync("Inspection", captured, Images());
        var save = Task.CompletedTask;
        try
        {
            Assert.True(await Task.Run(() => writing.Wait(TimeSpan.FromSeconds(5))));
            edited.CarrierImages[0].Region = new(4, 5, 6, 7);
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
        var store = VirtualTestSupport.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipe = store.LoadRecipe("Inspection");
        recipe.BoltInspection.DataMatrix1.ThresholdMinimum = 51;
        recipe.BoltInspection.DataMatrix2.ThresholdMinimum = 180;
        recipe.BoltInspection.DataMatrix2.TryInverted = false;
        recipe.Pcb.BoltPoints[0].BrightnessThreshold = 91;
        recipe.Pcb.BoltPoints[0].MinimumBrightRatio = 0.25;
        recipe.Pcb.BoltPoints.Add(new() { Id = VirtualTestSupport.BoltId(2), BrightnessThreshold = 172, MinimumBrightRatio = 0.75 });
        recipe.CarrierImages.Add(new() { Number = 3, BoltId = VirtualTestSupport.BoltId(2), Region = new(2, 2, 10, 10) });
        recipe.CarrierImages.Add(new() { Number = 4, IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink2, Region = new(2, 2, 10, 10) });
        store.SaveRecipe(recipe, images: [new(1, png), new(2, png), new(3, png), new(4, png)]);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = CreateEditor(store, recipes, new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        await VirtualTestSupport.RunOnStaAsync(() =>
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
            matrixThreshold.SetBinding(TextBox.TextProperty, new Binding("Preview.DataMatrixThresholdMinimum")
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
            Assert.False(editor.Preview.Recipe.BoltInspection.DataMatrix1.TryInverted);
            matrixThreshold.Text = "73";
            list.SelectedItem = editor.Points.Single(point => point.IsDataMatrix && point.HeatSink == HeatSinkSlot.HeatSink2);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal("180", matrixThreshold.Text);
            Assert.Equal(73, editor.Preview.Recipe.BoltInspection.DataMatrix1.ThresholdMinimum);
            Assert.False(inverted.IsChecked);
            list.SelectedItem = editor.Points.Single(point => point.Bolt?.Id == VirtualTestSupport.BoltId(1));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(VirtualTestSupport.BoltId(1), editor.SelectedPoint!.Bolt!.Id);
            Assert.Equal("91", threshold.Text);
            Assert.Equal("25", minimum.Text);
            threshold.Text = "103";
            list.SelectedItem = editor.Points.Single(point => point.Bolt?.Id == VirtualTestSupport.BoltId(2));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(VirtualTestSupport.BoltId(2), editor.SelectedPoint!.Bolt!.Id);
            Assert.Equal("172", threshold.Text);
            Assert.Equal("75", minimum.Text);
            Assert.Equal(103, editor.Preview.Recipe.Pcb.BoltPoints[0].BrightnessThreshold);
            GC.KeepAlive(root);
        });
    }

    [Theory]
    [InlineData(HeatSinkSlot.HeatSink1, false)]
    [InlineData(HeatSinkSlot.HeatSink2, false)]
    [InlineData(HeatSinkSlot.HeatSink1, true)]
    public async Task EditedDataMatrixThresholdMinimumIsSharedWithAutomaticInspection(HeatSinkSlot heatSink, bool editDuringPreparation)
    {
        await using var services = MachineTestSupport.CreateDiagnosticServices();
        var recipes = services.GetRequiredService<RecipeManager>();
        var camera = services.GetRequiredService<VirtualCamera>();
        camera.SourceImage = await new VirtualCamera(
            () => (13, 15, 0), () => [],
            () => [new(new() { X = 13, Y = 15 }, 4, 4, "PCB-123")]).CaptureAsync();
        var image = InspectionPreviewViewModel.CreateBitmap(camera.SourceImage);
        recipes.Current.BoltInspection.DataMatrix1.ThresholdMinimum = 0;
        recipes.Current.BoltInspection.DataMatrix2.ThresholdMinimum = 0;
        recipes.Current.BoltInspection.DataMatrix1.ThresholdMaximum = 0;
        recipes.Current.BoltInspection.DataMatrix2.ThresholdMaximum = 0;
        var recipeEditor = services.GetRequiredService<RecipeEditorViewModel>();
        Assert.True(await recipeEditor.SaveAsync(Enum.GetValues<HeatSinkSlot>().Select((pcb, index) =>
            new RecipeImageItem(new CarrierImageTile
            {
                Number = index + 1, HeatSink = pcb, IsBarcode = true, Center = new(),
                Region = new(0, 0, image.PixelWidth, image.PixelHeight),
            }, image)).ToArray()));
        var editor = services.GetRequiredService<InspectionTeachingViewModel>();
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        editor.SelectedPoint = editor.Points.Single(point => point.IsDataMatrix && point.HeatSink == heatSink);
        var inspector = services.GetRequiredService<InspectionStation>();
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry);
        motion.Initialize();
        Assert.True(await inspector.HomeHorizontalAsync());
        Assert.False((await inspector.ReadBarcodeAsync(heatSink, CancellationToken.None)).Success);

        foreach (var threshold in new[] { 128, 0 })
        {
            var context = new VirtualTestSupport.PausedSynchronizationContext();
            Task<InspectionCapture>? pending = null;
            try
            {
                if (editDuringPreparation)
                {
                    var previous = SynchronizationContext.Current;
                    try
                    {
                        SynchronizationContext.SetSynchronizationContext(context);
                        pending = inspector.ReadBarcodeAsync(heatSink, CancellationToken.None);
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(previous);
                    }
                    await VirtualTestSupport.WaitUntilAsync(() => context.HasPending);
                }
                await VirtualTestSupport.RunOnStaAsync(() =>
                {
                    var input = new TextBox();
                    var root = new UserControl
                    {
                        DataContext = editor, Content = input,
                        BindingGroup = new BindingGroup { Name = "InspectionInputs" },
                    };
                    input.SetBinding(TextBox.TextProperty, new Binding("Preview.DataMatrixThresholdMinimum")
                    {
                        BindingGroupName = "InspectionInputs", UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                        ValidatesOnExceptions = true,
                    });
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    input.Text = threshold.ToString();
                    editor.DataMatrix!.ThresholdMaximum = threshold;
                    Assert.False(Validation.GetHasError(input));
                    Assert.Equal(threshold, editor.Preview.DataMatrixThresholdMinimum);
                    GC.KeepAlive(root);
                });
                Assert.Same(recipes.Current, editor.Preview.Recipe);
                Assert.Equal(threshold, recipes.Current.BoltInspection.GetDataMatrix(heatSink).ThresholdMinimum);
                context.Release();
                var result = await (pending ?? inspector.ReadBarcodeAsync(heatSink, CancellationToken.None));
                Assert.Equal(threshold == 128 ? "PCB-123" : null, result.Barcode);
                Assert.Equal(threshold == 128, result.Success);
                Assert.Equal(threshold, result.Threshold);
                Assert.Equal(threshold == 0, result.Dilated);
                await editor.SaveCommand.ExecuteAsync(null);
                Assert.Null(editor.Error);
                var stored = services.GetRequiredService<MachineStore>().LoadRecipe(recipes.Current.Name);
                Assert.Equal(threshold, stored.BoltInspection.GetDataMatrix(heatSink).ThresholdMinimum);
                var other = heatSink == HeatSinkSlot.HeatSink1 ? HeatSinkSlot.HeatSink2 : HeatSinkSlot.HeatSink1;
                Assert.Equal(0, recipes.Current.BoltInspection.GetDataMatrix(other).ThresholdMinimum);
            }
            finally
            {
                context.Release();
                if (pending is not null)
                    await pending;
            }
        }
    }

    [Fact]
    public async Task BinaryPreviewFollowsSelectedPointThresholdAndRoi()
    {
        var recipe = new Recipe();
        var bolt = new BoltPoint { Id = VirtualTestSupport.BoltId(1), BrightnessThreshold = 128 };
        recipe.Pcb.BoltPoints.Add(bolt);
        recipe.BoltInspection.DataMatrix1.ThresholdMinimum = 128;
        var pixels = Enumerable.Repeat((byte)90, 20 * 20 * 3).ToArray();
        var frame = new ImageFrame(20, 20, 60, pixels);
        var original = InspectionPreviewViewModel.CreateBitmap(frame);
        var preview = new InspectionPreviewViewModel(recipe);
        preview.Clear(HeatSinkSlot.HeatSink1);
        preview.SetSavedImage(original, new(2, 3, 10, 12));
        Assert.Equal((10, 12), (preview.Overlay!.PixelWidth, preview.Overlay.PixelHeight));
        Assert.All(InspectionPreviewViewModel.CreateFrame(preview.Overlay).Pixels, pixel => Assert.Equal(0, pixel));
        preview.DataMatrixThresholdMinimum = 80;
        Assert.All(InspectionPreviewViewModel.CreateFrame(preview.Overlay!).Pixels, pixel => Assert.Equal(255, pixel));
        Assert.Equal(80, recipe.BoltInspection.DataMatrix1.ThresholdMinimum);
        await preview.InspectAsync(CancellationToken.None);
        Assert.NotNull(preview.Overlay);
        Assert.Same(original, preview.Image);
        preview.SetSavedImage(original, new(1, 1, 6, 8));
        Assert.Equal((6, 8), (preview.Overlay!.PixelWidth, preview.Overlay.PixelHeight));
        preview.Clear(bolt: bolt);
        preview.SetSavedImage(original, new(2, 3, 10, 12));
        Assert.All(InspectionPreviewViewModel.CreateFrame(preview.Overlay!).Pixels, pixel => Assert.Equal(0, pixel));
        preview.BrightnessThreshold = 80;
        Assert.All(InspectionPreviewViewModel.CreateFrame(preview.Overlay!).Pixels, pixel => Assert.Equal(255, pixel));
        preview.MinimumBrightPercent = 42.5;
        Assert.Equal(0.425, bolt.MinimumBrightRatio);
        Assert.Throws<ArgumentOutOfRangeException>(() => preview.MinimumBrightPercent = 101);
        Assert.Throws<ArgumentOutOfRangeException>(() => preview.MinimumBrightPercent = double.NaN);
        Assert.Equal(0.425, bolt.MinimumBrightRatio);
        Assert.Same(original, preview.Image);
        Assert.Equal(pixels, InspectionPreviewViewModel.CreateFrame(preview.Image!).Pixels);
    }

    [Fact]
    public async Task InspectionEditsShareTheActiveRecipeAndSaveWithoutReplacingPositionsOrImages()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = CreateEditor(store, recipes, new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.Same(recipes.Current, editor.Preview.Recipe);
        Assert.True(editor.Preview.HasImage);
        editor.DrawRegionCommand.Execute(new Rect(0, 0, 8, 8));
        editor.DataMatrix!.TryInverted = false;
        editor.DataMatrix.ThresholdMinimum = 72;
        editor.DataMatrix.ThresholdMaximum = 80;
        editor.DataMatrix.ThresholdStep = 5;
        editor.DataMatrix.DilationRadius = 2;
        editor.SelectedPoint = editor.Points.Single(point => !point.IsDataMatrix);
        editor.Preview.BrightnessThreshold = 214;
        editor.Preview.MinimumBrightPercent = 40;
        editor.SelectedPoint.Bolt!.MinimumTurns = 12.5;
        editor.SelectedPoint.Bolt.MaximumTurns = 15.75;
        Assert.Equal(12.5, recipes.Current.Pcb.BoltPoints[0].MinimumTurns);
        Assert.Equal(15.75, recipes.Current.Pcb.BoltPoints[0].MaximumTurns);
        Assert.Equal(214, recipes.Current.Pcb.BoltPoints[0].BrightnessThreshold);
        Assert.Null(recipes.Current.Pcb.BoltPoints[0].LightLevel);
        Assert.Equal(255, recipes.Current.BoltInspection.LightLevel);
        Assert.False(recipes.Current.BoltInspection.DataMatrix1.TryInverted);

        // Teaching and inspection retain the same objects as coordinates and lights change.
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
        editor.SelectedPoint.Bolt.MaximumTurns = 18.75;
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
            Assert.Equal(72, recipe.BoltInspection.DataMatrix1.ThresholdMinimum);
            Assert.Equal(80, recipe.BoltInspection.DataMatrix1.ThresholdMaximum);
            Assert.Equal(5, recipe.BoltInspection.DataMatrix1.ThresholdStep);
            Assert.Equal(2, recipe.BoltInspection.DataMatrix1.DilationRadius);
            Assert.Equal(0, recipe.BoltInspection.DataMatrix2.ThresholdMinimum);
            Assert.Equal(87, recipe.Pcb.BoltPoints[0].LightLevel);
            Assert.Equal(214, recipe.Pcb.BoltPoints[0].BrightnessThreshold);
            Assert.Equal(0.4, recipe.Pcb.BoltPoints[0].MinimumBrightRatio);
            Assert.Equal(12.5, recipe.Pcb.BoltPoints[0].MinimumTurns);
            Assert.Equal(18.75, recipe.Pcb.BoltPoints[0].MaximumTurns);
        }
        Assert.Equal(png, store.LoadRecipeImage("Inspection", 1));
        Assert.Equal(png, store.LoadRecipeImage("Inspection", 2));
        Assert.Same(bolt, recipes.Current.Pcb.BoltPoints[0]);
        Assert.Same(bolt, editor.SelectedPoint.Bolt);
    }

    [Fact]
    public async Task FailedInspectionSaveKeepsLiveEditsAndReportsTheDatabaseFailure()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = CreateEditor(store, recipes, new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.NotNull(editor.Message);
        var before = JsonSerializer.Serialize(recipes.Current);
        editor.DataMatrix!.ThresholdMinimum = 17;
        using var connection = new SqliteConnection($"Data Source={store.DatabaseFile}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER FailInspection BEFORE UPDATE ON Recipes BEGIN SELECT RAISE(ABORT, 'inspection write failed'); END";
        await command.ExecuteNonQueryAsync();

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(editor.Error);
        Assert.Null(editor.Message);
        Assert.Same(recipes.Current, editor.Preview.Recipe);
        Assert.Equal(17, recipes.Current.BoltInspection.DataMatrix1.ThresholdMinimum);
        Assert.Equal(before, JsonSerializer.Serialize(store.LoadRecipe("Inspection")));
    }

    [Fact]
    public async Task SavedProductionImagesCanBeReinspectedWithoutChangingRecordedResults()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var png = await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var directory = Path.Combine(Path.GetDirectoryName(store.DatabaseFile)!, $"Results-{Guid.NewGuid():N}");
        var databaseFile = Path.Combine(directory, "PCB-2026-09.db");
        var record = new PcbRecord(7, DateTimeOffset.Now, DateTimeOffset.Now, "INSPECTION", HeatSinkSlot.HeatSink1,
            "ABC", AssemblyResult.Ok, AssemblyResult.Ok, AssemblyResult.Ng, new Dictionary<Guid, BoltResult>(),
            new Dictionary<Guid, BoltResult>(), new Dictionary<Guid, bool> { [VirtualTestSupport.BoltId(1)] = false }, [VirtualTestSupport.BoltId(1)]);
        var image = new PcbInspectionImage(VirtualTestSupport.BoltId(1), DateTimeOffset.Now, new(2, 2, 10, 10), false, null, 0, 0.5, png);
        store.SavePcb(databaseFile, record);
        store.SavePcbImage(databaseFile, record.Number, image);
        var editor = CreateEditor(store, recipes, new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance), new() { Directory = directory },
            NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        await editor.Results.RefreshHistoryCommand.ExecuteAsync(null);
        editor.ImageMode = InspectionImageMode.Recorded;
        editor.Results.SelectedRecord = Assert.Single(editor.Results.Records);
        await editor.LoadRecordCommand.ExecutionTask!;
        Assert.Null(editor.Error);
        Assert.NotNull(editor.HistoryImageTarget);
        Assert.Equal(VirtualTestSupport.BoltId(1), editor.SelectedPoint!.Bolt?.Id);
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
        Assert.False(saved.BoltPresenceResults[VirtualTestSupport.BoltId(1)]);
        var savedImage = Assert.Single(store.LoadPcbImages(saved));
        Assert.False(savedImage.Success);
        Assert.Equal(image.Region, savedImage.Region);
        Assert.Equal(png, savedImage.Png);
        Assert.Equal(png, store.LoadRecipeImage("Inspection", 2));
        editor.Preview.Recipe.Name = "Other";
        Assert.Null(editor.HistoryImageTarget);

        editor.Preview.Recipe.Name = "Inspection";
        store.SavePcbImage(databaseFile, record.Number, image with { Png = [1, 2, 3] });
        await editor.LoadRecordCommand.ExecuteAsync(null);
        Assert.NotNull(editor.SelectedHistoryImage!.Error);
        Assert.Null(editor.HistoryImageTarget);
        Assert.False(editor.Preview.HasImage);
        Assert.False(editor.IsInspectAllowed);

        editor.Results.SelectedRecord = editor.Results.SelectedRecord! with { DatabaseFile = Path.Combine(directory, "missing.db") };
        await editor.LoadRecordCommand.ExecutionTask!;
        Assert.NotNull(editor.Error);
        Assert.Null(editor.LoadedRecord);
        Assert.Empty(editor.HistoryImages);
        Assert.Null(editor.SelectedHistoryImage);
        Assert.Null(editor.HistoryImageTarget);
    }

    [Fact]
    public async Task ReopeningTeachingRefreshesGrabbedPixelsAndKeepsSelectionAndUnsavedInspectionEdits()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = CreateEditor(store, recipes, new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        editor.SelectedPoint = editor.Points.Single(point => point.Bolt?.Id == VirtualTestSupport.BoltId(1));
        editor.DrawRegionCommand.Execute(new Rect(0, 0, 8, 8));
        editor.Preview.BrightnessThreshold = 173;
        editor.Preview.MinimumBrightPercent = 42;
        editor.SelectedPoint.Bolt!.MinimumTurns = 8.25;
        editor.Preview.Recipe.BoltInspection.DataMatrix1.TryInverted = false;
        var previousImage = editor.Preview.Image;
        var png = await SaveRecipeAsync(store, brightness: 90);

        editor.Activate();
        await editor.RefreshImagesCommand.ExecutionTask!;

        Assert.Null(editor.Error);
        Assert.Equal(VirtualTestSupport.BoltId(1), editor.SelectedPoint!.Bolt?.Id);
        Assert.NotSame(previousImage, editor.Preview.Image);
        Assert.All(InspectionPreviewViewModel.CreateFrame(editor.Preview.Image!).Pixels, pixel => Assert.Equal(90, pixel));
        Assert.Equal(new PixelRegion(6, 6, 8, 8), editor.SelectedPoint.Metadata!.Region);
        Assert.Equal(173, editor.Preview.BrightnessThreshold);
        Assert.Equal(42, editor.Preview.MinimumBrightPercent);
        Assert.Equal(8.25, editor.SelectedPoint.Bolt!.MinimumTurns);
        Assert.False(editor.Preview.Recipe.BoltInspection.DataMatrix1.TryInverted);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.Equal(png, store.LoadRecipeImage("Inspection", 2));
    }

    [Fact]
    public async Task ReopeningTeachingRemovesDeletedActivePointsAndKeepsRemainingInspectionEdits()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = CreateEditor(store, recipes, new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance), new(), NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        editor.DrawRegionCommand.Execute(new Rect(0, 0, 8, 8));
        editor.DataMatrix!.ThresholdMinimum = 73;
        editor.SelectedPoint = editor.Points.Single(point => point.Bolt?.Id == VirtualTestSupport.BoltId(1));

        // Remove Bolt edits the active recipe before the operator saves it to the database.
        recipes.Current.Pcb.BoltPoints.Clear();
        recipes.Current.CarrierImages.RemoveAll(tile => !tile.IsBarcode);
        Assert.Single(store.LoadRecipe("Inspection").Pcb.BoltPoints);

        editor.Activate();
        await editor.RefreshImagesCommand.ExecutionTask!;

        Assert.Null(editor.Error);
        Assert.Empty(editor.Preview.Recipe.Pcb.BoltPoints);
        Assert.True(Assert.Single(editor.Points, point => point.Metadata is not null).IsDataMatrix);
        Assert.Same(editor.Points[0], editor.SelectedPoint);
        Assert.Null(editor.SelectedPoint!.Bolt);
        Assert.True(editor.IsDataMatrixSelected);
        Assert.True(editor.Preview.HasImage);
        Assert.Equal(73, editor.DataMatrix!.ThresholdMinimum);
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
    public async Task ActiveRecipeChangeRefreshesInspectionPointsAndKeepsTheSharedRecipe()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        await recipes.SaveAsync("Other");
        recipes.Current.Pcb.BoltPoints[0].X = 99;
        recipes.Current.BoltInspection.DataMatrix1.ThresholdMinimum = 73;
        await recipes.SaveAsync("Other");
        await recipes.LoadAsync("Inspection");
        var editor = CreateEditor(store, recipes,
            new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance), new(),
            NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        var previousBolt = editor.Points.Single(point => point.Bolt is not null).Bolt;

        await recipes.LoadAsync("Other");
        await editor.RefreshImagesCommand.ExecutionTask!;

        Assert.Null(editor.Error);
        Assert.Same(recipes.Current, editor.Preview.Recipe);
        Assert.Equal("Other", editor.Preview.Recipe.Name);
        var point = editor.Points.Single(point => point.Bolt is not null);
        Assert.NotSame(previousBolt, point.Bolt);
        Assert.Same(recipes.Current.Pcb.BoltPoints[0], point.Bolt);
        Assert.Equal(99, point.Position!.X);
        Assert.Equal(73, editor.Preview.DataMatrixThresholdMinimum);
    }

    [Fact]
    public async Task ActiveRecipeChangeDuringInitialImageLoadDiscardsPreviousPixels()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        await SaveRecipeAsync(store, 255);
        var other = store.LoadRecipe("Inspection");
        other.Name = "Other";
        store.SaveRecipe(other, sourceRecipe: "Inspection");
        await SaveRecipeAsync(store, 0);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = CreateEditor(store, recipes,
            new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance), new(),
            NullLogger<InspectionTeachingViewModel>.Instance);
        var paused = new VirtualTestSupport.PausedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task loading;
        try
        {
            SynchronizationContext.SetSynchronizationContext(paused);
            loading = editor.RefreshImagesCommand.ExecuteAsync(null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => paused.HasPending, TimeSpan.FromSeconds(2)));
            Assert.False(editor.IsLoaded);
            await recipes.LoadAsync("Other");
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => editor.IsLoaded, TimeSpan.FromSeconds(2)));
            var shutdown = editor.ShutdownAsync();
            Assert.False(shutdown.IsCompleted);
            paused.Release();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(loading.IsCompleted);
            if (editor.RefreshImagesCommand.ExecutionTask is { } refreshing)
                await refreshing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(editor.Error);
            Assert.Equal("Other", editor.Preview.Recipe.Name);
            Assert.Same(recipes.Current, editor.Preview.Recipe);
            Assert.NotNull(editor.Preview.Image);
            Assert.Equal(255, Assert.Single(InspectionPreviewViewModel.CreateFrame(editor.Preview.Image).Pixels.Distinct()));
        }
        finally
        {
            paused.Release();
            await loading.WaitAsync(TimeSpan.FromSeconds(2));
            await editor.ShutdownAsync();
        }
    }

    [Fact]
    public async Task InspectionShutdownDrainsImagesAndPreventsRecipeReloads()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        await SaveRecipeAsync(store);
        var recipes = new RecipeManager(store, new());
        await recipes.LoadAsync("Inspection");
        var editor = CreateEditor(store, recipes,
            new InspectionImageLoader(store, NullLogger<InspectionImageLoader>.Instance), new(),
            NullLogger<InspectionTeachingViewModel>.Instance);
        await editor.RefreshImagesCommand.ExecuteAsync(null);
        var image = editor.Preview.Image;
        var paused = new VirtualTestSupport.PausedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task loading;
        try
        {
            SynchronizationContext.SetSynchronizationContext(paused);
            loading = editor.RefreshImagesCommand.ExecuteAsync(null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        Task? shutdown = null;
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => paused.HasPending, TimeSpan.FromSeconds(2)));
            shutdown = editor.ShutdownAsync();
            Assert.False(shutdown.IsCompleted);
            paused.Release();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(loading.IsCompleted);
            Assert.Same(image, editor.Preview.Image);

            await recipes.LoadAsync("Inspection");
            Assert.Same(loading, editor.RefreshImagesCommand.ExecutionTask);
            editor.Activate();
            Assert.Same(loading, editor.RefreshImagesCommand.ExecutionTask);
            Assert.Null(editor.Error);
        }
        finally
        {
            paused.Release();
            await loading.WaitAsync(TimeSpan.FromSeconds(2));
            if (shutdown is not null)
                await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            await editor.ShutdownAsync();
        }
    }

    [Fact]
    public async Task FinishedImageLoadDoesNotRestoreAPointDeletedBeforeUiPublication()
    {
        await using var services = MachineTestSupport.CreateDiagnosticServices();
        var recipes = services.GetRequiredService<RecipeManager>();
        var bolt = new BoltPoint { Id = VirtualTestSupport.BoltId(1), X = 10, Y = 20 };
        recipes.Current.Pcb.BoltPoints.Add(bolt);
        var editor = services.GetRequiredService<RecipeEditorViewModel>();
        var image = InspectionPreviewViewModel.CreateBitmap(new ImageFrame(2, 2, 6, new byte[12]));
        Assert.True(await editor.SaveAsync([
            new(new CarrierImageTile { Number = 1, BoltId = VirtualTestSupport.BoltId(1) }, image),
            new(new CarrierImageTile { Number = 2, IsBarcode = true }, image),
        ]));
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var context = new VirtualTestSupport.PausedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            teaching.Activate();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => context.HasPending, TimeSpan.FromSeconds(2)));
            teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Position.Bolt == bolt);
            Assert.True(teaching.IsRemoveBoltPointAllowed);
            teaching.RemoveBoltPointCommand.Execute(null);
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            teaching.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(TeachingViewModel.CarrierImages))
                    published.TrySetResult();
            };
            context.Release();
            await published.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(Assert.Single(teaching.CarrierImages).Metadata.IsBarcode);
            Assert.True(Assert.Single(recipes.Current.CarrierImages).IsBarcode);
            Assert.Empty(recipes.Current.Pcb.BoltPoints);
            Assert.Null(teaching.CameraError);
        }
        finally
        {
            context.Release();
            await teaching.ShutdownAsync();
            await services.GetRequiredService<MachineController>().ShutdownAsync();
        }
    }

    [Fact]
    public async Task HistorySelectionPublishesLatestImagesAndShutdownDrainsPreviousLoad()
    {
        await using var services = MachineTestSupport.CreateDiagnosticServices();
        var store = services.GetRequiredService<MachineStore>();
        var editor = services.GetRequiredService<InspectionTeachingViewModel>();
        var record = new PcbRecord(1, DateTimeOffset.Now, DateTimeOffset.Now, "Default", HeatSinkSlot.HeatSink1,
            null, AssemblyResult.Pending, AssemblyResult.Pending, AssemblyResult.Pending,
            new Dictionary<Guid, BoltResult>(), new Dictionary<Guid, BoltResult>(), new Dictionary<Guid, bool>(), [])
        {
            DatabaseFile = Path.Combine(store.DatabaseFile + ".results", "PCB-2026-10.db"),
        };
        var next = record with { Number = 2 };
        store.SavePcb(record.DatabaseFile, record);
        store.SavePcb(next.DatabaseFile!, next);
        var paused = new VirtualTestSupport.PausedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(paused);
            editor.ImageMode = InspectionImageMode.Recorded;
            editor.Results.SelectedRecord = record;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        var pending = editor.LoadRecordCommand.ExecutionTask!;
        Task? shutdown = null;
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => paused.HasPending, TimeSpan.FromSeconds(2)));
            editor.Results.SelectedRecord = next;
            var latest = editor.LoadRecordCommand.ExecutionTask!;
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => editor.LoadedRecord == next, TimeSpan.FromSeconds(2)));
            Assert.False(pending.IsCompleted);
            shutdown = editor.ShutdownAsync();
            Assert.False(shutdown.IsCompleted);
            paused.Release();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(pending.IsCompleted);
            Assert.True(latest.IsCompleted);
            Assert.Same(next, editor.LoadedRecord);
            Assert.Null(editor.Error);
        }
        finally
        {
            paused.Release();
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            await (shutdown ?? editor.ShutdownAsync()).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task HistoryDirectoryChangeDiscardsPendingListAndImageLoads()
    {
        await using var services = MachineTestSupport.CreateDiagnosticServices();
        var store = services.GetRequiredService<MachineStore>();
        var editor = services.GetRequiredService<InspectionTeachingViewModel>();
        var directory = Path.Combine(Path.GetTempPath(), $"IBTM-history-switch-{Guid.NewGuid():N}");
        var record = new PcbRecord(1, DateTimeOffset.Now, DateTimeOffset.Now, "Default", HeatSinkSlot.HeatSink1,
            null, AssemblyResult.Pending, AssemblyResult.Pending, AssemblyResult.Pending,
            new Dictionary<Guid, BoltResult>(), new Dictionary<Guid, BoltResult>(), new Dictionary<Guid, bool>(), [])
        {
            DatabaseFile = Path.Combine(directory, "PCB-2026-09.db"),
        };
        store.SavePcb(record.DatabaseFile, record);
        foreach (var load in new[] { editor.Results.RefreshHistoryCommand, editor.LoadRecordCommand })
        {
            editor.Results.HistoryDirectory = directory;
            var context = new VirtualTestSupport.PausedSynchronizationContext();
            var previous = SynchronizationContext.Current;
            Task pending;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                if (load == editor.LoadRecordCommand)
                {
                    editor.ImageMode = InspectionImageMode.Recorded;
                    editor.Results.SelectedRecord = record;
                    pending = load.ExecutionTask!;
                }
                else
                {
                    pending = load.ExecuteAsync(null);
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
            try
            {
                Assert.True(await VirtualTestSupport.WaitUntilAsync(() => context.HasPending, TimeSpan.FromSeconds(2)));
                editor.Results.HistoryDirectory = Path.Combine(directory, "other");
                context.Release();
                await pending.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Empty(editor.Results.Records);
                Assert.Null(editor.LoadedRecord);
                Assert.Null(editor.Results.SelectedRecord);
                Assert.Empty(editor.HistoryImages);
                Assert.Null(editor.Error);
            }
            finally
            {
                context.Release();
                await pending.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        await editor.ShutdownAsync();
    }

    private static async Task<byte[]> SaveRecipeAsync(MachineStore store, byte brightness = 0)
    {
        var png = await Task.Run(() =>
        {
            var bitmap = InspectionPreviewViewModel.CreateBitmap(new ImageFrame(20, 20, 60, Enumerable.Repeat(brightness, 1200).ToArray()));
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap, null, null, null));
            encoder.Save(stream);
            return stream.ToArray();
        });
        var recipe = new Recipe { Name = "Inspection" };
        recipe.Pcb.BoltPoints.Add(new() { Id = VirtualTestSupport.BoltId(1), X = 10, Y = 20 });
        recipe.CarrierImages = [
            new() { Number = 1, IsBarcode = true, Region = new(2, 2, 10, 10), Center = new() { X = 1, Y = 2 } },
            new() { Number = 2, BoltId = VirtualTestSupport.BoltId(1), Region = new(2, 2, 10, 10) },
        ];
        store.SaveRecipe(recipe, images: [new(1, png), new(2, png)]);
        return png;
    }
}
