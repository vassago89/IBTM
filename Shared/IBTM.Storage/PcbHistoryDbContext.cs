using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IBTM.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace IBTM.Storage;

internal sealed class PcbHistoryDbContext : DbContext
{
    public PcbHistoryDbContext(string file, SqliteOpenMode mode)
        : base(new DbContextOptionsBuilder<PcbHistoryDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = file, Mode = mode, ForeignKeys = true }.ToString()).Options)
    {
    }

    internal DbSet<PcbRow> Pcbs => Set<PcbRow>();
    internal DbSet<PcbBoltCurveRow> PcbBoltCurves => Set<PcbBoltCurveRow>();
    internal DbSet<PcbHeightCurveRow> PcbHeightCurves => Set<PcbHeightCurveRow>();
    internal DbSet<PcbImageRow> PcbImages => Set<PcbImageRow>();
    internal DbSet<SqliteSchemaEntry> Schema => Set<SqliteSchemaEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SqliteSchemaEntry>().HasNoKey().ToView("sqlite_master");
        var pcb = modelBuilder.Entity<PcbRow>();
        pcb.HasKey(row => row.Number);
        pcb.Property(row => row.Number).ValueGeneratedNever();

        var bolt = modelBuilder.Entity<PcbBoltCurveRow>();
        bolt.HasKey(row => new { row.PcbNumber, row.Head, row.BoltId, row.Stage });
        bolt.HasOne<PcbRow>().WithMany(row => row.BoltCurves).HasForeignKey(row => row.PcbNumber);
        var height = modelBuilder.Entity<PcbHeightCurveRow>();
        height.HasKey(row => row.PcbNumber);
        height.HasOne<PcbRow>().WithOne(row => row.HeightCurve).HasForeignKey<PcbHeightCurveRow>(row => row.PcbNumber);
        var image = modelBuilder.Entity<PcbImageRow>();
        image.HasKey(row => new { row.PcbNumber, row.Target });
        image.HasOne<PcbRow>().WithMany().HasForeignKey(row => row.PcbNumber);
    }

    // Older monthly files may contain only Pcbs and PcbImages. Use the current EF model for the missing tables.
    internal void CreateMissingTables()
    {
        var existing = Schema.Where(row => row.Type == "table").Select(row => row.Name).ToHashSet();
        if (Model.GetRelationalModel().Tables.All(table => existing.Contains(table.Name)))
            return;

        var model = this.GetService<IDesignTimeModel>().Model;
        var missing = this.GetService<IMigrationsModelDiffer>().GetDifferences(null, model.GetRelationalModel())
            .OfType<CreateTableOperation>().Where(table => !existing.Contains(table.Name)).ToArray();
        var commands = this.GetService<IMigrationsSqlGenerator>().Generate(missing, model);
        var connection = this.GetService<IRelationalConnection>();
        foreach (var command in commands)
            command.ExecuteNonQuery(connection);
    }
}

internal sealed class SqliteSchemaEntry
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}

internal sealed class PcbRow
{
    public PcbRow()
    {
        BoltCurves = [];
    }

    public long Number { get; set; }
    public string Value { get; set; } = "";
    public List<PcbBoltCurveRow> BoltCurves { get; }
    public PcbHeightCurveRow? HeightCurve { get; set; }

    public PcbRecord ReadRecord(string file)
    {
        var record = JsonSerializer.Deserialize<PcbRecord>(Value)
            ?? throw new InvalidDataException(UiText.Format($"PCB {Number} has no result data."));
        if (record.ShootingBoltResults is null)
        {
            // Earlier records stored the shooting head under PcbBoltResults.
            using var document = JsonDocument.Parse(Value);
            var shooting = document.RootElement.GetProperty("PcbBoltResults").Deserialize<Dictionary<Guid, BoltResult>>()
                ?? throw new InvalidDataException(UiText.Format($"PCB {Number} has no result data."));
            record = record with { ShootingBoltResults = shooting };
        }
        return record with { Number = Number, DatabaseFile = Path.GetFullPath(file) };
    }
}

internal sealed class PcbBoltCurveRow
{
    public long PcbNumber { get; set; }
    public FasteningHead Head { get; set; }
    public string BoltId { get; set; } = "";
    public BoltFasteningStage Stage { get; set; }
    public string Value { get; set; } = "";
}

internal sealed class PcbHeightCurveRow
{
    public long PcbNumber { get; set; }
    public string Value { get; set; } = "";
}

internal sealed class PcbImageRow
{
    public long PcbNumber { get; set; }
    public string Target { get; set; } = "";
    public string Metadata { get; set; } = "";
    public required byte[] Png { get; set; }
}
