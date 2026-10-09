using System;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.Storage;

public sealed record PcbInspectionImage(
    [property: JsonIgnore] Guid? BoltId,
    DateTimeOffset CapturedAt,
    PixelRegion Region,
    bool Success,
    string? Barcode,
    double? BrightRatio,
    double? MinimumBrightRatio,
    [property: JsonIgnore] byte[] Png,
    int? Threshold = null,
    bool? Dilated = null);
