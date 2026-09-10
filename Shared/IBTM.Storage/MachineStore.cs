using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using IBTM.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IBTM.Storage;

public sealed record RecipeImage(int Number, byte[] Image);

public sealed class SavedSettings(IReadOnlyDictionary<string, string> values)
{
    public T Get<T>()
        where T : Setting, new()
    {
        return values.TryGetValue(typeof(T).Name, out var json)
            ? JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException(
                $"{typeof(T).Name} is empty.")
            : new T();
    }
}

public sealed class MachineStore
{
    private readonly DbContextOptions<MachineDb> _options;
    public string DatabaseFile { get; }

    public MachineStore(string? databaseFile = null)
    {
        DatabaseFile = Path.GetFullPath(databaseFile ?? MachineDb.DefaultFile);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabaseFile)!);
        _options = MachineDb.CreateOptions(DatabaseFile);
        using var db = new MachineDb(_options);
        // Settings and recipes evolve inside JSON, not as database columns.
        db.Database.EnsureCreated();
        // Correct only the obsolete conveyor output keys; retain configured channel numbers.
        db.Database.ExecuteSqlRaw("""
            UPDATE Settings
            SET Value = json_remove(
                json_set(Value, '$.Outputs.MainConveyorForward',
                    json(COALESCE(json_extract(Value, '$.Outputs.MainConveyorForward'),
                                  json_extract(Value, '$.Outputs.MainConveyorReverse')))),
                '$.Outputs.MainConveyorReverse')
            WHERE Key = 'ConveyorHardwareSettings'
              AND json_type(Value, '$.Outputs.MainConveyorReverse') IS NOT NULL;

            UPDATE Settings
            SET Value = json_remove(Value, '$.Outputs.MainConveyorNormalSpeed', '$.Outputs.NgConveyorNormalSpeed')
            WHERE Key IN ('ConveyorHardwareSettings', 'NgConveyorHardwareSettings')
              AND (json_type(Value, '$.Outputs.MainConveyorNormalSpeed') IS NOT NULL
                OR json_type(Value, '$.Outputs.NgConveyorNormalSpeed') IS NOT NULL);
            """);

        // These renamed outputs reverse ON/OFF meaning. Keep channels and swap the feedback pair.
        foreach (var (section, oldSignal, newSignal) in new[]
        {
            ("ConveyorHardwareSettings", "PcbPlacementBackupPlateUp", "PcbPlacementBackupPlateDown"),
            ("ConveyorHardwareSettings", "BoltFasteningBackupPlateUp", "BoltFasteningBackupPlateDown"),
            ("ConveyorHardwareSettings", "InspectionBackupPlateUp", "InspectionBackupPlateDown"),
            ("ConveyorHardwareSettings", "PcbPlacementStopperUp", "PcbPlacementStopperDown"),
            ("ConveyorHardwareSettings", "BoltFasteningStopperUp", "BoltFasteningStopperDown"),
            ("ConveyorHardwareSettings", "InspectionStopperUp", "InspectionStopperDown"),
            ("NgConveyorHardwareSettings", "NgConveyorStopperUp", "NgConveyorStopperDown"),
            ("NgCarrierTransferHardwareSettings", "NgCarrierPickupDown", "NgCarrierPickupUp"),
            ("NgCarrierTransferHardwareSettings", "NgCarrierGripperClose", "NgCarrierGripperOpen"),
            ("NgShuttleHardwareSettings", "NgShuttleDown", "NgShuttleUp"),
            ("BoltFasteningHardwareSettings", "PickupHeadDown", "PickupHeadUp"),
            ("BoltFasteningHardwareSettings", "ShootingHeadDown", "ShootingHeadUp"),
        })
        {
            var oldPath = $"$.Outputs.{oldSignal}";
            var newPath = $"$.Outputs.{newSignal}";
            db.Database.ExecuteSqlRaw("""
                UPDATE Settings
                SET Value = json_remove(
                    json_set(Value, {1}, json(COALESCE(
                        json_extract(Value, {1}),
                        json_set(json_extract(Value, {0}),
                            '$.Feedback.OnInput', json_extract(Value, {0} || '.Feedback.OffInput'),
                            '$.Feedback.OffInput', json_extract(Value, {0} || '.Feedback.OnInput'))))),
                    {0})
                WHERE Key = {2}
                  AND json_type(Value, {0}) IS NOT NULL;
                """, oldPath, newPath, section);
        }

        // Correct the old default DI pairs once; leave custom channel assignments intact.
        foreach (var (section, signal, oldUp, oldDown) in new[]
        {
            ("ConveyorHardwareSettings", "PcbPlacementStopper", 57, 58),
            ("ConveyorHardwareSettings", "BoltFasteningStopper", 64, 65),
            ("ConveyorHardwareSettings", "InspectionStopper", 71, 72),
            ("NgConveyorHardwareSettings", "NgConveyorStopper", 87, 88),
        })
        {
            db.Database.ExecuteSqlRaw("""
                UPDATE Settings
                SET Value = json_set(Value, {1}, {4}, {2}, {3})
                WHERE Key = {0}
                  AND json_extract(Value, {1}) = {3}
                  AND json_extract(Value, {2}) = {4};
                """, section, $"$.Inputs.{signal}Up", $"$.Inputs.{signal}Down", oldUp, oldDown);
        }

        // Restore mappings omitted by the sensorless NG stopper version; retain configured channels.
        db.Database.ExecuteSqlRaw("""
            UPDATE Settings
            SET Value = json_set(Value,
                '$.Inputs.NgConveyorStopperDown', COALESCE(json_extract(Value, '$.Inputs.NgConveyorStopperDown'), 87),
                '$.Inputs.NgConveyorStopperUp', COALESCE(json_extract(Value, '$.Inputs.NgConveyorStopperUp'), 88),
                '$.Outputs.NgConveyorStopperDown.Feedback',
                json(COALESCE(json_extract(Value, '$.Outputs.NgConveyorStopperDown.Feedback'),
                    json_object('OnInput', 'NgConveyorStopperDown', 'OffInput', 'NgConveyorStopperUp'))))
            WHERE Key = 'NgConveyorHardwareSettings'
              AND (json_extract(Value, '$.Inputs.NgConveyorStopperUp') IS NULL
                OR json_extract(Value, '$.Inputs.NgConveyorStopperDown') IS NULL
                OR json_extract(Value, '$.Outputs.NgConveyorStopperDown.Feedback') IS NULL);
            """);
    }

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

    public void Backup(string target)
    {
        CopyDatabase(DatabaseFile, target);
    }

    public void PrepareRestore(string source)
    {
        using (var connection = CreateConnection(source, SqliteOpenMode.ReadOnly))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Settings', 'Recipes', 'RecipeImages')";
            if ((long)command.ExecuteScalar()! != 3)
                throw new InvalidDataException("Select an IBTM machine database, not the training database.");
            command.CommandText = "PRAGMA quick_check";
            if (!Equals(command.ExecuteScalar(), "ok"))
                throw new InvalidDataException("The selected database failed its integrity check.");
        }

        CopyDatabase(source, DatabaseFile + ".restore");
    }

    public static void RestorePending(string? databaseFile = null)
    {
        var target = Path.GetFullPath(databaseFile ?? MachineDb.DefaultFile);
        var pending = target + ".restore";
        if (!File.Exists(pending))
            return;
        if (File.Exists(target))
            CopyDatabase(target, target + ".previous");
        CopyDatabase(pending, target);
        File.Delete(pending);
    }

    private static void CopyDatabase(string source, string target)
    {
        if (string.Equals(
            Path.GetFullPath(source),
            Path.GetFullPath(target),
            StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source and destination databases must be different.");
        using var from = CreateConnection(source, SqliteOpenMode.ReadOnly);
        using var to = CreateConnection(target, SqliteOpenMode.ReadWriteCreate);
        from.Open();
        to.Open();
        from.BackupDatabase(to);
    }

    private static SqliteConnection CreateConnection(string path, SqliteOpenMode mode)
    {
        return new(
            new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path), Mode = mode, Pooling = false }.ToString());
    }
}
