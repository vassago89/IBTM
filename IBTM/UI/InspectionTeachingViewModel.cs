using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Inspection;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

// Offline image/recipe editing. This page has no camera, motion or I/O ownership.
public partial class InspectionTeachingViewModel : ObservableObject
{
    private readonly MachineStore _store;
    private readonly RecipeManager _recipes;
    private readonly ILogger<InspectionTeachingViewModel> _log;
    private readonly IAsyncRelayCommand[] _commands;
    private readonly InspectionImages _images;
    private IReadOnlyList<CarrierImageTileView> _carrierImages;

    public InspectionTeachingViewModel(MachineStore store, RecipeManager recipes, InspectionImages images,
        PcbHistorySettings history, ILogger<InspectionTeachingViewModel> log)
    {
        _store = store;
        _recipes = recipes;
        _log = log;
        _images = images;
        _carrierImages = [];
        Draft = new();
        Preview = new(Draft);
        Points = [];
        HistoryImages = [];
        Records = [];
        RecipeNames = [];
        SelectedRecipeName = recipes.Current.Name;
        LoadRecipeCommand = new AsyncRelayCommand(LoadRecipeAsync);
        RefreshImagesCommand = new AsyncRelayCommand(RefreshImagesAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        InspectCommand = new AsyncRelayCommand(InspectAsync, () => IsInspectAllowed);
        RefreshHistoryCommand = new AsyncRelayCommand(RefreshHistoryAsync);
        LoadOlderCommand = new AsyncRelayCommand(LoadOlderAsync);
        LoadRecordCommand = new AsyncRelayCommand(LoadRecordAsync);
        DrawRegionCommand = new RelayCommand<Rect>(DrawRegion, _ => IsDrawRegionAllowed);
        UseHistoryImageCommand = new RelayCommand(UseHistoryImage, () => IsUseHistoryImageAllowed);
        ShowRecipeImageCommand = new RelayCommand(ShowRecipeImage);
        _commands = [LoadRecipeCommand, RefreshImagesCommand, SaveCommand, InspectCommand, RefreshHistoryCommand, LoadOlderCommand, LoadRecordCommand];
        foreach (var command in _commands)
            command.PropertyChanged += OnCommandChanged;
        HistoryDirectory = history.Directory;
    }

    public Recipe Draft { get; }
    public InspectionPreview Preview { get; }
    public ObservableCollection<PcbRecord> Records { get; }
    public IAsyncRelayCommand LoadRecipeCommand { get; }
    public IAsyncRelayCommand RefreshImagesCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand InspectCommand { get; }
    public IAsyncRelayCommand RefreshHistoryCommand { get; }
    public IAsyncRelayCommand LoadOlderCommand { get; }
    public IAsyncRelayCommand LoadRecordCommand { get; }
    public IRelayCommand<Rect> DrawRegionCommand { get; }
    public IRelayCommand UseHistoryImageCommand { get; }
    public IRelayCommand ShowRecipeImageCommand { get; }

    [ObservableProperty] public partial IReadOnlyList<string> RecipeNames { get; private set; }
    [ObservableProperty] public partial string? SelectedRecipeName { get; set; }
    [ObservableProperty] public partial IReadOnlyList<InspectionPoint> Points { get; private set; }
    [ObservableProperty] public partial InspectionPoint? SelectedPoint { get; set; }
    [ObservableProperty] public partial string? Error { get; private set; }
    [ObservableProperty] public partial string? Message { get; private set; }
    [ObservableProperty] public partial string? ImageSource { get; private set; }
    [ObservableProperty] public partial string? OriginalResult { get; private set; }
    [ObservableProperty] public partial bool IsLoaded { get; private set; }
    [ObservableProperty] public partial string HistoryDirectory { get; set; }
    [ObservableProperty] public partial bool HasOlder { get; private set; } = true;
    [ObservableProperty] public partial PcbRecord? SelectedRecord { get; set; }
    [ObservableProperty] public partial PcbRecord? LoadedRecord { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<PcbInspectionImageView> HistoryImages { get; private set; }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseHistoryImageCommand))]
    public partial PcbInspectionImageView? SelectedHistoryImage { get; set; }

    public bool IsBusy => _commands.Any(command => command.IsRunning);
    public bool IsIdle => !IsBusy;
    public bool IsDataMatrixSelected => SelectedPoint?.IsDataMatrix == true;
    public DataMatrixInspectionRecipe? DataMatrix => IsDataMatrixSelected
        ? Draft.BoltInspection.GetDataMatrix(SelectedPoint!.HeatSink) : null;

    public void Activate()
    {
        try
        {
            var selectedName = SelectedRecipeName;
            RecipeNames = _store.RecipeNames;
            // ComboBox item matching is case-sensitive even though recipe identity is not.
            SelectedRecipeName = RecipeNames.FirstOrDefault(name => MachineStore.IsSameRecipeName(name, selectedName))
                ?? selectedName;
            if (!IsBusy)
            {
                if (IsLoaded)
                    _ = RefreshImagesCommand.ExecuteAsync(null);
                else if (RecipeNames.Any(name => MachineStore.IsSameRecipeName(name, SelectedRecipeName)))
                    _ = LoadRecipeCommand.ExecuteAsync(null);
            }
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            _log.LogError(exception, "Inspection teaching recipe list failed.");
        }
    }

    public Task ShutdownAsync()
    {
        return CommandShutdown.CancelAndWaitAsync(_commands);
    }

    private void OnCommandChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsIdle));
        }
    }

    private async Task LoadRecipeAsync(CancellationToken token)
    {
        await LoadRecipeImagesAsync(SelectedRecipeName, preserveEdits: false, token);
    }

    private async Task RefreshImagesAsync(CancellationToken token)
    {
        if (IsLoaded)
            await LoadRecipeImagesAsync(Draft.Name, preserveEdits: true, token);
    }

    private async Task LoadRecipeImagesAsync(string? name, bool preserveEdits, CancellationToken token)
    {
        Error = null;
        Message = null;
        if (string.IsNullOrWhiteSpace(name))
            return;
        try
        {
            Recipe? activeRecipe = null;
            lock (_recipes.InspectionSync)
            {
                if (MachineStore.IsSameRecipeName(_recipes.Current.Name, name))
                {
                    // Teaching owns the point list, including edits not yet saved to the database.
                    activeRecipe = JsonSerializer.Deserialize<Recipe>(JsonSerializer.Serialize(_recipes.Current))!;
                }
            }
            var loaded = activeRecipe ?? await Task.Run(() => _store.LoadRecipe(name), token);
            var images = await _images.LoadRecipeAsync(loaded, token);
            token.ThrowIfCancellationRequested();
            var selected = SelectedPoint;
            if (preserveEdits)
                loaded.ApplyInspectionSettings(Draft);
            SelectedPoint = null;
            Draft.ReplaceWith(loaded);
            _carrierImages = images;
            Points = Enum.GetValues<HeatSinkSlot>().SelectMany(pcb => InspectionPoint.ForPcb(Draft, pcb)).ToArray();
            IsLoaded = true;
            OnPropertyChanged(nameof(Draft));
            SelectedPoint = (preserveEdits && selected is not null
                ? Points.FirstOrDefault(point => point.HeatSink == selected.HeatSink
                    && point.IsDataMatrix == selected.IsDataMatrix && point.Bolt?.Id == selected.Bolt?.Id)
                : null) ?? Points.FirstOrDefault(point => point.Metadata is not null) ?? Points.FirstOrDefault();
            var unlinked = Draft.CarrierImages.Count(tile => !Points.Any(point => point.Matches(tile)));
            Message = unlinked > 0 ? $"{unlinked} unlinked image(s) excluded." : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Error = exception.Message;
            _log.LogError(exception, "Inspection teaching load failed for {Recipe}.", name);
        }
    }

    partial void OnSelectedPointChanged(InspectionPoint? value)
    {
        ShowRecipeImage();
        OnPropertyChanged(nameof(IsDataMatrixSelected));
        OnPropertyChanged(nameof(DataMatrix));
        UseHistoryImageCommand.NotifyCanExecuteChanged();
    }

    private void ShowRecipeImage()
    {
        InspectCommand.Cancel();
        Error = null;
        ImageSource = null;
        OriginalResult = null;
        Preview.Clear(IsDataMatrixSelected ? SelectedPoint!.HeatSink : null, SelectedPoint?.Bolt);
        if (SelectedPoint is { } point)
        {
            if (point.GetImage(_carrierImages) is { } image)
            {
                var region = point.Metadata!.Region ?? PixelRegion.CenteredSquare(image.PixelWidth, image.PixelHeight,
                    Math.Min(image.PixelWidth, image.PixelHeight) / 4);
                try
                {
                    Preview.SetSavedImage(image, region);
                }
                catch (Exception exception)
                {
                    Error = exception.Message;
                    _log.LogError(exception, "Inspection teaching preview failed for image {Number}.", point.Metadata.Number);
                }
            }
            ImageSource = $"Recipe · {Draft.Name} · {point.Title}";
        }
        InspectCommand.NotifyCanExecuteChanged();
        DrawRegionCommand.NotifyCanExecuteChanged();
    }

    private bool IsInspectAllowed => Preview.HasImage && SelectedPoint?.Metadata is not null;

    private bool IsDrawRegionAllowed => !IsBusy && IsInspectAllowed;

    private void DrawRegion(Rect bounds)
    {
        if (Preview.Image is not { } image || SelectedPoint?.Metadata is not { } metadata || bounds.IsEmpty)
            return;
        var region = PixelRegion.CenteredSquare(image.PixelWidth, image.PixelHeight, (int)Math.Ceiling(Math.Max(bounds.Width, bounds.Height)));
        metadata.Region = region;
        Preview.SetSavedImage(image, region);
        Message = "ROI changed · not saved";
    }

    private async Task InspectAsync(CancellationToken token)
    {
        Error = null;
        try
        {
            if (IsInspectAllowed)
                await Preview.InspectAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Error = exception.Message;
            _log.LogError(exception, "Offline inspection failed.");
        }
    }

    private async Task SaveAsync(CancellationToken token)
    {
        if (!IsLoaded)
            return;
        Error = null;
        try
        {
            await _recipes.SaveInspectionAsync(Draft, token);
            Message = MachineStore.IsSameRecipeName(_recipes.Current.Name, Draft.Name)
                ? "Saved · applies from the next inspection point."
                : $"Saved to recipe '{Draft.Name}'.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Error = exception.Message;
            _log.LogError(exception, "Inspection teaching save failed for {Recipe}.", Draft.Name);
        }
    }

    private async Task RefreshHistoryAsync(CancellationToken token)
    {
        LoadOlderCommand.Cancel();
        LoadRecordCommand.Cancel();
        Records.Clear();
        SelectedRecord = null;
        LoadedRecord = null;
        HistoryImages = [];
        SelectedHistoryImage = null;
        await LoadOlderAsync(token);
    }

    partial void OnHistoryDirectoryChanged(string value)
    {
        RefreshHistoryCommand.Cancel();
        LoadOlderCommand.Cancel();
        LoadRecordCommand.Cancel();
        Records.Clear();
        SelectedRecord = null;
        LoadedRecord = null;
        HistoryImages = [];
        SelectedHistoryImage = null;
        HasOlder = true;
        Error = null;
        Message = null;
    }

    private async Task LoadOlderAsync(CancellationToken token)
    {
        Error = null;
        var before = Records.Count > 0 ? Records[^1].Number : (long?)null;
        var directory = HistoryDirectory;
        try
        {
            var records = await Task.Run(() => _store.LoadPcbs(directory, before), token);
            token.ThrowIfCancellationRequested();
            if (directory != HistoryDirectory)
                return;
            foreach (var record in records)
                Records.Add(record);
            HasOlder = records.Count == 100;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (directory == HistoryDirectory)
                Error = exception.Message;
            _log.LogError(exception, "Inspection history load failed for {Directory}.", directory);
        }
    }

    private async Task LoadRecordAsync(CancellationToken token)
    {
        if (SelectedRecord is not { } record)
            return;
        var directory = HistoryDirectory;
        Error = null;
        Message = null;
        LoadedRecord = null;
        HistoryImages = [];
        SelectedHistoryImage = null;
        try
        {
            var images = await _images.LoadRecordAsync(record, token);
            token.ThrowIfCancellationRequested();
            if (directory != HistoryDirectory || SelectedRecord != record)
                return;
            LoadedRecord = record;
            HistoryImages = images;
            SelectedHistoryImage = images.FirstOrDefault();
            Message = images.Length == 0 ? "No inspection images saved for this PCB." : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (directory == HistoryDirectory && SelectedRecord == record)
                Error = exception.Message;
            _log.LogError(exception, "Inspection image history failed for PCB {Number}.", record.Number);
        }
    }

    private bool IsUseHistoryImageAllowed => LoadedRecord is not null
        && MachineStore.IsSameRecipeName(LoadedRecord.RecipeName, Draft.Name)
        && SelectedHistoryImage is not null
        && Points.Any(point => point.Metadata is not null && point.HeatSink == LoadedRecord.HeatSink
            && point.Bolt?.Id == SelectedHistoryImage.Record.BoltId);

    private void UseHistoryImage()
    {
        if (!IsUseHistoryImageAllowed)
            return;
        var saved = SelectedHistoryImage!;
        var target = Points.FirstOrDefault(point => point.Metadata is not null && point.HeatSink == LoadedRecord!.HeatSink
            && point.Bolt?.Id == saved.Record.BoltId);
        if (target is null)
            return;
        var reference = target.GetImage(_carrierImages);
        if (reference is null || reference.PixelWidth != saved.Image.PixelWidth || reference.PixelHeight != saved.Image.PixelHeight)
        {
            Error = "Image dimensions do not match the recipe image.";
            return;
        }
        Error = null;
        SelectedPoint = target;
        InspectCommand.Cancel();
        Preview.Clear(IsDataMatrixSelected ? target.HeatSink : null, target.Bolt);
        try
        {
            Preview.SetSavedImage(saved.Image, target.Metadata!.Region ?? saved.Record.Region);
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            _log.LogError(exception, "Inspection history preview failed for PCB {Number}.", LoadedRecord!.Number);
            return;
        }
        ImageSource = $"PCB {LoadedRecord!.Number} · {saved.Title} · {saved.Record.CapturedAt:yyyy-MM-dd HH:mm:ss}";
        OriginalResult = $"Recorded {saved.Verdict} · {saved.Details}";
    }
}
