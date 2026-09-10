using System;
using System.IO;
using System.Linq;
using IBTM.Core;
using IBTM.Inspection;
using IBTM.Inspection.Training;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class BoltTrainingStoreTests
{
    [Fact]
    public void SettingsAndSamplesPersistWhenReopened()
    {
        var file = Path.Combine(Path.GetTempPath(), $"IBTM-training-{Guid.NewGuid():N}.db");
        var store = new BoltTrainingStore(file);
        store.SaveSettings(new() { MaxEpochs = 9, BatchSize = 3, MaskThreshold = 0.7f });
        var id = store.AddImage("Capture", new ImageFrame(1, 1, 3, [20, 40, 60]), 1);
        store.SetIncluded(id, false);

        var reopened = new BoltTrainingStore(file);
        var settings = reopened.LoadSettings();
        Assert.Equal(9, settings.MaxEpochs);
        Assert.Equal(3, settings.BatchSize);
        Assert.Equal(0.7f, settings.MaskThreshold);
        var sample = Assert.Single(reopened.GetSamples());
        Assert.Equal(id, sample.Id);
        Assert.Equal("Capture", sample.Name);
        Assert.Equal(1, sample.RegionSize);
        Assert.Equal(BoltLabel.Unlabeled, sample.Label);
        Assert.False(sample.Included);

        using var connection = new SqliteConnection($"Data Source={file}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory'";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void CollectionFiltersEachInspectionAndKeepsUnlabeledOriginals()
    {
        var file = Path.Combine(Path.GetTempPath(), $"IBTM-collection-{Guid.NewGuid():N}.db");
        var store = new BoltTrainingStore(file);
        var settings = store.LoadSettings();
        Assert.Equal(InspectionImageCollection.Off, settings.ImageCollection);
        var recipeName = "First";
        var collector = new BoltImageCollector(store, settings, () => recipeName);
        var pixels = Enumerable.Range(0, 240 * 180 * 3).Select(index => (byte)(index % 251)).ToArray();
        var original = new ImageFrame(240, 180, 720, pixels);
        var timestamp = DateTimeOffset.UtcNow;
        var ok = new BoltInspectionImage(original, 1, HeatSinkSlot.HeatSink1, 160, true, timestamp);
        var ng = ok with { BoltNumber = 2, HeatSink = HeatSinkSlot.HeatSink2, Present = false };
        foreach (var mode in Enum.GetValues<InspectionImageCollection>())
        {
            settings.ImageCollection = mode;
            collector.Collect(ok);
            collector.Collect(ng);
        }

        var samples = store.GetSamples();
        Assert.Equal(3, samples.Count); // Off: 0, All: 2, NG Only: 1.
        Assert.All(
            samples,
            sample =>
            {
                Assert.Equal(BoltLabel.Unlabeled, sample.Label);
                Assert.Empty(sample.Polygon);
                Assert.Equal(160, sample.RegionSize);
                Assert.Equal(timestamp, sample.Inspection!.CapturedAt);
                Assert.Equal("First", sample.Inspection.RecipeName);
            });
        Assert.Equal(
            new[] { AssemblyResult.Ok, AssemblyResult.Ng, AssemblyResult.Ng },
            samples.Select(sample => sample.Inspection!.Result));
        Assert.Equal(2, samples[^1].Inspection!.BoltNumber);
        Assert.Equal(HeatSinkSlot.HeatSink2, samples[^1].Inspection!.HeatSink);
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = file }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Image FROM Samples WHERE Id = $id";
        command.Parameters.AddWithValue("$id", samples[0].Id);
        var decoded = BoltTrainingImages.Decode((byte[])command.ExecuteScalar()!);
        Assert.Equal((240, 180), (decoded.Width, decoded.Height));
        Assert.Equal(pixels, decoded.Pixels);
        Assert.True(store.GetDatabaseSizeBytes() > 0);

        command.CommandText = "CREATE TRIGGER FailCollection BEFORE INSERT ON Samples BEGIN SELECT RAISE(ABORT, 'storage unavailable'); END";
        command.ExecuteNonQuery();
        collector.Collect(ng); // A storage error must not escape into the inspection loop.
        Assert.Contains("storage unavailable", collector.Error);
        command.CommandText = "DROP TRIGGER FailCollection";
        command.ExecuteNonQuery();
        collector.Collect(ng);
        Assert.Equal(3, store.GetSamples().Count); // No automatic retries after the error.
        collector.ClearError();
        recipeName = "Next";
        collector.Collect(ng);
        Assert.Equal("Next", store.GetSamples()[^1].Inspection!.RecipeName);
        settings.ImageCollection = InspectionImageCollection.Off;
        store.SaveSettings(settings);
        collector.Collect(ng);
        Assert.Equal(4, store.GetSamples().Count);
        Assert.Equal(
            InspectionImageCollection.Off,
            new BoltTrainingStore(file).LoadSettings().ImageCollection);
    }
}
