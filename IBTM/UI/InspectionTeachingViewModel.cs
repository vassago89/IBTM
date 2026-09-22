using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
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

    public InspectionTeachingViewModel(MachineStore store, RecipeManager recipes,
        PcbHistorySettings history, ILogger<InspectionTeachingViewModel> log)
    {
        _store = store;
        _recipes = recipes;
        _log = log;
        Draft = new();
        Preview = new(Draft);
        Points = [];
        HistoryImages = [];
        Records = [];
        RecipeNames = [];
        HistoryDirectory = history.Directory;
        SelectedRecipeName = recipes.Current.Name;
        LoadRecipeCommand = new AsyncRelayCommand(LoadRecipeAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        InspectCommand = new AsyncRelayCommand(InspectAsync);
        RefreshHistoryCommand = new AsyncRelayCommand(RefreshHistoryAsync);
        LoadOlderCommand = new AsyncRelayCommand(LoadOlderAsync);
        LoadRecordCommand = new AsyncRelayCommand(LoadRecordAsync);
        DrawRegionCommand = new RelayCommand<Rect>(DrawRegion, IsDrawRegionAllowed);
        UseHistoryImageCommand = new RelayCommand(UseHistoryImage, () => IsUseHistoryImageAllowed);
        ShowRecipeImageCommand = new RelayCommand(ShowRecipeImage);
        MeasureCommand = new RelayCommand<ImageRuler>(Measure);
        ApplyResolutionCommand = new RelayCommand(ApplyResolution, () => RulerResolution is > 0);
        _commands = [LoadRecipeCommand, SaveCommand, InspectCommand, RefreshHistoryCommand, LoadOlderCommand, LoadRecordCommand];
        foreach (var command in _commands)
            command.PropertyChanged += OnCommandChanged;
    }

    public Recipe Draft { get; }
    public InspectionPreview Preview { get; }
    public ObservableCollection<PcbRecord> Records { get; }
    public IAsyncRelayCommand LoadRecipeCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand InspectCommand { get; }
    public IAsyncRelayCommand RefreshHistoryCommand { get; }
    public IAsyncRelayCommand LoadOlderCommand { get; }
    public IAsyncRelayCommand LoadRecordCommand { get; }
    public IRelayCommand<Rect> DrawRegionCommand { get; }
    public IRelayCommand UseHistoryImageCommand { get; }
    public IRelayCommand ShowRecipeImageCommand { get; }
    public IRelayCommand<ImageRuler> MeasureCommand { get; }
    public IRelayCommand ApplyResolutionCommand { get; }

    [ObservableProperty] public partial IReadOnlyList<string> RecipeNames { get; private set; }
    [ObservableProperty] public partial string? SelectedRecipeName { get; set; }
    [ObservableProperty] public partial IReadOnlyList<CarrierImageTileView> Points { get; private set; }
    [ObservableProperty] public partial CarrierImageTileView? SelectedPoint { get; set; }
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
    [ObservableProperty] public partial bool IsMeasuring { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RulerResolution))]
    [NotifyCanExecuteChangedFor(nameof(ApplyResolutionCommand))]
    public partial ImageRuler? Ruler { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RulerResolution))]
    [NotifyCanExecuteChangedFor(nameof(ApplyResolutionCommand))]
    public partial double? RulerMillimeters { get; set; }

    public bool IsBusy => _commands.Any(command => command.IsRunning);
    public bool IsIdle => !IsBusy;
    public bool IsDataMatrixSelected => SelectedPoint?.Metadata.IsBarcode == true;
    public bool IsBoltSelected => SelectedBolt is not null;
    public BoltPoint? SelectedBolt => SelectedPoint is { Metadata.IsBarcode: false } point
        ? Draft.Pcb.BoltPoints.SingleOrDefault(bolt => bolt.HeatSink == point.Metadata.HeatSink && bolt.Number == point.Metadata.BoltNumber)
        : null;
    public DataMatrixInspectionRecipe? DataMatrix => IsDataMatrixSelected
        ? Draft.BoltInspection.GetDataMatrix(SelectedPoint!.Metadata.HeatSink) : null;
    public double? RulerResolution => Ruler is { PixelLength: >= 1 } ruler && RulerMillimeters is > 0
        && double.IsFinite(RulerMillimeters.Value) ? RulerMillimeters.Value / ruler.PixelLength : null;

    public void Activate()
    {
        try
        {
            RecipeNames = _recipes.GetRecipeNames();
            if (!IsLoaded && !LoadRecipeCommand.IsRunning && RecipeNames.Contains(SelectedRecipeName))
                _ = LoadRecipeCommand.ExecuteAsync(null);
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
        Error = null;
        Message = null;
        var name = SelectedRecipeName;
        if (string.IsNullOrWhiteSpace(name))
            return;
        try
        {
            var loaded = await Task.Run(() =>
            {
                var recipe = _store.LoadRecipe<Recipe>(name);
                var images = recipe.CarrierImages.OrderBy(tile => tile.HeatSink).ThenBy(tile => !tile.IsBarcode)
                    .ThenBy(tile => tile.BoltNumber).Select(tile =>
                    {
                        token.ThrowIfCancellationRequested();
                        return new CarrierImageTileView(tile, DecodeImage(_store.LoadRecipeImage(name, tile.Number)));
                    }).ToArray();
                return (Recipe: recipe, Images: images);
            }, token);
            token.ThrowIfCancellationRequested();
            SelectedPoint = null;
            Draft.ReplaceWith(loaded.Recipe);
            Points = loaded.Images;
            IsLoaded = true;
            OnPropertyChanged(nameof(Draft));
            SelectedPoint = Points.FirstOrDefault();
            Message = Points.Count == 0 ? "No recorded inspection positions. Record positions in Teaching first." : "Recipe images loaded.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Error = exception.Message;
            _log.LogError(exception, "Inspection teaching load failed for {Recipe}.", name);
        }
    }

    partial void OnSelectedPointChanged(CarrierImageTileView? value)
    {
        ShowRecipeImage();
        OnPropertyChanged(nameof(IsDataMatrixSelected));
        OnPropertyChanged(nameof(IsBoltSelected));
        OnPropertyChanged(nameof(SelectedBolt));
        OnPropertyChanged(nameof(DataMatrix));
        UseHistoryImageCommand.NotifyCanExecuteChanged();
    }

    private void ShowRecipeImage()
    {
        InspectCommand.Cancel();
        Error = null;
        ImageSource = null;
        Ruler = null;
        RulerMillimeters = null;
        OriginalResult = null;
        Preview.Clear(IsDataMatrixSelected ? SelectedPoint!.Metadata.HeatSink : null, SelectedBolt);
        if (SelectedPoint is not { } point)
            return;
        var region = point.Metadata.Region ?? PixelRegion.CenteredSquare(point.Image.PixelWidth, point.Image.PixelHeight,
            Math.Min(point.Image.PixelWidth, point.Image.PixelHeight) / 4);
        try
        {
            Preview.SetSavedImage(point.Image, region);
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            _log.LogError(exception, "Inspection teaching preview failed for image {Number}.", point.Metadata.Number);
        }
        ImageSource = $"Recipe · {Draft.Name} · {point.Metadata.HeatSink.GetDescription()} · "
            + (point.Metadata.IsBarcode ? "Data Matrix" : $"Bolt {point.Metadata.BoltNumber}");
    }

    private bool IsDrawRegionAllowed(Rect bounds)
    {
        return !IsBusy && !IsMeasuring && SelectedPoint is not null && Preview.HasImage;
    }

    private void DrawRegion(Rect bounds)
    {
        if (Preview.Image is not { } image || SelectedPoint is not { } point || bounds.IsEmpty)
            return;
        var region = PixelRegion.CenteredSquare(image.PixelWidth, image.PixelHeight, (int)Math.Ceiling(Math.Max(bounds.Width, bounds.Height)));
        point.Metadata.Region = region;
        Preview.SetSavedImage(image, region);
        Message = "ROI changed in this editing copy. Save applies it to the next inspection point.";
    }

    private void Measure(ImageRuler? ruler)
    {
        Ruler = ruler;
    }

    private void ApplyResolution()
    {
        Draft.CarrierImageMillimetersPerPixel = RulerResolution!.Value;
        OnPropertyChanged(nameof(Draft));
        Message = "Resolution updated. Save keeps the change; recorded coordinates stay fixed.";
    }

    private async Task InspectAsync(CancellationToken token)
    {
        Error = null;
        try
        {
            if (SelectedPoint is not null && Preview.HasImage)
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
            Message = "Inspection settings saved. The active recipe uses them from the next inspection point.";
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
        Records.Clear();
        await LoadOlderAsync(token);
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
            foreach (var record in records)
                Records.Add(record);
            HasOlder = records.Count == 100;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Error = exception.Message;
            _log.LogError(exception, "Inspection history load failed for {Directory}.", directory);
        }
    }

    private async Task LoadRecordAsync(CancellationToken token)
    {
        if (SelectedRecord is not { } record)
            return;
        Error = null;
        try
        {
            var images = await Task.Run(() => _store.LoadPcbImages(record)
                .Select(image => new PcbInspectionImageView(image, DecodeImage(image.Png))).ToArray(), token);
            token.ThrowIfCancellationRequested();
            LoadedRecord = record;
            HistoryImages = images;
            SelectedHistoryImage = images.FirstOrDefault();
            Message = images.Length == 0 ? "No saved inspection images for this PCB." : "Select an image and use it for reinspection.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Error = exception.Message;
            _log.LogError(exception, "Inspection image history failed for PCB {Number}.", record.Number);
        }
    }

    private bool IsUseHistoryImageAllowed => LoadedRecord?.RecipeName == Draft.Name && SelectedHistoryImage is not null
        && Points.Any(point => point.Metadata.HeatSink == LoadedRecord.HeatSink
            && (point.Metadata.IsBarcode ? SelectedHistoryImage.Record.BoltNumber is null
                : point.Metadata.BoltNumber == SelectedHistoryImage.Record.BoltNumber));

    private void UseHistoryImage()
    {
        if (!IsUseHistoryImageAllowed)
            return;
        var saved = SelectedHistoryImage!;
        var target = Points.Single(point => point.Metadata.HeatSink == LoadedRecord!.HeatSink
            && (point.Metadata.IsBarcode ? saved.Record.BoltNumber is null : point.Metadata.BoltNumber == saved.Record.BoltNumber));
        if (target.Image.PixelWidth != saved.Image.PixelWidth || target.Image.PixelHeight != saved.Image.PixelHeight)
        {
            Error = "Saved result image dimensions differ from the recipe image. Select an image with the same resolution.";
            return;
        }
        Error = null;
        SelectedPoint = target;
        InspectCommand.Cancel();
        Preview.Clear(IsDataMatrixSelected ? target.Metadata.HeatSink : null, SelectedBolt);
        try
        {
            Preview.SetSavedImage(saved.Image, target.Metadata.Region ?? saved.Record.Region);
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            _log.LogError(exception, "Inspection history preview failed for PCB {Number}.", LoadedRecord!.Number);
            return;
        }
        ImageSource = $"PCB {LoadedRecord!.Number} · {saved.Title} · {saved.Record.CapturedAt:yyyy-MM-dd HH:mm:ss}";
        OriginalResult = $"Recorded {saved.Verdict} · {saved.Details}";
        Ruler = null;
        RulerMillimeters = null;
    }

    private static BitmapSource DecodeImage(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var image = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();
        return image;
    }
}
