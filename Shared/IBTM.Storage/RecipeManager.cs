using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Inspection;

namespace IBTM.Storage;

public sealed class RecipeManager
{
    private readonly MachineStore _database;
    private readonly RecipeSelectionSettings _selection;
    private readonly SemaphoreSlim _saveGate;
    private string? _imageRecipeName;

    public RecipeManager(MachineStore database, RecipeSelectionSettings selection)
    {
        _database = database;
        _selection = selection;
        _saveGate = new(1, 1);
        InspectionSync = new();
        Current = new();
    }

    public event Action? Changed;
    public event Action? InspectionSettingsChanged;

    public Recipe Current { get; }

    public Lock InspectionSync { get; }

    public async Task SaveInspectionAsync(Recipe edited, CancellationToken cancellationToken = default)
    {
        var snapshot = JsonSerializer.Deserialize<Recipe>(JsonSerializer.Serialize(edited))!;
        var applied = false;
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() => _database.SaveInspectionSettings(snapshot, cancellationToken), cancellationToken);
            // Publish before allowing another save to snapshot the current recipe.
            lock (InspectionSync)
            {
                if (Current.Name == snapshot.Name)
                {
                    Current.ApplyInspectionSettings(snapshot);
                    applied = true;
                }
            }
        }
        finally
        {
            _saveGate.Release();
        }
        if (applied)
            InspectionSettingsChanged?.Invoke();
    }

    public void New()
    {
        lock (InspectionSync)
        {
            Current.ReplaceWith(new Recipe { Name = "New" });
            _imageRecipeName = null;
        }
        Changed?.Invoke();
    }

    public async Task LoadAsync(string name, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await Task.Run(() => _database.LoadRecipe(name), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_selection.LastRecipeName != loaded.Name)
            {
                await _database.SaveSettingsAsync(
                    [new RecipeSelectionSettings { LastRecipeName = loaded.Name }],
                    cancellationToken);
            }

            // Apply a committed selection even if cancellation arrives afterward.
            lock (InspectionSync)
            {
                Current.ReplaceWith(loaded);
                _imageRecipeName = Current.Name;
                _selection.LastRecipeName = Current.Name;
            }
        }
        finally
        {
            _saveGate.Release();
        }
        Changed?.Invoke();
    }

    public async Task SaveAsync(string name, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            Recipe snapshot;
            string? imageRecipeName;
            lock (InspectionSync)
            {
                snapshot = JsonSerializer.Deserialize<Recipe>(JsonSerializer.Serialize(Current))!;
                imageRecipeName = _imageRecipeName;
            }
            var originalName = snapshot.Name;
            snapshot.Name = name;
            await Task.Run(
                () => _database.SaveRecipe(
                    snapshot,
                    imageRecipeName,
                    selection: new RecipeSelectionSettings { LastRecipeName = name },
                    cancellationToken: cancellationToken),
                cancellationToken);
            lock (InspectionSync)
            {
                if (Current.Name == originalName)
                    Saved(name);
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task SaveImagesAsync(
        string name,
        List<CarrierImageTile> tiles,
        IEnumerable<RecipeImage> images,
        CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            Recipe snapshot;
            lock (InspectionSync)
            {
                snapshot = JsonSerializer.Deserialize<Recipe>(JsonSerializer.Serialize(Current))!;
            }
            var originalName = snapshot.Name;
            var capturedTiles = JsonSerializer.Deserialize<List<CarrierImageTile>>(JsonSerializer.Serialize(tiles))!;
            foreach (var tile in capturedTiles)
            {
                // Gantry capture owns images/positions; inspection teaching owns existing ROIs.
                var current = snapshot.CarrierImages.SingleOrDefault(item => item.Number == tile.Number
                    && item.HeatSink == tile.HeatSink && item.IsBarcode == tile.IsBarcode && item.BoltNumber == tile.BoltNumber);
                if (current is not null)
                    tile.Region = current.Region;
            }
            snapshot.Name = name;
            snapshot.CarrierImages = capturedTiles;
            await Task.Run(
                () => _database.SaveRecipe(
                    snapshot,
                    images: images,
                    selection: new RecipeSelectionSettings { LastRecipeName = name },
                    cancellationToken: cancellationToken),
                cancellationToken);
            lock (InspectionSync)
            {
                if (Current.Name == originalName)
                {
                    for (var index = 0; index < tiles.Count; index++)
                        tiles[index].Region = capturedTiles[index].Region;
                    Current.CarrierImages = tiles;
                    Saved(name);
                }
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void Saved(string name)
    {
        Current.Name = name;
        _imageRecipeName = name;
        _selection.LastRecipeName = name;
    }
}
