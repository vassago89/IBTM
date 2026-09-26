using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public partial class PcbDetailsViewModel : ObservableObject
{
    private readonly MachineStore _store;
    private readonly ILogger<PcbDetailsViewModel> _log;
    private int _imageRequest;

    public PcbDetailsViewModel(MachineStore store, ILogger<PcbDetailsViewModel> log)
    {
        _store = store;
        _log = log;
        LoadImagesCommand = new AsyncRelayCommand(LoadImagesAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        BoltResults = [];
        Images = [];
    }

    public IAsyncRelayCommand LoadImagesCommand { get; }

    [ObservableProperty]
    public partial PcbRecord? Record { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PcbBoltResultView> BoltResults { get; private set; }

    [ObservableProperty]
    public partial PcbBoltResultView? SelectedBolt { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PcbInspectionImageView> Images { get; private set; }

    [ObservableProperty]
    public partial PcbInspectionImageView? SelectedImage { get; set; }

    [ObservableProperty]
    public partial string? ImageError { get; private set; }

    partial void OnRecordChanged(PcbRecord? oldValue, PcbRecord? newValue)
    {
        var selected = SelectedBolt;
        BoltResults = newValue is null ? [] : newValue.PcbBoltResults
            .Select(pair => new PcbBoltResultView(pair.Key, FasteningHead.Shooting, pair.Value))
            .Concat(newValue.PickupBoltResults.Select(pair => new PcbBoltResultView(pair.Key, FasteningHead.Pickup, pair.Value)))
            .OrderBy(row => row.Number).ThenBy(row => row.Head).ToArray();
        SelectedBolt = BoltResults.FirstOrDefault(row => row.Number == selected?.Number && row.Head == selected.Head)
            ?? BoltResults.FirstOrDefault();
        if (oldValue?.Number != newValue?.Number || oldValue?.DatabaseFile != newValue?.DatabaseFile)
        {
            Images = [];
            SelectedImage = null;
            RefreshImages();
        }
    }

    partial void OnSelectedBoltChanged(PcbBoltResultView? value)
    {
        if (value is not null)
            SelectedImage = Images.FirstOrDefault(image => image.Record.BoltNumber == value.Number);
    }

    public void RefreshImages()
    {
        LoadImagesCommand.Cancel();
        _ = LoadImagesCommand.ExecuteAsync(null);
    }

    private async Task LoadImagesAsync(CancellationToken cancellationToken)
    {
        var request = ++_imageRequest;
        var record = Record;
        ImageError = null;
        if (record is null)
            return;
        try
        {
            var images = await Task.Run(() =>
            {
                var saved = _store.LoadPcbImages(record);
                return saved.Select(image =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bitmap = InspectionPreview.DecodeImage(image.Png);
                    return new PcbInspectionImageView(image, bitmap);
                }).ToArray();
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (request != _imageRequest)
                return;
            var hasSelection = SelectedImage is not null || SelectedBolt is not null;
            var selectedNumber = SelectedImage is { } selected
                ? selected.Record.BoltNumber : SelectedBolt?.Number;
            Images = images;
            SelectedImage = hasSelection
                ? images.FirstOrDefault(image => image.Record.BoltNumber == selectedNumber)
                : images.FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (request != _imageRequest)
                return;
            ImageError = $"Inspection images could not be loaded: {exception.Message}";
            _log.LogError(exception, "PCB {Number} image history load failed.", record.Number);
        }
    }
}

public sealed record PcbBoltResultView(int Number, FasteningHead Head, BoltResult Result)
{
    public string HeadLabel => Head == FasteningHead.Pickup ? "H1 · Pickup" : "H2 · Shooting";
    public string Title => $"Bolt {Number} · {HeadLabel}";
    public string Verdict => Result.Source == BoltResultSource.DryRun ? "DRY RUN" : Result.Success ? "OK" : "NG";
    public double? TotalTurns => Result.Controller?.Angle3 / 360.0;
    public string? ControllerStatus => Result.Controller is { } data
        ? $"{((AdcEventStatus)data.StatusCode).GetDescription()} ({data.StatusCode})" : null;
    public string? Direction => Result.Controller is { } data
        ? $"{((AdcDirection)data.DirectionCode).GetDescription()} ({data.DirectionCode})" : null;
    public string? ControllerErrorDescription => Result.Controller is { } data
        ? AdcControllerError.Describe(data.ErrorCode) : null;
    public string RegisterText => Result.Controller?.Registers is { } registers
        ? string.Join("  ", registers.Select((value, index) => $"{3200 + index}: {value:X4}")) : "Not recorded";
}

public sealed record PcbInspectionImageView(PcbInspectionImage Record, BitmapSource Image)
{
    public string Title => Record.BoltNumber is { } number ? $"Bolt {number}" : "Data Matrix";
    public string Verdict => Record.Success ? "OK" : "NG";
    public Rect Region => new(Record.Region.X, Record.Region.Y, Record.Region.Width, Record.Region.Height);
    public string Details => Record.BoltNumber.HasValue
        ? $"Bright {Record.BrightRatio:P2} · Required ≥ {Record.MinimumBrightRatio:P2}"
        : Record.Barcode ?? "Data Matrix not read";
    public string Resolution => $"{Image.PixelWidth} × {Image.PixelHeight} px";
}
