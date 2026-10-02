using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Inspection;

namespace IBTM.Storage;

public sealed partial class RecipeManager : ObservableObject
{
    private readonly MachineStore _database;
    private readonly RecipeSelectionSettings _selection;
    private readonly SemaphoreSlim _saveGate;

    public RecipeManager(MachineStore database, RecipeSelectionSettings selection)
    {
        _database = database;
        _selection = selection;
        _saveGate = new(1, 1);
        InspectionSync = new();
        Current = new();
        Counts = new(0, 0);
    }

    public event Action? Changed;
    public event Action? InspectionSettingsChanged;

    public Recipe Current { get; }

    [ObservableProperty]
    public partial ProductionCounts Counts { get; private set; }

    public Lock InspectionSync { get; }

    public async Task SaveInspectionAsync(Recipe edited, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() => _database.SaveInspectionSettings(edited, cancellationToken), cancellationToken);
        }
        finally
        {
            _saveGate.Release();
        }
        if (ReferenceEquals(Current, edited))
            NotifyInspectionChanged();
    }

    public void NotifyInspectionChanged()
    {
        InspectionSettingsChanged?.Invoke();
    }

    public async Task NewAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var counts = await Task.Run(() => _database.LoadProductionCounts("New"), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (InspectionSync)
            {
                Current.CopyFrom(new Recipe { Name = "New" });
            }
            Counts = counts;
        }
        finally
        {
            _saveGate.Release();
        }
        Changed?.Invoke();
    }

    public async Task RecordProductionAsync(int okCount, int ngCount)
    {
        var name = Current.Name;
        // A completed inspection must be saved even if STOP arrives at the same time.
        await _saveGate.WaitAsync();
        try
        {
            var counts = await Task.Run(() => _database.AddProductionCounts(name, okCount, ngCount));
            if (MachineStore.IsSameRecipeName(Current.Name, name))
                Counts = counts;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task ClearProductionCountsAsync()
    {
        var name = Current.Name;
        await _saveGate.WaitAsync();
        try
        {
            await Task.Run(() => _database.ClearProductionCounts(name));
            if (MachineStore.IsSameRecipeName(Current.Name, name))
                Counts = new(0, 0);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task LoadAsync(string name, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await Task.Run(() => _database.LoadRecipe(name), cancellationToken);
            var counts = await Task.Run(() => _database.LoadProductionCounts(loaded.Name), cancellationToken);
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
                Current.CopyFrom(loaded);
                _selection.LastRecipeName = Current.Name;
            }
            Counts = counts;
        }
        finally
        {
            _saveGate.Release();
        }
        Changed?.Invoke();
    }

    public async Task SaveAsync(
        string name,
        List<CarrierImageTile>? tiles = null,
        IEnumerable<RecipeImage>? images = null,
        CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var counts = await Task.Run(() => _database.LoadProductionCounts(name), cancellationToken);
            if (tiles is not null)
            {
                foreach (var tile in tiles)
                {
                    // Capture changes the image/position, not the current inspection ROI.
                    var current = Current.CarrierImages.SingleOrDefault(item => item.Number == tile.Number
                        && item.HeatSink == tile.HeatSink && item.IsBarcode == tile.IsBarcode && item.BoltId == tile.BoltId);
                    if (current is not null)
                        tile.Region = current.Region;
                }
            }
            await Task.Run(
                () => _database.SaveRecipe(
                    Current,
                    Current.Name,
                    images: images,
                    selection: new RecipeSelectionSettings { LastRecipeName = name },
                    cancellationToken: cancellationToken,
                    name: name,
                    tiles: tiles),
                cancellationToken);
            lock (InspectionSync)
            {
                if (tiles is not null)
                {
                    foreach (var tile in tiles)
                    {
                        // Inspection edits can continue while the image is being saved.
                        var current = Current.CarrierImages.SingleOrDefault(item => item.Number == tile.Number
                            && item.HeatSink == tile.HeatSink && item.IsBarcode == tile.IsBarcode && item.BoltId == tile.BoltId);
                        if (current is not null)
                            tile.Region = current.Region;
                    }
                    Current.CarrierImages = tiles;
                }
                Current.Name = name;
                _selection.LastRecipeName = name;
            }
            Counts = counts;
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
