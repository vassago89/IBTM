using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using IBTM.Core;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

// Shared DB reads and frozen image decoding; each screen owns its selection and editing lifetime.
public sealed class InspectionImageLoader
{
    private readonly MachineStore _store;
    private readonly ILogger<InspectionImageLoader> _log;

    public InspectionImageLoader(MachineStore store, ILogger<InspectionImageLoader> log)
    {
        _store = store;
        _log = log;
    }

    public Task<RecipeImageItem[]> LoadRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        var name = recipe.Name;
        var tiles = recipe.CarrierImages.ToArray();
        return Task.Run(() => tiles.Select(tile =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? bytes = null;
            try
            {
                var started = Stopwatch.GetTimestamp();
                bytes = _store.LoadRecipeImage(name, tile.Number);
                var readMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                started = Stopwatch.GetTimestamp();
                var image = InspectionPreviewViewModel.DecodeImage(bytes);
                _log.LogInformation("Recipe image {Recipe}/{Image}: DB read={ReadMs:F1} ms, decode={DecodeMs:F1} ms, pixels={Width}x{Height}.",
                    name, tile.Number, readMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    image.PixelWidth, image.PixelHeight);
                return new RecipeImageItem(tile, image);
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException
                or ArgumentException or InvalidOperationException or COMException)
            {
                _log.LogError(exception, "Recipe image {Recipe}/{Image} could not be loaded.", name, tile.Number);
                return new RecipeImageItem(tile, null,
                    bytes is null ? UiText.Get("No reference image") : UiText.Get("Image could not be decoded."))
                {
                    UnreadablePng = bytes,
                };
            }
        }).ToArray(), cancellationToken);
    }

    public Task<PcbInspectionImageItem[]> LoadRecordAsync(
        PcbRecord record, Recipe recipe, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => _store.LoadPcbImages(record).Select(image =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            BitmapSource? bitmap = null;
            string? error = null;
            try
            {
                if (image.CapturedAt < record.CreatedAt)
                {
                    error = UiText.Get("This image belongs to an earlier PCB record.");
                    _log.LogWarning("PCB {Number}, image {BoltId}: capture {CapturedAt} predates PCB creation {CreatedAt}.",
                        record.Number, image.BoltId, image.CapturedAt, record.CreatedAt);
                }
                else
                    bitmap = InspectionPreviewViewModel.DecodeImage(image.Png);
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException
                or ArgumentException or InvalidOperationException or COMException)
            {
                error = UiText.Get("Image could not be decoded.");
                _log.LogError(exception, "PCB {Number}, image {BoltId} could not be decoded.", record.Number, image.BoltId);
            }
            return new PcbInspectionImageItem(image, bitmap, recipe,
                image.BoltId is { } id ? record.GetBoltOrdinal(id) : null, error);
        }).ToArray(), cancellationToken);
    }
}
