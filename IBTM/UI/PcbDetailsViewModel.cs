using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly InspectionImages _images;
    private readonly ILogger<PcbDetailsViewModel> _log;

    public PcbDetailsViewModel(InspectionImages images, ILogger<PcbDetailsViewModel> log)
    {
        _images = images;
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

    public IReadOnlyList<PcbBoltPresenceView> PresenceResults => Record is { } record
        ? record.BoltPresenceResults.Select(pair => new PcbBoltPresenceView(
            pair.Key, record.GetBoltOrdinal(pair.Key), pair.Value, record.BoltNames?.GetValueOrDefault(pair.Key)))
            .OrderBy(row => row.Ordinal).ToArray() : [];

    partial void OnRecordChanged(PcbRecord? oldValue, PcbRecord? newValue)
    {
        OnPropertyChanged(nameof(PresenceResults));
        var selected = SelectedBolt;
        var selectedImage = SelectedImage;
        BoltResults = newValue is null ? [] : newValue.ShootingBoltResults
            .Select(pair => new PcbBoltResultView(pair.Key, newValue.GetBoltOrdinal(pair.Key), FasteningHead.Shooting, pair.Value,
                newValue.BoltPresenceResults.TryGetValue(pair.Key, out var present) ? present : null,
                newValue.BoltNames?.GetValueOrDefault(pair.Key)))
            .Concat(newValue.PickupBoltResults.Select(pair => new PcbBoltResultView(pair.Key, newValue.GetBoltOrdinal(pair.Key), FasteningHead.Pickup, pair.Value,
                newValue.BoltPresenceResults.TryGetValue(pair.Key, out var present) ? present : null,
                newValue.BoltNames?.GetValueOrDefault(pair.Key))))
            .OrderBy(row => row.Ordinal).ThenBy(row => row.Head).ToArray();
        SelectedBolt = BoltResults.FirstOrDefault(row => row.BoltId == selected?.BoltId && row.Head == selected.Head)
            ?? BoltResults.FirstOrDefault();
        if (oldValue?.Number != newValue?.Number || oldValue?.DatabaseFile != newValue?.DatabaseFile)
        {
            Images = [];
            SelectedImage = null;
            RefreshImages();
        }
        else
        {
            SelectedImage = selectedImage;
        }
    }

    partial void OnSelectedBoltChanged(PcbBoltResultView? value)
    {
        if (value is not null)
            SelectedImage = Images.FirstOrDefault(image => image.Record.BoltId == value.BoltId);
    }

    public void RefreshImages()
    {
        _ = LoadImagesCommand.ExecuteAsync(null);
    }

    private async Task LoadImagesAsync(CancellationToken cancellationToken)
    {
        var record = Record;
        ImageError = null;
        if (record is null)
            return;
        try
        {
            var images = await _images.LoadRecordAsync(record, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;
            var hasSelection = SelectedImage is not null || SelectedBolt is not null;
            var selectedBoltId = SelectedImage is { } selected
                ? selected.Record.BoltId : SelectedBolt?.BoltId;
            Images = images;
            SelectedImage = hasSelection
                ? images.FirstOrDefault(image => image.Record.BoltId == selectedBoltId)
                : images.FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            ImageError = $"Inspection images could not be loaded: {exception.Message}";
            _log.LogError(exception, "PCB {Number} image history load failed.", record.Number);
        }
    }
}

public sealed record PcbBoltPresenceView(Guid BoltId, int? Ordinal, bool Present, string? Name)
{
    public string BoltLabel => BoltPoint.GetDisplayName(Name, Ordinal);
}

public sealed record PcbBoltResultView(
    Guid BoltId, int? Ordinal, FasteningHead Head, BoltResult Result, bool? Present = null, string? Name = null)
{
    public string HeadLabel => Head == FasteningHead.Pickup ? "H1 · Pickup" : "H2 · Shooting";
    public string BoltLabel => BoltPoint.GetDisplayName(Name, Ordinal);
    public string Title => $"{BoltLabel} · {HeadLabel}";
    public string Verdict => Result.Source == BoltResultSource.DryRun ? "DRY RUN" : Result.Success ? "OK" : "NG";
    public string VisionVerdict => Present is not { } present ? "—" : present ? "OK" : "NG";
    public string TurnsVerdict => Result.TurnsResult switch
    {
        AssemblyResult.Ok => "OK",
        AssemblyResult.Ng => "NG",
        AssemblyResult.Pending => "No data",
        _ => "Not set",
    };
    public string? ControllerStatus => Result.Controller is { } data
        ? $"{((AdcEventStatus)data.StatusCode).GetDescription()} ({data.StatusCode})" : null;
    public string? Direction => Result.Controller is { } data
        ? $"{((AdcDirection)data.DirectionCode).GetDescription()} ({data.DirectionCode})" : null;
    public string? ControllerErrorDescription => Result.Controller is { } data
        ? AdcControllerError.Describe(data.ErrorCode) : null;
    public string RegisterText => Result.Controller?.Registers is { } registers
        ? string.Join("  ", registers.Select((value, index) => $"{3200 + index}: {value:X4}")) : "Not recorded";
}
