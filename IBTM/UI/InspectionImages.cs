using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using IBTM.Core;
using IBTM.Inspection;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

// Shared DB reads and frozen image decoding; each screen owns its selection and editing lifetime.
public sealed class InspectionImages
{
    private readonly MachineStore _store;
    private readonly ILogger<InspectionImages> _log;

    public InspectionImages(MachineStore store, ILogger<InspectionImages> log)
    {
        _store = store;
        _log = log;
    }

    public Task<CarrierImageTileView[]> LoadRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        var name = recipe.Name;
        var tiles = recipe.CarrierImages.ToArray();
        return Task.Run(() => tiles.Select(tile =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            var bytes = _store.LoadRecipeImage(name, tile.Number);
            var readMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            var image = InspectionPreview.DecodeImage(bytes);
            _log.LogInformation("Recipe image {Recipe}/{Image}: DB read={ReadMs:F1} ms, decode={DecodeMs:F1} ms, pixels={Width}x{Height}.",
                name, tile.Number, readMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                image.PixelWidth, image.PixelHeight);
            return new CarrierImageTileView(tile, image);
        }).ToArray(), cancellationToken);
    }

    public Task<PcbInspectionImageView[]> LoadRecordAsync(PcbRecord record, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => _store.LoadPcbImages(record).Select(image =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new PcbInspectionImageView(image, InspectionPreview.DecodeImage(image.Png),
                image.BoltId is { } id ? record.GetBoltOrdinal(id) : null,
                image.BoltId is { } boltId ? record.BoltNames?.GetValueOrDefault(boltId) : null);
        }).ToArray(), cancellationToken);
    }
}

public sealed record CarrierImageTileView(
    CarrierImageTile Metadata,
    BitmapSource Image)
{
    public override string ToString()
    {
        return $"FOV {Metadata.Number}";
    }
}

public sealed record PcbInspectionImageView(PcbInspectionImage Record, BitmapSource Image, int? Ordinal = null, string? Name = null)
{
    public string Title => Record.BoltId.HasValue ? BoltPoint.GetDisplayName(Name, Ordinal) : "Data Matrix";
    public string Verdict => Record.Success ? "OK" : "NG";
    public Rect Region => new(Record.Region.X, Record.Region.Y, Record.Region.Width, Record.Region.Height);
    public string Details => Record.BoltId.HasValue
        ? $"Bright {Record.BrightRatio:P2} · Required ≥ {Record.MinimumBrightRatio:P2}"
        : Record.Barcode ?? "Data Matrix not read";
    public string Resolution => $"{Image.PixelWidth} × {Image.PixelHeight} px";

    public override string ToString()
    {
        return $"{Title} · {Verdict}";
    }
}
