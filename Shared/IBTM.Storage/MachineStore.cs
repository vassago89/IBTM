using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
                UiText.Format($"{typeof(T).Name} is empty."))
            : new T();
    }
}

public sealed class MachineStore
{
    public const int PcbHistoryPageSize = 100;

    private readonly DbContextOptions<MachineDbContext> _options;
    private readonly Lock _pcbHistoryGate;
    private string? _pcbCounterDirectory;

    public MachineStore(string? databaseFile = null)
    {
        _pcbHistoryGate = new();
        DatabaseFile = Path.GetFullPath(
            databaseFile ?? Path.Combine(AppContext.BaseDirectory, "Data", "Machine.db"));
        Directory.CreateDirectory(Path.GetDirectoryName(DatabaseFile)!);
        _options = new DbContextOptionsBuilder<MachineDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = DatabaseFile }.ToString())
            .Options;
        using var db = new MachineDbContext(_options);
        // Settings and recipes evolve inside JSON, not as database columns.
        db.Database.EnsureCreated();
    }

    public string DatabaseFile { get; }

    public ProductionCounts LoadProductionCounts(string recipeName)
    {
        using var db = new MachineDbContext(_options);
        return db.ProductionCounts.Where(row => row.RecipeName == recipeName)
            .Select(row => new ProductionCounts(row.OkCount, row.NgCount))
            .SingleOrDefault() ?? new(0, 0);
    }

    public ProductionCounts AddProductionCounts(string recipeName, int okCount, int ngCount)
    {
        using var db = new MachineDbContext(_options);
        using var transaction = db.Database.BeginTransaction();
        var row = db.ProductionCounts.SingleOrDefault(item => item.RecipeName == recipeName);
        if (row is null)
            db.ProductionCounts.Add(row = new() { RecipeName = recipeName });
        row.OkCount += okCount;
        row.NgCount += ngCount;
        db.SaveChanges();
        transaction.Commit();
        return new(row.OkCount, row.NgCount);
    }

    public void ClearProductionCounts(string recipeName)
    {
        using var db = new MachineDbContext(_options);
        db.ProductionCounts.Where(row => row.RecipeName == recipeName).ExecuteDelete();
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
            UiText.Format($"Recipe '{name}' is empty."));
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
            ?? throw new InvalidDataException(UiText.Format($"Recipe '{edited.Name}' is empty."));
        saved.ValidateBoltIds();
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
            bolt.MinimumHeight = changed.MinimumHeight;
            bolt.MaximumHeight = changed.MaximumHeight;
        }
        edited.CopyImageRegionsTo(saved.CarrierImages.Where(tile => tile.IsBarcode));
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
                throw new InvalidDataException(UiText.Format($"Recipe '{sourceRecipe}' has missing images."));
            // Read one retained image at a time while the save-as transaction owns the source.
            images = imageNumbers.Select(number => new RecipeImage(number, db.RecipeImages
                .Where(row => row.RecipeName == sourceRecipe && row.Number == number)
                .Select(row => row.Image).Single()));
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
            foreach (var image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = new RecipeImageRow { RecipeName = saved.Name, Number = image.Number, Image = image.Image };
                db.RecipeImages.Add(row);
                db.SaveChanges();
                db.Entry(row).State = EntityState.Detached;
            }
        }
        else
        {
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
            .SingleOrDefault() ?? throw new FileNotFoundException(UiText.Format($"Recipe '{name}', image {number} is missing."));
    }

    public long NextPcbNumber(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        lock (_pcbHistoryGate)
        {
            // Reconcile restored counters on first use and when the results folder changes.
            long lastSavedNumber = 0;
            if (!string.Equals(_pcbCounterDirectory, directory, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory, "PCB-????-??.db"))
                {
                    using var results = new PcbHistoryDbContext(file, SqliteOpenMode.ReadOnly);
                    lastSavedNumber = Math.Max(lastSavedNumber, results.Pcbs.Max(row => (long?)row.Number) ?? 0);
                }
            }
            using var db = new MachineDbContext(_options);
            using var transaction = db.Database.BeginTransaction();
            var counter = db.PcbCounter.Find(1)
                ?? throw new InvalidDataException(UiText.Get("The PCB counter is missing."));
            counter.Number = checked(Math.Max(counter.Number, lastSavedNumber) + 1);
            db.SaveChanges();
            transaction.Commit();
            _pcbCounterDirectory = directory;
            return counter.Number;
        }
    }

    public void SavePcb(string databaseFile, PcbRecord record)
    {
        if (!record.IsCurveDataLoaded)
            throw new InvalidOperationException(UiText.Get("Load the complete PCB result before saving it."));
        // Readers must wait until a new monthly file's schema is committed.
        using var historyLock = _pcbHistoryGate.EnterScope();
        Directory.CreateDirectory(Path.GetDirectoryName(databaseFile)!);
        using var db = new PcbHistoryDbContext(databaseFile, SqliteOpenMode.ReadWriteCreate);
        using var transaction = db.Database.BeginTransaction();
        db.CreateMissingTables();
        var pcb = db.Pcbs.Find(record.Number);
        if (pcb is null)
            db.Pcbs.Add(pcb = new() { Number = record.Number });
        else
        {
            using var saved = JsonDocument.Parse(pcb.Value);
            if (!saved.RootElement.GetProperty(nameof(record.CreatedAt)).GetDateTimeOffset().EqualsExact(record.CreatedAt))
                throw new InvalidDataException(UiText.Format($"PCB {record.Number} already belongs to a different production record ({databaseFile})."));
        }
        pcb.Value = JsonSerializer.Serialize(record);

        // Replace the complete snapshot so removed/manual results cannot keep an old graph.
        db.PcbBoltCurves.Where(row => row.PcbNumber == record.Number).ExecuteDelete();
        foreach (var (head, results) in new[]
        {
            (FasteningHead.Shooting, record.ShootingBoltResults),
            (FasteningHead.Pickup, record.PickupBoltResults),
        })
        {
            foreach (var (boltId, result) in results)
            {
                foreach (var stage in new[]
                {
                    result.PreliminaryResult ?? result.PreviousFinalResult?.PreliminaryResult,
                    result.PreviousFinalResult,
                    result,
                })
                {
                    if (stage?.TorqueCurve is not { } curve)
                        continue;
                    db.PcbBoltCurves.Add(new()
                    {
                        PcbNumber = record.Number, Head = head, BoltId = boltId.ToString("D"),
                        Stage = stage.Stage, Value = JsonSerializer.Serialize(curve),
                    });
                }
            }
        }
        var heightCurves = record.HeightResults.Where(pair => pair.Value.Sample.Readings.Length > 0)
            .ToDictionary(pair => pair.Key.ToString("D"), pair => pair.Value.Sample.Readings);
        if (record.HeightReference is { } plane)
        {
            if (plane.Point1.Readings.Length > 0) heightCurves.Add(nameof(plane.Point1), plane.Point1.Readings);
            if (plane.Point2.Readings.Length > 0) heightCurves.Add(nameof(plane.Point2), plane.Point2.Readings);
            if (plane.Point3.Readings.Length > 0) heightCurves.Add(nameof(plane.Point3), plane.Point3.Readings);
        }
        db.PcbHeightCurves.Where(row => row.PcbNumber == record.Number).ExecuteDelete();
        if (heightCurves.Count > 0)
            db.PcbHeightCurves.Add(new() { PcbNumber = record.Number, Value = JsonSerializer.Serialize(heightCurves) });
        db.SaveChanges();
        transaction.Commit();
    }

    public IReadOnlyList<PcbRecord> LoadPcbs(string directory, long? beforeNumber = null, int? count = null)
    {
        using var historyLock = _pcbHistoryGate.EnterScope();
        var records = new List<PcbRecord>();
        if (!Directory.Exists(directory))
            return records;
        foreach (var file in Directory.EnumerateFiles(directory, "PCB-????-??.db")
            .OrderByDescending(Path.GetFileName))
        {
            using var db = new PcbHistoryDbContext(file, SqliteOpenMode.ReadOnly);
            var query = db.Pcbs.AsNoTracking();
            if (beforeNumber.HasValue)
                query = query.Where(row => row.Number < beforeNumber.Value);
            query = query.OrderByDescending(row => row.Number);
            if (count is >= 0)
                query = query.Take(count.Value - records.Count);
            foreach (var row in query)
                records.Add(row.ReadRecord(file) with { IsCurveDataLoaded = false });
            if (records.Count == count)
                break;
        }
        return records;
    }

    public PcbRecord LoadPcb(PcbRecord record)
    {
        if (record.DatabaseFile is null)
            return record;
        using var db = new PcbHistoryDbContext(record.DatabaseFile, SqliteOpenMode.ReadOnly);
        var tables = db.Schema.Where(row => row.Type == "table").Select(row => row.Name).ToHashSet();
        var query = db.Pcbs.AsNoTracking();
        if (tables.Contains(nameof(db.PcbBoltCurves)))
            query = query.Include(row => row.BoltCurves);
        if (tables.Contains(nameof(db.PcbHeightCurves)))
            query = query.Include(row => row.HeightCurve);
        var pcb = query.SingleOrDefault(row => row.Number == record.Number);
        var saved = pcb?.ReadRecord(record.DatabaseFile);
        if (saved is null || saved.CreatedAt != record.CreatedAt)
            throw new InvalidDataException(UiText.Format($"PCB {record.Number} result was not found ({record.DatabaseFile})."));
        if (pcb!.HeightCurve is { } heightCurve)
        {
            var traces = JsonSerializer.Deserialize<Dictionary<string, ProbeReading[]>>(heightCurve.Value)
                ?? throw new InvalidDataException(UiText.Format($"PCB {record.Number} has invalid graph data."));
            HeightSample RestoreReadings(HeightSample sample, string key)
            {
                return traces.TryGetValue(key, out var readings) ? sample with { Readings = readings } : sample;
            }
            saved = saved with
            {
                HeightReference = saved.HeightReference is { } plane ? plane with
                {
                    Point1 = RestoreReadings(plane.Point1, nameof(plane.Point1)),
                    Point2 = RestoreReadings(plane.Point2, nameof(plane.Point2)),
                    Point3 = RestoreReadings(plane.Point3, nameof(plane.Point3)),
                } : null,
                HeightResults = saved.HeightResults.ToDictionary(pair => pair.Key, pair => pair.Value with
                {
                    Sample = RestoreReadings(pair.Value.Sample, pair.Key.ToString("D")),
                }),
            };
        }
        var curves = pcb.BoltCurves.ToDictionary(row => (row.Head, Guid.Parse(row.BoltId), row.Stage),
            row => JsonSerializer.Deserialize<AdcTorqueCurve>(row.Value)
                ?? throw new InvalidDataException(UiText.Format($"PCB {record.Number} has invalid graph data.")));
        return saved with
        {
            ShootingBoltResults = saved.ShootingBoltResults.ToDictionary(pair => pair.Key,
                pair => RestoreCurves(pair.Value, FasteningHead.Shooting, pair.Key)),
            PickupBoltResults = saved.PickupBoltResults.ToDictionary(pair => pair.Key,
                pair => RestoreCurves(pair.Value, FasteningHead.Pickup, pair.Key)),
        };

        BoltResult RestoreCurves(BoltResult result, FasteningHead head, Guid boltId)
        {
            return result with
            {
                TorqueCurve = curves.GetValueOrDefault((head, boltId, result.Stage)),
                PreliminaryResult = result.PreliminaryResult is { } preliminary
                    ? RestoreCurves(preliminary, head, boltId) : null,
                PreviousFinalResult = result.PreviousFinalResult is { } final
                    ? RestoreCurves(final, head, boltId) : null,
            };
        }
    }

    public void SavePcbImage(string databaseFile, long pcbNumber, PcbInspectionImage image)
    {
        if (image.BoltId == Guid.Empty)
            throw new ArgumentException(UiText.Get("A bolt image requires a nonempty GUID; use null for Data Matrix."), nameof(image));
        using var db = new PcbHistoryDbContext(databaseFile, SqliteOpenMode.ReadWrite);
        using var transaction = db.Database.BeginTransaction();
        var target = (image.BoltId ?? Guid.Empty).ToString("D");
        var metadata = JsonSerializer.Serialize(image);
        var updated = db.PcbImages.Where(row => row.PcbNumber == pcbNumber && row.Target == target)
            .ExecuteUpdate(update => update.SetProperty(row => row.Metadata, metadata).SetProperty(row => row.Png, image.Png));
        if (updated == 0)
        {
            db.PcbImages.Add(new() { PcbNumber = pcbNumber, Target = target, Metadata = metadata, Png = image.Png });
            db.SaveChanges();
        }
        transaction.Commit();
    }

    public void DeletePcbImages(string databaseFile, long pcbNumber)
    {
        using var db = new PcbHistoryDbContext(databaseFile, SqliteOpenMode.ReadWrite);
        db.PcbImages.Where(row => row.PcbNumber == pcbNumber).ExecuteDelete();
    }

    public IReadOnlyList<PcbInspectionImage> LoadPcbImages(PcbRecord record)
    {
        if (record.DatabaseFile is null)
            return [];
        using var db = new PcbHistoryDbContext(record.DatabaseFile, SqliteOpenMode.ReadOnly);
        var images = new List<PcbInspectionImage>();
        foreach (var row in db.PcbImages.AsNoTracking().Where(row => row.PcbNumber == record.Number).OrderBy(row => row.Target))
        {
            var target = Guid.Parse(row.Target);
            var image = JsonSerializer.Deserialize<PcbInspectionImage>(row.Metadata)
                ?? throw new InvalidDataException(UiText.Format($"PCB {record.Number} has invalid inspection image metadata."));
            if (image.InspectionPass == record.InspectionPass)
                images.Add(image with { BoltId = target == Guid.Empty ? null : target, Png = row.Png });
        }
        return images.OrderBy(image => image.BoltId is { } id ? record.GetBoltOrdinal(id) ?? int.MaxValue : 0).ToArray();
    }
}
