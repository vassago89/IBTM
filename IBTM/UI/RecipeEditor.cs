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
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name = recipe.Name;

    [ObservableProperty]
    private IReadOnlyList<string> _recipes = store.GetRecipeNames();
    public Recipe Recipe => recipe;
    public string ActiveName => recipe.Name;

    public event Action? Changed;

    public void Refresh()
    {
        Name = recipe.Name;
        RefreshRecipes();
    }

    public Task ShutdownAsync() =>
        CommandShutdown.WaitAsync(CommandShutdown.Capture(SaveCommand, LoadCommand));

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync()
    {
        using var operation = operations.Link();
        var name = Name.Trim();
        if (recipe.CarrierImages.Count > 0
            && !string.Equals(
                _imageRecipeName,
                name,
                StringComparison.OrdinalIgnoreCase))
        {
            recipe.CarrierImages = await Task.Run(() => store.CopyRecipeImages(
                _imageRecipeName,
                name,
                recipe.CarrierImages));
        }

        recipe.Name = name;
        _imageRecipeName = name;
        Name = recipe.Name;
        OnPropertyChanged(nameof(ActiveName));
        await Task.Run(() => store.SaveRecipeAsync(recipe));
        await SelectAsync(recipe.Name);
        RefreshRecipes();
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name);

    [RelayCommand]
    private async Task LoadAsync(string recipeName)
    {
        using var operation = operations.Link();
        recipe.ReplaceWith(await store.LoadRecipeAsync(recipeName));
        _imageRecipeName = recipe.Name;
        Name = recipe.Name;
        OnPropertyChanged(nameof(ActiveName));
        await SelectAsync(recipe.Name);
        Changed?.Invoke();
    }

    [RelayCommand]
    private void New()
    {
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
        return selection.SaveAsync();
    }

    public async Task SaveCarrierImagesAsync(
        IReadOnlyList<CarrierImageTileView> images)
    {
        var name = Name.Trim();
        recipe.CarrierImages = await Task.Run(() => store.SaveRecipeImages(
            name,
            images.Select(image => (image.Center, image.Image))));
        _imageRecipeName = name;
        await SaveAsync();
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
