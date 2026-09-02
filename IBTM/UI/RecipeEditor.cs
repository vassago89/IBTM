using System;
using System.Collections.Generic;
using System.IO;
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
            CopyCarrierImages(_imageRecipeName, name);
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
        var directory = store.GetRecipeImageDirectory(recipe.Name);
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.GetFiles(directory, "*.png"))
        {
            File.Delete(path);
        }
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
        var path = store.GetRecipeImagePath(recipe.Name, tile.Number);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
    }

    public IReadOnlyList<CarrierImageTileView> LoadCarrierImages() =>
        recipe.CarrierImages
            .Select(tile => new CarrierImageTileView(
                tile.Number,
                tile.Center,
                LoadImage(store.GetRecipeImagePath(
                    recipe.Name,
                    tile.Number))))
            .ToArray();

    private static BitmapSource LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var image = decoder.Frames[0];
        image.Freeze();
        return image;
    }

    private void CopyCarrierImages(string sourceRecipe, string targetRecipe)
    {
        Directory.CreateDirectory(
            store.GetRecipeImageDirectory(targetRecipe));
        foreach (var tile in recipe.CarrierImages)
        {
            File.Copy(
                store.GetRecipeImagePath(sourceRecipe, tile.Number),
                store.GetRecipeImagePath(targetRecipe, tile.Number),
                overwrite: true);
        }
    }
}
