using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public partial class RecipeEditorViewModel : ObservableObject
{
    private readonly ILogger<RecipeEditorViewModel>? _log;
    private readonly RecipeManager _recipes;
    private readonly MachineStore _database;
    private readonly OperationCancellation _operations;
    private bool _shuttingDown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaveAllowed))]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> Recipes { get; set; }

    public RecipeEditorViewModel(
        RecipeManager recipes,
        MachineStore database,
        OperationCancellation operations,
        ILogger<RecipeEditorViewModel>? log = null)
    {
        LoadCommand = new AsyncRelayCommand<string>(LoadAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        NewCommand = new AsyncRelayCommand(NewAsync);

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
        var previous = RefreshCommand.ExecutionTask;
        try
        {
            if (_shuttingDown)
                return;
            var names = await Task.Run(() => _database.RecipeNames, cancellationToken);
            if (!_shuttingDown && !cancellationToken.IsCancellationRequested)
                Recipes = names;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_shuttingDown && !cancellationToken.IsCancellationRequested)
                ReportError(exception);
        }
        finally
        {
            // Saving or reopening teaching can replace an unfinished list query.
            if (previous is { IsCompleted: false })
                await previous;
        }
    }

    public Task ShutdownAsync()
    {
        _shuttingDown = true;
        _recipes.Changed -= OnRecipeChanged;
        return CommandShutdown.CancelAndWaitAsync([LoadCommand, RefreshCommand, NewCommand]);
    }

    public async Task<bool> SaveAsync(
        IReadOnlyList<RecipeImageItem>? images = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSaveAllowed)
        {
            Error = UiText.Get("Enter a recipe name before saving.");
            return false;
        }
        Error = null;
        var name = Name.Trim();
        var activeToken = cancellationToken;
        try
        {
            using var operation = _operations.Link(cancellationToken);
            activeToken = operation.Token;
            await _recipes.SaveAsync(
                name,
                images?.Select(image => image.Metadata).ToList(),
                images?.Where(image => image.Image is not null || image.UnreadablePng is not null).Select(image =>
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
            if (!_shuttingDown && RefreshCommand.IsRunning)
                _ = RefreshCommand.ExecuteAsync(null);
            OnRecipeChanged();
            if (!Recipes.Any(savedName => MachineStore.IsSameRecipeName(savedName, Name)))
                Recipes = Recipes.Append(Name).Order(StringComparer.Ordinal).ToArray();
            return true;
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested || _operations.IsShuttingDown)
        {
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        return false;
    }

    public IAsyncRelayCommand<string> LoadCommand { get; }

    private async Task LoadAsync(string? recipeName, CancellationToken cancellationToken)
    {
        Error = null;
        var activeToken = cancellationToken;
        try
        {
            ArgumentNullException.ThrowIfNull(recipeName);
            using var operation = _operations.TryBegin(cancellationToken);
            if (operation is null)
            {
                Error = UiText.Get("Stop the current operation before changing the recipe.");
                return;
            }
            activeToken = operation.Token;
            await _recipes.LoadAsync(recipeName, operation.Token);
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested || _operations.IsShuttingDown)
        {
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    public IAsyncRelayCommand NewCommand { get; }

    private async Task NewAsync()
    {
        Error = null;
        var activeToken = CancellationToken.None;
        try
        {
            using var operation = _operations.TryBegin();
            if (operation is null)
            {
                Error = UiText.Get("Stop the current operation before changing the recipe.");
                return;
            }
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            await _recipes.NewAsync(operation.Token);
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested || _operations.IsShuttingDown)
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

    private void ReportError(Exception exception)
    {
        _log?.LogError(exception, "Recipe operation failed.");
        Error = UiText.Format($"Recipe operation failed: {exception.GetBaseException().Message}");
    }
}
