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
    public Recipe Recipe => recipe;
    public string ActiveName => recipe.Name;
    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    public event Action? Changed;

    public void Refresh()
    {
        Name = recipe.Name;
        RefreshRecipes();
    }

    public Task ShutdownAsync() =>
        CommandShutdown.WaitAsync(CommandShutdown.Capture(SaveCommand, LoadCommand));

    [RelayCommand(CanExecute = nameof(CanSave))]
    public Task SaveAsync() => SaveAsync((name, token) =>
        store.SaveRecipeAsync(recipe, name, _imageRecipeName, token));

    [RelayCommand]
    private Task LoadAsync(string recipeName) => RunAsync(async token =>
    {
        var loaded = await store.LoadRecipeAsync(recipeName, token);
        token.ThrowIfCancellationRequested();
        recipe.ReplaceWith(loaded);
        _imageRecipeName = recipe.Name;
        Name = recipe.Name;
        OnPropertyChanged(nameof(ActiveName));
        Changed?.Invoke();
        await SelectAsync(recipe.Name);
    });

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

    private void RefreshRecipes() => Recipes = store.GetRecipeNames();

    private Task SelectAsync(string recipeName)
    {
        selection.LastRecipeName = recipeName;
        return Task.Run(() => store.Database.SaveSettings([selection]));
    }

    private async Task SavedAsync(string name)
    {
        recipe.Name = name;
        _imageRecipeName = name;
        Name = name;
        OnPropertyChanged(nameof(ActiveName));
        await SelectAsync(name);
        RefreshRecipes();
    }

    public Task<bool> SaveCarrierImagesAsync(
        IReadOnlyList<CarrierImageTileView> images, CancellationToken cancellationToken = default) =>
        SaveAsync(async (name, token) =>
            recipe.CarrierImages = await store.SaveRecipeImagesAsync(recipe, name, images, token), cancellationToken);

    private Task<bool> SaveAsync(
        Func<string, CancellationToken, Task> save, CancellationToken cancellationToken = default)
    {
        if (!CanSave)
        {
            Error = "Enter a recipe name before saving.";
            return Task.FromResult(false);
        }
        var name = Name.Trim();
        return RunAsync(async token =>
        {
            await save(name, token);
            await SavedAsync(name);
        }, cancellationToken);
    }

    private async Task<bool> RunAsync(
        Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        Error = null;
        try
        {
            using var operation = operations.Link(cancellationToken);
            await action(operation.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception)
        {
            Error = $"Recipe operation failed: {exception.GetBaseException().Message}";
            return false;
        }
    }

    public Task<CarrierImageTileView[]> LoadCarrierImagesAsync(CancellationToken cancellationToken = default)
    {
        var name = recipe.Name;
        var tiles = recipe.CarrierImages;
        if (tiles.Count == 0) return Task.FromResult<CarrierImageTileView[]>([]);
        return Task.Run(() => tiles.Select(tile =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new CarrierImageTileView(tile.Number, tile.Center,
                store.LoadRecipeImage(name, tile.Number));
        }).ToArray(), cancellationToken);
    }
}
