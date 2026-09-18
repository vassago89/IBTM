using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;

namespace IBTM.UI;

public partial class RecipeEditor : ObservableObject
{
    private readonly RecipeStore _store;
    private readonly RecipeSelectionSettings _selection;
    private readonly Recipe _recipe;
    private readonly OperationCancellation _operations;
    private string _imageRecipeName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private IReadOnlyList<string> _recipes;

    public RecipeEditor(
        RecipeStore store,
        RecipeSelectionSettings selection,
        Recipe recipe,
        OperationCancellation operations)
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);
        LoadCommand = new AsyncRelayCommand<string>(LoadAsync);
        NewCommand = new RelayCommand(New);

        _store = store;
        _selection = selection;
        _recipe = recipe;
        _operations = operations;
        _imageRecipeName = _recipe.Name;
        _name = _recipe.Name;
        _recipes = _store.GetRecipeNames();
    }

    public event Action? Changed;

    public Recipe Recipe
    {
        get
        {
            return _recipe;
        }
    }

    public string ActiveName
    {
        get
        {
            return _recipe.Name;
        }
    }

    public bool CanSave
    {
        get
        {
            return !string.IsNullOrWhiteSpace(Name);
        }
    }

    public void Refresh()
    {
        Name = _recipe.Name;
        Recipes = _store.GetRecipeNames();
    }

    public Task ShutdownAsync()
    {
        return CommandShutdown.WaitAsync(CommandShutdown.Capture(SaveCommand, LoadCommand));
    }

    public IAsyncRelayCommand SaveCommand { get; }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!ValidateName())
            return;
        Error = null;
        var name = Name.Trim();
        try
        {
            using var operation = _operations.Link(cancellationToken);
            await _store.SaveRecipeAsync(
                _recipe,
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

    public IAsyncRelayCommand<string> LoadCommand { get; }

    private async Task LoadAsync(string? recipeName)
    {
        Error = null;
        try
        {
            ArgumentNullException.ThrowIfNull(recipeName);
            using var operation = _operations.Link();
            var loaded = await _store.LoadRecipeAsync(recipeName, operation.Token);
            await _store.Database.SaveSettingsAsync(
                [new RecipeSelectionSettings { LastRecipeName = loaded.Name }],
                operation.Token);

            // Once selection is committed, apply it even if cancellation arrives afterward.
            _recipe.ReplaceWith(loaded);
            _imageRecipeName = _recipe.Name;
            _selection.LastRecipeName = _recipe.Name;
            Name = _recipe.Name;
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

    public IRelayCommand NewCommand { get; }

    private void New()
    {
        Error = null;
        _recipe.ReplaceWith(new Recipe { Name = "New" });
        _imageRecipeName = _recipe.Name;
        Name = _recipe.Name;
        OnPropertyChanged(nameof(ActiveName));
        Changed?.Invoke();
    }

    private void Saved(string name)
    {
        _recipe.Name = name;
        _imageRecipeName = name;
        _selection.LastRecipeName = name;
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
            using var operation = _operations.Link(cancellationToken);
            _recipe.CarrierImages = await _store.SaveRecipeImagesAsync(
                _recipe,
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
        var name = _recipe.Name;
        var tiles = _recipe.CarrierImages;
        if (tiles.Count == 0)
            return Task.FromResult<CarrierImageTileView[]>([]);
        return Task.Run(
            () => tiles.Select(
                tile =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new CarrierImageTileView(
                        tile,
                        _store.LoadRecipeImage(name, tile.Number));
                })
                .ToArray(),
            cancellationToken);
    }
}
