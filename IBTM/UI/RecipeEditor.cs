using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public partial class RecipeEditor : ObservableObject
{
    private readonly ILogger<RecipeEditor>? _log;
    private readonly RecipeManager _recipes;
    private readonly MachineStore _database;
    private readonly OperationCancellation _operations;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaveAllowed))]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> Recipes { get; set; }

    public RecipeEditor(
        RecipeManager recipes,
        MachineStore database,
        OperationCancellation operations,
        ILogger<RecipeEditor>? log = null)
    {
        LoadCommand = new AsyncRelayCommand<string>(LoadAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        NewCommand = new RelayCommand(New);

        _log = log;
        _recipes = recipes;
        _database = database;
        _operations = operations;
        Name = recipes.Current.Name;
        Recipes = [];
        recipes.Changed += OnRecipeChanged;
    }

    public string ActiveName => _recipes.Current.Name;

    public bool IsSaveAllowed => !string.IsNullOrWhiteSpace(Name);

    public IAsyncRelayCommand RefreshCommand { get; }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var names = await Task.Run(() => _database.RecipeNames, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
                Recipes = names;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
                ReportError(exception);
        }
    }

    public Task ShutdownAsync()
    {
        return CommandShutdown.CancelAndWaitAsync([LoadCommand, RefreshCommand]);
    }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!ValidateName())
            return false;
        Error = null;
        var name = Name.Trim();
        try
        {
            using var operation = _operations.Link(cancellationToken);
            await _recipes.SaveAsync(name, operation.Token);
            Saved();
            return true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        return false;
    }

    public IAsyncRelayCommand<string> LoadCommand { get; }

    private async Task LoadAsync(string? recipeName)
    {
        Error = null;
        try
        {
            ArgumentNullException.ThrowIfNull(recipeName);
            using var operation = _operations.TryBegin();
            if (operation is null)
            {
                Error = "Stop the current operation before changing the recipe.";
                return;
            }
            await _recipes.LoadAsync(recipeName, operation.Token);
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
        try
        {
            using var operation = _operations.TryBegin();
            if (operation is null)
            {
                Error = "Stop the current operation before changing the recipe.";
                return;
            }
            operation.Token.ThrowIfCancellationRequested();
            _recipes.New();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    private void OnRecipeChanged()
    {
        Name = _recipes.Current.Name;
        OnPropertyChanged(nameof(ActiveName));
    }

    private void Saved()
    {
        if (RefreshCommand.IsRunning)
            _ = RefreshCommand.ExecuteAsync(null);
        Name = _recipes.Current.Name;
        OnPropertyChanged(nameof(ActiveName));
        if (!Recipes.Any(name => MachineStore.IsSameRecipeName(name, Name)))
            Recipes = Recipes.Append(Name).Order(StringComparer.Ordinal).ToArray();
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
            await _recipes.SaveImagesAsync(
                name,
                images.Select(image => image.Metadata).ToList(),
                images.Where(image => image.Image is not null || image.UnreadablePng is not null).Select(image =>
                {
                    operation.Token.ThrowIfCancellationRequested();
                    if (image.Image is null)
                        return new RecipeImage(image.Metadata.Number, image.UnreadablePng!);
                    using var stream = new MemoryStream();
                    var encoder = new PngBitmapEncoder();
                    // Encode pixels only; decoder metadata can belong to another thread.
                    encoder.Frames.Add(BitmapFrame.Create(image.Image, null, null, null));
                    encoder.Save(stream);
                    return new RecipeImage(image.Metadata.Number, stream.ToArray());
                }),
                operation.Token);
            Saved();
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
        if (IsSaveAllowed)
            return true;
        Error = "Enter a recipe name before saving.";
        return false;
    }

    private void ReportError(Exception exception)
    {
        _log?.LogError(exception, "Recipe operation failed.");
        Error = $"Recipe operation failed: {exception.GetBaseException().Message}";
    }
}
