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
    private string? _imageRecipeName;

    public RecipeManager(MachineStore database, RecipeSelectionSettings selection)
    {
        _database = database;
        _selection = selection;
        InspectionSync = new();
        Current = new();
    }

    public event Action? Changed;

    public Recipe Current { get; }

    public Lock InspectionSync { get; }

    public async Task SaveInspectionAsync(Recipe edited, CancellationToken cancellationToken = default)
    {
        var snapshot = JsonSerializer.Deserialize<Recipe>(JsonSerializer.Serialize(edited))!;
        await Task.Run(() => _database.SaveInspectionSettings(snapshot, cancellationToken), cancellationToken);
        // Publish only committed settings. Automatic inspection snapshots these at each point's start.
        lock (InspectionSync)
        {
            if (Current.Name == snapshot.Name)
                Current.ApplyInspectionSettings(snapshot);
        }
    }

    public IReadOnlyList<string> GetRecipeNames()
    {
        return _database.GetRecipeNames();
    }

    public void New()
    {
        Current.ReplaceWith(new Recipe { Name = "New" });
        _imageRecipeName = null;
        Changed?.Invoke();
    }

    public async Task LoadAsync(string name, CancellationToken cancellationToken = default)
    {
        var loaded = await Task.Run(() => _database.LoadRecipe<Recipe>(name), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_selection.LastRecipeName != loaded.Name)
        {
            await _database.SaveSettingsAsync(
                [new RecipeSelectionSettings { LastRecipeName = loaded.Name }],
                cancellationToken);
        }

        // Apply a committed selection even if cancellation arrives afterward.
        Current.ReplaceWith(loaded);
        _imageRecipeName = Current.Name;
        _selection.LastRecipeName = Current.Name;
        Changed?.Invoke();
    }

    public async Task SaveAsync(string name, CancellationToken cancellationToken = default)
    {
        await Task.Run(
            () =>
            {
                var document = JsonSerializer.SerializeToNode(Current)!;
                document[nameof(Recipe.Name)] = name;
                _database.SaveRecipe(
                    name,
                    document,
                    Current.CarrierImages.Select(tile => tile.Number).ToArray(),
                    _imageRecipeName,
                    selection: new RecipeSelectionSettings { LastRecipeName = name },
                    cancellationToken: cancellationToken);
            },
            cancellationToken);
        Saved(name);
    }

    public async Task SaveImagesAsync(
        string name,
        List<CarrierImageTile> tiles,
        IEnumerable<RecipeImage> images,
        CancellationToken cancellationToken = default)
    {
        await Task.Run(
            () =>
            {
                var document = JsonSerializer.SerializeToNode(Current)!;
                document[nameof(Recipe.Name)] = name;
                document[nameof(Recipe.CarrierImages)] = JsonSerializer.SerializeToNode(tiles);
                _database.SaveRecipe(
                    name,
                    document,
                    tiles.Select(tile => tile.Number).ToArray(),
                    images: images,
                    selection: new RecipeSelectionSettings { LastRecipeName = name },
                    cancellationToken: cancellationToken);
            },
            cancellationToken);
        Current.CarrierImages = tiles;
        Saved(name);
    }

    private void Saved(string name)
    {
        Current.Name = name;
        _imageRecipeName = name;
        _selection.LastRecipeName = name;
    }

    public byte[] LoadImage(string name, int number)
    {
        return _database.LoadRecipeImage(name, number);
    }
}
