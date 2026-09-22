using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using Microsoft.EntityFrameworkCore;

namespace IBTM.Storage;

public sealed record RecipeImage(int Number, byte[] Image);

public sealed class SavedSettings
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public SavedSettings(IReadOnlyDictionary<string, string> values)
    {
        _values = values;
    }

    public T Get<T>()
        where T : Setting, new()
    {
        return _values.TryGetValue(typeof(T).Name, out var json)
            ? JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException(
                $"{typeof(T).Name} is empty.")
            : new T();
    }
}

public sealed partial class MachineStore
{
    private readonly DbContextOptions<MachineDb> _options;

    public MachineStore(string? databaseFile = null)
    {
        DatabaseFile = Path.GetFullPath(databaseFile ?? MachineDb.DefaultFile);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabaseFile)!);
        _options = MachineDb.CreateOptions(DatabaseFile);
        using var db = new MachineDb(_options);
        // Settings and recipes evolve inside JSON, not as database columns.
        db.Database.EnsureCreated();
        // EnsureCreated does not add tables to an existing settings/recipe database.
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS PcbCounter (
                Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                Number INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO PcbCounter (Id, Number) VALUES (1, 0);
            """);
    }

    public string DatabaseFile { get; }

    public bool HasData
    {
        get
        {
            using var db = new MachineDb(_options);
            return db.Settings.Any() || db.Recipes.Any();
        }
    }

    public SavedSettings LoadSettings()
    {
        using var db = new MachineDb(_options);
        return new(db.Settings.AsNoTracking().ToDictionary(row => row.Key, row => row.Value));
    }

    public void SaveSettings(
        IEnumerable<Setting> settings,
        CancellationToken cancellationToken = default)
    {
        using var db = new MachineDb(_options);
        var saved = db.Settings.ToDictionary(row => row.Key);
        foreach (var setting in settings.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = setting.GetType().Name;
            if (!saved.TryGetValue(key, out var row))
                db.Settings.Add(row = new() { Key = key });
            row.Value = JsonSerializer.Serialize(setting, setting.GetType());
        }

        cancellationToken.ThrowIfCancellationRequested();
        db.SaveChanges();
    }

    public async Task SaveSettingsAsync(
        IEnumerable<Setting> settings,
        CancellationToken cancellationToken = default)
    {
        await Task.Run(() => SaveSettings(settings, cancellationToken), cancellationToken);
    }

    public IReadOnlyList<string> GetRecipeNames()
    {
        using var db = new MachineDb(_options);
        return db.Recipes.OrderBy(row => row.Name).Select(row => row.Name).ToArray();
    }

    public T LoadRecipe<T>(string name)
    {
        using var db = new MachineDb(_options);
        var json = db.Recipes.Where(row => row.Name == name).Select(row => row.Value).Single();
        return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException(
            $"Recipe '{name}' is empty.");
    }

    public void SaveInspectionSettings(Recipe edited, CancellationToken cancellationToken = default)
    {
        using var db = new MachineDb(_options);
        using var transaction = db.Database.BeginTransaction();
        var row = db.Recipes.Single(item => item.Name == edited.Name);
        var saved = JsonSerializer.Deserialize<Recipe>(row.Value)
            ?? throw new InvalidDataException($"Recipe '{edited.Name}' is empty.");
        saved.ApplyInspectionSettings(edited);
        row.Value = JsonSerializer.Serialize(saved);
        cancellationToken.ThrowIfCancellationRequested();
        db.SaveChanges();
        transaction.Commit();
    }

    public void SaveRecipe<T>(
        string name,
        T recipe,
        IReadOnlyCollection<int> imageNumbers,
        string? sourceRecipe = null,
        IEnumerable<RecipeImage>? images = null,
        Setting? selection = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new MachineDb(_options);
        using var transaction = db.Database.BeginTransaction();
        var copyImages = images is null
            && sourceRecipe is not null
            && !string.Equals(name, sourceRecipe, StringComparison.OrdinalIgnoreCase);
        if (copyImages)
        {
            var count = db.RecipeImages.Count(
                row => row.RecipeName == sourceRecipe && imageNumbers.Contains(row.Number));
            if (count != imageNumbers.Count)
                throw new InvalidDataException($"Recipe '{sourceRecipe}' has missing images.");
        }

        var saved = db.Recipes.SingleOrDefault(row => row.Name == name);
        if (saved is null)
            db.Recipes.Add(saved = new() { Name = name });
        saved.Value = JsonSerializer.Serialize(recipe);
        db.SaveChanges();
        if (images is not null)
        {
            db.RecipeImages.Where(row => row.RecipeName == name).ExecuteDelete();
            db.ChangeTracker.Clear();
            foreach (var image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                db.RecipeImages.Add(
                    new() { RecipeName = saved.Name, Number = image.Number, Image = image.Image });
                db.SaveChanges();
                db.ChangeTracker.Clear();
            }
        }
        else
        {
            if (copyImages)
            {
                db.RecipeImages.Where(row => row.RecipeName == name).ExecuteDelete();
                // Copy BLOBs inside SQLite, without loading every image into application memory.
                if (imageNumbers.Count > 0)
                    db.Database.ExecuteSql(
                        $"INSERT INTO RecipeImages (RecipeName, Number, Image) SELECT {saved.Name}, Number, Image FROM RecipeImages WHERE RecipeName = {sourceRecipe}");
            }

            db.RecipeImages.Where(row => row.RecipeName == name && !imageNumbers.Contains(row.Number))
                .ExecuteDelete();
        }

        if (selection is not null)
        {
            var key = selection.GetType().Name;
            var row = db.Settings.SingleOrDefault(row => row.Key == key);
            if (row is null)
                db.Settings.Add(row = new() { Key = key });
            row.Value = JsonSerializer.Serialize(selection, selection.GetType());
            db.SaveChanges();
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public byte[] LoadRecipeImage(string name, int number)
    {
        using var db = new MachineDb(_options);
        return db.RecipeImages.Where(row => row.RecipeName == name && row.Number == number)
            .Select(row => row.Image)
            .Single();
    }
}
