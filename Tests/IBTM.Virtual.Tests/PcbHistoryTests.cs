using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.Storage;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class PcbHistoryTests
{
    [Fact]
    public async Task DataMatrixNgExclusionPersistsWithFailedReadAndPassingInspection()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var settings = new MachineSettings();
        Assert.False(settings.InspectionGantry.ExcludeDataMatrixFromNg);
        settings.InspectionGantry.ExcludeDataMatrixFromNg = true;
        settings.PcbHistory.Directory = store.DatabaseFile + ".results";
        await store.SaveSettingsAsync(settings.Sections);
        Assert.True(new MachineStore(store.DatabaseFile).LoadSettings()
            .Get<InspectionGantrySettings>().ExcludeDataMatrixFromNg);
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddVirtualApplication(settings).BuildServiceProvider();
        var history = services.GetRequiredService<PcbHistoryWriter>();
        var assembly = services.GetRequiredService<InspectionStation>().Station.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.IsDataMatrixNgExcluded = true;
        assembly.PcbBarcode = null;
        assembly.RecordBoltPresence(Guid.NewGuid(), true);
        assembly.CompleteInspection();
        assembly.RecordInspectionCapture(new(null, DateTimeOffset.Now,
            new ImageFrame(1, 1, 3, [20, 20, 20]), new(0, 0, 1, 1), false));
        await history.FlushAsync();

        settings.InspectionGantry.ExcludeDataMatrixFromNg = false;
        var reopened = new MachineStore(store.DatabaseFile);
        var record = Assert.Single(reopened.LoadPcbs(settings.PcbHistory.Directory));
        Assert.True(record.IsDataMatrixNgExcluded);
        Assert.Null(record.PcbBarcode);
        Assert.Equal(AssemblyResult.Ng, record.PcbBarcodeResult);
        Assert.Equal(AssemblyResult.Ok, record.InspectionResult);
        Assert.Equal(AssemblyResult.Ok, record.Result);
        var image = Assert.Single(reopened.LoadPcbImages(record));
        Assert.False(image.Success);
        Assert.Equal(UiText.Get("Not read"), new PcbInspectionImageItem(image, null, new Recipe()).Verdict);
    }

    [Fact]
    public async Task ResultHistoryPersistsEachStageCurveAndReloadsItForDetails()
    {
        await using var services = MachineTestSupport.CreateDiagnosticServices();
        var history = services.GetRequiredService<PcbHistoryWriter>();
        var store = services.GetRequiredService<MachineStore>();
        var settings = services.GetRequiredService<MachineSettings>();
        settings.PcbHistory.Directory = store.DatabaseFile + ".results";
        var assembly = services.GetRequiredService<BoltFasteningStation>().Station.GetAssembly(HeatSinkSlot.HeatSink1);
        var boltId = VirtualTestSupport.BoltId(1);
        var preliminaryCurve = new AdcTorqueCurve(1, 30, [0, 0.5], 60, 0.5, 0.5, 1, 0);
        var finalCurve = new AdcTorqueCurve(2, 30, [0.5, 1], 60, 1, 1, 2, 0);
        var retighteningCurve = new AdcTorqueCurve(3, 30, [0.75, 1], 60, 1, 1, 3, 0);
        var preliminary = new BoltResult(true, 0.5)
        {
            Stage = BoltFasteningStage.Preliminary,
            TorqueCurve = preliminaryCurve,
        };
        PcbRecord? published = null;
        history.Saved += record => published = record;
        var firstFinal = new BoltResult(true, 1)
        {
            Stage = BoltFasteningStage.FinalBeforeRetightening,
            PreliminaryResult = preliminary,
            TorqueCurve = finalCurve,
        };
        assembly.RecordBolt(FasteningHead.Pickup, boltId, firstFinal);
        assembly.RecordBolt(FasteningHead.Pickup, boltId, new(true, 1)
        {
            Stage = BoltFasteningStage.Retightening,
            PreviousFinalResult = firstFinal,
            TorqueCurve = retighteningCurve,
        });
        await history.FlushAsync();

        Assert.NotNull(published);
        var details = services.GetRequiredService<PcbResultsViewModel>();
        details.Record = published;
        var stages = Assert.Single(details.BoltResults).StageResults.ToArray();
        Assert.Equal(3, stages.Length);
        Assert.Same(preliminaryCurve, stages[0].Result.TorqueCurve);
        Assert.Same(finalCurve, stages[1].Result.TorqueCurve);
        Assert.Same(retighteningCurve, stages[2].Result.TorqueCurve);
        var reopened = VirtualTestSupport.OpenMachineStore(store.DatabaseFile);
        details.Record = Assert.Single(reopened.LoadPcbs(settings.PcbHistory.Directory));
        var restored = Assert.Single(details.BoltResults).StageResults.ToArray();
        Assert.Equal(new[] { BoltFasteningStage.Preliminary, BoltFasteningStage.FinalBeforeRetightening,
            BoltFasteningStage.Retightening }, restored.Select(row => row.Result.Stage));
        Assert.Equal(preliminaryCurve.Torques, restored[0].Result.TorqueCurve!.Torques);
        Assert.Equal(finalCurve.Torques, restored[1].Result.TorqueCurve!.Torques);
        Assert.Equal(retighteningCurve.Torques, restored[2].Result.TorqueCurve!.Torques);
        Assert.Equal(30, restored[1].Result.TorqueCurve!.SampleMilliseconds);
        Assert.Equal(60, restored[1].Result.TorqueCurve!.FasteningMilliseconds);
        Assert.Equal(0, restored[1].Result.TorqueCurve!.ReceivedAt);
        Assert.NotSame(finalCurve, restored[1].Result.TorqueCurve);
        Assert.Same(details.SelectedBolt, details.SelectedBoltStage);
        Assert.Equal(BoltFasteningStage.Retightening, details.SelectedBoltStage!.Result.Stage);
    }

    [Fact]
    public async Task DataMatrixThresholdAndDilationSurviveHistoryWriterAndReload()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = store.DatabaseFile + ".results";
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddVirtualApplication(settings).BuildServiceProvider();
        var history = services.GetRequiredService<PcbHistoryWriter>();
        var assembly = services.GetRequiredService<InspectionStation>().Station.GetAssembly(HeatSinkSlot.HeatSink2);
        assembly.PcbBarcode = "PCB-DOTS-123";
        assembly.RecordInspectionCapture(new(null, DateTimeOffset.Now,
            new ImageFrame(1, 1, 3, [20, 20, 20]), new(0, 0, 1, 1), true,
            Barcode: "PCB-DOTS-123", Threshold: 55, Dilated: true));
        await history.FlushAsync();
        var reopened = new MachineStore(store.DatabaseFile);
        var record = Assert.Single(reopened.LoadPcbs(settings.PcbHistory.Directory));
        var image = Assert.Single(reopened.LoadPcbImages(record));
        Assert.Equal(55, image.Threshold);
        Assert.True(image.Dilated);
        Assert.Equal("PCB-DOTS-123", image.Barcode);
        Assert.True(image.Success);
        var details = new PcbInspectionImageItem(image, null, new Recipe()).Details;
        Assert.Contains("55", details);
        Assert.Contains(UiText.Get(" · Dilate ON"), details);

        // Older records carry no attempt metadata; missing feedback must remain unknown.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<PcbInspectionImage>(
            """{"BoltId":null,"CapturedAt":"2026-10-02T00:00:00Z","Region":{"X":0,"Y":0,"Width":1,"Height":1},"Success":true,"Barcode":"OLD"}""")!;
        Assert.Null(legacy.Threshold);
        Assert.Null(legacy.Dilated);
        Assert.Equal("OLD", new PcbInspectionImageItem(legacy, null, new Recipe()).Details);
    }

    [Fact]
    public async Task ProductionCountsClearWithoutChangingPcbHistory()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-counts-{Guid.NewGuid():N}");
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddVirtualApplication(settings).BuildServiceProvider();
        var view = services.GetRequiredService<OperationViewModel>();
        var recipes = view.Recipes;
        await recipes.SaveAsync("Default");
        var inspection = services.GetRequiredService<InspectionStation>();
        var history = services.GetRequiredService<PcbHistoryWriter>();
        var assembly = inspection.Station.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.PcbBarcode = null;
        await history.FlushAsync();
        Assert.Single(view.PcbRecords);
        Assert.Equal(0, recipes.Counts.TotalCount); // A partial NG result is not a completed PCB.

        await recipes.RecordProductionAsync(1, 2);
        Assert.Equal(3, recipes.Counts.TotalCount);
        Assert.Equal(new ProductionCounts(1, 2), recipes.Counts);
        assembly.RecordBoltPresence(Guid.NewGuid(), false);
        await history.FlushAsync();
        Assert.Equal(3, recipes.Counts.TotalCount);

        // Save-as starts its own total; overwriting recipe settings must retain that total.
        await recipes.SaveAsync("Other");
        Assert.Equal(new ProductionCounts(0, 0), recipes.Counts);
        await recipes.RecordProductionAsync(2, 1);
        await recipes.SaveAsync("Other");
        Assert.Equal(new ProductionCounts(2, 1), recipes.Counts);
        await recipes.LoadAsync("DEFAULT");
        Assert.Equal(new ProductionCounts(1, 2), recipes.Counts);

        var reopened = new RecipeManager(new MachineStore(store.DatabaseFile), new());
        await reopened.LoadAsync("Default");
        Assert.Equal(recipes.Counts, reopened.Counts);

        var record = Assert.Single(view.PcbRecords);
        await view.ClearCountsCommand.ExecuteAsync(null);
        Assert.Equal(new ProductionCounts(0, 0), recipes.Counts);
        Assert.Equal(record, Assert.Single(view.PcbRecords));
        Assert.Equal(record.Number, Assert.Single(store.LoadPcbs(settings.PcbHistory.Directory)).Number);

        await reopened.LoadAsync("Default");
        Assert.Equal(new ProductionCounts(0, 0), reopened.Counts);
        await reopened.LoadAsync("Other");
        Assert.Equal(new ProductionCounts(2, 1), reopened.Counts);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => recipes.RecordProductionAsync(1, 0)));
        Assert.Equal(new ProductionCounts(4, 0), recipes.Counts);
        await reopened.LoadAsync("Default");
        Assert.Equal(recipes.Counts, reopened.Counts);

        using var connection = new SqliteConnection($"Data Source={store.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER FailCountClear BEFORE DELETE ON ProductionCounts BEGIN SELECT RAISE(ABORT, 'count reset failed'); END";
        command.ExecuteNonQuery();
        await view.ClearCountsCommand.ExecuteAsync(null);
        Assert.NotNull(view.CountResetError);
        Assert.Equal(new ProductionCounts(4, 0), recipes.Counts);
        Assert.Equal(recipes.Counts, store.LoadProductionCounts("Default"));
    }

    [Fact]
    public void ProductionCountsAreAddedToExistingMachineDatabase()
    {
        var original = VirtualTestSupport.OpenMachineStore();
        original.SaveRecipe(new Recipe { Name = "Existing" });
        using var connection = new SqliteConnection($"Data Source={original.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE ProductionCounts";
        command.ExecuteNonQuery();

        var updated = new MachineStore(original.DatabaseFile);
        Assert.Equal("Existing", updated.LoadRecipe("Existing").Name);
        Assert.Equal(new ProductionCounts(0, 0), updated.LoadProductionCounts("Existing"));
        Assert.Equal(new ProductionCounts(1, 2), updated.AddProductionCounts("Existing", 1, 2));
        Assert.Equal(new ProductionCounts(3, 3), updated.AddProductionCounts("EXISTING", 2, 1));
        Assert.Equal(new ProductionCounts(3, 3), new MachineStore(original.DatabaseFile).LoadProductionCounts("Existing"));
    }

    [Fact]
    public void EmptyBoltGuidCannotOverwriteSavedDataMatrixImage()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var file = Path.Combine(store.DatabaseFile + ".results", "PCB-2026-09.db");
        var capturedAt = DateTimeOffset.Now;
        var record = new PcbRecord(1, capturedAt, capturedAt, "Default", HeatSinkSlot.HeatSink1,
            "PCB-123", AssemblyResult.Ok, AssemblyResult.Pending, AssemblyResult.Pending,
            new Dictionary<Guid, BoltResult>(), new Dictionary<Guid, BoltResult>(), new Dictionary<Guid, bool>(), [])
        {
            DatabaseFile = file,
        };
        store.SavePcb(file, record);
        var dataMatrix = new PcbInspectionImage(null, capturedAt, new(0, 0, 1, 1), true, "PCB-123", null, null, [1, 2, 3]);
        store.SavePcbImage(file, record.Number, dataMatrix);

        var invalid = dataMatrix with { BoltId = Guid.Empty, Barcode = null, Success = false, Png = [4, 5, 6] };
        Assert.Throws<ArgumentException>(() => store.SavePcbImage(file, record.Number, invalid));
        var saved = Assert.Single(store.LoadPcbImages(record));
        Assert.Null(saved.BoltId);
        Assert.Equal(dataMatrix.Barcode, saved.Barcode);
        Assert.True(saved.Success);
        Assert.Equal(dataMatrix.Png, saved.Png);

        var boltImage = invalid with { BoltId = Guid.NewGuid() };
        store.SavePcbImage(file, record.Number, boltImage);
        var images = store.LoadPcbImages(record);
        Assert.Equal(2, images.Count);
        Assert.Null(images[0].BoltId);
        Assert.Equal(dataMatrix.Png, images[0].Png);
        Assert.Equal(boltImage.BoltId, images[1].BoltId);
        Assert.Equal(boltImage.Png, images[1].Png);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurnsLimitsAndIndependentVerdictsSurviveHistoryReload(bool dryRun)
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-turns-{Guid.NewGuid():N}");
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddVirtualApplication(settings).BuildServiceProvider();
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new() { MinimumTurns = 3, MaximumTurns = 6 }, new() { Head = FasteningHead.Pickup, MaximumTurns = 4 }];
        var history = services.GetRequiredService<PcbHistoryWriter>();
        var assembly = services.GetRequiredService<BoltFasteningStation>().Station.GetAssembly(HeatSinkSlot.HeatSink1);
        foreach (var bolt in recipe.Pcb.BoltPoints)
        {
            assembly.RecordBolt(bolt.Head, bolt.Id, new(true, dryRun ? null : 8,
                dryRun ? BoltResultSource.DryRun : BoltResultSource.Controller)
            {
                MinimumTurns = bolt.MinimumTurns,
                MaximumTurns = bolt.MaximumTurns,
                Controller = dryRun ? null : new("Virtual", 1, 1, 1000, 1, 8, 800, 100, 200, 1800, 1, 0, 0, 1, 0, null),
            });
            assembly.RecordBoltPresence(bolt.Id, true);
        }
        assembly.CompleteFastening();
        assembly.CompleteInspection();
        recipe.Pcb.BoltPoints[1].MaximumTurns = 10;
        await history.FlushAsync();

        var record = Assert.Single(new MachineStore(store.DatabaseFile).LoadPcbs(settings.PcbHistory.Directory));
        Assert.Equal(dryRun ? AssemblyResult.Ng : AssemblyResult.Ok, record.FasteningResult);
        Assert.Equal(AssemblyResult.Ok, record.InspectionResult);
        Assert.Equal(dryRun ? AssemblyResult.Pending : AssemblyResult.Ng, record.TurnsResult);
        Assert.Equal(AssemblyResult.Ng, record.Result);
        Assert.Equal(3, Assert.Single(record.ShootingBoltResults).Value.MinimumTurns);
        Assert.Equal(6, Assert.Single(record.ShootingBoltResults).Value.MaximumTurns);
        Assert.Null(Assert.Single(record.PickupBoltResults).Value.MinimumTurns);
        Assert.Equal(4, Assert.Single(record.PickupBoltResults).Value.MaximumTurns);
        Assert.Equal(dryRun ? (double?)null : 5, Assert.Single(record.PickupBoltResults).Value.TotalTurns);
        var details = services.GetRequiredService<PcbResultsViewModel>();
        details.Record = record;
        Assert.Equal(dryRun ? new[] { UiText.Get("No data"), UiText.Get("No data") } : new[] { "OK", "NG" },
            details.BoltResults.Select(row => row.TurnsVerdict));
        Assert.All(details.BoltResults, row =>
        {
            Assert.Equal(dryRun ? UiText.Get("Dry run · NG") : "OK", row.Verdict);
            Assert.Equal("OK", row.VisionVerdict);
        });
        await details.LoadImagesCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task SixNamedBoltsKeepSeparateResultsAfterRecipeReloadAndReordering()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var recipe = new Recipe();
        for (var index = 0; index < 6; index++)
        {
            recipe.Pcb.BoltPoints.Add(new()
            {
                Name = index == 5 ? null : "고정",
                Head = index < 4 ? FasteningHead.Shooting : FasteningHead.Pickup,
            });
        }
        store.SaveRecipe(recipe);
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-six-bolts-{Guid.NewGuid():N}");
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddVirtualApplication(settings).BuildServiceProvider();
        var recipes = services.GetRequiredService<RecipeManager>();
        await recipes.LoadAsync(recipe.Name);
        var bolts = recipes.Current.Pcb.GetFasteningPoints(settings.BoltFastening.FirstFasteningHead).ToArray();
        Assert.Equal(6, bolts.Select(bolt => bolt.Id).Distinct().Count());
        Assert.DoesNotContain(bolts, bolt => bolt.Id == Guid.Empty);

        var history = services.GetRequiredService<PcbHistoryWriter>();
        var assembly = services.GetRequiredService<BoltFasteningStation>().Station.GetAssembly(HeatSinkSlot.HeatSink1);
        bolts[0].Name = "작업 중 변경";
        for (var index = 0; index < bolts.Length; index++)
        {
            assembly.RecordBolt(bolts[index].Head, bolts[index].Id,
                new(index != 4, index == 4 ? null : 8 + index, Error: index == 4 ? "ADC response error" : null));
            assembly.RecordBoltPresence(bolts[index].Id, index != 4);
        }
        assembly.CompleteFastening();
        assembly.RecordInspectionCapture(new(bolts[4].Id, DateTimeOffset.Now,
            new ImageFrame(2, 1, 6, [0, 0, 255, 0, 255, 0]), new PixelRegion(0, 0, 1, 1), false,
            BrightRatio: 0.1, MinimumBrightRatio: 0.8));
        recipes.Current.Pcb.BoltPoints.Move(0, 5);
        bolts[4].Name = "변경된 이름";
        await recipes.SaveAsync(recipe.Name);
        await history.FlushAsync();

        var reopened = new MachineStore(store.DatabaseFile);
        var record = Assert.Single(reopened.LoadPcbs(settings.PcbHistory.Directory));
        Assert.Equal(4, record.ShootingBoltResults.Count);
        Assert.Equal(2, record.PickupBoltResults.Count);
        Assert.Equal(AssemblyResult.Ng, record.FasteningResult);
        Assert.Equal(bolts.Select(bolt => bolt.Id), record.BoltIds);
        Assert.Equal(bolts.Select(bolt => bolt.Id).Order(), reopened.LoadRecipe(recipe.Name).Pcb.BoltPoints.Select(bolt => bolt.Id).Order());
        using (var connection = new SqliteConnection($"Data Source={record.DatabaseFile};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM Pcbs WHERE Number=$number";
            command.Parameters.AddWithValue("$number", record.Number);
            var json = (string)command.ExecuteScalar()!;
            Assert.DoesNotContain("BoltNumber", json);
            using var saved = System.Text.Json.JsonDocument.Parse(json);
            Assert.False(saved.RootElement.TryGetProperty("BoltNames", out _));
            // Keep the existing database JSON name while the code names the shooting head explicitly.
            Assert.All(saved.RootElement.GetProperty("PcbBoltResults").EnumerateObject(),
                result => Assert.True(Guid.TryParse(result.Name, out var id) && id != Guid.Empty));
            Assert.All(saved.RootElement.GetProperty(nameof(PcbRecord.PickupBoltResults)).EnumerateObject(),
                result => Assert.True(Guid.TryParse(result.Name, out var id) && id != Guid.Empty));
        }

        var details = services.GetRequiredService<PcbResultsViewModel>();
        details.Record = record;
        Assert.Equal(6, details.BoltResults.Count);
        Assert.Empty(details.InspectionOnlyResults);
        for (var index = 0; index < bolts.Length; index++)
        {
            var row = details.BoltResults[index];
            Assert.Equal(bolts[index].Id, row.BoltId);
            Assert.Equal(index + 1, row.Ordinal);
            Assert.Equal(bolts[index].Head, row.Head);
            Assert.Equal(index != 4, row.Result.Success);
            Assert.Equal(index == 4 ? null : (double?)(8 + index), row.Result.Torque);
            Assert.Equal(index switch
            {
                0 => "작업 중 변경",
                4 => "변경된 이름",
                5 => "Bolt 6",
                _ => "고정",
            }, row.BoltLabel);
            Assert.Equal(index != 4, row.Present);
        }
        details.SelectedBolt = details.BoltResults[4];
        Assert.Equal("ADC response error", details.SelectedBolt.Result.Error);
        Assert.True(details.BoltResults[5].Result.Success);
        await details.LoadImagesCommand.ExecuteAsync(null);
        Assert.Equal(bolts[4].Id, details.SelectedImage?.Record.BoltId);
        Assert.Equal("변경된 이름", details.SelectedImage?.Title);
        bolts[4].Name = null;
        Assert.Equal("Bolt 5", details.SelectedBolt.BoltLabel);
        Assert.Equal("Bolt 5", details.SelectedImage?.Title);
        bolts[4].Name = "변경된 이름";
        var selectedImage = details.SelectedImage;
        details.SelectedBolt = details.BoltResults[0];
        Assert.Null(details.SelectedImage);
        details.SelectedImage = selectedImage;
        Assert.Equal(bolts[4].Id, details.SelectedBolt?.BoltId);
        var dataMatrix = selectedImage! with { Record = selectedImage.Record with { BoltId = null } };
        details.SelectedImage = dataMatrix;
        Assert.Null(details.SelectedBolt);
        Assert.Same(dataMatrix, details.SelectedImage);
        details.SelectedImage = selectedImage;

        var other = store.LoadRecipe(recipe.Name);
        other.Name = "Other";
        foreach (var bolt in other.Pcb.BoltPoints)
            bolt.Name = "Other recipe bolt";
        store.SaveRecipe(other);
        await recipes.LoadAsync("Other");
        Assert.Equal("Bolt 5", details.SelectedBolt?.BoltLabel);
        Assert.Equal("Bolt 5", details.SelectedImage?.Title);
        details.Record = record with { UpdatedAt = record.UpdatedAt.AddSeconds(1) };
        Assert.Equal(bolts[4].Id, details.SelectedBolt?.BoltId);
        Assert.Equal("Bolt 5", details.SelectedBolt?.BoltLabel);
        await details.LoadImagesCommand.ExecuteAsync(null);
        Assert.Equal("Bolt 5", details.SelectedImage?.Title);

        details.Record = record with
        {
            ShootingBoltResults = new Dictionary<Guid, BoltResult>(),
            PickupBoltResults = new Dictionary<Guid, BoltResult>(),
        };
        Assert.Empty(details.BoltResults);
        Assert.Equal(6, details.InspectionOnlyResults.Count);
        Assert.Equal("Bolt 5", details.InspectionOnlyResults.Single(row => row.BoltId == bolts[4].Id).BoltLabel);

        // Old name copies are ignored; the result's GUID order supplies unnamed bolt numbers.
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(record))!;
        legacyJson["BoltNames"] = new System.Text.Json.Nodes.JsonObject
        {
            [bolts[4].Id.ToString()] = "저장된 옛 이름",
        };
        var legacy = System.Text.Json.JsonSerializer.Deserialize<PcbRecord>(legacyJson.ToJsonString())!;
        details.Record = legacy with { Number = record.Number, DatabaseFile = record.DatabaseFile };
        Assert.Equal(Enumerable.Range(1, 6).Select(number => $"Bolt {number}"),
            details.BoltResults.Select(row => row.BoltLabel));
        Assert.Empty(details.InspectionOnlyResults);
        await details.LoadImagesCommand.ExecuteAsync(null);
        Assert.Equal(bolts[4].Id, details.SelectedImage?.Record.BoltId);
        Assert.Equal("Bolt 5", details.SelectedImage?.Title);
        details.Record = legacy with
        {
            ShootingBoltResults = new Dictionary<Guid, BoltResult>(),
            PickupBoltResults = new Dictionary<Guid, BoltResult>(),
        };
        Assert.Equal(Enumerable.Range(1, 6).Select(number => $"Bolt {number}"),
            details.InspectionOnlyResults.Select(row => row.BoltLabel));
    }

    [Fact]
    public async Task LockedDatabaseDoesNotBlockAssemblyCreationOrResultCollection()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-locked-{Guid.NewGuid():N}");
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddVirtualApplication(settings).BuildServiceProvider();
        var history = services.GetRequiredService<PcbHistoryWriter>();
        var work = services.GetRequiredService<PcbPlacer>().Station;
        var snapshots = new List<PcbRecord>();
        history.Saved += snapshots.Add;

        using var connection = new SqliteConnection($"Data Source={store.DatabaseFile}");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var collect = Task.Run(() =>
        {
            var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
            assembly.RecordBolt(FasteningHead.Pickup, VirtualTestSupport.BoltId(1), new(false, null, Error: "First result"));
            assembly.RecordBolt(FasteningHead.Pickup, VirtualTestSupport.BoltId(1), new(true, 8));
            return assembly;
        });
        try
        {
            var assembly = await collect.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(assembly.PcbNumber);
            Assert.Equal(3, history.PendingCount);
        }
        finally
        {
            transaction.Rollback();
            await collect;
        }

        await history.FlushAsync();
        Assert.Equal(0, history.PendingCount);
        Assert.Equal(3, snapshots.Count);
        Assert.Empty(snapshots[0].PickupBoltResults);
        Assert.Equal("First result", snapshots[1].PickupBoltResults[VirtualTestSupport.BoltId(1)].Error);
        Assert.True(snapshots[2].PickupBoltResults[VirtualTestSupport.BoltId(1)].Success);
        var saved = Assert.Single(store.LoadPcbs(settings.PcbHistory.Directory));
        Assert.Equal(1, saved.Number);
        Assert.Equal(8, saved.PickupBoltResults[VirtualTestSupport.BoltId(1)].Torque);
        Assert.Equal(AssemblyResult.Ng, saved.FasteningResult); // Existing overwrite/sticky NG policy stays intact.
    }

    [Fact]
    public async Task DisposalWaitsForQueuedResultsToReachTheDatabase()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-exit-{Guid.NewGuid():N}");
        var services = new ServiceCollection().AddSingleton(store)
            .AddVirtualApplication(settings).BuildServiceProvider();
        _ = services.GetRequiredService<PcbHistoryWriter>();
        using var connection = new SqliteConnection($"Data Source={store.DatabaseFile}");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var assembly = services.GetRequiredService<BoltFasteningStation>().Station.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBolt(FasteningHead.Pickup, VirtualTestSupport.BoltId(1), new(true, 8.2));
        var closing = services.DisposeAsync().AsTask();
        try
        {
            Assert.False(closing.IsCompleted);
        }
        finally
        {
            transaction.Rollback();
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var record = Assert.Single(store.LoadPcbs(settings.PcbHistory.Directory));
        Assert.Equal(8.2, record.PickupBoltResults[VirtualTestSupport.BoltId(1)].Torque);
    }

    [Fact]
    public async Task CounterFailureRetainsAssemblyAndLaterResultsForRetry()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-counter-{Guid.NewGuid():N}");
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddVirtualApplication(settings).BuildServiceProvider();
        var history = services.GetRequiredService<PcbHistoryWriter>();
        var work = services.GetRequiredService<PcbPlacer>().Station;
        using (var connection = new SqliteConnection($"Data Source={store.DatabaseFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE PcbCounter";
            command.ExecuteNonQuery();
        }

        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        var failure = await Assert.ThrowsAsync<IOException>(history.FlushAsync);
        Assert.Equal(history.SaveError, failure.Message);
        Assert.IsType<SqliteException>(failure.InnerException);
        Assert.Null(assembly.PcbNumber);
        assembly.RecordBolt(FasteningHead.Pickup, VirtualTestSupport.BoltId(2), new(true, 8.1));
        Assert.Same(assembly, work.GetAssembly(HeatSinkSlot.HeatSink1));
        Assert.Equal(2, history.PendingCount);

        _ = new MachineStore(store.DatabaseFile); // Restore the missing counter.
        await history.FlushAsync();
        Assert.Null(history.SaveError);
        Assert.Equal(0, history.PendingCount);
        Assert.Equal(1, assembly.PcbNumber);
        var saved = Assert.Single(store.LoadPcbs(settings.PcbHistory.Directory));
        Assert.Equal(8.1, saved.PickupBoltResults[VirtualTestSupport.BoltId(2)].Torque);
        Assert.Equal(2, store.NextPcbNumber(settings.PcbHistory.Directory));
    }

    [Fact]
    public async Task SaveFailureKeepsNumberAndImageUntilOperatorRetries()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-unwritable-{Guid.NewGuid():N}");
        File.WriteAllText(settings.PcbHistory.Directory, "A file blocks creation of the results folder.");
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddVirtualApplication(settings).BuildServiceProvider();
        var view = services.GetRequiredService<OperationViewModel>();
        var history = services.GetRequiredService<PcbHistoryWriter>();
        var work = services.GetRequiredService<InspectionStation>();
        var assembly = work.Station.GetAssembly(HeatSinkSlot.HeatSink1);
        try
        {
            await Assert.ThrowsAsync<IOException>(history.FlushAsync);
            Assert.Equal(1, assembly.PcbNumber);
            var pixels = new byte[] { 0, 0, 255, 0, 255, 0 };
            assembly.RecordInspectionCapture(new(VirtualTestSupport.BoltId(1), DateTimeOffset.Now,
                new ImageFrame(2, 1, 6, pixels), new(0, 0, 1, 1), true));
            Array.Clear(pixels); // Encoding must use the image captured when the event was raised.
            assembly.RecordBoltPresence(VirtualTestSupport.BoltId(1), true);
            assembly.CompleteInspection();
            Assert.Equal(4, history.PendingCount);
            Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
        }
        finally
        {
            File.Delete(settings.PcbHistory.Directory);
        }

        await view.RetryPcbSaveCommand.ExecuteAsync(null);
        Assert.Null(history.SaveError);
        Assert.Equal(0, history.PendingCount);
        var record = Assert.Single(store.LoadPcbs(settings.PcbHistory.Directory));
        Assert.Equal(1, record.Number);
        Assert.Equal(AssemblyResult.Ok, record.InspectionResult);
        Assert.True(record.BoltPresenceResults[VirtualTestSupport.BoltId(1)]);
        var image = Assert.Single(store.LoadPcbImages(record));
        using var stream = new MemoryStream(image.Png);
        var decoded = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            System.Windows.Media.Imaging.BitmapFrame.Create(stream), System.Windows.Media.PixelFormats.Bgr24, null, 0);
        var restored = new byte[6];
        decoded.CopyPixels(restored, 6, 0);
        Assert.Equal(new byte[] { 0, 0, 255, 0, 255, 0 }, restored);
        Assert.Equal(2, store.NextPcbNumber(settings.PcbHistory.Directory));
        await view.ShutdownAsync();
    }

    [Fact]
    public async Task CounterUpgradesExistingDatabaseAndContinuesAcrossReopen()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var settings = new PcbHistorySettings { Directory = store.DatabaseFile + ".results" };
        store.SaveSettings([settings]);
        using (var connection = new SqliteConnection($"Data Source={store.DatabaseFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Recipes (Name, Value) VALUES ('Existing', '{"Name":"Existing","X":123.4}')
                """;
            command.ExecuteNonQuery();
            command.CommandText = "DROP TABLE PcbCounter";
            command.ExecuteNonQuery();
        }

        store = new(store.DatabaseFile);
        Assert.Equal(1, store.NextPcbNumber(settings.Directory));
        var numbers = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => store.NextPcbNumber(settings.Directory))));
        Assert.Equal(Enumerable.Range(2, 8).Select(number => (long)number), numbers.Order());
        var reopened = new MachineStore(store.DatabaseFile);
        Assert.Equal(10, reopened.NextPcbNumber(settings.Directory));
        Assert.Equal(settings.Directory, reopened.LoadSettings().Get<PcbHistorySettings>().Directory);
        Assert.Equal("Existing", Assert.Single(reopened.RecipeNames));
        using var db = new SqliteConnection($"Data Source={store.DatabaseFile}");
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Pcbs'";
        Assert.Equal(0L, query.ExecuteScalar()); // Main DB holds only the counter, not results.
    }

    [Fact]
    public void MonthlyFilesKeepOldResultsAndPageNewestFirst()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var directory = Path.Combine(Path.GetTempPath(), $"PCB-history-{Guid.NewGuid():N}");
        var september = new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.FromHours(9));
        var october = september.AddSeconds(2);
        var first = new PcbRecord(store.NextPcbNumber(directory), september, september, "Recipe A",
            HeatSinkSlot.HeatSink1, null, AssemblyResult.Pending, AssemblyResult.Pending,
            AssemblyResult.Pending, new System.Collections.Generic.Dictionary<Guid, BoltResult>(),
            new System.Collections.Generic.Dictionary<Guid, BoltResult>(),
            new System.Collections.Generic.Dictionary<Guid, bool>(), [VirtualTestSupport.BoltId(1), VirtualTestSupport.BoltId(2)]);
        var oldFile = Path.Combine(directory, "PCB-2026-09.db");
        var newFile = Path.Combine(directory, "PCB-2026-10.db");
        store.SavePcb(oldFile, first);
        Assert.Empty(store.LoadPcbImages(first with { DatabaseFile = oldFile }));
        var second = first with { Number = store.NextPcbNumber(directory), CreatedAt = october, UpdatedAt = october };
        store.SavePcb(newFile, second);
        store.SavePcb(newFile, second with { Number = store.NextPcbNumber(directory) });
        store.SavePcb(oldFile, first with
        {
            UpdatedAt = october, PcbBarcode = "PCB-A", PcbBarcodeResult = AssemblyResult.Ok,
            FasteningResult = AssemblyResult.Ng, InspectionResult = AssemblyResult.Ng,
            ShootingBoltResults = new System.Collections.Generic.Dictionary<Guid, BoltResult>
            {
                [VirtualTestSupport.BoltId(1)] = new(false, 0.75, Error: "Controller error 42")
                {
                    RecordedAt = september,
                    Controller = new("COM10", 1, 21, 876, 3, 1.2, 950, 3156, 19, 3175,
                        9, 42, 0, 6, 87, [21, 876, 3, 120, 75, 950, 3156, 19, 3175, 9, 42, 0, 6, 87])
                    {
                        TorqueCompensationPercent = 95,
                    },
                },
            },
            PickupBoltResults = new System.Collections.Generic.Dictionary<Guid, BoltResult> { [VirtualTestSupport.BoltId(2)] = new(true, 1.2) },
            BoltPresenceResults = new System.Collections.Generic.Dictionary<Guid, bool> { [VirtualTestSupport.BoltId(1)] = false, [VirtualTestSupport.BoltId(2)] = true },
        });

        var reopened = new MachineStore(store.DatabaseFile);
        var page = reopened.LoadPcbs(directory, count: 2);
        Assert.Equal(new long[] { 3, 2 }, page.Select(record => record.Number));
        var saved = Assert.Single(reopened.LoadPcbs(directory, page[^1].Number, count: 2));
        Assert.Equal(1, saved.Number);
        Assert.Equal(september, saved.CreatedAt);
        Assert.Equal(october, saved.UpdatedAt);
        Assert.Equal("PCB-A", saved.PcbBarcode);
        Assert.Equal(AssemblyResult.Ng, saved.Result);
        Assert.Equal("Controller error 42", saved.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Error);
        Assert.Equal(september, saved.ShootingBoltResults[VirtualTestSupport.BoltId(1)].RecordedAt);
        Assert.Equal(19, saved.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Controller!.Angle2);
        Assert.Equal((ushort)95, saved.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Controller!.TorqueCompensationPercent);
        Assert.Equal(new ushort[] { 21, 876, 3, 120, 75, 950, 3156, 19, 3175, 9, 42, 0, 6, 87 },
            saved.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Controller!.Registers);
        Assert.Equal(1.2, saved.PickupBoltResults[VirtualTestSupport.BoltId(2)].Torque);
        Assert.False(saved.BoltPresenceResults[VirtualTestSupport.BoltId(1)]);
        Assert.Equal(2, Directory.GetFiles(directory, "*.db").Length);
        Assert.Equal(4, reopened.NextPcbNumber(directory));
    }

    [Fact]
    public void RestoredCounterCannotReuseSavedPcbNumbersOrOverwriteTheirResults()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var directory = store.DatabaseFile + ".results";
        var september = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(9));
        var october = september.AddDays(1);
        var record = new PcbRecord(207, september, september, "Default", HeatSinkSlot.HeatSink1,
            null, AssemblyResult.Pending, AssemblyResult.Pending, AssemblyResult.Pending,
            new Dictionary<Guid, BoltResult>(), new Dictionary<Guid, BoltResult>(), new Dictionary<Guid, bool>(), []);
        store.SavePcb(Path.Combine(directory, "PCB-2026-09.db"), record);
        var file = Path.Combine(directory, "PCB-2026-10.db");
        var existing = record with { Number = 203, CreatedAt = october, UpdatedAt = october };
        store.SavePcb(file, existing);
        store.SavePcbImage(file, existing.Number, new(null, october, new(0, 0, 1, 1), true,
            "ORIGINAL", null, null, [1, 2, 3]));

        // The settings counter is still zero. All retained months must be considered.
        Assert.Equal(208, store.NextPcbNumber(directory));
        Assert.Equal(209, new MachineStore(store.DatabaseFile).NextPcbNumber(directory));
        Assert.Throws<InvalidDataException>(() => store.SavePcb(file,
            existing with { CreatedAt = october.AddMinutes(8), PcbBarcode = "DIFFERENT" }));

        var saved = store.LoadPcbs(directory).Single(item => item.Number == 203);
        Assert.Equal(october, saved.CreatedAt);
        Assert.Null(saved.PcbBarcode);
        Assert.Equal("ORIGINAL", Assert.Single(store.LoadPcbImages(saved)).Barcode);
    }

    [Fact]
    public async Task PcbIdentityAndLiveDetailsFollowResultsAcrossStations()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-flow-{Guid.NewGuid():N}");
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddVirtualApplication(settings).BuildServiceProvider();
        var view = services.GetRequiredService<OperationViewModel>(); // Also constructs the machine/history subscription.
        var history = services.GetRequiredService<PcbHistoryWriter>();
        var placement = services.GetRequiredService<PcbPlacer>().Station;
        var fastening = services.GetRequiredService<BoltFasteningStation>().Station;
        var inspection = services.GetRequiredService<InspectionStation>();
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new() { Id = VirtualTestSupport.BoltId(1) }, new() { Id = VirtualTestSupport.BoltId(2) }];
        var first = placement.GetAssembly(HeatSinkSlot.HeatSink1);
        var second = placement.GetAssembly(HeatSinkSlot.HeatSink2);
        placement.TransferAssembliesTo(fastening, placement.CurrentJob, fastening.CurrentJob);
        var third = placement.GetAssembly(HeatSinkSlot.HeatSink1);
        await history.FlushAsync();
        Assert.Equal(new long[] { 3, 2, 1 }, view.PcbRecords.Select(record => record.Number));
        Assert.Same(first, fastening.GetAssembly(HeatSinkSlot.HeatSink1));
        view.PcbDetails.Record = view.PcbRecords.Single(record => record.Number == first.PcbNumber);
        recipe.Pcb.BoltPoints.Move(0, 1);
        first.RecordBolt(FasteningHead.Shooting, VirtualTestSupport.BoltId(1), new(false, 0.5, Error: "NG torque"));
        first.RecordBolt(FasteningHead.Pickup, VirtualTestSupport.BoltId(2), new(true, 1.1));
        first.CompleteFastening();
        fastening.TransferAssembliesTo(inspection.Station, fastening.CurrentJob, inspection.Station.CurrentJob);
        Assert.Same(first, inspection.Station.GetAssembly(HeatSinkSlot.HeatSink1));
        Assert.Same(second, inspection.Station.GetAssembly(HeatSinkSlot.HeatSink2));
        first.PcbBarcode = null;
        first.RecordBoltPresence(VirtualTestSupport.BoltId(1), false);
        first.RecordInspectionCapture(new(VirtualTestSupport.BoltId(1), DateTimeOffset.Now,
            new ImageFrame(2, 1, 6, [0, 0, 255, 0, 255, 0]), new PixelRegion(0, 0, 1, 1), false,
            BrightRatio: 0.1, MinimumBrightRatio: 0.8));
        first.CompleteInspection();
        await history.FlushAsync();
        Assert.Equal(first.PcbNumber, view.PcbDetails.Record!.Number);
        Assert.Equal(AssemblyResult.Ng, view.PcbDetails.Record.PcbBarcodeResult);
        Assert.Equal("NG torque", view.PcbDetails.Record.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Error);
        Assert.Equal(1.1, view.PcbDetails.Record.PickupBoltResults[VirtualTestSupport.BoltId(2)].Torque);
        Assert.False(view.PcbDetails.Record.BoltPresenceResults[VirtualTestSupport.BoltId(1)]);
        await view.LoadOlderPcbsCommand.ExecuteAsync(null);
        Assert.Equal(3, view.PcbRecords.Count);
        Assert.Null(view.PcbHistoryError);
        await view.PcbDetails.LoadImagesCommand.ExecuteAsync(null);
        var image = Assert.Single(view.PcbDetails.Images);
        Assert.Equal(VirtualTestSupport.BoltId(1), image.Record.BoltId);
        Assert.Equal(1, image.Ordinal);
        Assert.Equal(new[] { VirtualTestSupport.BoltId(1), VirtualTestSupport.BoltId(2) }, view.PcbDetails.Record!.BoltIds);
        Assert.Equal(2, recipe.Pcb.GetBoltOrdinal(VirtualTestSupport.BoltId(1)));
        Assert.Equal(0.1, image.Record.BrightRatio);
        Assert.NotNull(image.Image);
        Assert.Equal(2, image.Image.PixelWidth);
        Assert.False(image.Record.Success);
        view.PcbDetails.SelectedBolt = view.PcbDetails.BoltResults.Single(bolt => bolt.BoltId == VirtualTestSupport.BoltId(2));
        Assert.Null(view.PcbDetails.SelectedImage);
        await view.PcbDetails.LoadImagesCommand.ExecuteAsync(null);
        Assert.Null(view.PcbDetails.SelectedImage);
        view.PcbDetails.SelectedBolt = view.PcbDetails.BoltResults.Single(bolt => bolt.BoltId == VirtualTestSupport.BoltId(1));
        Assert.Equal(VirtualTestSupport.BoltId(1), view.PcbDetails.SelectedImage!.Record.BoltId);
        Assert.Equal(3, store.LoadPcbs(settings.PcbHistory.Directory).Count);

        var originalFolder = settings.PcbHistory.Directory;
        var paused = new VirtualTestSupport.PausedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task previousLoad;
        try
        {
            SynchronizationContext.SetSynchronizationContext(paused);
            previousLoad = view.LoadOlderPcbsCommand.ExecuteAsync(null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => paused.HasPending, TimeSpan.FromSeconds(2)));
            settings.PcbHistory.Directory = Path.Combine(originalFolder, "new folder");
            view.Activate();
            Assert.NotSame(previousLoad, view.LoadOlderPcbsCommand.ExecutionTask);
            await view.LoadOlderPcbsCommand.ExecutionTask!;
        }
        finally
        {
            paused.Release();
            await previousLoad.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Empty(view.PcbRecords);
        third.PcbBarcode = "Still in original file";
        await history.FlushAsync();
        Assert.Equal("Still in original file", store.LoadPcbs(originalFolder)[0].PcbBarcode);
        Assert.Empty(view.PcbRecords);
        Assert.False(Directory.Exists(settings.PcbHistory.Directory));
        var fourth = placement.GetAssembly(HeatSinkSlot.HeatSink2);
        await history.FlushAsync();
        Assert.Equal(4, fourth.PcbNumber);
        Assert.Equal(4, Assert.Single(store.LoadPcbs(settings.PcbHistory.Directory)).Number);
        Assert.Equal(4, Assert.Single(view.PcbRecords).Number);
        view.PcbDetails.Record = null;
        Assert.Empty(view.PcbDetails.Images);
        await view.ShutdownAsync();

        settings.PcbHistory.Directory = originalFolder;
        await using var restarted = new ServiceCollection().AddSingleton(new MachineStore(store.DatabaseFile))
            .AddVirtualApplication(settings).BuildServiceProvider();
        var reopenedView = restarted.GetRequiredService<OperationViewModel>();
        await reopenedView.LoadOlderPcbsCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 3, 2, 1 }, reopenedView.PcbRecords.Select(record => record.Number));
        Assert.Empty(restarted.GetRequiredService<PcbPlacer>().Station.Assemblies);
        reopenedView.PcbDetails.Record = reopenedView.PcbRecords[^1];
        Assert.Equal("NG torque", reopenedView.PcbDetails.Record.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Error);
        await reopenedView.PcbDetails.LoadImagesCommand.ExecuteAsync(null);
        Assert.Single(reopenedView.PcbDetails.Images);
        Assert.Empty(store.LoadPcbImages(reopenedView.PcbRecords[1]));
        await reopenedView.ShutdownAsync();
    }

    [Fact]
    public async Task RestartKeepsPcbNumberAndRecordedResults()
    {
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-restart-{Guid.NewGuid():N}");
        var store = VirtualTestSupport.OpenMachineStore();
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddVirtualApplication(settings).BuildServiceProvider();
        var history = services.GetRequiredService<PcbHistoryWriter>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        services.GetRequiredService<VirtualIoService>().SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        var first = work.GetAssembly(HeatSinkSlot.HeatSink1);
        first.RecordBolt(FasteningHead.Shooting, VirtualTestSupport.BoltId(1), new(false, null, Error: "Timeout"));
        work.Restart(work.CurrentJob);
        Assert.Same(first, work.GetAssembly(HeatSinkSlot.HeatSink1));
        work.Complete(work.CurrentJob);
        await history.FlushAsync();
        Assert.Equal("Timeout", Assert.Single(store.LoadPcbs(settings.PcbHistory.Directory)).ShootingBoltResults[VirtualTestSupport.BoltId(1)].Error);
    }

    [Fact]
    public async Task SelectingPickupResultKeepsItsHeadWhenTheImageAlsoHasAShootingResult()
    {
        await using var services = MachineTestSupport.CreateDiagnosticServices();
        var store = services.GetRequiredService<MachineStore>();
        var details = services.GetRequiredService<PcbResultsViewModel>();
        var bolt = VirtualTestSupport.BoltId(1);
        var result = new BoltResult(true, 5);
        var record = new PcbRecord(1, DateTimeOffset.Now, DateTimeOffset.Now, "Default", HeatSinkSlot.HeatSink1,
            null, AssemblyResult.Ok, AssemblyResult.Ok, AssemblyResult.Ok,
            new Dictionary<Guid, BoltResult> { [bolt] = result },
            new Dictionary<Guid, BoltResult> { [bolt] = result with { Torque = 6 } },
            new Dictionary<Guid, bool> { [bolt] = true }, [bolt])
        {
            DatabaseFile = Path.Combine(store.DatabaseFile + ".results", "PCB-2026-10.db"),
        };
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(
            InspectionPreviewViewModel.CreateBitmap(new ImageFrame(2, 2, 6, new byte[12])), null, null, null));
        encoder.Save(stream);
        store.SavePcb(record.DatabaseFile, record);
        foreach (var imageBolt in new Guid?[] { null, bolt })
            store.SavePcbImage(record.DatabaseFile, record.Number,
                new(imageBolt, record.CreatedAt, new(0, 0, 1, 1), true, null, 1, 0.5, stream.ToArray()));
        details.Record = record;
        await details.LoadImagesCommand.ExecutionTask!;
        details.SelectedImage = details.Images.Single(image => image.Record.BoltId is null);
        var pickup = details.BoltResults.Single(row => row.Head == FasteningHead.Pickup);
        details.SelectedBolt = pickup;
        Assert.Same(pickup, details.SelectedBolt);
        Assert.Same(pickup, details.SelectedBoltStage);
        Assert.Equal(bolt, details.SelectedImage?.Record.BoltId);

        await details.LoadImagesCommand.ExecuteAsync(null);
        Assert.Same(pickup, details.SelectedBolt);
        Assert.Equal(6, details.SelectedBoltStage?.Result.Torque);
    }

    [Fact]
    public async Task OperationShutdownWaitsForPendingResultImagesAndStopsLaterReloads()
    {
        await using var services = MachineTestSupport.CreateDiagnosticServices();
        var store = services.GetRequiredService<MachineStore>();
        var details = services.GetRequiredService<PcbResultsViewModel>();
        var operation = services.GetRequiredService<OperationViewModel>();
        var record = new PcbRecord(1, DateTimeOffset.Now, DateTimeOffset.Now, "Default", HeatSinkSlot.HeatSink1,
            null, AssemblyResult.Pending, AssemblyResult.Pending, AssemblyResult.Pending,
            new Dictionary<Guid, BoltResult>(), new Dictionary<Guid, BoltResult>(), new Dictionary<Guid, bool>(), [])
        {
            DatabaseFile = Path.Combine(store.DatabaseFile + ".results", "PCB-2026-10.db"),
        };
        store.SavePcb(record.DatabaseFile, record);
        var paused = new VirtualTestSupport.PausedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(paused);
            details.Record = record;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        var pending = details.LoadImagesCommand.ExecutionTask!;
        Task? shutdown = null;
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => paused.HasPending, TimeSpan.FromSeconds(2)));
            var next = record with { Number = 2 };
            store.SavePcb(next.DatabaseFile!, next);
            details.Record = next;
            var replacement = details.LoadImagesCommand.ExecutionTask!;
            details.Record = null; // Closing the result window clears its selection.
            var cleared = details.LoadImagesCommand.ExecutionTask!;
            shutdown = operation.ShutdownAsync();
            Assert.False(shutdown.IsCompleted);
            paused.Release();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(pending.IsCompleted);
            Assert.True(replacement.IsCompleted);
            Assert.True(cleared.IsCompleted);
            Assert.Empty(details.Images);
            details.Record = record with { Number = 3 };
            Assert.Same(cleared, details.LoadImagesCommand.ExecutionTask);
            Assert.Null(details.ImageError);
        }
        finally
        {
            paused.Release();
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            if (shutdown is not null)
                await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PcbSelectionDiscardsPreviousImageLoadAndItsError(bool corruptImage, bool earlierPcbImage)
    {
        await using var services = MachineTestSupport.CreateDiagnosticServices();
        var store = services.GetRequiredService<MachineStore>();
        var details = services.GetRequiredService<PcbResultsViewModel>();
        var record = new PcbRecord(1, DateTimeOffset.Now, DateTimeOffset.Now, "Default", HeatSinkSlot.HeatSink1,
            null, AssemblyResult.Pending, AssemblyResult.Pending, AssemblyResult.Pending,
            new Dictionary<Guid, BoltResult>(), new Dictionary<Guid, BoltResult>(), new Dictionary<Guid, bool>(), [])
        {
            DatabaseFile = Path.Combine(store.DatabaseFile + ".results", "PCB-2026-09.db"),
        };
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(
            InspectionPreviewViewModel.CreateBitmap(new ImageFrame(2, 2, 6, new byte[12])), null, null, null));
        encoder.Save(stream);
        store.SavePcb(record.DatabaseFile, record);
        store.SavePcbImage(record.DatabaseFile, record.Number,
            new(null, earlierPcbImage ? record.CreatedAt.AddMinutes(-8) : record.CreatedAt,
                new(0, 0, 1, 1), true, "PCB-1", null, null,
                corruptImage ? [1, 2, 3] : stream.ToArray()));
        store.SavePcbImage(record.DatabaseFile, record.Number,
            new(VirtualTestSupport.BoltId(1), record.CreatedAt, new(0, 0, 1, 1), true, null, 1, 0.5, stream.ToArray()));
        var next = record with { Number = 2 };
        store.SavePcb(next.DatabaseFile!, next);

        var context = new VirtualTestSupport.PausedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            details.Record = record;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        var pending = details.LoadImagesCommand.ExecutionTask!;
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => context.HasPending, TimeSpan.FromSeconds(2)));
            details.Record = next;
            context.Release();
            await details.LoadImagesCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(2));
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Same(next, details.Record);
            Assert.Empty(details.Images);
            Assert.Null(details.SelectedImage);
            Assert.Null(details.ImageError);

            details.Record = record;
            await details.LoadImagesCommand.ExecutionTask!;
            Assert.Equal(2, details.Images.Count);
            Assert.NotNull(details.Images.Single(image => image.Record.BoltId is not null).Image);
            var barcodeImage = details.Images.Single(image => image.Record.BoltId is null);
            if (corruptImage || earlierPcbImage)
            {
                Assert.NotNull(details.ImageError);
                Assert.Null(barcodeImage.Image);
                Assert.NotNull(barcodeImage.Error);
                Assert.Equal("—", barcodeImage.Verdict);
                Assert.Equal(barcodeImage.Error, barcodeImage.Details);
            }
            else
            {
                Assert.Null(details.ImageError);
                Assert.Equal("PCB-1", barcodeImage.Record.Barcode);
                Assert.NotNull(barcodeImage.Image);
            }
        }
        finally
        {
            context.Release();
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }
}
