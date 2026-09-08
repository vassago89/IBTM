using System;
using System.IO;
using System.Linq;
using System.Windows;
using IBTM.Core;
using IBTM.Inspection;
using IBTM.Inspection.Training;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class BoltTrainingStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingDatabaseIsAdoptedWithoutReplacingTrainingData(bool hasSettings)
    {
        var file = Path.Combine(Path.GetTempPath(), $"IBTM-training-migration-{Guid.NewGuid():N}.db");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file }.ToString());
        try
        {
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE Samples (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Image BLOB NOT NULL,
                        RegionSize INTEGER NOT NULL, Label INTEGER NOT NULL DEFAULT 0,
                        SampleUse INTEGER NOT NULL DEFAULT 0, Included INTEGER NOT NULL DEFAULT 1,
                        Polygon TEXT NOT NULL DEFAULT '[]');
                    CREATE TABLE Model (Id INTEGER PRIMARY KEY CHECK (Id = 1), Weights BLOB NOT NULL,
                        Epochs INTEGER NOT NULL, ValidationLoss REAL NOT NULL, TrainedAt TEXT NOT NULL);
                    INSERT INTO Samples VALUES (17, 'Existing capture', X'01020304', 192, 2, 1, 0,
                        '[{"X":12,"Y":24},{"X":64,"Y":24},{"X":64,"Y":80}]');
                    INSERT INTO Model VALUES (1, X'05060708', 23, 0.125, '2026-09-01T00:00:00.0000000+00:00');
                    """;
                command.ExecuteNonQuery();
                if (hasSettings)
                {
                    command.CommandText = """
                        CREATE TABLE TrainingSettings (Id INTEGER PRIMARY KEY CHECK (Id = 1), Value TEXT NOT NULL);
                        INSERT INTO TrainingSettings VALUES (1,
                            '{"MaxEpochs":70,"BatchSize":4,"LearningRate":0.0002,"Patience":6}');
                        """;
                    command.ExecuteNonQuery();
                }
            }

            var store = new BoltTrainingStore(file);
            var sample = Assert.Single(store.GetSamples());
            Assert.Equal(17, sample.Id);
            Assert.Equal("Existing capture", sample.Name);
            Assert.Equal(192, sample.RegionSize);
            Assert.Equal(BoltLabel.Bolt, sample.Label);
            Assert.Equal(BoltSampleUse.Validation, sample.Use);
            Assert.False(sample.Included);
            Assert.Equal(new[] { new Point(12, 24), new Point(64, 24), new Point(64, 80) }, sample.Polygon);
            Assert.Equal(hasSettings ? 70 : 50, store.LoadSettings().MaxEpochs);
            Assert.Equal(hasSettings ? 4 : 8, store.LoadSettings().BatchSize);
            Assert.Equal(0.5f, store.LoadSettings().MaskThreshold);

            store.SaveSettings(new() { MaxEpochs = 9, BatchSize = 3, LearningRate = 0.0005, Patience = 2, MaskThreshold = 0.7f });
            store.SetIncluded(17, true);
            var pixels = new byte[] { 20, 40, 60 };
            var id = store.AddImage("New capture", new ImageFrame(1, 1, 3, pixels), 1);
            Assert.True(id > 17);

            var reopened = new BoltTrainingStore(file);
            Assert.Equal(2, reopened.GetSamples().Count);
            Assert.True(reopened.GetSamples().Single(value => value.Id == 17).Included);
            Assert.Equal(9, reopened.LoadSettings().MaxEpochs);
            Assert.Equal(0.0005, reopened.LoadSettings().LearningRate);
            Assert.Equal(0.7f, reopened.LoadSettings().MaskThreshold);
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT hex(Image) FROM Samples WHERE Id = 17";
            Assert.Equal("01020304", check.ExecuteScalar());
            check.CommandText = "SELECT hex(Weights) FROM Model WHERE Id = 1";
            Assert.Equal("05060708", check.ExecuteScalar());
            check.CommandText = "SELECT Epochs FROM Model WHERE Id = 1";
            Assert.Equal(23L, check.ExecuteScalar());
            check.CommandText = "SELECT ValidationLoss FROM Model WHERE Id = 1";
            Assert.Equal(0.125, check.ExecuteScalar());
            check.CommandText = "SELECT TrainedAt FROM Model WHERE Id = 1";
            Assert.Equal("2026-09-01T00:00:00.0000000+00:00", check.ExecuteScalar());
            check.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
            Assert.Equal(2L, check.ExecuteScalar());
            check.CommandText = "SELECT Image FROM Samples WHERE Id = $id";
            check.Parameters.AddWithValue("$id", id);
            Assert.Equal(pixels, BoltTrainingImages.Decode((byte[])check.ExecuteScalar()!).Pixels);
        }
        finally
        {
            connection.Dispose();
            SqliteConnection.ClearPool(connection);
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(file + suffix);
        }
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
        Assert.All(samples, sample =>
        {
            Assert.Equal(BoltLabel.Unlabeled, sample.Label);
            Assert.Empty(sample.Polygon);
            Assert.Equal(160, sample.RegionSize);
            Assert.Equal(timestamp, sample.Inspection!.CapturedAt);
            Assert.Equal("First", sample.Inspection.RecipeName);
        });
        Assert.Equal(new[] { AssemblyResult.Ok, AssemblyResult.Ng, AssemblyResult.Ng },
            samples.Select(sample => sample.Inspection!.Result));
        Assert.Equal(2, samples[^1].Inspection!.BoltNumber);
        Assert.Equal(HeatSinkSlot.HeatSink2, samples[^1].Inspection!.HeatSink);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file }.ToString());
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
        Assert.Equal(InspectionImageCollection.Off, new BoltTrainingStore(file).LoadSettings().ImageCollection);
    }
}
