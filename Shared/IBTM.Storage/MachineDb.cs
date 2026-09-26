using Microsoft.EntityFrameworkCore;

namespace IBTM.Storage;

internal sealed class MachineDb : DbContext
{
    public MachineDb(DbContextOptions<MachineDb> options) : base(options)
    {
    }

    internal DbSet<SettingRow> Settings => Set<SettingRow>();

    internal DbSet<RecipeRow> Recipes => Set<RecipeRow>();

    internal DbSet<RecipeImageRow> RecipeImages => Set<RecipeImageRow>();

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
    public RecipeImageRow()
    {
        Image = [];
    }

    public string RecipeName { get; set; } = "";
    public int Number { get; set; }
    public byte[] Image { get; set; }
}
