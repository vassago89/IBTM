using System;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.Inspection;

[JsonConverter(typeof(JsonStringEnumConverter<InspectionReferencePoint>))]
public enum InspectionReferencePoint
{
    Fiducial1, Fiducial2, HeightReference1, HeightReference2, HeightReference3,
}

public sealed class CarrierImageTile
{
    public int Number { get; set; }
    // Data Matrix position. Bolt inspection XY belongs to BoltPoint.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AxisPosition? Center { get; set; }
    public PixelRegion? Region { get; set; }
    public Guid? BoltId { get; set; }
    public bool IsBarcode { get; set; }
    public HeatSinkSlot HeatSink { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InspectionReferencePoint? ReferencePoint { get; set; }

    // A null target selects Data Matrix, never an unlinked bolt image.
    public bool IsForTarget(HeatSinkSlot heatSink, Guid? boltId)
    {
        return HeatSink == heatSink && ReferencePoint is null
            && (boltId is { } id ? id != Guid.Empty && !IsBarcode && BoltId == id : IsBarcode);
    }
}
