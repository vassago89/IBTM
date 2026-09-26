using System;
using System.Collections.Generic;
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
    public async Task LockedDatabaseDoesNotBlockAssemblyCreationOrResultCollection()
    {
        var store = VirtualTest.OpenMachineStore();
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-locked-{Guid.NewGuid():N}");
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddIbtmApplication(settings).BuildServiceProvider();
        var history = services.GetRequiredService<PcbHistory>();
        var work = services.GetRequiredService<PcbPlacer>().Station;
        var snapshots = new List<PcbRecord>();
        history.Saved += snapshots.Add;

        using var connection = new SqliteConnection($"Data Source={store.DatabaseFile}");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var collect = Task.Run(() =>
        {
            var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
            assembly.RecordBolt(FasteningHead.Pickup, 1, new(false, null, Error: "First result"));
            assembly.RecordBolt(FasteningHead.Pickup, 1, new(true, 8));
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
        Assert.Equal("First result", snapshots[1].PickupBoltResults[1].Error);
        Assert.True(snapshots[2].PickupBoltResults[1].Success);
        var saved = Assert.Single(store.LoadPcbs(settings.PcbHistory.Directory));
        Assert.Equal(1, saved.Number);
        Assert.Equal(8, saved.PickupBoltResults[1].Torque);
        Assert.Equal(AssemblyResult.Ng, saved.FasteningResult); // Existing overwrite/sticky NG policy stays intact.
    }

    [Fact]
    public async Task DisposalWaitsForQueuedResultsToReachTheDatabase()
    {
        var store = VirtualTest.OpenMachineStore();
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-exit-{Guid.NewGuid():N}");
        var services = new ServiceCollection().AddSingleton(store)
            .AddIbtmApplication(settings).BuildServiceProvider();
        _ = services.GetRequiredService<PcbHistory>();
        using var connection = new SqliteConnection($"Data Source={store.DatabaseFile}");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var assembly = services.GetRequiredService<BoltFasteningStation>().Station.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBolt(FasteningHead.Pickup, 1, new(true, 8.2));
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
        Assert.Equal(8.2, record.PickupBoltResults[1].Torque);
    }

    [Fact]
    public async Task CounterFailureRetainsAssemblyAndLaterResultsForRetry()
    {
        var store = VirtualTest.OpenMachineStore();
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-counter-{Guid.NewGuid():N}");
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddIbtmApplication(settings).BuildServiceProvider();
        var history = services.GetRequiredService<PcbHistory>();
        var work = services.GetRequiredService<PcbPlacer>().Station;
        using (var connection = new SqliteConnection($"Data Source={store.DatabaseFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE PcbCounter";
            command.ExecuteNonQuery();
        }

        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        await Assert.ThrowsAsync<IOException>(history.FlushAsync);
        Assert.NotNull(history.SaveError);
        Assert.Null(assembly.PcbNumber);
        assembly.RecordBolt(FasteningHead.Pickup, 2, new(true, 8.1));
        Assert.Same(assembly, work.GetAssembly(HeatSinkSlot.HeatSink1));
        Assert.Equal(2, history.PendingCount);

        _ = new MachineStore(store.DatabaseFile); // Restore the missing counter.
        await history.FlushAsync();
        Assert.Null(history.SaveError);
        Assert.Equal(0, history.PendingCount);
        Assert.Equal(1, assembly.PcbNumber);
        var saved = Assert.Single(store.LoadPcbs(settings.PcbHistory.Directory));
        Assert.Equal(8.1, saved.PickupBoltResults[2].Torque);
        Assert.Equal(2, store.NextPcbNumber());
    }

    [Fact]
    public async Task SaveFailureKeepsNumberAndImageUntilOperatorRetries()
    {
        var store = VirtualTest.OpenMachineStore();
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-unwritable-{Guid.NewGuid():N}");
        File.WriteAllText(settings.PcbHistory.Directory, "A file blocks creation of the results folder.");
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddIbtmApplication(settings).BuildServiceProvider();
        var view = services.GetRequiredService<OperationViewModel>();
        var history = services.GetRequiredService<PcbHistory>();
        var work = services.GetRequiredService<InspectionStation>();
        var assembly = work.Station.GetAssembly(HeatSinkSlot.HeatSink1);
        try
        {
            await Assert.ThrowsAsync<IOException>(history.FlushAsync);
            Assert.Equal(1, assembly.PcbNumber);
            var pixels = new byte[] { 0, 0, 255, 0, 255, 0 };
            assembly.RecordInspectionCapture(new(1, DateTimeOffset.Now,
                new ImageFrame(2, 1, 6, pixels), new(0, 0, 1, 1), true));
            Array.Clear(pixels); // Encoding must use the image captured when the event was raised.
            assembly.RecordBoltPresence(1, true);
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
        Assert.True(record.BoltPresenceResults[1]);
        var image = Assert.Single(store.LoadPcbImages(record));
        using var stream = new MemoryStream(image.Png);
        var decoded = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            System.Windows.Media.Imaging.BitmapFrame.Create(stream), System.Windows.Media.PixelFormats.Bgr24, null, 0);
        var restored = new byte[6];
        decoded.CopyPixels(restored, 6, 0);
        Assert.Equal(new byte[] { 0, 0, 255, 0, 255, 0 }, restored);
        Assert.Equal(2, store.NextPcbNumber());
        await view.ShutdownAsync();
    }

    [Fact]
    public async Task CounterUpgradesExistingDatabaseAndContinuesAcrossReopen()
    {
        var store = VirtualTest.OpenMachineStore();
        var settings = new PcbHistorySettings { Directory = Path.Combine(Path.GetTempPath(), "PCB results") };
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
        Assert.Equal(1, store.NextPcbNumber());
        var numbers = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(store.NextPcbNumber)));
        Assert.Equal(Enumerable.Range(2, 8).Select(number => (long)number), numbers.Order());
        var reopened = new MachineStore(store.DatabaseFile);
        Assert.Equal(10, reopened.NextPcbNumber());
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
        Assert.Empty(store.LoadPcbImages(first with { DatabaseFile = oldFile }));
        var second = first with { Number = store.NextPcbNumber(), CreatedAt = october, UpdatedAt = october };
        store.SavePcb(newFile, second);
        store.SavePcb(newFile, second with { Number = store.NextPcbNumber() });
        store.SavePcb(oldFile, first with
        {
            UpdatedAt = october, PcbBarcode = "PCB-A", PcbBarcodeResult = AssemblyResult.Ok,
            FasteningResult = AssemblyResult.Ng, InspectionResult = AssemblyResult.Ng,
            PcbBoltResults = new System.Collections.Generic.Dictionary<int, BoltResult>
            {
                [1] = new(false, 0.75, Error: "Controller error 42")
                {
                    RecordedAt = september,
                    Controller = new("COM10", 1, 21, 876, 3, 1.2, 950, 3156, 19, 3175,
                        9, 42, 0, 6, 87, [21, 876, 3, 120, 75, 950, 3156, 19, 3175, 9, 42, 0, 6, 87]),
                },
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
        Assert.Equal(september, saved.PcbBoltResults[1].RecordedAt);
        Assert.Equal(19, saved.PcbBoltResults[1].Controller!.Angle2);
        Assert.Equal(new ushort[] { 21, 876, 3, 120, 75, 950, 3156, 19, 3175, 9, 42, 0, 6, 87 },
            saved.PcbBoltResults[1].Controller!.Registers);
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
        var history = services.GetRequiredService<PcbHistory>();
        var placement = services.GetRequiredService<PcbPlacer>().Station;
        var fastening = services.GetRequiredService<BoltFasteningStation>().Station;
        var inspection = services.GetRequiredService<InspectionStation>();
        var first = placement.GetAssembly(HeatSinkSlot.HeatSink1);
        var second = placement.GetAssembly(HeatSinkSlot.HeatSink2);
        placement.TransferAssembliesTo(fastening, placement.CurrentJob, fastening.CurrentJob);
        var third = placement.GetAssembly(HeatSinkSlot.HeatSink1);
        await history.FlushAsync();
        Assert.Equal(new long[] { 3, 2, 1 }, view.PcbRecords.Select(record => record.Number));
        Assert.Same(first, fastening.GetAssembly(HeatSinkSlot.HeatSink1));
        view.SelectedPcb = view.PcbRecords.Single(record => record.Number == first.PcbNumber);
        first.RecordBolt(FasteningHead.Shooting, 1, new(false, 0.5, Error: "NG torque"));
        first.RecordBolt(FasteningHead.Pickup, 2, new(true, 1.1));
        first.CompleteFastening();
        fastening.TransferAssembliesTo(inspection.Station, fastening.CurrentJob, inspection.Station.CurrentJob);
        Assert.Same(first, inspection.Station.GetAssembly(HeatSinkSlot.HeatSink1));
        Assert.Same(second, inspection.Station.GetAssembly(HeatSinkSlot.HeatSink2));
        first.PcbBarcode = null;
        first.RecordBoltPresence(1, false);
        first.RecordInspectionCapture(new(1, DateTimeOffset.Now,
            new ImageFrame(2, 1, 6, [0, 0, 255, 0, 255, 0]), new PixelRegion(0, 0, 1, 1), false,
            BrightRatio: 0.1, MinimumBrightRatio: 0.8));
        first.CompleteInspection();
        await history.FlushAsync();
        Assert.Equal(first.PcbNumber, view.SelectedPcb!.Number);
        Assert.Equal(AssemblyResult.Ng, view.SelectedPcb.PcbBarcodeResult);
        Assert.Equal("NG torque", view.SelectedPcb.PcbBoltResults[1].Error);
        Assert.Equal(1.1, view.SelectedPcb.PickupBoltResults[2].Torque);
        Assert.False(view.SelectedPcb.BoltPresenceResults[1]);
        await view.LoadOlderPcbsCommand.ExecuteAsync(null);
        Assert.Equal(3, view.PcbRecords.Count);
        Assert.Null(view.PcbHistoryError);
        await view.PcbDetails.LoadImagesCommand.ExecuteAsync(null);
        var image = Assert.Single(view.PcbDetails.Images);
        Assert.Equal(1, image.Record.BoltNumber);
        Assert.Equal(0.1, image.Record.BrightRatio);
        Assert.Equal(2, image.Image.PixelWidth);
        Assert.False(image.Record.Success);
        view.PcbDetails.SelectedBolt = view.PcbDetails.BoltResults.Single(bolt => bolt.Number == 2);
        Assert.Null(view.PcbDetails.SelectedImage);
        await view.PcbDetails.LoadImagesCommand.ExecuteAsync(null);
        Assert.Null(view.PcbDetails.SelectedImage);
        view.PcbDetails.SelectedBolt = view.PcbDetails.BoltResults.Single(bolt => bolt.Number == 1);
        Assert.Equal(1, view.PcbDetails.SelectedImage!.Record.BoltNumber);
        Assert.Equal(3, store.LoadPcbs(settings.PcbHistory.Directory).Count);

        var originalFolder = settings.PcbHistory.Directory;
        settings.PcbHistory.Directory = Path.Combine(originalFolder, "new folder");
        third.PcbBarcode = "Still in original file";
        await history.FlushAsync();
        Assert.Equal("Still in original file", store.LoadPcbs(originalFolder)[0].PcbBarcode);
        Assert.False(Directory.Exists(settings.PcbHistory.Directory));
        var fourth = placement.GetAssembly(HeatSinkSlot.HeatSink2);
        await history.FlushAsync();
        Assert.Equal(4, fourth.PcbNumber);
        Assert.Equal(4, Assert.Single(store.LoadPcbs(settings.PcbHistory.Directory)).Number);
        view.ClosePcbDetailsCommand.Execute(null);
        Assert.Null(view.SelectedPcb);
        await view.ShutdownAsync();

        settings.PcbHistory.Directory = originalFolder;
        await using var restarted = new ServiceCollection().AddSingleton(new MachineStore(store.DatabaseFile))
            .AddIbtmApplication(settings).BuildServiceProvider();
        var reopenedView = restarted.GetRequiredService<OperationViewModel>();
        await reopenedView.LoadOlderPcbsCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 3, 2, 1 }, reopenedView.PcbRecords.Select(record => record.Number));
        Assert.Empty(restarted.GetRequiredService<PcbPlacer>().Station.Assemblies);
        reopenedView.SelectedPcb = reopenedView.PcbRecords[^1];
        Assert.Equal("NG torque", reopenedView.SelectedPcb.PcbBoltResults[1].Error);
        await reopenedView.PcbDetails.LoadImagesCommand.ExecuteAsync(null);
        Assert.Single(reopenedView.PcbDetails.Images);
        Assert.Empty(store.LoadPcbImages(reopenedView.PcbRecords[1]));
        await reopenedView.ShutdownAsync();
    }

    [Fact]
    public async Task StationaryRepeatGetsNewNumberButRestartKeepsTheSamePcb()
    {
        var settings = new MachineSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-repeat-{Guid.NewGuid():N}");
        var store = VirtualTest.OpenMachineStore();
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddIbtmApplication(settings).BuildServiceProvider();
        var history = services.GetRequiredService<PcbHistory>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        services.GetRequiredService<VirtualIoService>().SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        var first = work.GetAssembly(HeatSinkSlot.HeatSink1);
        first.RecordBolt(FasteningHead.Shooting, 1, new(false, null, Error: "Timeout"));
        work.Restart(work.CurrentJob);
        Assert.Same(first, work.GetAssembly(HeatSinkSlot.HeatSink1));
        work.Complete(work.CurrentJob);
        work.StartRepeat(work.CurrentJob);
        var next = work.GetAssembly(HeatSinkSlot.HeatSink1);
        await history.FlushAsync();
        Assert.Equal(first.PcbNumber + 1, next.PcbNumber);
        Assert.Empty(next.PcbBoltResults);
        Assert.Equal("Timeout", store.LoadPcbs(settings.PcbHistory.Directory)[1].PcbBoltResults[1].Error);
    }
}
