using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Inspection;
using Microsoft.Data.Sqlite;
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

public sealed class MachineStore
{
    public const int PcbHistoryPageSize = 100;

    private readonly DbContextOptions<MachineDbContext> _options;

    public MachineStore(string? databaseFile = null)
    {
        DatabaseFile = Path.GetFullPath(
            databaseFile ?? Path.Combine(AppContext.BaseDirectory, "Data", "Machine.db"));
        Directory.CreateDirectory(Path.GetDirectoryName(DatabaseFile)!);
        _options = new DbContextOptionsBuilder<MachineDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = DatabaseFile }.ToString())
            .Options;
        using var db = new MachineDbContext(_options);
        // Settings and recipes evolve inside JSON, not as database columns.
        db.Database.EnsureCreated();
        // EnsureCreated does not add tables to an existing settings/recipe database.
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS PcbCounter (
                Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                Number INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO PcbCounter (Id, Number) VALUES (1, 0);
            CREATE TABLE IF NOT EXISTS ProductionCounts (
                RecipeName TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                OkCount INTEGER NOT NULL,
                NgCount INTEGER NOT NULL
            );
            """);
    }

    public string DatabaseFile { get; }

    public ProductionCounts LoadProductionCounts(string recipeName)
    {
        using var db = new MachineDbContext(_options);
        db.Database.OpenConnection();
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT OkCount, NgCount FROM ProductionCounts WHERE RecipeName = $recipe";
        command.Parameters.Add(new SqliteParameter("$recipe", recipeName));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetInt64(1)) : new(0, 0);
    }

    public ProductionCounts AddProductionCounts(string recipeName, int okCount, int ngCount)
    {
        using var db = new MachineDbContext(_options);
        db.Database.OpenConnection();
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO ProductionCounts (RecipeName, OkCount, NgCount) VALUES ($recipe, $ok, $ng)
            ON CONFLICT(RecipeName) DO UPDATE SET
                OkCount = ProductionCounts.OkCount + excluded.OkCount,
                NgCount = ProductionCounts.NgCount + excluded.NgCount
            RETURNING OkCount, NgCount
            """;
        command.Parameters.Add(new SqliteParameter("$recipe", recipeName));
        command.Parameters.Add(new SqliteParameter("$ok", okCount));
        command.Parameters.Add(new SqliteParameter("$ng", ngCount));
        using var reader = command.ExecuteReader();
        reader.Read();
        return new(reader.GetInt64(0), reader.GetInt64(1));
    }

    public void ClearProductionCounts(string recipeName)
    {
        using var db = new MachineDbContext(_options);
        db.Database.ExecuteSql($"DELETE FROM ProductionCounts WHERE RecipeName = {recipeName}");
    }

    public SavedSettings LoadSettings()
    {
        using var db = new MachineDbContext(_options);
        return new(db.Settings.AsNoTracking().ToDictionary(row => row.Key, row => row.Value));
    }

    public void SaveSettings(
        IEnumerable<Setting> settings,
        CancellationToken cancellationToken = default)
    {
        using var db = new MachineDbContext(_options);
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

    public Task SaveSettingsAsync(
        IEnumerable<Setting> settings,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => SaveSettings(settings, cancellationToken), cancellationToken);
    }

    public IReadOnlyList<string> RecipeNames
    {
        get
        {
            using var db = new MachineDbContext(_options);
            return db.Recipes.OrderBy(row => row.Name).Select(row => row.Name).ToArray();
        }
    }

    // Match SQLite NOCASE: fold ASCII letters and preserve all other characters.
    public static bool IsSameRecipeName(string? first, string? second)
    {
        if (first == second)
            return true;
        if (first is null || second is null || first.Length != second.Length)
            return false;
        for (var index = 0; index < first.Length; index++)
        {
            var left = first[index];
            var right = second[index];
            if (left is >= 'A' and <= 'Z')
                left = (char)(left + ('a' - 'A'));
            if (right is >= 'A' and <= 'Z')
                right = (char)(right + ('a' - 'A'));
            if (left != right)
                return false;
        }
        return true;
    }

    public Recipe LoadRecipe(string name)
    {
        using var db = new MachineDbContext(_options);
        var json = db.Recipes.Where(row => row.Name == name).Select(row => row.Value).Single();
        var recipe = JsonSerializer.Deserialize<Recipe>(json) ?? throw new InvalidDataException(
            $"Recipe '{name}' is empty.");
        if (recipe.Pcb.BoltPoints.Any(bolt => bolt.Id == Guid.Empty))
        {
            // Re-read under the write transaction so concurrent loads keep the same repaired IDs.
            using var transaction = db.Database.BeginTransaction();
            var row = db.Recipes.Single(row => row.Name == name);
            var document = JsonNode.Parse(row.Value)!;
            var bolts = document[nameof(Recipe.Pcb)]!["TaughtBolts"]!.AsArray();
            foreach (var bolt in bolts)
            {
                if ((bolt![nameof(BoltPoint.Id)]?.GetValue<Guid>() ?? Guid.Empty) == Guid.Empty)
                    bolt[nameof(BoltPoint.Id)] = JsonValue.Create(Guid.NewGuid());
            }
            recipe = document.Deserialize<Recipe>()!;
            recipe.ValidateBoltIds();
            // Save before publishing the recipe; another load must never generate different IDs.
            row.Value = JsonSerializer.Serialize(recipe);
            db.SaveChanges();
            transaction.Commit();
            return recipe;
        }
        recipe.ValidateBoltIds();
        return recipe;
    }

    public void SaveInspectionSettings(Recipe edited, CancellationToken cancellationToken = default)
    {
        edited.ValidateBoltIds();
        edited.BoltInspection.DataMatrix1.Validate();
        edited.BoltInspection.DataMatrix2.Validate();
        using var db = new MachineDbContext(_options);
        using var transaction = db.Database.BeginTransaction();
        var row = db.Recipes.Single(item => item.Name == edited.Name);
        var saved = JsonSerializer.Deserialize<Recipe>(row.Value)
            ?? throw new InvalidDataException($"Recipe '{edited.Name}' is empty.");
        saved.ValidateBoltIds();
        saved.BoltInspection.BrightnessThreshold = edited.BoltInspection.BrightnessThreshold;
        saved.BoltInspection.MinimumBrightRatio = edited.BoltInspection.MinimumBrightRatio;
        foreach (var pcb in Enum.GetValues<HeatSinkSlot>())
        {
            var target = saved.BoltInspection.GetDataMatrix(pcb);
            var source = edited.BoltInspection.GetDataMatrix(pcb);
            target.TryHarder = source.TryHarder;
            target.TryInverted = source.TryInverted;
            target.AutoRotate = source.AutoRotate;
            target.PureBarcode = source.PureBarcode;
            target.ThresholdMinimum = source.ThresholdMinimum;
            target.ThresholdMaximum = source.ThresholdMaximum;
            target.ThresholdStep = source.ThresholdStep;
            target.DilationRadius = source.DilationRadius;
        }
        foreach (var bolt in saved.Pcb.BoltPoints)
        {
            var changed = edited.Pcb.BoltPoints.SingleOrDefault(item => item.Id == bolt.Id);
            if (changed is null)
                continue;
            bolt.BrightnessThreshold = changed.BrightnessThreshold;
            bolt.MinimumBrightRatio = changed.MinimumBrightRatio;
            bolt.MinimumTurns = changed.MinimumTurns;
            bolt.MaximumTurns = changed.MaximumTurns;
        }
        foreach (var tile in saved.CarrierImages)
        {
            if (!tile.IsBarcode)
            {
                var bolt = saved.Pcb.BoltPoints.SingleOrDefault(item => item.Id == tile.BoltId);
                if (bolt is null || !edited.Pcb.BoltPoints.Any(item => item.Id == bolt.Id))
                    continue;
            }
            var changed = edited.CarrierImages.SingleOrDefault(item => item.Number == tile.Number
                && item.HeatSink == tile.HeatSink && item.IsBarcode == tile.IsBarcode && item.BoltId == tile.BoltId);
            if (changed is not null)
                tile.Region = changed.Region;
        }
        row.Value = JsonSerializer.Serialize(saved);
        cancellationToken.ThrowIfCancellationRequested();
        db.SaveChanges();
        transaction.Commit();
    }

    public void SaveRecipe(
        Recipe recipe,
        string? sourceRecipe = null,
        IEnumerable<RecipeImage>? images = null,
        RecipeSelectionSettings? selection = null,
        CancellationToken cancellationToken = default,
        string? name = null,
        List<CarrierImageTile>? tiles = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        recipe.ValidateBoltIds();
        recipe.BoltInspection.DataMatrix1.Validate();
        recipe.BoltInspection.DataMatrix2.Validate();
        name ??= recipe.Name;
        tiles ??= recipe.CarrierImages;
        var imageNumbers = tiles.Select(tile => tile.Number).ToArray();
        using var db = new MachineDbContext(_options);
        using var transaction = db.Database.BeginTransaction();
        var copyImages = images is null
            && sourceRecipe is not null
            && !IsSameRecipeName(name, sourceRecipe);
        if (copyImages)
        {
            var count = db.RecipeImages.Count(
                row => row.RecipeName == sourceRecipe && imageNumbers.Contains(row.Number));
            if (count != imageNumbers.Length)
                throw new InvalidDataException($"Recipe '{sourceRecipe}' has missing images.");
        }

        var saved = db.Recipes.SingleOrDefault(row => row.Name == name);
        if (saved is null)
            db.Recipes.Add(saved = new() { Name = name });
        // Save-as and a newly captured image only override the persisted fields.
        // Publish their name/metadata to the live recipe after the transaction commits.
        var document = JsonSerializer.SerializeToNode(recipe)!;
        document[nameof(Recipe.Name)] = name;
        if (!ReferenceEquals(tiles, recipe.CarrierImages))
            document[nameof(Recipe.CarrierImages)] = JsonSerializer.SerializeToNode(tiles);
        saved.Value = document.ToJsonString();
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
                if (imageNumbers.Length > 0)
                    db.Database.ExecuteSql(
                        $"INSERT INTO RecipeImages (RecipeName, Number, Image) SELECT {saved.Name}, Number, Image FROM RecipeImages WHERE RecipeName = {sourceRecipe}");
            }

            db.RecipeImages.Where(row => row.RecipeName == name && !imageNumbers.Contains(row.Number))
                .ExecuteDelete();
        }

        if (selection is not null)
        {
            var key = nameof(RecipeSelectionSettings);
            var row = db.Settings.SingleOrDefault(row => row.Key == key);
            if (row is null)
                db.Settings.Add(row = new() { Key = key });
            row.Value = JsonSerializer.Serialize(selection);
            db.SaveChanges();
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public byte[] LoadRecipeImage(string name, int number)
    {
        using var db = new MachineDbContext(_options);
        return db.RecipeImages.Where(row => row.RecipeName == name && row.Number == number)
            .Select(row => row.Image)
            .SingleOrDefault() ?? throw new FileNotFoundException($"Recipe '{name}', image {number} is missing.");
    }

    public long NextPcbNumber(string directory)
    {
        // Machine.db may have been restored while the monthly result files were retained.
        long lastSavedNumber = 0;
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "PCB-????-??.db"))
            {
                using var results = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = file,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString());
                results.Open();
                using var latest = results.CreateCommand();
                latest.CommandText = "SELECT COALESCE(MAX(Number), 0) FROM Pcbs";
                lastSavedNumber = Math.Max(lastSavedNumber, (long)latest.ExecuteScalar()!);
            }
        }
        using var db = new MachineDbContext(_options);
        db.Database.OpenConnection();
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "UPDATE PcbCounter SET Number = MAX(Number, $saved) + 1 WHERE Id = 1 RETURNING Number";
        command.Parameters.Add(new SqliteParameter("$saved", lastSavedNumber));
        return (long)(command.ExecuteScalar()
            ?? throw new InvalidDataException("The PCB counter is missing."));
    }

    public void SavePcb(string databaseFile, PcbRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databaseFile)!);
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databaseFile }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS Pcbs (Number INTEGER NOT NULL PRIMARY KEY, Value TEXT NOT NULL)";
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO Pcbs (Number, Value) VALUES ($number, $value)
            ON CONFLICT(Number) DO UPDATE SET Value = excluded.Value
            WHERE json_extract(Pcbs.Value, '$.CreatedAt') = json_extract(excluded.Value, '$.CreatedAt')
            """;
        command.Parameters.AddWithValue("$number", record.Number);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(record));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException($"PCB {record.Number} already belongs to a different production record ({databaseFile}).");
    }

    public IReadOnlyList<PcbRecord> LoadPcbs(string directory, long? beforeNumber = null, int count = PcbHistoryPageSize)
    {
        var records = new List<PcbRecord>();
        if (!Directory.Exists(directory))
            return records;
        foreach (var file in Directory.EnumerateFiles(directory, "PCB-????-??.db")
            .OrderByDescending(Path.GetFileName))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Number, Value FROM Pcbs WHERE ($before IS NULL OR Number < $before) ORDER BY Number DESC LIMIT $count";
            command.Parameters.AddWithValue("$before", (object?)beforeNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("$count", count - records.Count);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var record = JsonSerializer.Deserialize<PcbRecord>(reader.GetString(1))
                    ?? throw new InvalidDataException($"PCB {reader.GetInt64(0)} has no result data.");
                records.Add(record with { Number = reader.GetInt64(0), DatabaseFile = Path.GetFullPath(file) });
            }
            if (records.Count == count)
                break;
        }
        return records;
    }

    public void SavePcbImage(string databaseFile, long pcbNumber, PcbInspectionImage image)
    {
        if (image.BoltId == Guid.Empty)
            throw new ArgumentException("A bolt image requires a nonempty GUID; use null for Data Matrix.", nameof(image));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databaseFile }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS PcbImages (
                PcbNumber INTEGER NOT NULL, Target TEXT NOT NULL,
                Metadata TEXT NOT NULL, Png BLOB NOT NULL,
                PRIMARY KEY (PcbNumber, Target))
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO PcbImages (PcbNumber, Target, Metadata, Png) VALUES ($pcb, $target, $metadata, $png)
            ON CONFLICT(PcbNumber, Target) DO UPDATE SET Metadata=excluded.Metadata, Png=excluded.Png
            """;
        command.Parameters.AddWithValue("$pcb", pcbNumber);
        command.Parameters.AddWithValue("$target", (image.BoltId ?? Guid.Empty).ToString("D"));
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(image));
        command.Parameters.AddWithValue("$png", image.Png);
        command.ExecuteNonQuery();
    }

    public void DeletePcbImages(string databaseFile, long pcbNumber)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databaseFile }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PcbImages'";
        if ((long)command.ExecuteScalar()! == 0)
            return;
        command.CommandText = "DELETE FROM PcbImages WHERE PcbNumber=$pcb";
        command.Parameters.AddWithValue("$pcb", pcbNumber);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<PcbInspectionImage> LoadPcbImages(PcbRecord record)
    {
        if (record.DatabaseFile is null)
            return [];
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = record.DatabaseFile,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PcbImages'";
        if ((long)command.ExecuteScalar()! == 0)
            return [];
        command.CommandText = "SELECT Metadata, Png FROM PcbImages WHERE PcbNumber=$pcb ORDER BY Target";
        command.Parameters.AddWithValue("$pcb", record.Number);
        using var reader = command.ExecuteReader();
        var images = new List<PcbInspectionImage>();
        while (reader.Read())
        {
            var image = JsonSerializer.Deserialize<PcbInspectionImage>(reader.GetString(0))
                ?? throw new InvalidDataException($"PCB {record.Number} has invalid inspection image metadata.");
            images.Add(image with { Png = (byte[])reader[1] });
        }
        return images.OrderBy(image => image.BoltId is { } id ? record.GetBoltOrdinal(id) ?? int.MaxValue : 0).ToArray();
    }
}
