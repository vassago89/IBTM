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

public partial class PcbResultsViewModel : ObservableObject
{
    private readonly InspectionImageLoader _images;
    private readonly ILogger<PcbResultsViewModel> _log;

    public PcbResultsViewModel(InspectionImageLoader images, ILogger<PcbResultsViewModel> log)
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
    public partial IReadOnlyList<PcbInspectionImageItem> Images { get; private set; }

    [ObservableProperty]
    public partial PcbInspectionImageItem? SelectedImage { get; set; }

    [ObservableProperty]
    public partial string? ImageError { get; private set; }

    public IReadOnlyList<PcbBoltPresenceView> InspectionOnlyResults
    {
        get
        {
            if (Record is not { } record)
                return [];
            return record.BoltPresenceResults
                .Where(pair => !record.ShootingBoltResults.ContainsKey(pair.Key)
                    && !record.PickupBoltResults.ContainsKey(pair.Key))
                .Select(pair => new PcbBoltPresenceView(
                    pair.Key, record.GetBoltOrdinal(pair.Key), pair.Value, record.BoltNames?.GetValueOrDefault(pair.Key)))
                .OrderBy(row => row.Ordinal)
                .ToArray();
        }
    }

    partial void OnRecordChanged(PcbRecord? oldValue, PcbRecord? newValue)
    {
        OnPropertyChanged(nameof(InspectionOnlyResults));
        var selected = SelectedBolt;
        var selectedImage = SelectedImage;
        BoltResults = newValue is null ? [] : newValue.ShootingBoltResults
            .Select(pair => (pair.Key, pair.Value, Head: FasteningHead.Shooting))
            .Concat(newValue.PickupBoltResults.Select(pair => (pair.Key, pair.Value, Head: FasteningHead.Pickup)))
            .Select(row => new PcbBoltResultView(row.Key, newValue.GetBoltOrdinal(row.Key), row.Head, row.Value,
                newValue.BoltPresenceResults.TryGetValue(row.Key, out var present) ? present : null,
                newValue.BoltNames?.GetValueOrDefault(row.Key)))
            .OrderBy(row => row.Result.RecordedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(row => row.Ordinal).ThenBy(row => row.Head).ToArray();
        SelectedBolt = BoltResults.FirstOrDefault(row => row.BoltId == selected?.BoltId && row.Head == selected.Head)
            ?? BoltResults.FirstOrDefault();
        if (oldValue?.Number != newValue?.Number || oldValue?.DatabaseFile != newValue?.DatabaseFile)
        {
            Images = [];
            SelectedImage = null;
            _ = LoadImagesCommand.ExecuteAsync(null);
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

    partial void OnSelectedImageChanged(PcbInspectionImageItem? value)
    {
        if (value is not null)
            SelectedBolt = BoltResults.FirstOrDefault(bolt => bolt.BoltId == value.Record.BoltId);
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
            var failed = images.Where(image => image.Error is not null).Select(image => image.Title).ToArray();
            ImageError = failed.Length == 0 ? null : UiText.Format($"Image unavailable: {string.Join(", ", failed)}");
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
            ImageError = UiText.Format($"Inspection images could not be loaded: {exception.Message}");
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
    public string HeadLabel => UiText.Get(Head);
    public string BoltLabel => BoltPoint.GetDisplayName(Name, Ordinal);
    public string Title => $"{BoltLabel} · {HeadLabel}";
    public string Verdict => Result.Source == BoltResultSource.DryRun ? UiText.Get("DRY RUN") : Result.Success ? "OK" : "NG";
    public string VisionVerdict => Present is not { } present ? "—" : present ? "OK" : "NG";

    public string TurnsVerdict
    {
        get
        {
            switch (Result.TurnsResult)
            {
                case AssemblyResult.Ok:
                    return "OK";
                case AssemblyResult.Ng:
                    return "NG";
                case AssemblyResult.Pending:
                    return UiText.Get("No data");
                default:
                    return UiText.Get("Not set");
            }
        }
    }

    public string? ControllerStatus
    {
        get
        {
            return Result.Controller is { } data
                ? $"{UiText.Get((AdcEventStatus)data.StatusCode)} ({data.StatusCode})" : null;
        }
    }

    public string? Direction
    {
        get
        {
            return Result.Controller is { } data
                ? $"{UiText.Get((AdcDirection)data.DirectionCode)} ({data.DirectionCode})" : null;
        }
    }

    public string? ControllerErrorDescription
    {
        get
        {
            return Result.Controller is { } data
                ? AdcControllerError.Describe(data.ErrorCode) : null;
        }
    }

    public string RegisterText
    {
        get
        {
            return Result.Controller?.Registers is { } registers
                ? string.Join("  ", registers.Select((value, index) => $"{3200 + index}: {value:X4}"))
                : UiText.Get("Not recorded");
        }
    }
}
