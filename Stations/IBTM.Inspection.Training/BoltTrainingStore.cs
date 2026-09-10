using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using IBTM.Core;
using Microsoft.EntityFrameworkCore;

namespace IBTM.Inspection.Training;

public enum BoltLabel
{
    [Description("Unlabeled")]
    Unlabeled = 0,
    [Description("Empty")]
    Empty = 1,
    [Description("Bolt")]
    Bolt = 2,
}

public enum BoltSampleUse
{
    [Description("Training")]
    Training = 0,
    [Description("Validation")]
    Validation = 1,
}

public sealed record BoltSampleInfo(
    long Id,
    string Name,
    BoltLabel Label,
    BoltSampleUse Use,
    bool Included,
    int RegionSize,
    Point[] Polygon,
    BoltImageInspection? Inspection = null);
internal sealed record BoltTrainedModel(byte[] Weights, int Epochs, double ValidationLoss);

public sealed class BoltTrainingStore
{
    private readonly DbContextOptions<BoltTrainingDb> _options;

    public BoltTrainingStore() : this(BoltTrainingDb.DefaultFile)
    {
    }

    public BoltTrainingStore(string databaseFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databaseFile))!);
        _options = BoltTrainingDb.CreateOptions(databaseFile);
        using var db = new BoltTrainingDb(_options);
        db.Database.EnsureCreated();
    }

    public BoltTrainingSettings LoadSettings()
    {
        using var db = new BoltTrainingDb(_options);
        return db.TrainingSettings.Select(row => row.Value).SingleOrDefault() is string json
            ? JsonSerializer.Deserialize<BoltTrainingSettings>(json)!
            : new BoltTrainingSettings();
    }

    public void SaveSettings(BoltTrainingSettings settings)
    {
        using var db = new BoltTrainingDb(_options);
        var row = db.TrainingSettings.Find(1);
        if (row is null)
            row = db.TrainingSettings.Add(new()).Entity;
        row.Value = JsonSerializer.Serialize(settings);
        db.SaveChanges();
    }

    public long AddImage(
        string name,
        ImageFrame image,
        int regionSize,
        BoltImageInspection? inspection = null)
    {
        if (regionSize <= 0 || regionSize > image.Width || regionSize > image.Height)
            throw new ArgumentOutOfRangeException(
                nameof(regionSize),
                "The central ROI must fit inside the image.");
        using var db = new BoltTrainingDb(_options);
        var sample = new BoltTrainingSample
        {
            Name = name,
            Image = BoltTrainingImages.Encode(image),
            RegionSize = regionSize,
            Inspection = inspection is null ? null : JsonSerializer.Serialize(inspection),
        };
        db.Samples.Add(sample);
        db.SaveChanges();
        return sample.Id;
    }

    public IReadOnlyList<BoltSampleInfo> GetSamples()
    {
        using var db = new BoltTrainingDb(_options);
        return db.Samples.OrderBy(sample => sample.Id)
            .Select(
                sample =>
                    new
                    {
                        sample.Id,
                        sample.Name,
                        sample.Label,
                        sample.SampleUse,
                        sample.Included,
                        sample.RegionSize,
                        sample.Polygon,
                        sample.Inspection
                    })
            .AsEnumerable()
            .Select(
                sample =>
                    new BoltSampleInfo(
                        sample.Id,
                        sample.Name,
                        sample.Label,
                        sample.SampleUse,
                        sample.Included,
                        sample.RegionSize,
                        JsonSerializer.Deserialize<Point[]>(sample.Polygon)!,
                        sample.Inspection is null
                            ? null
                            : JsonSerializer.Deserialize<BoltImageInspection>(sample.Inspection)))
            .ToArray();
    }

    public long GetDatabaseSizeBytes()
    {
        using var db = new BoltTrainingDb(_options);
        return db.Database.SqlQueryRaw<long>("SELECT page_count * page_size AS Value FROM pragma_page_count(), pragma_page_size()")
            .Single();
    }

    internal ImageFrame LoadImage(long id)
    {
        using var db = new BoltTrainingDb(_options);
        var image = db.Samples.Where(sample => sample.Id == id).Select(sample => sample.Image).Single();
        return BoltTrainingImages.Decode(image);
    }

    internal void SaveLabel(long id, BoltLabel label, Point[] polygon, int regionSize)
    {
        using var db = new BoltTrainingDb(_options);
        var json = JsonSerializer.Serialize(polygon);
        db.Samples.Where(sample => sample.Id == id)
            .ExecuteUpdate(
                update =>
                    update.SetProperty(
                        sample => sample.SampleUse,
                        sample =>
                            sample.Label == label
                                ? sample.SampleUse
                                : db.Samples.Count(other => other.Label == label && other.Id != id) % 5 == 0
                                    ? BoltSampleUse.Validation
                                    : BoltSampleUse.Training)
                        .SetProperty(sample => sample.Label, label)
                        .SetProperty(sample => sample.RegionSize, regionSize)
                        .SetProperty(sample => sample.Polygon, json));
    }

    public void SetIncluded(long id, bool included)
    {
        using var db = new BoltTrainingDb(_options);
        db.Samples.Where(sample => sample.Id == id)
            .ExecuteUpdate(update => update.SetProperty(sample => sample.Included, included));
    }

    internal BoltTrainedModel LoadModel()
    {
        using var db = new BoltTrainingDb(_options);
        return db.Models.Select(
            model => new BoltTrainedModel(model.Weights, model.Epochs, model.ValidationLoss))
            .SingleOrDefault() ?? throw new InvalidOperationException("No trained bolt model. Train the model in Bolt Training first.");
    }

    internal void SetUse(long id, BoltSampleUse use)
    {
        using var db = new BoltTrainingDb(_options);
        db.Samples.Where(sample => sample.Id == id)
            .ExecuteUpdate(update => update.SetProperty(sample => sample.SampleUse, use));
    }

    internal void SaveModel(BoltTrainedModel model)
    {
        using var db = new BoltTrainingDb(_options);
        var row = new BoltTrainingModel
        {
            Weights = model.Weights,
            Epochs = model.Epochs,
            ValidationLoss = model.ValidationLoss,
            TrainedAt = DateTimeOffset.UtcNow,
        };
        db.Entry(row).State = db.Models.Any() ? EntityState.Modified : EntityState.Added;
        db.SaveChanges();
    }
}
