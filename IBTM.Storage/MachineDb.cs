using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IBTM.Storage;

internal sealed class MachineDb : DbContext
{
    internal static string DefaultFile
    {
        get
        {
            return Path.Combine(AppContext.BaseDirectory, "Data", "Machine.db");
        }
    }

    public MachineDb() : this(CreateOptions(DefaultFile))
    {
    }

    public MachineDb(DbContextOptions<MachineDb> options) : base(options)
    {
    }

    internal DbSet<SettingRow> Settings
    {
        get
        {
            return Set<SettingRow>();
        }
    }

    internal DbSet<RecipeRow> Recipes
    {
        get
        {
            return Set<RecipeRow>();
        }
    }

    internal DbSet<RecipeImageRow> RecipeImages
    {
        get
        {
            return Set<RecipeImageRow>();
        }
    }

    internal static DbContextOptions<MachineDb> CreateOptions(string path)
    {
        return new DbContextOptionsBuilder<MachineDb>().UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString())
            .Options;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SettingRow>().HasKey(row => row.Key);
        var recipe = modelBuilder.Entity<RecipeRow>();
        recipe.HasKey(row => row.Name);
        recipe.Property(row => row.Name).UseCollation("NOCASE");
        var image = modelBuilder.Entity<RecipeImageRow>();
        image.HasKey(row => new { row.RecipeName, row.Number });
        image.Property(row => row.RecipeName).UseCollation("NOCASE");
        image.HasOne<RecipeRow>()
            .WithMany()
            .HasForeignKey(row => row.RecipeName)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SettingRow
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

internal sealed class RecipeRow
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

internal sealed class RecipeImageRow
{
    public string RecipeName { get; set; } = "";
    public int Number { get; set; }
    public byte[] Image { get; set; } = [];
}
