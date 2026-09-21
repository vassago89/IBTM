using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    public async Task CounterUpgradesExistingDatabaseAndContinuesAcrossReopen()
    {
        var store = VirtualTest.OpenMachineStore();
        var settings = new PcbHistorySettings { Directory = Path.Combine(Path.GetTempPath(), "PCB results") };
        store.SaveSettings([settings]);
        store.SaveRecipe("Existing", new { Name = "Existing", X = 123.4 }, []);
        using (var connection = new SqliteConnection($"Data Source={store.DatabaseFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE PcbCounter";
            command.ExecuteNonQuery();
        }

        store = new(store.DatabaseFile);
        Assert.Equal(1, store.NextPcbNumber());
        var numbers = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(store.NextPcbNumber)));
        Assert.Equal(Enumerable.Range(2, 8).Select(number => (long)number), numbers.Order());
        var reopened = new MachineStore(store.DatabaseFile);
        Assert.Equal(10, reopened.NextPcbNumber());
        Assert.Equal(settings.Directory, reopened.LoadSettings().Get<PcbHistorySettings>().Directory);
        Assert.Equal("Existing", Assert.Single(reopened.GetRecipeNames()));
        using var db = new SqliteConnection($"Data Source={store.DatabaseFile}");
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Pcbs'";
        Assert.Equal(0L, query.ExecuteScalar()); // Main DB holds only the counter, not results.
    }

    [Fact]
    public void MonthlyFilesKeepOldResultsAndPageNewestFirst()
    {
        var store = VirtualTest.OpenMachineStore();
        var directory = Path.Combine(Path.GetTempPath(), $"PCB-history-{Guid.NewGuid():N}");
        var september = new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.FromHours(9));
        var october = september.AddSeconds(2);
        var first = new PcbRecord(store.NextPcbNumber(), september, september, "Recipe A",
            HeatSinkSlot.HeatSink1, null, AssemblyResult.Pending, AssemblyResult.Pending,
            AssemblyResult.Pending, new System.Collections.Generic.Dictionary<int, BoltResult>(),
            new System.Collections.Generic.Dictionary<int, BoltResult>(),
            new System.Collections.Generic.Dictionary<int, bool>());
        var oldFile = Path.Combine(directory, "PCB-2026-09.db");
        var newFile = Path.Combine(directory, "PCB-2026-10.db");
        store.SavePcb(oldFile, first);
        var second = first with { Number = store.NextPcbNumber(), CreatedAt = october, UpdatedAt = october };
        store.SavePcb(newFile, second);
        store.SavePcb(newFile, second with { Number = store.NextPcbNumber() });
        store.SavePcb(oldFile, first with
        {
            UpdatedAt = october, PcbBarcode = "PCB-A", PcbBarcodeResult = AssemblyResult.Ok,
            FasteningResult = AssemblyResult.Ng, InspectionResult = AssemblyResult.Ng,
            PcbBoltResults = new System.Collections.Generic.Dictionary<int, BoltResult>
            {
                [1] = new(false, 0.75, Error: "Controller error 42"),
            },
            PickupBoltResults = new System.Collections.Generic.Dictionary<int, BoltResult> { [2] = new(true, 1.2) },
            BoltPresenceResults = new System.Collections.Generic.Dictionary<int, bool> { [1] = false, [2] = true },
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
        Assert.Equal("Controller error 42", saved.PcbBoltResults[1].Error);
        Assert.Equal(1.2, saved.PickupBoltResults[2].Torque);
        Assert.False(saved.BoltPresenceResults[1]);
        Assert.Equal(2, Directory.GetFiles(directory, "*.db").Length);
        Assert.Equal(4, reopened.NextPcbNumber());
    }

    [Fact]
    public async Task PcbIdentityAndLiveDetailsFollowResultsAcrossStations()
    {
        var store = VirtualTest.OpenMachineStore();
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-flow-{Guid.NewGuid():N}");
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddIbtmApplication(settings).BuildServiceProvider();
        var view = services.GetRequiredService<OperationViewModel>(); // Also constructs the machine/history subscription.
        var placement = services.GetRequiredService<PcbPlacementWork>();
        var fastening = services.GetRequiredService<BoltFasteningWork>();
        var inspection = services.GetRequiredService<InspectionWork>();
        var first = placement.GetAssembly(HeatSinkSlot.HeatSink1);
        var second = placement.GetAssembly(HeatSinkSlot.HeatSink2);
        placement.TransferAssembliesTo(fastening, placement.CurrentJob);
        var third = placement.GetAssembly(HeatSinkSlot.HeatSink1);
        Assert.Equal(new long[] { 3, 2, 1 }, view.PcbRecords.Select(record => record.Number));
        Assert.Same(first, fastening.GetAssembly(HeatSinkSlot.HeatSink1));
        view.SelectedPcb = view.PcbRecords.Single(record => record.Number == first.PcbNumber);
        first.RecordPcbBolt(1, new(false, 0.5, Error: "NG torque"));
        first.RecordPickupBolt(2, new(true, 1.1));
        first.CompleteFastening();
        fastening.TransferAssembliesTo(inspection, fastening.CurrentJob);
        Assert.Same(first, inspection.GetAssembly(HeatSinkSlot.HeatSink1));
        Assert.Same(second, inspection.GetAssembly(HeatSinkSlot.HeatSink2));
        first.PcbBarcode = null;
        first.RecordBoltPresence(1, false);
        first.CompleteInspection();
        Assert.Equal(first.PcbNumber, view.SelectedPcb!.Number);
        Assert.Equal(AssemblyResult.Ng, view.SelectedPcb.PcbBarcodeResult);
        Assert.Equal("NG torque", view.SelectedPcb.PcbBoltResults[1].Error);
        Assert.Equal(1.1, view.SelectedPcb.PickupBoltResults[2].Torque);
        Assert.False(view.SelectedPcb.BoltPresenceResults[1]);
        await view.LoadOlderPcbsCommand.ExecuteAsync(null);
        Assert.Equal(3, view.PcbRecords.Count);
        Assert.Null(view.PcbHistoryError);
        Assert.Equal(3, store.LoadPcbs(settings.PcbHistory.Directory).Count);

        var originalFolder = settings.PcbHistory.Directory;
        settings.PcbHistory.Directory = Path.Combine(originalFolder, "new folder");
        third.PcbBarcode = "Still in original file";
        Assert.Equal("Still in original file", store.LoadPcbs(originalFolder)[0].PcbBarcode);
        Assert.False(Directory.Exists(settings.PcbHistory.Directory));
        var fourth = placement.GetAssembly(HeatSinkSlot.HeatSink2);
        Assert.Equal(4, fourth.PcbNumber);
        Assert.Equal(4, Assert.Single(store.LoadPcbs(settings.PcbHistory.Directory)).Number);
        view.ClosePcbDetailsCommand.Execute(null);
        Assert.Null(view.SelectedPcb);
        await view.ShutdownAsync();
    }

    [Fact]
    public async Task StationaryRepeatGetsNewNumberButRestartKeepsTheSamePcb()
    {
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-repeat-{Guid.NewGuid():N}");
        var store = VirtualTest.OpenMachineStore();
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddIbtmApplication(settings).BuildServiceProvider();
        _ = services.GetRequiredService<PcbHistory>();
        var work = services.GetRequiredService<BoltFasteningWork>();
        services.GetRequiredService<VirtualIoService>().SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        var first = work.GetAssembly(HeatSinkSlot.HeatSink1);
        first.RecordPcbBolt(1, new(false, null, Error: "Timeout"));
        work.Restart(work.CurrentJob);
        Assert.Same(first, work.GetAssembly(HeatSinkSlot.HeatSink1));
        work.Complete(work.CurrentJob);
        work.StartRepeat(work.CurrentJob);
        var next = work.GetAssembly(HeatSinkSlot.HeatSink1);
        Assert.Equal(first.PcbNumber + 1, next.PcbNumber);
        Assert.Empty(next.PcbBoltResults);
        Assert.Equal("Timeout", store.LoadPcbs(settings.PcbHistory.Directory)[1].PcbBoltResults[1].Error);
    }
}
