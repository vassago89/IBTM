using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
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
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsSaveAllowed);
        LoadCommand = new AsyncRelayCommand<string>(LoadAsync);
        NewCommand = new RelayCommand(New);
        SaveCommand.PropertyChanged += OnCommandChanged;
        LoadCommand.PropertyChanged += OnCommandChanged;

        _log = log;
        _recipes = recipes;
        _database = database;
        _operations = operations;
        Name = recipes.Current.Name;
        Recipes = database.RecipeNames;
        recipes.Changed += OnRecipeChanged;
    }

    public bool IsBusy => SaveCommand.IsRunning || LoadCommand.IsRunning;

    private void OnCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
            OnPropertyChanged(nameof(IsBusy));
    }

    public string ActiveName => _recipes.Current.Name;

    public bool IsSaveAllowed => !string.IsNullOrWhiteSpace(Name);

    public void Refresh()
    {
        Recipes = _database.RecipeNames;
    }

    public Task ShutdownAsync()
    {
        return CommandShutdown.WaitAsync(CommandShutdown.Capture(SaveCommand, LoadCommand));
    }

    public IAsyncRelayCommand SaveCommand { get; }

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
            using var operation = _operations.Link();
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
        _recipes.New();
    }

    private void OnRecipeChanged()
    {
        Name = _recipes.Current.Name;
        OnPropertyChanged(nameof(ActiveName));
    }

    private void Saved()
    {
        Name = _recipes.Current.Name;
        OnPropertyChanged(nameof(ActiveName));
        if (!Recipes.Contains(Name))
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
                images.Select(image =>
                {
                    operation.Token.ThrowIfCancellationRequested();
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

    public Task<CarrierImageTileView[]> LoadCarrierImagesAsync(
        CancellationToken cancellationToken = default)
    {
        var name = _recipes.Current.Name;
        var tiles = _recipes.Current.CarrierImages;
        var bolts = _recipes.Current.Pcb.BoltPoints;
        if (tiles.Count == 0)
            return Task.FromResult<CarrierImageTileView[]>([]);
        return Task.Run(
            () => tiles.Select(
                tile =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var image = InspectionPreview.DecodeImage(_database.LoadRecipeImage(name, tile.Number));
                    return new CarrierImageTileView(tile, image, tile.IsBarcode ? null : bolts.SingleOrDefault(
                        bolt => bolt.HeatSink == tile.HeatSink && bolt.Number == tile.BoltNumber));
                })
                .ToArray(),
            cancellationToken);
    }
}
