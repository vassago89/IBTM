using System;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.Storage;

public sealed record PcbInspectionImage(
    int? BoltNumber,
    DateTimeOffset CapturedAt,
    PixelRegion Region,
    bool Success,
    string? Barcode,
    double? BrightRatio,
    double? MinimumBrightRatio,
    [property: JsonIgnore] byte[] Png);
