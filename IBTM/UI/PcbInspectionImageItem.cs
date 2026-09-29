using System.Windows;
using System.Windows.Media.Imaging;
using IBTM.Core;
using IBTM.Storage;

namespace IBTM.UI;

public sealed record PcbInspectionImageItem(
    PcbInspectionImage Record, BitmapSource? Image, int? Ordinal = null, string? Name = null, string? Error = null)
{
    public string Title => Record.BoltId.HasValue ? BoltPoint.GetDisplayName(Name, Ordinal) : UiText.Get("Data Matrix");
    public string Verdict => Record.Success ? "OK" : "NG";
    public Rect Region => new(Record.Region.X, Record.Region.Y, Record.Region.Width, Record.Region.Height);

    public string Details
    {
        get
        {
            return Record.BoltId.HasValue
                ? UiText.Format($"Bright {Record.BrightRatio:P2} · Required ≥ {Record.MinimumBrightRatio:P2}")
                : Record.Barcode ?? UiText.Get("Data Matrix not read");
        }
    }

    public string? Resolution => Image is { } image ? $"{image.PixelWidth} × {image.PixelHeight} px" : null;

    public override string ToString()
    {
        return $"{Title} · {Verdict}";
    }
}
