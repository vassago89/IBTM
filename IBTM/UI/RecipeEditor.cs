using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;

namespace IBTM.UI;

public partial class RecipeEditor(
    MachineStore store,
    RecipeSelectionSettings selection,
    Recipe recipe) : ObservableObject
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
        var name = Name.Trim();
        if (recipe.CarrierImages.Count > 0
            && !string.Equals(
                _imageRecipeName,
                name,
                StringComparison.OrdinalIgnoreCase))
        {
            store.CopyRecipeImages(
                _imageRecipeName,
                name,
                recipe.CarrierImages.Select(tile => tile.Number));
        }

        recipe.Name = name;
        _imageRecipeName = name;
        Name = recipe.Name;
        OnPropertyChanged(nameof(ActiveName));
        await store.SaveRecipeAsync(recipe);
        await SelectAsync(recipe.Name);
        RefreshRecipes();
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name);

    [RelayCommand]
    private async Task LoadAsync(string recipeName)
    {
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

    public void ClearCarrierImages()
    {
        recipe.Name = Name.Trim();
        _imageRecipeName = recipe.Name;
        Name = recipe.Name;
        OnPropertyChanged(nameof(ActiveName));
        recipe.CarrierImages.Clear();
        store.ClearRecipeImages(recipe.Name);
    }

    public void SaveCarrierImage(
        AxisPosition center,
        BitmapSource image)
    {
        var tile = new CarrierImageTile
        {
            Number = recipe.CarrierImages.Count + 1,
            Center = center,
        };
        recipe.CarrierImages.Add(tile);
        store.SaveRecipeImage(recipe.Name, tile.Number, image);
    }

    public IReadOnlyList<CarrierImageTileView> LoadCarrierImages() =>
        recipe.CarrierImages
            .Select(tile => new CarrierImageTileView(
                tile.Number,
                tile.Center,
                store.LoadRecipeImage(recipe.Name, tile.Number)))
            .ToArray();
}
