using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.Storage;

public sealed record PcbRecord(
    [property: JsonIgnore] long Number,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string RecipeName,
    HeatSinkSlot HeatSink,
    string? PcbBarcode,
    AssemblyResult PcbBarcodeResult,
    AssemblyResult FasteningResult,
    AssemblyResult InspectionResult,
    IReadOnlyDictionary<Guid, BoltResult> ShootingBoltResults,
    IReadOnlyDictionary<Guid, BoltResult> PickupBoltResults,
    IReadOnlyList<Guid> BoltIds)
{
    public int InspectionPass { get; init; }
    public bool IsDataMatrixNgExcluded { get; init; }
    public PcbAlignment? Alignment { get; init; }
    public string? FiducialError { get; init; }
    public bool IsFiducialNgExcluded { get; init; }
    public HeightReferencePlane? HeightReference { get; init; }
    public IReadOnlyDictionary<Guid, BoltHeightResult> HeightResults { get; init; } = new Dictionary<Guid, BoltHeightResult>();

    // The order belongs to this recorded PCB, independent of later recipe edits.
    public int? GetBoltOrdinal(Guid boltId)
    {
        for (var index = 0; index < BoltIds.Count; index++)
        {
            if (BoltIds[index] == boltId)
                return index + 1;
        }
        return null;
    }

    [JsonIgnore]
    public string? DatabaseFile { get; init; }

    [JsonIgnore]
    public bool IsCurveDataLoaded { get; init; } = true;

    [JsonIgnore]
    public AssemblyResult Result => FasteningResult == AssemblyResult.Ng ? AssemblyResult.Ng : InspectionResult;
}
