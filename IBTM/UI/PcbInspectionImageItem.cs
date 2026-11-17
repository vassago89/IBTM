using System.Windows;
using System.Windows.Media.Imaging;
using IBTM.Core;
using IBTM.Storage;

namespace IBTM.UI;

public sealed record PcbInspectionImageItem(
    PcbInspectionImage Record, BitmapSource? Image, Recipe? Recipe, int? Ordinal = null, string? Error = null)
{
    public string Title
    {
        get
        {
            if (Record.BoltId is not { } id)
                return UiText.Get("Data Matrix");
            return Recipe?.Pcb.GetBoltName(id, Ordinal) ?? BoltPoint.GetDisplayName(null, Ordinal);
        }
    }
    public string? Verdict => Error is not null ? "—"
        : Record.BoltId is null ? UiText.Get(Record.Success == true ? "Recognized" : "Not read")
        : null;
    public Rect? Region => Record.BoltId is null && Record.Region is { } region
        ? new(region.X, region.Y, region.Width, region.Height) : null;

    public string? Details
    {
        get
        {
            if (Error is not null)
                return Error;
            if (Record.BoltId.HasValue)
                return null;
            var details = Record.Barcode ?? UiText.Get("Data Matrix not read");
            if (Record.Threshold is { } threshold)
                details += UiText.Format($" · Threshold {threshold}");
            if (Record.Dilated is { } dilated)
                details += UiText.Get(dilated ? " · Dilate ON" : " · Dilate OFF");
            return details;
        }
    }

    public string? Resolution => Image is { } image ? $"{image.PixelWidth} × {image.PixelHeight} px" : null;

    public override string ToString()
    {
        return $"{Title} {Verdict}";
    }
}
