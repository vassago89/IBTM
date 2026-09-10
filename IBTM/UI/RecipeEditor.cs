using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;

namespace IBTM.UI;

public partial class RecipeEditor(
    RecipeStore store,
    RecipeSelectionSettings selection,
    Recipe recipe,
    OperationCancellation operations) : ObservableObject
{
    private string _imageRecipeName = recipe.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name = recipe.Name;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private IReadOnlyList<string> _recipes = store.GetRecipeNames();
    public Recipe Recipe
    {
        get
        {
            return recipe;
        }
    }

    public string ActiveName
    {
        get
        {
            return recipe.Name;
        }
    }

    public bool CanSave
    {
        get
        {
            return !string.IsNullOrWhiteSpace(Name);
        }
    }

    public event Action? Changed;

    public void Refresh()
    {
        Name = recipe.Name;
        Recipes = store.GetRecipeNames();
    }

    public Task ShutdownAsync()
    {
        return CommandShutdown.WaitAsync(CommandShutdown.Capture(SaveCommand, LoadCommand));
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!ValidateName())
            return;
        Error = null;
        var name = Name.Trim();
        try
        {
            using var operation = operations.Link(cancellationToken);
            await store.SaveRecipeAsync(
                recipe,
                name,
                _imageRecipeName,
                new RecipeSelectionSettings { LastRecipeName = name },
                operation.Token);
            Saved(name);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    [RelayCommand]
    private async Task LoadAsync(string recipeName)
    {
        Error = null;
        try
        {
            using var operation = operations.Link();
            var loaded = await store.LoadRecipeAsync(recipeName, operation.Token);
            await Task.Run(
                () => store.Database.SaveSettings(
                    [new RecipeSelectionSettings { LastRecipeName = loaded.Name }],
                    operation.Token),
                operation.Token);

            // Once selection is committed, apply it even if cancellation arrives afterward.
            recipe.ReplaceWith(loaded);
            _imageRecipeName = recipe.Name;
            selection.LastRecipeName = recipe.Name;
            Name = recipe.Name;
            OnPropertyChanged(nameof(ActiveName));
            Changed?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    [RelayCommand]
    private void New()
    {
        Error = null;
        recipe.ReplaceWith(new Recipe { Name = "New" });
        _imageRecipeName = recipe.Name;
        Name = recipe.Name;
        OnPropertyChanged(nameof(ActiveName));
        Changed?.Invoke();
    }

    private void Saved(string name)
    {
        recipe.Name = name;
        _imageRecipeName = name;
        selection.LastRecipeName = name;
        Name = name;
        OnPropertyChanged(nameof(ActiveName));
        if (!Recipes.Contains(name))
            Recipes = Recipes.Append(name).Order(StringComparer.Ordinal).ToArray();
    }

    public async Task<bool> SaveCarrierImagesAsync(
        IReadOnlyList<CarrierImageTileView> images,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateName())
            return false;
        Error = null;
        var name = Name.Trim();
        try
        {
            using var operation = operations.Link(cancellationToken);
            recipe.CarrierImages = await store.SaveRecipeImagesAsync(
                recipe,
                name,
                images,
                new RecipeSelectionSettings { LastRecipeName = name },
                operation.Token);
            Saved(name);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return false;
        }
    }

    private bool ValidateName()
    {
        if (CanSave)
            return true;
        Error = "Enter a recipe name before saving.";
        return false;
    }

    private void ReportError(Exception exception)
    {
        System.Diagnostics.Trace.TraceError("Recipe operation failed. {0}", exception);
        Error = $"Recipe operation failed: {exception.GetBaseException().Message}";
    }

    public Task<CarrierImageTileView[]> LoadCarrierImagesAsync(
        CancellationToken cancellationToken = default)
    {
        var name = recipe.Name;
        var tiles = recipe.CarrierImages;
        if (tiles.Count == 0)
            return Task.FromResult<CarrierImageTileView[]>([]);
        return Task.Run(
            () => tiles.Select(
                tile =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new CarrierImageTileView(
                        tile.Number,
                        tile.Center,
                        store.LoadRecipeImage(name, tile.Number),
                        tile.Region,
                        tile.BoltNumber,
                        tile.HeatSink);
                })
                .ToArray(),
            cancellationToken);
    }
}
