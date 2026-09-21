using System;

namespace IBTM.Core;

public sealed record InspectionCapture(
    int? BoltNumber,
    DateTimeOffset CapturedAt,
    ImageFrame Frame,
    PixelRegion Region,
    bool Success,
    string? Barcode = null,
    double? BrightRatio = null,
    double? MinimumBrightRatio = null);
