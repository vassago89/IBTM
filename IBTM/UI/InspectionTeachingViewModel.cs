using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

public enum InspectionTeachingTab
{
    Setup,
    History,
}

// Offline image/recipe editing. This page has no camera, motion or I/O ownership.
public partial class InspectionTeachingViewModel : ObservableObject
{
    private readonly MachineStore _store;
    private readonly RecipeManager _recipes;
    private readonly ILogger<InspectionTeachingViewModel> _log;
    private readonly IAsyncRelayCommand[] _commands;
    private readonly InspectionImageLoader _images;
    private IReadOnlyList<RecipeImageItem> _carrierImages;
    private bool _shuttingDown;

    public InspectionTeachingViewModel(MachineStore store, RecipeManager recipes, InspectionImageLoader images,
        PcbHistorySettings history, ILogger<InspectionTeachingViewModel> log)
    {
        _store = store;
        _recipes = recipes;
        _log = log;
        _images = images;
        _carrierImages = [];
        Preview = new(recipes.Current);
        Points = [];
        HistoryImages = [];
        Records = [];
        RefreshImagesCommand = new AsyncRelayCommand(RefreshImagesAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        InspectCommand = new AsyncRelayCommand(InspectAsync);
        RefreshHistoryCommand = new AsyncRelayCommand(RefreshHistoryAsync);
        LoadOlderCommand = new AsyncRelayCommand(LoadOlderAsync);
        LoadRecordCommand = new AsyncRelayCommand(LoadRecordAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        DrawRegionCommand = new RelayCommand<Rect>(DrawRegion);
        ClearHistoryFilterCommand = new RelayCommand(ClearHistoryFilter);
        ShowRecipeImageCommand = new RelayCommand(ShowRecipeImage);
        _commands = [RefreshImagesCommand, SaveCommand, InspectCommand, RefreshHistoryCommand, LoadOlderCommand, LoadRecordCommand];
        foreach (var command in _commands)
            command.PropertyChanged += OnCommandChanged;
        HistoryDirectory = history.Directory;
        recipes.Changed += OnRecipeChanged;
    }

    public InspectionPreviewViewModel Preview { get; }
    public ObservableCollection<PcbRecord> Records { get; }
    public IReadOnlyList<PcbRecord> FilteredRecords => Records.Where(MatchesHistoryRecord).ToArray();
    public IAsyncRelayCommand RefreshImagesCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand InspectCommand { get; }
    public IAsyncRelayCommand RefreshHistoryCommand { get; }
    public IAsyncRelayCommand LoadOlderCommand { get; }
    public IAsyncRelayCommand LoadRecordCommand { get; }
    public IRelayCommand<Rect> DrawRegionCommand { get; }
    public IRelayCommand ClearHistoryFilterCommand { get; }
    public IRelayCommand ShowRecipeImageCommand { get; }

    [ObservableProperty] public partial InspectionTeachingTab SelectedTab { get; set; }
    [ObservableProperty] public partial DateTime? HistoryDate { get; set; }
    [ObservableProperty] public partial string? HistorySearch { get; set; }
    [ObservableProperty] public partial AssemblyResult? HistoryResult { get; set; }
    [ObservableProperty] public partial IReadOnlyList<InspectionPoint> Points { get; private set; }
    [ObservableProperty] public partial InspectionPoint? SelectedPoint { get; set; }
    [ObservableProperty] public partial string? Error { get; private set; }
    [ObservableProperty] public partial string? HistoryImageError { get; private set; }
    [ObservableProperty] public partial string? Message { get; private set; }
    [ObservableProperty] public partial string? ImageSource { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OriginalResult))]
    public partial PcbInspectionImageItem? OriginalImage { get; private set; }
    [ObservableProperty] public partial bool IsLoaded { get; private set; }
    [ObservableProperty] public partial string HistoryDirectory { get; set; }
    [ObservableProperty] public partial bool HasOlder { get; private set; } = true;
    [ObservableProperty] public partial PcbRecord? SelectedRecord { get; set; }
    [ObservableProperty] public partial PcbRecord? LoadedRecord { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<PcbInspectionImageItem> HistoryImages { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryImageTarget))]
    public partial PcbInspectionImageItem? SelectedHistoryImage { get; set; }

    public bool IsBusy => _commands.Any(command => command.IsRunning);
    public bool IsIdle => !_shuttingDown && !IsBusy;
    public bool IsDataMatrixSelected => SelectedPoint?.IsDataMatrix == true;

    public string? OriginalResult => OriginalImage is { } saved ? UiText.Format($"Recorded {saved.Verdict} · {saved.Details}") : null;

    public DataMatrixInspectionRecipe? DataMatrix
    {
        get
        {
            return IsDataMatrixSelected
                ? Preview.Recipe.BoltInspection.GetDataMatrix(SelectedPoint!.HeatSink) : null;
        }
    }

    private void OnRecipeChanged()
    {
        if (_shuttingDown || !IsLoaded && !RefreshImagesCommand.IsRunning)
            return;
        // Drain the old image load before shutdown; cancelled pixels cannot be published.
        RefreshImagesCommand.Cancel();
        InspectCommand.Cancel();
        _ = RefreshImagesCommand.ExecuteAsync(null);
    }

    public void Activate()
    {
        if (IsIdle)
            _ = RefreshImagesCommand.ExecuteAsync(null);
    }

    public Task ShutdownAsync()
    {
        _shuttingDown = true;
        _recipes.Changed -= OnRecipeChanged;
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsInspectAllowed));
        OnPropertyChanged(nameof(IsDrawRegionAllowed));
        return CommandShutdown.CancelAndWaitAsync(_commands);
    }

    private void OnCommandChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsDrawRegionAllowed));
        }
    }

    private async Task RefreshImagesAsync(CancellationToken token)
    {
        var previous = RefreshImagesCommand.ExecutionTask;
        var recipe = _recipes.Current;
        Error = null;
        Message = null;
        try
        {
            if (_shuttingDown)
                return;
            var images = await _images.LoadRecipeAsync(recipe, token);
            if (_shuttingDown || token.IsCancellationRequested)
                return;
            var selected = SelectedPoint;
            SelectedPoint = null;
            _carrierImages = images;
            Points = Enum.GetValues<HeatSinkSlot>().SelectMany(pcb => InspectionPoint.ForPcb(Preview.Recipe, pcb)).ToArray();
            IsLoaded = true;
            SelectedPoint = (selected is not null
                ? Points.FirstOrDefault(point => point.HeatSink == selected.HeatSink
                    && point.IsDataMatrix == selected.IsDataMatrix && point.Bolt?.Id == selected.Bolt?.Id)
                : null) ?? Points.FirstOrDefault(point => point.Metadata is not null) ?? Points.FirstOrDefault();
            UpdateHistoryImages(HistoryImages, SelectedHistoryImage?.Record);
            var unlinked = Preview.Recipe.CarrierImages.Count(tile =>
                !Points.Any(point => tile.IsForTarget(point.HeatSink, point.Bolt?.Id)));
            var failed = images.Count(image => image.Error is not null);
            if (failed > 0)
                Message = UiText.Format($"{failed} reference image(s) unavailable.");
            else if (unlinked > 0)
                Message = UiText.Format($"{unlinked} unlinked image(s) excluded.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
                Error = exception.Message;
            _log.LogError(exception, "Inspection teaching load failed for {Recipe}.", recipe.Name);
        }
        finally
        {
            if (previous is { IsCompleted: false })
                await previous;
        }
    }

    partial void OnSelectedPointChanged(InspectionPoint? value)
    {
        if (SelectedTab == InspectionTeachingTab.Setup)
            ShowRecipeImage();
        OnPropertyChanged(nameof(IsDataMatrixSelected));
        OnPropertyChanged(nameof(DataMatrix));
        OnPropertyChanged(nameof(HistoryImageTarget));
    }

    private void ShowRecipeImage()
    {
        InspectCommand.Cancel();
        Error = null;
        Message = null;
        ImageSource = null;
        OriginalImage = null;
        Preview.Clear(IsDataMatrixSelected ? SelectedPoint!.HeatSink : null, SelectedPoint?.Bolt);
        if (SelectedPoint is { } point)
        {
            var loaded = point.FindImage(_carrierImages);
            Error = loaded?.Error;
            if (loaded is null)
                Message = UiText.Get("No reference image. Use Move to selected point, then Save X/Y + image.");
            if (loaded?.Image is { } image)
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
            ImageSource = UiText.Format($"Recipe · {Preview.Recipe.Name} · {point.Title}");
        }
        OnPropertyChanged(nameof(IsInspectAllowed));
        OnPropertyChanged(nameof(IsDrawRegionAllowed));
    }

    public bool IsInspectAllowed => !_shuttingDown && Preview.HasImage && SelectedPoint?.Metadata is not null;

    public bool IsDrawRegionAllowed => !IsBusy && IsInspectAllowed;

    public void OnInspectionSettingChanged(bool refreshBinaryImage)
    {
        if (Preview.HasImage)
            Error = null;
        Message = UiText.Get("Settings changed · not saved");
        if (!refreshBinaryImage)
            return;
        try
        {
            Preview.RefreshBinaryImage();
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            _log.LogError(exception, "Data Matrix preview refresh failed.");
        }
    }

    private void DrawRegion(Rect bounds)
    {
        if (!IsDrawRegionAllowed)
            return;
        if (Preview.Image is not { } image || SelectedPoint?.Metadata is not { } metadata || bounds.IsEmpty)
            return;
        var region = PixelRegion.CenteredSquare(image.PixelWidth, image.PixelHeight, (int)Math.Ceiling(Math.Max(bounds.Width, bounds.Height)));
        metadata.Region = region;
        _recipes.NotifyInspectionChanged();
        Preview.SetSavedImage(image, region);
        Message = UiText.Get("ROI changed · not saved");
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
            if (!token.IsCancellationRequested)
                Error = exception.Message;
            _log.LogError(exception, "Offline inspection failed.");
        }
    }

    private async Task SaveAsync(CancellationToken token)
    {
        if (!IsLoaded)
            return;
        Error = null;
        Message = null;
        try
        {
            await _recipes.SaveInspectionAsync(Preview.Recipe, token);
            _log.LogInformation(
                "Inspection settings saved: recipe={Recipe}, active={Active}, DataMatrix1={Minimum1}..{Maximum1}/{Step1}, dilation radius={Radius1}, DataMatrix2={Minimum2}..{Maximum2}/{Step2}, dilation radius={Radius2}.",
                Preview.Recipe.Name, MachineStore.IsSameRecipeName(_recipes.Current.Name, Preview.Recipe.Name),
                Preview.Recipe.BoltInspection.DataMatrix1.ThresholdMinimum,
                Preview.Recipe.BoltInspection.DataMatrix1.ThresholdMaximum,
                Preview.Recipe.BoltInspection.DataMatrix1.ThresholdStep,
                Preview.Recipe.BoltInspection.DataMatrix1.DilationRadius,
                Preview.Recipe.BoltInspection.DataMatrix2.ThresholdMinimum,
                Preview.Recipe.BoltInspection.DataMatrix2.ThresholdMaximum,
                Preview.Recipe.BoltInspection.DataMatrix2.ThresholdStep,
                Preview.Recipe.BoltInspection.DataMatrix2.DilationRadius);
            Message = UiText.Format($"Saved to recipe '{Preview.Recipe.Name}'.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Error = exception.Message;
            _log.LogError(exception, "Inspection teaching save failed for {Recipe}.", Preview.Recipe.Name);
        }
    }

    partial void OnSelectedTabChanged(InspectionTeachingTab value)
    {
        if (_shuttingDown)
            return;
        if (value == InspectionTeachingTab.Setup)
        {
            LoadRecordCommand.Cancel();
            SelectedPoint ??= Points.FirstOrDefault(point => point.Metadata is not null) ?? Points.FirstOrDefault();
            ShowRecipeImage();
        }
        else
        {
            UseHistoryImage();
            if (SelectedRecord is not null && LoadedRecord != SelectedRecord)
                _ = LoadRecordCommand.ExecuteAsync(null);
            if (Records.Count == 0 && !RefreshHistoryCommand.IsRunning && !LoadOlderCommand.IsRunning)
                _ = RefreshHistoryCommand.ExecuteAsync(null);
        }
    }

    private bool MatchesHistoryRecord(PcbRecord record)
    {
        var search = HistorySearch?.Trim();
        return (HistoryDate is null || record.CreatedAt.LocalDateTime.Date == HistoryDate.Value.Date)
            && (HistoryResult is null || record.InspectionResult == HistoryResult)
            && (string.IsNullOrEmpty(search)
                || record.PcbBarcode?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
                || record.Number.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
                || record.RecipeName.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnHistoryDateChanged(DateTime? value)
    {
        RefreshHistoryFilter();
    }

    partial void OnHistorySearchChanged(string? value)
    {
        RefreshHistoryFilter();
    }

    partial void OnHistoryResultChanged(AssemblyResult? value)
    {
        RefreshHistoryFilter();
    }

    private void ClearHistoryFilter()
    {
        HistoryDate = null;
        HistorySearch = null;
        HistoryResult = null;
    }

    private void RefreshHistoryFilter()
    {
        OnPropertyChanged(nameof(FilteredRecords));
        if (SelectedRecord is not null && !MatchesHistoryRecord(SelectedRecord))
            SelectedRecord = null;
    }

    partial void OnSelectedRecordChanged(PcbRecord? value)
    {
        LoadRecordCommand.Cancel();
        LoadedRecord = null;
        HistoryImages = [];
        SelectedHistoryImage = null;
        Error = null;
        Message = null;
        if (value is not null && !_shuttingDown)
            _ = LoadRecordCommand.ExecuteAsync(null);
    }

    partial void OnSelectedHistoryImageChanged(PcbInspectionImageItem? value)
    {
        if (SelectedTab == InspectionTeachingTab.History)
            UseHistoryImage();
    }

    private async Task RefreshHistoryAsync(CancellationToken token)
    {
        var selectedRecord = SelectedRecord;
        var selectedImage = SelectedHistoryImage;
        ClearHistory();
        await LoadOlderAsync(token);
        if (token.IsCancellationRequested || SelectedTab != InspectionTeachingTab.History || selectedRecord is null)
            return;
        SelectedRecord = Records.FirstOrDefault(record => record.Number == selectedRecord.Number
            && record.DatabaseFile == selectedRecord.DatabaseFile && MatchesHistoryRecord(record));
        if (SelectedRecord is not { } restored)
            return;
        await LoadRecordCommand.ExecutionTask!;
        if (!token.IsCancellationRequested && SelectedTab == InspectionTeachingTab.History
            && SelectedRecord == restored && selectedImage is not null)
        {
            SelectedHistoryImage = HistoryImages.FirstOrDefault(image => image.Record.BoltId == selectedImage.Record.BoltId)
                ?? SelectedHistoryImage;
        }
    }

    partial void OnHistoryDirectoryChanged(string value)
    {
        RefreshHistoryCommand.Cancel();
        ClearHistory();
        HasOlder = true;
        Error = null;
        Message = null;
    }

    private void ClearHistory()
    {
        LoadOlderCommand.Cancel();
        LoadRecordCommand.Cancel();
        Records.Clear();
        OnPropertyChanged(nameof(FilteredRecords));
        SelectedRecord = null;
        LoadedRecord = null;
        HistoryImages = [];
        SelectedHistoryImage = null;
    }

    private async Task LoadOlderAsync(CancellationToken token)
    {
        Error = null;
        var before = Records.Count > 0 ? Records[^1].Number : (long?)null;
        var directory = HistoryDirectory;
        try
        {
            var records = await Task.Run(() => _store.LoadPcbs(directory, before), token);
            if (token.IsCancellationRequested)
                return;
            foreach (var record in records)
                Records.Add(record);
            OnPropertyChanged(nameof(FilteredRecords));
            HasOlder = records.Count == MachineStore.PcbHistoryPageSize;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
                Error = exception.Message;
            _log.LogError(exception, "Inspection history load failed for {Directory}.", directory);
        }
    }

    private async Task LoadRecordAsync(CancellationToken token)
    {
        // Keep the previous command invocation in the shutdown wait, even after a new selection.
        var previous = LoadRecordCommand.ExecutionTask;
        var record = SelectedRecord;
        try
        {
            if (_shuttingDown || record is null)
                return;
            Error = null;
            Message = null;
            LoadedRecord = null;
            HistoryImages = [];
            SelectedHistoryImage = null;
            var images = await _images.LoadRecordAsync(record, Preview.Recipe, token);
            if (token.IsCancellationRequested || SelectedRecord != record)
                return;
            LoadedRecord = record;
            UpdateHistoryImages(images, images.FirstOrDefault()?.Record);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested && SelectedRecord == record)
                Error = exception.Message;
            _log.LogError(exception, "Inspection image history failed for PCB {Number}.", record?.Number);
        }
        finally
        {
            // Update the current result before waiting for a cancelled earlier read.
            if (previous is { IsCompleted: false })
                await previous;
        }
    }

    private void UpdateHistoryImages(IReadOnlyList<PcbInspectionImageItem> images, PcbInspectionImage? selected)
    {
        var recipe = LoadedRecord is { } record && MachineStore.IsSameRecipeName(record.RecipeName, Preview.Recipe.Name)
            ? Preview.Recipe : null;
        // Rebind labels without reading or decoding the saved pixels again.
        HistoryImages = images.Select(image => image with { Recipe = recipe }).ToArray();
        var previous = SelectedHistoryImage;
        SelectedHistoryImage = HistoryImages.FirstOrDefault(image => ReferenceEquals(image.Record, selected));
        if (SelectedTab == InspectionTeachingTab.History && SelectedHistoryImage == previous)
            UseHistoryImage();
    }

    public InspectionPoint? HistoryImageTarget
    {
        get
        {
            if (LoadedRecord is not { } record || SelectedHistoryImage is not { Image: not null } image
                || !MachineStore.IsSameRecipeName(record.RecipeName, Preview.Recipe.Name))
                return null;
            return Points.FirstOrDefault(point => point.Metadata?.IsForTarget(record.HeatSink, image.Record.BoltId) == true);
        }
    }

    private void UseHistoryImage()
    {
        InspectCommand.Cancel();
        Error = null;
        HistoryImageError = null;
        Message = null;
        ImageSource = null;
        OriginalImage = null;
        var saved = SelectedHistoryImage;
        var target = HistoryImageTarget;
        if (saved?.Image is { } image)
        {
            if (target is null)
            {
                HistoryImageError = LoadedRecord is { } record && !MachineStore.IsSameRecipeName(record.RecipeName, Preview.Recipe.Name)
                    ? UiText.Format($"Load recipe '{record.RecipeName}' to reinspect.")
                    : UiText.Get("No matching reference point. View only.");
            }
            else
            {
                var reference = target.FindImage(_carrierImages)?.Image;
                if (reference is null)
                {
                    HistoryImageError = UiText.Get("Reference image unavailable");
                    target = null;
                }
                else if (reference.PixelWidth != image.PixelWidth || reference.PixelHeight != image.PixelHeight)
                {
                    HistoryImageError = UiText.Get("Image dimensions do not match the recipe image.");
                    target = null;
                }
            }
            SelectedPoint = target;
            Preview.Clear(IsDataMatrixSelected ? target!.HeatSink : null, target?.Bolt);
            try
            {
                Preview.SetSavedImage(image, target is null ? null : target.Metadata!.Region ?? saved.Record.Region);
            }
            catch (Exception exception)
            {
                Preview.Clear();
                HistoryImageError = exception.Message;
                _log.LogError(exception, "Inspection history preview failed for PCB {Number}.", LoadedRecord!.Number);
            }
        }
        else
        {
            SelectedPoint = null;
            Preview.Clear();
            HistoryImageError = saved?.Error;
        }
        if (saved is not null)
        {
            ImageSource = $"PCB {LoadedRecord!.Number} · {saved.Title} · {saved.Record.CapturedAt:yyyy-MM-dd HH:mm:ss}";
            OriginalImage = saved;
        }
        OnPropertyChanged(nameof(IsInspectAllowed));
        OnPropertyChanged(nameof(IsDrawRegionAllowed));
    }
}
