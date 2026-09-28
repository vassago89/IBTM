using System;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.Storage;

public sealed record PcbInspectionImage(
    Guid? BoltId,
    DateTimeOffset CapturedAt,
    PixelRegion Region,
    bool Success,
    string? Barcode,
    double? BrightRatio,
    double? MinimumBrightRatio,
    [property: JsonIgnore] byte[] Png);
