using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IBTM.Inspection.Training;

internal sealed class BoltTrainingDb : DbContext
{
    internal static string DefaultFile
    {
        get
        {
            return Path.Combine(AppContext.BaseDirectory, "TrainingData", "BoltTraining.db");
        }
    }

    public BoltTrainingDb(DbContextOptions<BoltTrainingDb> options) : base(options)
    {
    }

    internal DbSet<BoltTrainingSample> Samples
    {
        get
        {
            return Set<BoltTrainingSample>();
        }
    }

    internal DbSet<BoltTrainingModel> Models
    {
        get
        {
            return Set<BoltTrainingModel>();
        }
    }

    internal DbSet<BoltTrainingSettingsRow> TrainingSettings
    {
        get
        {
            return Set<BoltTrainingSettingsRow>();
        }
    }

    internal static DbContextOptions<BoltTrainingDb> CreateOptions(string databaseFile)
    {
        return new DbContextOptionsBuilder<BoltTrainingDb>().UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = databaseFile }.ToString())
            .Options;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var sample = modelBuilder.Entity<BoltTrainingSample>();
        sample.ToTable(nameof(Samples));
        sample.Property(value => value.Label).HasDefaultValue(BoltLabel.Unlabeled);
        sample.Property(value => value.SampleUse).HasDefaultValue(BoltSampleUse.Training);
        sample.Property(value => value.Included).HasDefaultValue(true);
        sample.Property(value => value.Polygon).HasDefaultValue("[]");

        var model = modelBuilder.Entity<BoltTrainingModel>();
        model.ToTable("Model", table => table.HasCheckConstraint("CK_Model_Id", "Id = 1"));
        model.Property(value => value.Id).ValueGeneratedNever();

        var settings = modelBuilder.Entity<BoltTrainingSettingsRow>();
        settings.ToTable(
            nameof(TrainingSettings),
            table => table.HasCheckConstraint("CK_TrainingSettings_Id", "Id = 1"));
        settings.Property(value => value.Id).ValueGeneratedNever();
    }
}

internal sealed class BoltTrainingSample
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public byte[] Image { get; set; } = [];
    public int RegionSize { get; set; }
    public BoltLabel Label { get; set; }
    public BoltSampleUse SampleUse { get; set; }
    public bool Included { get; set; } = true;
    public string Polygon { get; set; } = "[]";
    public string? Inspection { get; set; }
}

internal sealed class BoltTrainingModel
{
    public int Id { get; set; } = 1;
    public byte[] Weights { get; set; } = [];
    public int Epochs { get; set; }
    public double ValidationLoss { get; set; }
    public DateTimeOffset TrainedAt { get; set; }
}

internal sealed class BoltTrainingSettingsRow
{
    public int Id { get; set; } = 1;
    public string Value { get; set; } = "";
}
