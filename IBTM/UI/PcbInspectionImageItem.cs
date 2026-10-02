using System.Windows;
using System.Windows.Media.Imaging;
using IBTM.Core;
using IBTM.Storage;

namespace IBTM.UI;

public sealed record PcbInspectionImageItem(
    PcbInspectionImage Record, BitmapSource? Image, Recipe Recipe, int? Ordinal = null, string? Error = null)
{
    public string Title
    {
        get
        {
            return Record.BoltId is { } id
                ? Recipe.Pcb.GetBoltName(id, Ordinal)
                : UiText.Get("Data Matrix");
        }
    }
    public string Verdict => Error is not null ? "—" : Record.Success ? "OK" : "NG";
    public Rect Region => new(Record.Region.X, Record.Region.Y, Record.Region.Width, Record.Region.Height);

    public string Details
    {
        get
        {
            if (Error is not null)
                return Error;
            if (Record.BoltId.HasValue)
                return UiText.Format($"Bright {Record.BrightRatio:P2} · Required ≥ {Record.MinimumBrightRatio:P2}");
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
        return $"{Title} · {Verdict}";
    }
}
