using System;
using IBTM.Core;

namespace IBTM.Inspection;

public sealed record BoltInspectionImage(
    ImageFrame Image,
    int BoltNumber,
    HeatSinkSlot HeatSink,
    int RegionSize,
    bool Present,
    DateTimeOffset CapturedAt);
